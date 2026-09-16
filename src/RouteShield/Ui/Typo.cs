using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace RouteShield.Ui;

/// <summary>
/// Letter-spacing for the design system's uppercase labels. WPF has no tracking property, so
/// the spacing is drawn with hair spaces — close to the 0.1em the system asks for, and only
/// ever applied to short decorative labels rather than to body copy.
/// </summary>
public static class Typo
{
    private const char HairSpace = ' ';

    public static readonly DependencyProperty TrackedProperty = DependencyProperty.RegisterAttached(
        "Tracked",
        typeof(string),
        typeof(Typo),
        new PropertyMetadata(null, OnTrackedChanged));

    public static void SetTracked(DependencyObject element, string? value) =>
        element.SetValue(TrackedProperty, value);

    public static string? GetTracked(DependencyObject element) =>
        (string?)element.GetValue(TrackedProperty);

    public static string Space(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length * 2);

        foreach (var character in text)
        {
            if (builder.Length > 0)
            {
                builder.Append(HairSpace);
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void OnTrackedChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is TextBlock block)
        {
            block.Text = Space(args.NewValue as string);
        }
    }
}
