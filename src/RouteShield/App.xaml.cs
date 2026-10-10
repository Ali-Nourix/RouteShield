using System.Windows;
using System.Windows.Threading;
using RouteShield.Ui;
using RouteShield.Views;

namespace RouteShield;

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\RouteShield.SingleInstance";

    private Mutex? _instanceLock;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);

        _instanceLock = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // A second launch — usually from the logon task — should surface the running window
            // rather than start a second core.
            ShellWindow.ActivateExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // The palette is chosen before the first window exists, so a dark desktop never sees
        // a light flash while the full settings file is still being read.
        ThemeManager.Apply(SettingsStore.PeekTheme());

        var startMinimised = args.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        var shell = new ShellWindow(startMinimised);
        MainWindow = shell;
        shell.Show();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        AppLog.FlushFiles();
        _instanceLock?.Dispose();
        base.OnExit(args);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppLog.Write(LogCategory.Settings, $"Unhandled error: {args.Exception}");
        AppLog.FlushFiles();

        MessageBox.Show(
            args.Exception.Message,
            "RouteShield stopped unexpectedly",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        args.Handled = true;
    }
}
