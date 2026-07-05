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

    // The UI-thread dispatcher, captured in OnLaunched. Used to defer tray-menu
    // teardown out of the flyout-item click message (disposing the TaskbarIcon
    // synchronously inside its own Click handler can hang the flyout close).
    private DispatcherQueue? _uiDispatcher;

    // Set only when the user chooses Exit from the tray; distinguishes a real
    // shutdown from a "hide to tray" close so OnWindowClosing can veto the latter.
    private bool _isExiting;

    // When true (launched with --no-tray), the app runs as a plain window with no
    // system-tray icon and closing the window exits the process.
    private bool _noTray;

    // Single-instance guard (normal tray mode only). The first instance owns this
    // mutex and listens on a named event; a later normal launch (e.g. the user
    // clicks the Start Menu / desktop shortcut while the run-at-login instance is
    // already sitting in the tray) signals the event and exits, so the running
    // instance surfaces its window instead of a duplicate tray icon appearing.
    // --no-tray and --demo runs are intentionally exempt so they can coexist with
    // the normal tray instance (e.g. running Demo for screenshots).
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.EventWaitHandle? _showWindowSignal;

    private const string SingleInstanceMutexName = "Searchlight.SingleInstance.Mutex";
    private const string ShowWindowEventName = "Searchlight.SingleInstance.ShowWindow";

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

    // Verbose breadcrumbs (e.g. per-move resize tracing) are noisy and only useful
    // when actively diagnosing. They stay in the code but are gated behind the
    // SEARCHLIGHT_VERBOSE=1 environment variable so normal runs keep a clean log.
    internal static bool VerboseLogging { get; } =
        string.Equals(
            System.Environment.GetEnvironmentVariable("SEARCHLIGHT_VERBOSE"),
            "1",
            System.StringComparison.Ordinal);

    /// <summary>Logs only when <see cref="VerboseLogging"/> is enabled.</summary>
    internal static void LogVerbose(string message)
    {
        if (VerboseLogging)
        {
            Log(message);
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
        _uiDispatcher = dispatcher;

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

        // Single-instance: only the normal tray app participates. If another normal
        // instance is already running, tell it to show its window and exit this one
        // (prevents a duplicate tray icon when run-at-login + a shortcut both fire).
        if (!_noTray && !useMock && !TryAcquireSingleInstance(dispatcher))
        {
            Log("OnLaunched: another instance is running -> signalled it and exiting");
            Exit();
            return;
        }

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

        // ScriptTray behavior for minimize too: when the tray exists, minimizing (-)
        // hides the window to the tray instead of leaving a taskbar button, matching
        // the close-to-tray behavior. AppWindow.Changed fires only AFTER the window has
        // already minimized to a taskbar button, so it can't suppress the taskbar
        // minimize. Instead we subclass the window and intercept WM_SYSCOMMAND /
        // SC_MINIMIZE before the OS minimizes, and hide to the tray. Skipped in
        // --no-tray mode (there is nowhere to hide to).
        if (!_noTray)
        {
            MinimizeToTrayHelper.Enable(_window, () =>
            {
                if (!_isExiting)
                {
                    _window?.AppWindow.Hide();
                }
            });
        }

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
        // Use Command (not the Click event) on the tray context-menu items. In an
        // unpackaged WinUI 3 host the MenuFlyoutItem.Click event on the H.NotifyIcon
        // TaskbarIcon's flyout does NOT fire reliably (the click body never runs),
        // which is why the tray Exit previously did nothing. The RelayCommand path is
        // the same mechanism as LeftClickCommand below, which works reliably.
        var openItem = new MenuFlyoutItem
        {
            Text = "Open",
            Command = new RelayCommand(ShowWindow),
        };

        var refreshItem = new MenuFlyoutItem
        {
            Text = "Refresh",
            Command = new RelayCommand(() =>
            {
                if (_viewModel?.RefreshCommand.CanExecute(null) == true)
                {
                    _viewModel.RefreshCommand.Execute(null);
                }
            }),
        };

        var exitItem = new MenuFlyoutItem
        {
            Text = "Exit",
            Command = new RelayCommand(ExitApplication),
        };

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

    /// <summary>
    /// Acquires the single-instance guard for normal tray mode. Returns <c>true</c>
    /// when this is the first instance (and starts a background listener that
    /// surfaces the window when a later launch signals it). Returns <c>false</c>
    /// when another instance already owns the guard — in that case it signals the
    /// running instance to show its window, and the caller should exit.
    /// </summary>
    private bool TryAcquireSingleInstance(DispatcherQueue dispatcher)
    {
        _instanceMutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another normal instance is already running: signal it to show its window.
            try
            {
                if (System.Threading.EventWaitHandle.TryOpenExisting(ShowWindowEventName, out System.Threading.EventWaitHandle? existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log($"TryAcquireSingleInstance: failed to signal running instance: {ex.Message}");
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }

        // First instance: listen for later launches asking us to surface the window.
        var signal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowSignal = signal;

        var listener = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    signal.WaitOne();
                }
                catch
                {
                    return; // handle disposed on exit
                }

                dispatcher.TryEnqueue(ShowWindow);
            }
        })
        {
            IsBackground = true,
            Name = "Searchlight.ShowWindowListener",
        };
        listener.Start();

        return true;
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
        Log("ExitApplication: entered");
        _isExiting = true;

        // Best-effort graceful teardown. Any single step failing must NOT prevent
        // the process from terminating, so each is guarded and we fall through to
        // the hard Environment.Exit below regardless.
        try { _trayIcon?.Dispose(); } catch (Exception ex) { Log($"ExitApplication: tray dispose failed: {ex.Message}"); }

        // Release the single-instance guard so a future launch can start cleanly.
        try { _showWindowSignal?.Dispose(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }

        // Disposing the provider disposes the singletons it owns — MainViewModel and
        // the ISessionWatcher — so we must NOT also dispose them manually (double
        // dispose). The watcher/view-model fields are just cached resolved instances.
        try { _services?.Dispose(); } catch (Exception ex) { Log($"ExitApplication: services dispose failed: {ex.Message}"); }

        try { _window?.Close(); } catch { }

        try { Exit(); } catch { }

        // Guarantee the process actually terminates. For an unpackaged WinUI 3 app,
        // Application.Exit() alone is unreliable when lingering COM/tray references
        // keep the message loop alive; a hard exit is safe here because this app is
        // read-only and holds no unsaved state.
        Log("ExitApplication: forcing process exit");
        Environment.Exit(0);
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
