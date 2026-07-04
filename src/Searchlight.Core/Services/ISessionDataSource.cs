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
    /// Fast first pass: cheap placeholder rows for every session (id/folder/kind/
    /// mtime only, sorted newest-first), with no yaml parse or bulk enrichment.
    /// Each row must be upgraded via <see cref="EnrichOne"/> before display.
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

    /// <summary>Todos read from the given session's store.</summary>
    IReadOnlyList<SessionTodo> ReadTodos(SessionInfo session);
}
