using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Searchlight.Abstractions;
using Searchlight.Avalonia.Services;
using Searchlight.Composition;
using Searchlight.Services;
using Searchlight.ViewModels;

namespace Searchlight.Avalonia;

/// <summary>
/// Avalonia application and composition root. Builds the DI container over the
/// platform-neutral Core plus this host's platform services. The session store
/// is chosen by <c>--source=claude|copilot|both</c>; without the flag the host
/// auto-detects which stores exist on disk and shows the combined list when
/// both do. <c>--demo</c> runs against the synthetic mock instead.
/// Like the WinUI host, the app lives in the tray: minimize and close both
/// hide the window, the tray menu shows/exits, and a second launch surfaces
/// the running instance instead of duplicating it. <c>--no-tray</c> opts out
/// of all of that (plain window, no single-instance guard).
/// </summary>
public sealed class App : Application
{
    /// <summary>Which session store(s) back the app.</summary>
    private enum SessionSource
    {
        Claude,
        Copilot,
        Both,
    }

    private ServiceProvider? _services;
    private SingleInstanceGuard? _instanceGuard;
    private bool _isExiting;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? [];
            bool demo = args.Contains("--demo");
            bool noTray = args.Contains("--no-tray");

            // Single-instance: only the normal tray app participates (mirrors the
            // WinUI host) — --no-tray and --demo runs can coexist with it. If a
            // normal instance is already running, surface its window and exit this
            // one instead of adding a second tray icon.
            if (!noTray && !demo)
            {
                _instanceGuard = SingleInstanceGuard.Acquire();
                if (!_instanceGuard.IsPrimary)
                {
                    SingleInstanceGuard.SignalPrimary();
                    _instanceGuard.Dispose();
                    Environment.Exit(0);
                }
            }

            var clipboard = new AvaloniaClipboardService();

            var services = new ServiceCollection();
            services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();

            if (demo)
            {
                // Any core's mock mode is identical; Claude is the arbitrary pick.
                services.AddClaudeCore(useMock: true);
            }
            else
            {
                services.AddSingleton<IClipboardService>(clipboard);
                AddLiveSource(services, ResolveSource(args));
            }

            _services = services.BuildServiceProvider();

            var window = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
            };
            clipboard.AttachTo(window);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                _isExiting = true;
                _instanceGuard?.Dispose();
                _services.Dispose();
            };

            if (!noTray)
            {
                SetUpTray(desktop, window);
            }

            _instanceGuard?.ListenForShowRequests(() =>
                Dispatcher.UIThread.Post(() => ShowWindow(window)));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the tray icon (Show / Exit menu, click-to-show) and rewires the
    /// window so minimize and close both hide to the tray instead — matching the
    /// WinUI host's ScriptTray-style behavior.
    /// </summary>
    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var showItem = new NativeMenuItem("Show Searchlight");
        showItem.Click += (_, _) => ShowWindow(window);

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _isExiting = true;
            desktop.Shutdown();
        };

        var trayIcon = new TrayIcon
        {
            ToolTipText = "Searchlight",
            Icon = new WindowIcon(new Bitmap(AssetLoader.Open(
                new Uri("avares://Searchlight.Avalonia/Assets/app_32.png")))),
            Menu = new NativeMenu
            {
                Items = { showItem, new NativeMenuItemSeparator(), exitItem },
            },
        };
        trayIcon.Clicked += (_, _) => ShowWindow(window);
        TrayIcon.SetIcons(this, [trayIcon]);

        // Hide-to-tray only where a tray host is guaranteed (Windows taskbar,
        // macOS menu bar). On Linux the icon silently no-ops on desktops with
        // no StatusNotifier host (e.g. stock GNOME) — hiding the window there
        // would leave the app running with no way to reach it, so close and
        // minimize keep their normal meaning.
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        // Close hides to the tray instead of exiting (unless we're exiting for
        // real — tray Exit, Cmd+Q, or OS shutdown).
        window.Closing += (_, e) =>
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        // Minimize also hides to the tray. The state is reset to Normal after
        // hiding so the next Show restores a visible window, not a minimized one.
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty
                && e.GetNewValue<WindowState>() == WindowState.Minimized)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    window.Hide();
                    window.WindowState = WindowState.Normal;
                });
            }
        };
    }

    private static void ShowWindow(MainWindow window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Registers the live core + resume launcher for the chosen source. The
    /// Claude and Copilot launchers are terminal-based and cross-platform; the
    /// combined view routes each resume to the CLI that owns the session.
    /// </summary>
    private static void AddLiveSource(ServiceCollection services, SessionSource source)
    {
        switch (source)
        {
            case SessionSource.Claude:
                services.AddClaudeCore(useMock: false);
                services.AddSingleton<IResumeLauncher, ClaudeTerminalResumeLauncher>();
                break;

            case SessionSource.Copilot:
                services.AddCopilotCore(useMock: false);
                // The Copilot store watcher lives in Core and is cross-platform,
                // but AddCopilotCore leaves it to the host (the WinUI host
                // registers its own); register it here.
                services.AddSingleton<ISessionWatcher, SessionWatcher>();
                services.AddSingleton<IResumeLauncher, CopilotTerminalResumeLauncher>();
                break;

            default:
                services.AddCombinedCore(useMock: false);
                services.AddSingleton<ClaudeTerminalResumeLauncher>();
                services.AddSingleton<CopilotTerminalResumeLauncher>();
                services.AddSingleton<IResumeLauncher, RoutingResumeLauncher>();
                break;
        }
    }

    /// <summary>
    /// Resolves which store(s) to show. An explicit <c>--source=</c> flag wins;
    /// otherwise auto-detect by which store roots exist on disk (both → the
    /// combined list). With neither store present, default to Claude — the UI
    /// just shows an empty list.
    /// </summary>
    private static SessionSource ResolveSource(string[] args)
    {
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--source=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = arg["--source=".Length..];
            if (string.Equals(value, "claude", StringComparison.OrdinalIgnoreCase))
            {
                return SessionSource.Claude;
            }

            if (string.Equals(value, "copilot", StringComparison.OrdinalIgnoreCase))
            {
                return SessionSource.Copilot;
            }

            if (string.Equals(value, "both", StringComparison.OrdinalIgnoreCase))
            {
                return SessionSource.Both;
            }
        }

        bool hasClaude = Directory.Exists(ClaudePaths.Projects);
        bool hasCopilot = Directory.Exists(CopilotPaths.SessionState);

        if (hasClaude && hasCopilot)
        {
            return SessionSource.Both;
        }

        return hasCopilot ? SessionSource.Copilot : SessionSource.Claude;
    }
}
