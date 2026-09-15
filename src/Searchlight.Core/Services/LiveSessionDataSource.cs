using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Live <see cref="ISessionDataSource"/> backed by the user's real <c>~/.copilot</c>
/// tree. Thin adapter that composes the aggregator (list + events enrichment) with
/// the native per-session detail readers (checkpoints and session.db todos).
/// All access is read-only.
/// </summary>
public sealed class LiveSessionDataSource : ISessionDataSource
{
    private readonly SessionAggregator _aggregator;
    private readonly CheckpointsReader _checkpoints;
    private readonly SessionDbReader _sessionDb;

    /// <summary>Creates the live data source over the given readers.</summary>
    public LiveSessionDataSource(
        SessionAggregator aggregator,
        CheckpointsReader checkpoints,
        SessionDbReader sessionDb)
    {
        _aggregator = aggregator;
        _checkpoints = checkpoints;
        _sessionDb = sessionDb;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionInfo> LoadAll() => _aggregator.LoadAll();

    /// <inheritdoc />
    public IReadOnlyList<SessionInfo> LoadCheap() => _aggregator.LoadCheap();

    /// <inheritdoc />
    public SessionInfo EnrichOne(SessionInfo session) => _aggregator.EnrichOne(session);

    /// <inheritdoc />
    public SessionInfo EnrichWithEvents(SessionInfo session) => _aggregator.EnrichWithEvents(session);

    /// <inheritdoc />
    public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) =>
        _checkpoints.Read(session.FolderPath);

    /// <inheritdoc />
    public SessionTodosResult ReadTodos(SessionInfo session, CancellationToken token = default) =>
        _sessionDb.ReadTodos(session.FolderPath, token);

    /// <inheritdoc />
    public string GetDetailsVersion(SessionInfo session) => string.Join("|",
        FileVersion.Read(CopilotPaths.WorkspaceYaml(session.FolderPath)),
        FileVersion.Read(CopilotPaths.EventsJsonl(session.FolderPath)));

    /// <inheritdoc />
    public string GetCheckpointsVersion(SessionInfo session)
    {
        string directory = CopilotPaths.CheckpointsDir(session.FolderPath);
        string directoryVersion = FileVersion.ReadDirectory(directory).ToString();
        if (!Directory.Exists(directory)) return directoryVersion;

        // ASSUMPTION: a checkpoint can be edited in place without changing its directory
        // timestamp. Inspect file versions only while this section is requested.
        return directoryVersion + "|" + string.Join("|",
            Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => $"{Path.GetFileName(path)}:{FileVersion.Read(path)}"));
    }

}
