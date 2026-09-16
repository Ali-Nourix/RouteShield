using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RouteShield.Services;

namespace RouteShield.Views;

public partial class ProcessPickerWindow : Window
{
    private IReadOnlyList<RunningProcessItem> _catalog = [];

    public ProcessPickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    public IReadOnlyList<RunningProcessItem> Selected { get; private set; } = [];

    private void Reload()
    {
        _catalog = ProcessCatalog.GetRunning();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = SearchBox.Text.Trim();

        ProcessList.ItemsSource = term.Length == 0
            ? _catalog
            : _catalog
                .Where(item => item.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                               || item.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs args) => ApplyFilter();

    private void OnRefresh(object sender, RoutedEventArgs args) => Reload();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var count = ProcessList.SelectedItems.Count;
        SelectionSummary.Text = count switch
        {
            0 => "Nothing selected",
            1 => "1 process selected",
            _ => $"{count} processes selected"
        };
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnAccept(object sender, RoutedEventArgs args)
    {
        Selected = ProcessList.SelectedItems.OfType<RunningProcessItem>().ToArray();
        DialogResult = Selected.Count > 0;
    }

    private void OnCancel(object sender, RoutedEventArgs args) => DialogResult = false;
}
