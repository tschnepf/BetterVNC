using System.Windows;
using BetterVnc.Protocol;

namespace BetterVnc.App.Views;

public partial class CredentialsDialog : Window
{
    public string? Username => UserBox.Visibility == Visibility.Visible ? UserBox.Text : null;
    public string Password => PasswordBox.Password;
    public bool Remember => RememberBox.IsChecked == true;

    public CredentialsDialog(CredentialKind kind, string prompt, string? username)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        bool needUser = kind == CredentialKind.UsernameAndPassword;
        UserLabel.Visibility = UserBox.Visibility = needUser ? Visibility.Visible : Visibility.Collapsed;
        UserBox.Text = username ?? "";
        Loaded += (_, _) => { if (needUser && string.IsNullOrEmpty(username)) UserBox.Focus(); else PasswordBox.Focus(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
