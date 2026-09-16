using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace RouteShield.Ui;

/// <summary>
/// Swaps the palette dictionary under the running application. Every brush in the interface
/// is looked up dynamically, so replacing Palette.Light.xaml with Palette.Dark.xaml re-colours
/// every open window in place. In System mode the choice follows the Windows app colour and
/// changes with it.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightPalette = "Theme/Palette.Light.xaml";
    private const string DarkPalette = "Theme/Palette.Dark.xaml";

    private static AppTheme _requested = AppTheme.System;
    private static bool? _applied;
    private static bool _listening;

    /// <summary>Raised on the UI thread after the palette changed.</summary>
    public static event Action? Changed;

    public static bool IsDark => _applied ?? false;

    public static void Apply(AppTheme theme)
    {
        _requested = theme;

        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark()
        };

        Swap(dark);

        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
    }

    /// <summary>Whether Windows is set to dark for applications; false when the value cannot be read.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (_requested != AppTheme.System || args.Category != UserPreferenceCategory.General)
        {
            return;
        }

        // SystemEvents calls back on its own thread; the resource dictionaries belong to the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() => Swap(SystemPrefersDark()));
    }

    private static void Swap(bool dark)
    {
        var application = Application.Current;
        if (application is null || _applied == dark)
        {
            return;
        }

        var merged = application.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri(dark ? DarkPalette : LightPalette, UriKind.Relative) };

        var index = -1;
        for (var candidate = 0; candidate < merged.Count; candidate++)
        {
            var source = merged[candidate].Source?.OriginalString ?? string.Empty;
            if (source.EndsWith(LightPalette, StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(DarkPalette, StringComparison.OrdinalIgnoreCase))
            {
                index = candidate;
                break;
            }
        }

        if (index >= 0)
        {
            merged[index] = palette;
        }
        else
        {
            merged.Insert(0, palette);
        }

        _applied = dark;
        Changed?.Invoke();
    }
}
