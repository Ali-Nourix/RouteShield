using System.Windows;
using System.Windows.Input;
using RouteShield.Services;
using RouteShield.Ui;
using RouteShield.ViewModels;

namespace RouteShield.Views;

/// <summary>The welcome screen: what RouteShield does, and the three things it needs first.</summary>
public partial class FirstRunWindow : Window
{
    private readonly ShellViewModel _model;

    public FirstRunWindow(ShellViewModel model)
    {
        InitializeComponent();

        _model = model;
        DataContext = model;

        VersionLine = $"VERSION {model.AppVersion} · {model.CoreVersionText.ToUpperInvariant()} · WINDOWS 10 / 11";
        ElevationState.Text = Typo.Space(Elevation.IsAdministrator ? "GRANTED" : "NEEDED");
    }

    public string VersionLine { get; }

    private void OnDragWindow(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnOpenProfiles(object sender, RoutedEventArgs args)
    {
        _model.Page = ShellPage.Profiles;
        Finish();
    }

    private void OnFinish(object sender, RoutedEventArgs args) => Finish();

    private void Finish()
    {
        _model.FinishFirstRunCommand.Execute(null);
        Close();
    }
}
