using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RouteShield.ViewModels;

namespace RouteShield.Views;

/// <summary>Adds or edits one subscription; the library itself stays uncluttered by the form.</summary>
public partial class SubscriptionWindow : Window
{
    private SubscriptionWindow() => InitializeComponent();

    public static SubscriptionEdit? Prompt(Window owner, VpnSubscription? existing)
    {
        var dialog = new SubscriptionWindow { Owner = owner };

        if (existing is not null)
        {
            dialog.TitleText.Text = "Edit subscription";
            dialog.NameBox.Text = existing.Name;
            dialog.UrlBox.Text = existing.Url;
            dialog.RemoveButton.Visibility = Visibility.Visible;
        }

        dialog.Loaded += (_, _) => (existing is null ? dialog.NameBox : dialog.UrlBox).Focus();
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private SubscriptionEdit? Result { get; set; }

    private static bool IsUsable(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private void OnUrlChanged(object sender, TextChangedEventArgs args)
    {
        var usable = UrlBox.Text.Trim().Length == 0 || IsUsable(UrlBox.Text);
        UrlHint.Visibility = usable ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = IsUsable(UrlBox.Text);
    }

    private void OnSave(object sender, RoutedEventArgs args)
    {
        if (!IsUsable(UrlBox.Text))
        {
            UrlHint.Visibility = Visibility.Visible;
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Subscription" : NameBox.Text.Trim();
        Result = new SubscriptionEdit(name, UrlBox.Text.Trim(), Remove: false);
        DialogResult = true;
    }

    private void OnRemove(object sender, RoutedEventArgs args)
    {
        Result = new SubscriptionEdit(NameBox.Text, UrlBox.Text, Remove: true);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs args) => DialogResult = false;

    private void OnDragWindow(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState == MouseButtonState.Pressed && args.OriginalSource is not TextBox)
        {
            DragMove();
        }
    }
}
