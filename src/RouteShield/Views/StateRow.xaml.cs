using System.Windows;
using System.Windows.Controls;

namespace RouteShield.Views;

/// <summary>One line of the protection-layer list: a label and the state it is currently in.</summary>
public partial class StateRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StateRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(StateRow), new PropertyMetadata(false));

    public static readonly DependencyProperty OnLabelProperty = DependencyProperty.Register(
        nameof(OnLabel), typeof(string), typeof(StateRow), new PropertyMetadata("ON"));

    public static readonly DependencyProperty OffLabelProperty = DependencyProperty.Register(
        nameof(OffLabel), typeof(string), typeof(StateRow), new PropertyMetadata("OFF"));

    /// <summary>Draws the pill in the accent colour when on, for the states that carry risk.</summary>
    public static readonly DependencyProperty EmphasiseProperty = DependencyProperty.Register(
        nameof(Emphasise), typeof(bool), typeof(StateRow), new PropertyMetadata(false));

    public StateRow() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public string OnLabel
    {
        get => (string)GetValue(OnLabelProperty);
        set => SetValue(OnLabelProperty, value);
    }

    public string OffLabel
    {
        get => (string)GetValue(OffLabelProperty);
        set => SetValue(OffLabelProperty, value);
    }

    public bool Emphasise
    {
        get => (bool)GetValue(EmphasiseProperty);
        set => SetValue(EmphasiseProperty, value);
    }
}
