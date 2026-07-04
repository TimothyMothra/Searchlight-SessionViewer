using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Live <see cref="ISessionDataSource"/> backed by the user's real <c>~/.copilot</c>
/// tree. Thin adapter that composes the aggregator (list + events enrichment) with
/// the per-session detail readers (checkpoints, status snapshots, session.db todos).
/// All access is read-only.
/// </summary>
public sealed class LiveSessionDataSource : ISessionDataSource
{
    private readonly SessionAggregator _aggregator;
    private readonly CheckpointsReader _checkpoints;
    private readonly SnapshotIndexReader _snapshots;
    private readonly SessionDbReader _sessionDb;

    /// <summary>Creates the live data source over the given readers.</summary>
    public LiveSessionDataSource(
        SessionAggregator aggregator,
        CheckpointsReader checkpoints,
        SnapshotIndexReader snapshots,
        SessionDbReader sessionDb)
    {
        _aggregator = aggregator;
        _checkpoints = checkpoints;
        _snapshots = snapshots;
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
    public IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId) =>
        _snapshots.LoadForSession(sessionId);

    /// <inheritdoc />
    public IReadOnlyList<SessionTodo> ReadTodos(SessionInfo session) =>
        _sessionDb.Read(session.FolderPath).Todos;
}
