using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace BetterVnc.Viewer.Views;

public enum MessageButtons { Ok, YesNo, OkCancel }

public enum MessageIcon { None, Info, Warning, Error, Question }

public enum MessageResult { None, Ok, Yes, No, Cancel }

/// <summary>A message box (Avalonia has none): the text, an icon, and OK, Yes/No or OK/Cancel buttons.</summary>
public partial class MessageDialog : Window
{
    private MessageResult _result;

    public MessageDialog()
    {
        InitializeComponent();
    }

    private MessageDialog(string text, string title, MessageButtons buttons, MessageIcon icon, MessageResult defaultResult) : this()
    {
        Title = title;
        MessageText.Text = text;
        (string glyph, string brush) = icon switch
        {
            MessageIcon.Info => ("i", "AccentBrush"),
            MessageIcon.Warning => ("!", "WarningBrush"),
            MessageIcon.Error => ("×", "DangerBrush"),
            MessageIcon.Question => ("?", "AccentBrush"),
            _ => ("", ""),
        };
        IconPanel.IsVisible = icon != MessageIcon.None;
        IconGlyph.Text = glyph;
        if (brush.Length > 0 && this.TryFindResource(brush, out var fill) && fill is IBrush b) IconCircle.Fill = b;

        // Closing the window without a button means the cautious answer.
        _result = buttons switch { MessageButtons.YesNo => MessageResult.No, MessageButtons.OkCancel => MessageResult.Cancel, _ => MessageResult.Ok };
        var choices = buttons switch
        {
            MessageButtons.YesNo => new[] { ("Yes", MessageResult.Yes), ("No", MessageResult.No) },
            MessageButtons.OkCancel => new[] { ("OK", MessageResult.Ok), ("Cancel", MessageResult.Cancel) },
            _ => new[] { ("OK", MessageResult.Ok) },
        };
        var preferred = defaultResult != MessageResult.None ? defaultResult : choices[0].Item2;
        foreach (var (label, result) in choices)
        {
            var button = new Button { Content = label, MinWidth = 84, IsDefault = result == preferred, IsCancel = result == _result };
            if (result == choices[0].Item2 && buttons != MessageButtons.Ok && this.TryFindResource("PrimaryButton", out var theme) && theme is Avalonia.Styling.ControlTheme t)
                button.Theme = t;
            button.Click += (_, _) => { _result = result; Close(result); };
            ButtonRow.Children.Add(button);
            if (result == preferred) Opened += (_, _) => button.Focus();
        }
    }

    /// <summary>
    /// Shows a message, modal to <paramref name="owner"/> when it is open, and returns the button chosen.
    /// <paramref name="defaultResult"/> is the button Enter presses (the first one when None).
    /// </summary>
    public static Task<MessageResult> ShowAsync(Window? owner, string text, string title, MessageButtons buttons = MessageButtons.Ok,
        MessageIcon icon = MessageIcon.Info, MessageResult defaultResult = MessageResult.None)
    {
        var dialog = new MessageDialog(text, title, buttons, icon, defaultResult);
        if (owner is { IsVisible: true }) return ShowModalAsync(dialog, owner);
        var done = new TaskCompletionSource<MessageResult>();
        dialog.Closed += (_, _) => done.TrySetResult(dialog._result);
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        return done.Task;
    }

    private static async Task<MessageResult> ShowModalAsync(MessageDialog dialog, Window owner)
    {
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}

/// <summary>Modal windows that the caller waits for without returning to the message loop's caller (a nested loop).</summary>
public static class Modal
{
    /// <summary>
    /// Shows <paramref name="dialog"/> modal to <paramref name="owner"/> and waits for it, running the UI thread's
    /// messages meanwhile, as WPF's ShowDialog does. For code that must answer synchronously (a credential prompt asked
    /// for from a connection thread). UI thread.
    /// </summary>
    public static TResult? Run<TResult>(Window dialog, Window owner)
    {
        var frame = new DispatcherFrame();
        var shown = dialog.ShowDialog<TResult>(owner);
        shown.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.UIThread.PushFrame(frame);
        return shown.IsCompletedSuccessfully ? shown.Result : default;
    }
}
