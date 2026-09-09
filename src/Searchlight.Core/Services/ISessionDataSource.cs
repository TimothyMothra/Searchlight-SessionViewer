using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Read-only façade over every per-session data source the UI needs. The live
/// implementation reads the user's real <c>~/.copilot</c> tree; a mock
/// implementation supplies synthetic data for demos, screenshots, and unit
/// tests. This single seam is what decouples the view-models from the
/// filesystem — swap the implementation to swap the entire data backing.
/// </summary>
public interface ISessionDataSource
{
    /// <summary>Full recent-sessions list, bulk-enriched, newest-first not guaranteed (caller sorts).</summary>
    IReadOnlyList<SessionInfo> LoadAll();

    /// <summary>
    /// Catalog pass: cached summaries for unchanged sessions, otherwise cheap
    /// placeholders (id/folder/kind/mtime), sorted newest-first. Only placeholders
    /// need upgrading via <see cref="EnrichOne"/>; live sources refresh bulk maps.
    /// </summary>
    IReadOnlyList<SessionInfo> LoadCheap();

    /// <summary>
    /// Fully enriches one cheap placeholder from <see cref="LoadCheap"/> with its
    /// workspace.yaml facts, presence flags, and bulk branch/snapshot/journal
    /// enrichment. Events head-parsing is still deferred to <see cref="EnrichWithEvents"/>.
    /// </summary>
    SessionInfo EnrichOne(SessionInfo session);

    /// <summary>Returns the session with its per-session events head parsed (or unchanged).</summary>
    SessionInfo EnrichWithEvents(SessionInfo session);

    /// <summary>Checkpoints for the given session (newest first).</summary>
    IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session);

    /// <summary>Recent status snapshots for the given session id (newest first).</summary>
    IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId);

    /// <summary>Fresh todo snapshot, read only on Todos activation or explicit Todos refresh.</summary>
    SessionTodosResult ReadTodos(SessionInfo session, CancellationToken token = default);

    /// <summary>
    /// Version of detail inputs, checked on a worker when selecting or refreshing.
    /// In-memory sources are immutable unless they override this value.
    /// </summary>
    string GetDetailsVersion(SessionInfo session) => string.Empty;
}
