using Searchlight.Abstractions;
using Searchlight.Composition;
using Searchlight.Diagnostics;
using Searchlight.Interop;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Searchlight;

/// <summary>
/// Application bootstrap. Constructs the read-only service layer, wires the
/// root <see cref="MainViewModel"/>, and owns the single main window plus the
/// system-tray icon (ScriptTray-style: the window hides to the tray on close and
/// only truly exits from the tray's Exit command).
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private ISessionWatcher? _watcher;
    private ServiceProvider? _services;
    private TaskbarIcon? _trayIcon;
    private SettingsService? _settingsService;

    // Set only when the user chooses Exit from the tray; distinguishes a real
    // shutdown from a "hide to tray" close so OnWindowClosing can veto the latter.
    private bool _isExiting;

    // When true (launched with --no-tray), the app runs as a plain window with no
    // system-tray icon and closing the window exits the process.
    private bool _noTray;

    // ASSUMPTION: temporary diagnostic sink to capture the UI-thread stowed
    // exception (WER shows only 0xc000027b in Microsoft.UI.Xaml.dll). Remove
    // this logging after the black-window/crash is root-caused.
    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Searchlight.log");

    internal static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                LogPath,
                $"{System.DateTimeOffset.Now:o} {message}{System.Environment.NewLine}");
        }
        catch
        {
            // Never let diagnostic logging throw.
        }
    }

    public App()
    {
        InitializeComponent();

        Log("=== App ctor ===");

        // Route the platform-neutral core's diagnostic breadcrumbs into the same
        // log file the host uses (Core cannot reference the exe's App.Log directly).
        CoreLog.Sink = Log;

        UnhandledException += (_, e) =>
        {
            Log($"UI UnhandledException: {e.Exception}");
            e.Handled = true;
        };

        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"AppDomain UnhandledException: {e.ExceptionObject}");

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>The root view-model, exposed so the window can bind to it.</summary>
    public MainViewModel? ViewModel => _viewModel;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // We are on the UI thread here: capture its dispatcher so the file
        // watcher can marshal incremental list updates back onto it.
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

        Log("OnLaunched: loading settings");
        var settingsService = new SettingsService();

        // On-demand elevation: if the user opted in via the Settings toggle and we
        // are not already elevated, relaunch as Administrator so `wt -w` resume
        // calls can reuse an elevated Terminal window. A cancelled UAC prompt just
        // continues non-elevated (shared-window reuse degrades to a new window).
        if (settingsService.Current.RunElevated && !ElevationHelper.IsElevated())
        {
            Log("OnLaunched: RunElevated set + not elevated -> relaunching elevated");
            if (ElevationHelper.RelaunchElevated())
            {
                Log("OnLaunched: elevated instance started; exiting non-elevated instance");
                Exit();
                return;
            }

            Log("OnLaunched: elevation cancelled/failed; continuing non-elevated");
        }

        _settingsService = settingsService;
        // React when the user flips a setting at runtime (e.g. turns on elevation).
        settingsService.Current.PropertyChanged += OnSettingsChanged;

        // Launch modes (unpackaged: read the raw process command line).
        //   --no-tray : run as a plain window, no tray icon; close = exit.
        //   --demo    : boot against the synthetic MockSessionDataSource (also the
        //               default when compiled in the Demo config via USE_MOCK).
        _noTray = HasFlag("--no-tray");
        bool useMock = ResolveUseMock();
        Log($"OnLaunched: noTray={_noTray} useMock={useMock}");

        Log("OnLaunched: building service provider");
        _services = BuildServices(settingsService, dispatcher, useMock);
        _viewModel = _services.GetRequiredService<MainViewModel>();
        _watcher = _services.GetRequiredService<ISessionWatcher>();

        Log("OnLaunched: creating window");
        _window = new MainWindow(_viewModel);

        // Titlebar / Alt-Tab icon for the window itself (same .ico as the exe/tray).
        _window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        // Default full-open size = 3x the left session-list pane width. The left
        // column is 380 DIP (MainView.xaml ColumnDefinition), so the window opens
        // at 1140 wide. ResizeLogical scales for the current DPI.
        // ASSUMPTION: keep LeftPaneWidthDip in sync with MainView.xaml's 380px column.
        const int LeftPaneWidthDip = 380;
        ForegroundWindowHelper.ResizeLogical(_window, LeftPaneWidthDip * 3, 860);

        // ScriptTray behavior: closing the window hides it to the tray instead
        // of exiting the process. Only the tray's Exit command really quits.
        // In --no-tray mode there is no tray, so a close exits the process.
        _window.AppWindow.Closing += OnWindowClosing;

        if (!_noTray)
        {
            InitializeTrayIcon();
        }

        _window.Activate();

        // Force the window on top of other windows on launch. Activate() alone
        // often leaves the window behind the launcher due to the OS foreground-lock.
        ForegroundWindowHelper.BringToFront(_window);
    }

    /// <summary>
    /// Creates the system-tray icon with a left-click "show window" action and a
    /// right-click context menu (Open / Refresh / Exit).
    /// </summary>
    private void InitializeTrayIcon()
    {
        var openItem = new MenuFlyoutItem { Text = "Open" };
        openItem.Click += (_, _) => ShowWindow();

        var refreshItem = new MenuFlyoutItem { Text = "Refresh" };
        refreshItem.Click += (_, _) =>
        {
            if (_viewModel?.RefreshCommand.CanExecute(null) == true)
            {
                _viewModel.RefreshCommand.Execute(null);
            }
        };

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new MenuFlyout();
        menu.Items.Add(openItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        // Tray glyph = the app's real .ico (Assets\app.ico, copied to output).
        // TaskbarIcon.IconSource is an ImageSource, so a BitmapImage assigns
        // directly. DecodePixelWidth=32 renders a crisp small tray frame from the
        // multi-res icon. Use an absolute file path (ms-appx:// does not resolve
        // for unpackaged WindowsPackageType=None apps).
        string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        var iconSource = new BitmapImage
        {
            UriSource = new Uri(icoPath),
            DecodePixelWidth = 32,
            DecodePixelHeight = 32,
        };

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Searchlight \u2014 Historical Session Viewer",
            IconSource = iconSource,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(ShowWindow),
            NoLeftClickDelay = true,
        };

        _trayIcon.ForceCreate();
    }

    /// <summary>Shows and focuses the main window (from the tray).</summary>
    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.AppWindow.Show();
        _window.Activate();
        ForegroundWindowHelper.BringToFront(_window);
    }

    /// <summary>
    /// Vetoes a normal window close and hides the window to the tray instead.
    /// When <see cref="_isExiting"/> is set (tray Exit), the close proceeds.
    /// </summary>
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting)
        {
            return;
        }

        // In --no-tray mode there is no tray to hide to, so a window close is a
        // real shutdown request: tear down and exit the process.
        if (_noTray)
        {
            args.Cancel = true;
            ExitApplication();
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>
    /// Handles runtime settings changes. Reacts to the elevation toggle in both
    /// directions: turning "Run as administrator" ON while non-elevated relaunches
    /// elevated (one UAC prompt); turning it OFF while elevated relaunches
    /// non-elevated (via explorer.exe, since a process cannot de-elevate in place).
    /// Either way the current instance tears down so the new one reflects the
    /// chosen integrity level. If elevation is cancelled at the UAC prompt, revert
    /// the toggle so it reflects reality.
    /// </summary>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.RunElevated))
        {
            return;
        }

        if (_settingsService?.Current.RunElevated == true && !ElevationHelper.IsElevated())
        {
            Log("Settings: RunElevated turned on -> relaunching elevated");
            if (ElevationHelper.RelaunchElevated())
            {
                Log("Settings: elevated instance started; exiting this instance");
                ExitApplication();
            }
            else
            {
                // UAC cancelled: undo the toggle so the switch does not show "on"
                // while the process is still non-elevated. This re-fires the event
                // with RunElevated=false, which the guard above ignores.
                Log("Settings: elevation cancelled; reverting toggle");
                _settingsService.Current.RunElevated = false;
            }
        }
        else if (_settingsService?.Current.RunElevated == false && ElevationHelper.IsElevated())
        {
            Log("Settings: RunElevated turned off while elevated -> relaunching non-elevated");
            if (ElevationHelper.RelaunchNonElevated())
            {
                Log("Settings: non-elevated instance started; exiting this instance");
                ExitApplication();
            }
            else
            {
                Log("Settings: non-elevated relaunch failed; staying elevated");
            }
        }
    }

    /// <summary>Tears down the tray icon and the DI container, then exits.</summary>
    private void ExitApplication()
    {
        _isExiting = true;

        _trayIcon?.Dispose();

        // Disposing the provider disposes the singletons it owns — MainViewModel and
        // the ISessionWatcher — so we must NOT also dispose them manually (double
        // dispose). The watcher/view-model fields are just cached resolved instances.
        _services?.Dispose();

        _window?.Close();

        Exit();
    }

    /// <summary>
    /// Builds the DI composition root. The host registers the Windows-only seams
    /// (UI dispatcher; in live mode the real resume launcher + file watcher) and the
    /// pre-built <see cref="SettingsService"/> instance, then <c>AddCopilotCore</c>
    /// contributes the portable readers, aggregator, data source, and view-models.
    /// In mock mode the core registers the synthetic data source, a no-op watcher,
    /// and a no-op resume launcher, so the host registers only the UI dispatcher.
    /// </summary>
    private static ServiceProvider BuildServices(SettingsService settingsService, DispatcherQueue dispatcher, bool useMock)
    {
        var services = new ServiceCollection();

        // Share the exact SettingsService instance the host already built (it was
        // needed for the startup elevation pre-check). Core uses TryAdd, so this wins.
        services.AddSingleton(settingsService);

        // The UI dispatcher is always a Windows/WinUI concern — Core never registers it.
        services.AddSingleton<IUiDispatcher>(new DispatcherQueueUiDispatcher(dispatcher));

        if (!useMock)
        {
            // Live mode: real resume launcher (copilot --resume=<id>) + FileSystemWatcher.
            services.AddSingleton<IResumeLauncher, ResumeLauncher>();
            services.AddSingleton<IClipboardService, ClipboardService>();
            services.AddSingleton<ISessionWatcher, SessionWatcher>();
        }

        // Portable readers + aggregator + data source + view-models. In mock mode this
        // also registers the mock data source, NullSessionWatcher, MockResumeLauncher.
        services.AddCopilotCore(useMock);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Resolves whether to boot against the mock datastore. The Demo build config
    /// defines USE_MOCK (always mock); otherwise a runtime <c>--demo</c> flag opts in.
    /// </summary>
    private static bool ResolveUseMock()
    {
#if USE_MOCK
        return true;
#else
        return HasFlag("--demo");
#endif
    }

    /// <summary>
    /// Case-insensitive check for a command-line flag. Unpackaged WinUI does not
    /// surface args via LaunchActivatedEventArgs, so read the raw process command line.
    /// </summary>
    private static bool HasFlag(string flag)
    {
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
