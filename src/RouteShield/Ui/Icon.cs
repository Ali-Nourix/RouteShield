using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RouteShield.Ui;

/// <summary>Original, dependency-free 24px outline symbols. Labels remain on every action.</summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata("Activity", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    private static readonly IReadOnlyDictionary<string, Geometry> Symbols = new Dictionary<string, string>
    {
        ["Dashboard"] = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z",
        ["Library"] = "M4,5 H20 V21 H4 Z M7,2 H17 M8,10 H16 M8,15 H14",
        ["Apps"] = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,17 H21 M17.5,13.5 V20.5",
        ["Shield"] = "M12,2 L21,6 V12 C21,17 17,20 12,22 C7,20 3,17 3,12 V6 Z M8,12 L11,15 L16,9",
        ["Activity"] = "M2,12 H6 L9,4 L14,20 L17,12 H22",
        ["Plus"] = "M12,4 V20 M4,12 H20",
        ["Download"] = "M12,3 V16 M7,11 L12,16 L17,11 M4,16 V21 H20 V16",
        ["Upload"] = "M12,17 V4 M7,9 L12,4 L17,9 M4,16 V21 H20 V16",
        ["Copy"] = "M8,8 H21 V21 H8 Z M4,16 H3 V3 H16 V4",
        ["Clipboard"] = "M8,5 H4 V22 H20 V5 H16 M8,2 H16 V7 H8 Z M8,12 H16 M8,17 H14",
        ["Search"] = "M18,10 A8,8 0 1 1 2,10 A8,8 0 1 1 18,10 M16,16 L22,22",
        ["Refresh"] = "M20,8 A9,9 0 1 0 21,16 M20,2 V8 H14",
        ["Connect"] = "M4,3 L21,12 L4,21 Z M4,12 H13",
        ["Globe"] = "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M2,12 H22 M12,2 C5,9 5,15 12,22 C19,15 19,9 12,2",
        ["Filter"] = "M3,4 H21 L14,12 V20 L10,18 V12 Z",
        ["Star"] = "M12,2 L15,8 L22,9 L17,14 L18,21 L12,18 L6,21 L7,14 L2,9 L9,8 Z",
        ["Tools"] = "M14,3 L14,8 L19,8 L22,5 C23,12 18,15 14,13 L6,21 L3,18 L11,10 C9,6 11,3 14,3 Z",
        ["Lock"] = "M5,10 H19 V22 H5 Z M8,10 V6 A4,4 0 0 1 16,6 V10 M12,15 V18",
        ["Folder"] = "M2,5 H10 L12,8 H22 V21 H2 Z",
        ["Trash"] = "M3,6 H21 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17",
        ["Link"] = "M10,7 L13,4 A5,5 0 0 1 20,11 L17,14 M7,10 L4,13 A5,5 0 0 0 11,20 L14,17 M8,16 L16,8",
        ["Check"] = "M4,12 L9,17 L20,6",
        ["ChevronDown"] = "M6,9 L12,15 L18,9",
        ["Close"] = "M5,5 L19,19 M19,5 L5,19",
        ["Clock"] = "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M12,6 V12 L16,15"
    }.ToDictionary(pair => pair.Key, pair => { var geometry = Geometry.Parse(pair.Value); geometry.Freeze(); return geometry; });

    public Icon()
    {
        Width = Height = 18;
        Focusable = false;
        IsHitTestVisible = false;
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (string.IsNullOrEmpty(Kind) || !Symbols.TryGetValue(Kind, out var geometry)) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 24d;
        var pen = new Pen(Foreground, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.PushTransform(new TranslateTransform((ActualWidth - scale * 24) / 2, (ActualHeight - scale * 24) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        context.DrawGeometry(null, pen, geometry);
        context.Pop(); context.Pop();
    }
}
