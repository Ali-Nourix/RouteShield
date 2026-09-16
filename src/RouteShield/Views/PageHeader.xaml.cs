using System.Windows;
using System.Windows.Controls;

namespace RouteShield.Views;

/// <summary>The kicker, title and action row that opens every page.</summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty KickerProperty = DependencyProperty.Register(
        nameof(Kicker), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

    public PageHeader() => InitializeComponent();

    public string Kicker
    {
        get => (string)GetValue(KickerProperty);
        set => SetValue(KickerProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}
