using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using RouteShield.ViewModels;

namespace RouteShield.Views;

public partial class DiagnosticsPage : UserControl
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is ShellViewModel model)
        {
            model.VisibleLog.CollectionChanged += OnLogChanged;
        }
    }

    /// <summary>Keeps the newest line in view, the way a console does.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Add && IsVisible)
        {
            Dispatcher.BeginInvoke(LogScroller.ScrollToEnd);
        }
    }
}
