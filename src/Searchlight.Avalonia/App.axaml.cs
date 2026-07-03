using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? [];
            bool demo = args.Contains("--demo");

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
            desktop.ShutdownRequested += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
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
