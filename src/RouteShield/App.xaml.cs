using System.Windows;
using System.Windows.Threading;
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

        var startMinimised = args.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        var shell = new ShellWindow(startMinimised);
        MainWindow = shell;
        shell.Show();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        _instanceLock?.Dispose();
        base.OnExit(args);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppLog.Write(LogCategory.Settings, $"Unhandled error: {args.Exception}");

        MessageBox.Show(
            args.Exception.Message,
            "RouteShield stopped unexpectedly",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        args.Handled = true;
    }
}
