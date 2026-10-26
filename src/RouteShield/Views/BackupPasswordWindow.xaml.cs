using System.Windows;

namespace RouteShield.Views;

public partial class BackupPasswordWindow : Window
{
    private readonly bool _creating;
    private BackupPasswordWindow(bool creating)
    {
        InitializeComponent();
        _creating = creating;
        ConfirmInput.Visibility = ConfirmLabel.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PasswordInput.Focus();
    }
    public static string? Prompt(Window owner, bool creating)
    {
        var dialog = new BackupPasswordWindow(creating) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.PasswordInput.Password : null;
    }
    private void OnDragTitle(object sender, System.Windows.Input.MouseButtonEventArgs args)
    {
        if (args.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }
    private void OnContinue(object sender, RoutedEventArgs args)
    {
        if (_creating && (PasswordInput.Password.Length < 12 || PasswordInput.Password != ConfirmInput.Password))
        { ErrorText.Text = "Use at least 12 characters and repeat the same password."; return; }
        DialogResult = true;
    }
}
