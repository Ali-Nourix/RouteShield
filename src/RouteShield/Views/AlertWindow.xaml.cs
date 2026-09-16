using System.Windows;
using System.Windows.Input;
using RouteShield.Ui;

namespace RouteShield.Views;

/// <summary>The design system's dialog: a kicker, a statement of what happened, and the detail.</summary>
public partial class AlertWindow : Window
{
    private AlertWindow() => InitializeComponent();

    public static void ShowAlert(Window owner, string kicker, string title, string body)
    {
        var dialog = Build(owner, kicker, title, body);
        dialog.CancelButton.Content = "Close";
        dialog.ShowDialog();
    }

    public static bool ShowConfirm(Window owner, string kicker, string title, string body, string confirmLabel)
    {
        var dialog = Build(owner, kicker, title, body);
        dialog.CancelButton.Content = "Not now";
        dialog.ConfirmButton.Content = confirmLabel;
        dialog.ConfirmButton.Visibility = Visibility.Visible;

        return dialog.ShowDialog() == true;
    }

    private static AlertWindow Build(Window owner, string kicker, string title, string body)
    {
        var dialog = new AlertWindow { Owner = owner };
        dialog.KickerText.Text = Typo.Space(kicker.ToUpperInvariant());
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;
        return dialog;
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs args) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs args) => DialogResult = false;
}
