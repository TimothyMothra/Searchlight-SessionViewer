using Searchlight.Abstractions;
using Searchlight.Services;
using Searchlight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Searchlight.Composition;

/// <summary>
/// Composition root for the platform-neutral core. Registers the read-only session
/// readers, the aggregator, settings, the selected <see cref="ISessionDataSource"/>
/// (live vs mock), and the view-models. Platform services that the core only knows
/// through abstractions — <see cref="IUiDispatcher"/> (always), and in live mode
/// <see cref="IResumeLauncher"/> / <see cref="ISessionWatcher"/> — are supplied by
/// the host front-end.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core services and view-models. When <paramref name="useMock"/>
    /// is <c>true</c>, the synthetic <see cref="MockSessionDataSource"/> is used and
    /// the core also supplies inert <see cref="IResumeLauncher"/> /
    /// <see cref="ISessionWatcher"/> implementations, so the host only needs to add a
    /// <see cref="IUiDispatcher"/>. In live mode the host must register the Windows
    /// resume launcher and file-system watcher.
    /// </summary>
    public static IServiceCollection AddCopilotCore(this IServiceCollection services, bool useMock)
    {
        // Read-only source readers (stateless singletons).
        services.AddSingleton<WorkspaceYamlReader>();
        services.AddSingleton<SessionStateScanner>();
        services.AddSingleton<EventsJsonlReader>();
        services.AddSingleton<SnapshotIndexReader>();
        services.AddSingleton<JournalReader>();
        services.AddSingleton<CheckpointsReader>();
        services.AddSingleton<SessionDbReader>();
        services.AddSingleton<SessionAggregator>();

        // Settings persistence (observable, JSON-backed). TryAdd so a host that
        // pre-builds a SettingsService (e.g. the tray exe needs one before the
        // container exists, for the startup elevation check) can register that same
        // instance and have it shared across the graph.
        services.TryAddSingleton<SettingsService>();
        services.TryAddSingleton<NotesService>();

        if (useMock)
        {
            services.AddSingleton<ISessionDataSource, MockSessionDataSource>();
            // Inert platform services so a mock host needs only a dispatcher.
            services.AddSingleton<IResumeLauncher, MockResumeLauncher>();
            services.AddSingleton<IClipboardService, MockClipboardService>();
            services.AddSingleton<ISessionWatcher, NullSessionWatcher>();
        }
        else
        {
            services.AddSingleton<ISessionDataSource, LiveSessionDataSource>();
            // IResumeLauncher + ISessionWatcher are registered by the Windows host.
        }

        // View-models.
        services.AddSingleton<DetailsViewModel>();
        services.AddSingleton<MainViewModel>();

        return services;
    }
}
