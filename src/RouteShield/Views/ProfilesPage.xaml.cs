using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RouteShield.ViewModels;

namespace RouteShield.Views;

public partial class ProfilesPage : UserControl
{
    public ProfilesPage() => InitializeComponent();

    private void OnSubscriptionClicked(object sender, MouseButtonEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: VpnSubscription subscription } &&
            DataContext is ShellViewModel model)
        {
            model.SelectedSubscription = subscription;
        }
    }
}
