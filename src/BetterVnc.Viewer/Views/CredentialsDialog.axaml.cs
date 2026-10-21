using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterVnc.Protocol;

namespace BetterVnc.Viewer.Views;

/// <summary>Asks for a password, or a user name and password; closes with true when the person chose Connect.</summary>
public partial class CredentialsDialog : Window
{
    public string? Username => UserBox.IsVisible ? UserBox.Text ?? "" : null;
    public string Password => PasswordBox.Text ?? "";
    public bool Remember => RememberBox.IsChecked == true;

    public CredentialsDialog()
    {
        InitializeComponent();
    }

    public CredentialsDialog(CredentialKind kind, string prompt, string? username) : this()
    {
        PromptText.Text = prompt;
        bool needUser = kind == CredentialKind.UsernameAndPassword;
        UserLabel.IsVisible = UserBox.IsVisible = needUser;
        UserBox.Text = username ?? "";
        Opened += (_, _) => { if (needUser && string.IsNullOrEmpty(username)) UserBox.Focus(); else PasswordBox.Focus(); };
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
