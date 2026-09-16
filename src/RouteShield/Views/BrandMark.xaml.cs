using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RouteShield.Views;

/// <summary>The RouteShield mark: a shield carrying the route that rises out of it.</summary>
public partial class BrandMark : UserControl
{
    public static readonly DependencyProperty ShieldBrushProperty = DependencyProperty.Register(
        nameof(ShieldBrush), typeof(Brush), typeof(BrandMark), new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty RouteBrushProperty = DependencyProperty.Register(
        nameof(RouteBrush), typeof(Brush), typeof(BrandMark), new PropertyMetadata(Brushes.OrangeRed));

    public BrandMark() => InitializeComponent();

    public Brush ShieldBrush
    {
        get => (Brush)GetValue(ShieldBrushProperty);
        set => SetValue(ShieldBrushProperty, value);
    }

    public Brush RouteBrush
    {
        get => (Brush)GetValue(RouteBrushProperty);
        set => SetValue(RouteBrushProperty, value);
    }
}
