# The guest side of BetterVnc.AgentBench: a full-screen window with known content for measuring the agent's stream.
#   - a page of syntax-coloured code on a dark background (screen content, where H.264 blurs text first);
#   - F14 starts or stops scrolling it at 60 Hz (6 px per frame), the motion case;
#   - F13 flips a 160x160 marker at the top right between black and white, for input-to-picture latency;
#   - red, green and blue squares under the marker tell the bench that this window is the one showing.
# F13 and F14 do nothing in other programs, so a key that lands elsewhere changes nothing there. The window closes
# itself after -Minutes.
# Runs on Windows PowerShell 5.1 (the C# below is C# 5 for its compiler). Start it as the signed-in user, e.g. Win+R:
#   powershell -w hidden -ep bypass -f C:\Users\Public\bvnc\bench-target.ps1
# The window class is compiled once into bench-target-<hash>.dll next to the script (compiling takes half a minute
# in a VM without a GPU); -CompileOnly does just that. It writes how many frames it drew per second to bench-target.log.
param([int]$Minutes = 30, [switch]$CompileOnly)

$source = @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public class BvncBenchTarget : Form
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    const int LineHeight = 18, Lines = 300, Step = 6, Fps = 60;
    readonly Bitmap page;
    readonly Rectangle textRect, markerRect;
    readonly string logPath;
    int offset, drawn, scheduled;
    volatile bool scrolling;
    bool markerWhite;

    public BvncBenchTarget(string logPath, int minutes)
    {
        this.logPath = logPath;
        var screen = Screen.PrimaryScreen.Bounds;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(30, 30, 30);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        textRect = new Rectangle(0, 0, screen.Width - 240, screen.Height);
        markerRect = new Rectangle(screen.Width - 200, 40, 160, 160);
        page = RenderPage(textRect.Width, Lines * LineHeight + textRect.Height);

        var quit = new System.Windows.Forms.Timer { Interval = Math.Max(1, minutes) * 60 * 1000 };
        quit.Tick += delegate { Close(); };
        quit.Start();
        var stats = new System.Windows.Forms.Timer { Interval = 1000 };
        stats.Tick += delegate
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " drawn " + drawn + (scrolling ? " scrolling" : "") + (Form.ActiveForm == this ? "" : " (not focused)") + Environment.NewLine); } catch { }
            drawn = 0;
        };
        stats.Start();
        Shown += delegate { Activate(); };
        KeyDown += OnKey;
        var ticker = new Thread(Tick) { IsBackground = true, Name = "scroll ticker" };
        ticker.Start();
    }

    static Bitmap RenderPage(int width, int height)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var bg = Color.FromArgb(30, 30, 30);
        var words = new[] { "var", "return", "if", "else", "foreach", "await", "private", "static", "readonly", "int", "string", "new" };
        var names = new[] { "frame", "encoder", "picture", "region", "bitrate", "viewer", "latency", "buffer", "session", "capture" };
        var rnd = new Random(1234);
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Consolas", 10.5f))
        {
            g.Clear(bg);
            for (int y = 0, i = 0; y < height; y += LineHeight, i++)
            {
                int n = i % Lines;
                if (n == 0) rnd = new Random(1234); // the page repeats, so scrolling can wrap seamlessly
                int x = 8;
                TextRenderer.DrawText(g, (n + 1).ToString().PadLeft(4), font, new Point(x, y), Color.FromArgb(133, 133, 133), bg);
                x = 60 + 4 * 8 * rnd.Next(0, 4);
                int parts = rnd.Next(3, 9);
                for (int p = 0; p < parts && x < width - 200; p++)
                {
                    string text; Color color;
                    switch (rnd.Next(5))
                    {
                        case 0: text = words[rnd.Next(words.Length)]; color = Color.FromArgb(86, 156, 214); break;
                        case 1: text = "\"" + names[rnd.Next(names.Length)] + " " + rnd.Next(1000) + "\""; color = Color.FromArgb(206, 145, 120); break;
                        case 2: text = "// " + names[rnd.Next(names.Length)] + " of the " + names[rnd.Next(names.Length)]; color = Color.FromArgb(106, 153, 85); break;
                        case 3: text = rnd.Next(100000).ToString(); color = Color.FromArgb(181, 206, 168); break;
                        default: text = names[rnd.Next(names.Length)] + "." + names[rnd.Next(names.Length)] + "(" + rnd.Next(64) + ");"; color = Color.FromArgb(212, 212, 212); break;
                    }
                    TextRenderer.DrawText(g, text, font, new Point(x, y), color, bg);
                    x += TextRenderer.MeasureText(text, font).Width + 6;
                }
            }
        }
        return bmp;
    }

    void Tick()
    {
        timeBeginPeriod(1);
        var clock = Stopwatch.StartNew();
        long next = 0, interval = Stopwatch.Frequency / Fps;
        while (!IsDisposed)
        {
            while (clock.ElapsedTicks < next) Thread.Sleep(1);
            next += interval;
            if (clock.ElapsedTicks > next + 4 * interval) next = clock.ElapsedTicks; // fell behind: don't catch up in a burst
            // One scroll step per tick, never more than one waiting on the UI thread.
            if (scrolling && Interlocked.CompareExchange(ref scheduled, 1, 0) == 0)
            {
                try { BeginInvoke((MethodInvoker)Advance); } catch { return; }
            }
        }
    }

    void Advance()
    {
        scheduled = 0;
        if (!scrolling) return;
        offset = (offset + Step) % (Lines * LineHeight);
        Invalidate(textRect);
        Update();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F13)
        {
            markerWhite = !markerWhite;
            Invalidate(markerRect);
            Update();
        }
        else if (e.KeyCode == Keys.F14) scrolling = !scrolling;
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        if (e.ClipRectangle.IntersectsWith(textRect))
        {
            g.DrawImage(page, textRect, new Rectangle(0, offset, textRect.Width, textRect.Height), GraphicsUnit.Pixel);
            drawn++;
        }
        using (var bg = new SolidBrush(Color.FromArgb(30, 30, 30)))
            g.FillRectangle(bg, new Rectangle(textRect.Right, 0, ClientSize.Width - textRect.Right, ClientSize.Height));
        g.FillRectangle(markerWhite ? Brushes.White : Brushes.Black, markerRect);
        // The beacon: pure red, green and blue squares the bench looks for before it sends any input.
        g.FillRectangle(Brushes.Red, new Rectangle(markerRect.X, 240, 40, 40));
        g.FillRectangle(Brushes.Lime, new Rectangle(markerRect.X + 60, 240, 40, 40));
        g.FillRectangle(Brushes.Blue, new Rectangle(markerRect.X + 120, 240, 40, 40));
    }

    public static void Run(string logPath, int minutes)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.Run(new BvncBenchTarget(logPath, minutes));
    }
}
'@

$hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($source)), 0, 6) -replace '-', ''
$dll = Join-Path $PSScriptRoot "bench-target-$hash.dll"
if (-not (Test-Path $dll)) { Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms, System.Drawing -OutputAssembly $dll -OutputType Library }
if ($CompileOnly) { "compiled $dll"; return }
Add-Type -Path $dll
[BvncBenchTarget]::Run((Join-Path $PSScriptRoot 'bench-target.log'), $Minutes)
