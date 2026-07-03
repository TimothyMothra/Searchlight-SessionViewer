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
        services.TryAddSingleton<SettingsService>();

        if (useMock)
        {
            services.AddMockPlatform();
        }
        else
        {
            services.AddCopilotReaders();
            services.AddSingleton<ISessionDataSource, LiveSessionDataSource>();
            // IResumeLauncher + ISessionWatcher are registered by the Windows host.
        }

        return services.AddViewModels();
    }

    /// <summary>
    /// Registers the core services and view-models backed by the Claude Code
    /// session store (<c>~/.claude/projects</c>) instead of Copilot's
    /// <c>~/.copilot</c>. When <paramref name="useMock"/> is <c>true</c>, the
    /// synthetic <see cref="MockSessionDataSource"/> is used and inert platform
    /// services are supplied, so the host only needs to add a
    /// <see cref="IUiDispatcher"/>. In live mode the host must register an
    /// <see cref="IResumeLauncher"/> and <see cref="IClipboardService"/>; a
    /// <see cref="ClaudeSessionWatcher"/> is registered here since it has no
    /// platform dependencies.
    /// </summary>
    public static IServiceCollection AddClaudeCore(this IServiceCollection services, bool useMock)
    {
        services.TryAddSingleton<SettingsService>();

        if (useMock)
        {
            services.AddMockPlatform();
        }
        else
        {
            services.AddClaudeReaders();
            services.AddSingleton<ISessionDataSource>(sp =>
                sp.GetRequiredService<ClaudeSessionDataSource>());
            services.AddSingleton<ISessionWatcher, ClaudeSessionWatcher>();
            // IResumeLauncher + IClipboardService are registered by the host.
        }

        return services.AddViewModels();
    }

    /// <summary>
    /// Registers the core services and view-models over the combined Claude Code
    /// + Copilot stores: <see cref="CompositeSessionDataSource"/> merges both
    /// session lists and <see cref="CompositeSessionWatcher"/> watches both
    /// roots. When <paramref name="useMock"/> is <c>true</c>, this is identical
    /// to the other cores' mock mode. In live mode the host must register an
    /// <see cref="IResumeLauncher"/> that routes per session (e.g. via
    /// <see cref="ClaudeSessionDataSource.OwnsSession"/>) and an
    /// <see cref="IClipboardService"/>.
    /// </summary>
    public static IServiceCollection AddCombinedCore(this IServiceCollection services, bool useMock)
    {
        services.TryAddSingleton<SettingsService>();

        if (useMock)
        {
            services.AddMockPlatform();
        }
        else
        {
            services.AddCopilotReaders();
            services.AddClaudeReaders();

            services.AddSingleton<LiveSessionDataSource>();
            services.AddSingleton<ISessionDataSource>(sp => new CompositeSessionDataSource(
                sp.GetRequiredService<ClaudeSessionDataSource>(),
                sp.GetRequiredService<LiveSessionDataSource>()));

            services.AddSingleton<ISessionWatcher>(_ => new CompositeSessionWatcher(
                new ClaudeSessionWatcher(),
                new SessionWatcher()));
            // IResumeLauncher + IClipboardService are registered by the host.
        }

        return services.AddViewModels();
    }

    /// <summary>Read-only readers for the Copilot store (stateless singletons).</summary>
    private static IServiceCollection AddCopilotReaders(this IServiceCollection services)
    {
        services.AddSingleton<WorkspaceYamlReader>();
        services.AddSingleton<SessionStateScanner>();
        services.AddSingleton<EventsJsonlReader>();
        services.AddSingleton<SnapshotIndexReader>();
        services.AddSingleton<JournalReader>();
        services.AddSingleton<CheckpointsReader>();
        services.AddSingleton<SessionDbReader>();
        services.AddSingleton<SessionAggregator>();
        return services;
    }

    /// <summary>
    /// Read-only readers + data source for the Claude Code store. The data
    /// source is registered concretely as well: resume launchers need
    /// <see cref="ClaudeSessionDataSource.TryGetProjectCwd"/> /
    /// <see cref="ClaudeSessionDataSource.OwnsSession"/>.
    /// </summary>
    private static IServiceCollection AddClaudeReaders(this IServiceCollection services)
    {
        services.AddSingleton<ClaudeSessionIndexReader>();
        services.AddSingleton<ClaudeJsonlHeadReader>();
        services.AddSingleton<ClaudeSessionDataSource>();
        return services;
    }

    /// <summary>
    /// Mock mode: the synthetic data source plus inert platform services, so a
    /// mock host needs only a dispatcher.
    /// </summary>
    private static IServiceCollection AddMockPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISessionDataSource, MockSessionDataSource>();
        services.AddSingleton<IResumeLauncher, MockResumeLauncher>();
        services.AddSingleton<IClipboardService, MockClipboardService>();
        services.AddSingleton<ISessionWatcher, NullSessionWatcher>();
        return services;
    }

    private static IServiceCollection AddViewModels(this IServiceCollection services)
    {
        services.AddSingleton<DetailsViewModel>();
        services.AddSingleton<MainViewModel>();
        return services;
    }
}
