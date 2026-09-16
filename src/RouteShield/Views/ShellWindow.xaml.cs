using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using RouteShield.Services;
using RouteShield.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace RouteShield.Views;

public partial class ShellWindow : Window, IUiHost
{
    private const string ActivationEventName = @"Global\RouteShield.Activate";

    private readonly ShellViewModel _model;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _trayStatus;
    private readonly ToolStripMenuItem _trayToggle;
    private EventWaitHandle? _activationSignal;
    private bool _started;
    private bool _quitting;

    public ShellWindow(bool startMinimised)
    {
        InitializeComponent();

        _model = new ShellViewModel(this);
        DataContext = _model;

        _trayStatus = new ToolStripMenuItem("Disconnected") { Enabled = false };
        _trayToggle = new ToolStripMenuItem("Connect", null, (_, _) => ToggleTunnel());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_trayStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripMenuItem("Open RouteShield", null, (_, _) => RestoreFromTray()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "RouteShield",
            Visible = true,
            ContextMenuStrip = menu
        };

        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _model.PropertyChanged += OnModelChanged;

        if (startMinimised)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    /// <summary>Wakes the window already running in this session instead of starting a second core.</summary>
    public static void ActivateExistingInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var signal))
        {
            using (signal)
            {
                signal.Set();
            }
        }
    }

    // ══ IUiHost ══

    public Task AlertAsync(string kicker, string title, string body)
    {
        AlertWindow.ShowAlert(this, kicker, title, body);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string kicker, string title, string body, string confirmLabel) =>
        Task.FromResult(AlertWindow.ShowConfirm(this, kicker, title, body, confirmLabel));

    public IReadOnlyList<RunningProcessItem>? PickRunningProcesses()
    {
        var picker = new ProcessPickerWindow { Owner = this };
        return picker.ShowDialog() == true ? picker.Selected : null;
    }

    // ══ Lifecycle ══

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        // Loaded fires again whenever the window returns from the tray.
        if (_started)
        {
            return;
        }

        _started = true;
        StartActivationListener();

        try
        {
            await _model.InitializeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "RouteShield could not start", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_model.ShowFirstRun)
        {
            var welcome = new FirstRunWindow(_model) { Owner = this };
            welcome.ShowDialog();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!_quitting && _model.CloseToTray)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }

        if (_quitting)
        {
            return;
        }

        args.Cancel = true;
        await ShutdownAsync();
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (WindowState == WindowState.Minimized && _model.CloseToTray)
        {
            HideToTray();
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ShellViewModel.StateWord) or nameof(ShellViewModel.IsConnected)))
        {
            return;
        }

        _trayStatus.Text = _model.IsConnected
            ? $"Connected · {_model.SelectedProfile?.Name ?? "tunnel"}"
            : _model.StateWord[..1] + _model.StateWord[1..].ToLowerInvariant();

        _trayToggle.Text = _model.IsConnected ? "Disconnect" : "Connect";
        _tray.Text = $"RouteShield · {_trayStatus.Text}";
    }

    private void ToggleTunnel()
    {
        var command = _model.IsConnected ? (System.Windows.Input.ICommand)_model.DisconnectCommand : _model.ConnectCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void StartActivationListener()
    {
        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);

        var signal = _activationSignal;
        _ = Task.Run(() =>
        {
            while (signal.WaitOne())
            {
                Dispatcher.BeginInvoke(RestoreFromTray);
            }
        });
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private async void Quit() => await ShutdownAsync();

    private async Task ShutdownAsync()
    {
        _quitting = true;
        _tray.Visible = false;

        try
        {
            await _model.ShutdownAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write(LogCategory.Settings, $"Shutdown was not clean: {exception.Message}");
        }

        _tray.Dispose();
        _activationSignal?.Dispose();
        Application.Current.Shutdown();
    }

    // ══ Window chrome ══

    private void OnMinimise(object sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;

    private void OnToggleMaximise(object sender, RoutedEventArgs args) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs args) => Close();

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("Assets/RouteShield.ico", UriKind.Relative));
        if (resource is null)
        {
            return System.Drawing.SystemIcons.Shield;
        }

        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
    }
}
