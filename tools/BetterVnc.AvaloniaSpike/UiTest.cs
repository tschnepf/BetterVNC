using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BetterVnc.AvaloniaSpike;

/// <summary>
/// Question 4: can the UI Automation checks used on the WPF viewer drive Avalonia windows? Shows ordinary controls
/// with names and automation ids (the kind the viewer's windows have) and prints every change the controls see, so an
/// outside UIA script's Invoke/SetValue/Toggle/Select calls can be confirmed from this output.
/// </summary>
internal static class UiTest
{
    public static async Task<int> RunAsync()
    {
        int seconds = Program.OptInt("--seconds", 60);
        var connect = new Button { Content = "Connect" };
        AutomationProperties.SetAutomationId(connect, "ConnectButton");
        connect.Click += (_, _) => Console.WriteLine("event: Connect clicked");
        var host = new TextBox { Text = "", Width = 200 };
        AutomationProperties.SetAutomationId(host, "HostBox");
        AutomationProperties.SetName(host, "Host");
        host.TextChanged += (_, _) => Console.WriteLine($"event: Host text = '{host.Text}'");
        var fullscreen = new CheckBox { Content = "Start fullscreen" };
        AutomationProperties.SetAutomationId(fullscreen, "FullscreenBox");
        fullscreen.IsCheckedChanged += (_, _) => Console.WriteLine($"event: Fullscreen = {fullscreen.IsChecked}");
        var picture = new ComboBox { ItemsSource = new[] { "Lossless", "Compressed", "Stream" }, SelectedIndex = 0, Width = 200 };
        AutomationProperties.SetAutomationId(picture, "PictureBox");
        picture.SelectionChanged += (_, _) => Console.WriteLine($"event: Picture = {picture.SelectedItem}");
        var list = new ListBox { ItemsSource = new[] { "Unraid VM 1", "Mac mini", "Office PC" }, Height = 110 };
        AutomationProperties.SetAutomationId(list, "ConnectionList");
        list.SelectionChanged += (_, _) => Console.WriteLine($"event: Connection = {list.SelectedItem}");
        list.DoubleTapped += (_, _) => Console.WriteLine($"event: Connection double-tapped = {list.SelectedItem}");
        var screen = new Border { Background = Brushes.DarkSlateBlue, Height = 80, Focusable = true };
        AutomationProperties.SetAutomationId(screen, "RemoteScreen");
        AutomationProperties.SetName(screen, "Remote screen");

        var panel = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(10), Children = { host, connect, fullscreen, picture, list, screen } };
        var window = await Program.ShowTestWindowAsync("BetterVNC UIA test", new Border { Background = Brushes.White, Child = panel }, 520, 640);
        Console.WriteLine($"ready: pid {Environment.ProcessId}, window '{window.Title}'");
        await Task.Delay(seconds * 1000);
        window.Close();
        return 0;
    }
}
