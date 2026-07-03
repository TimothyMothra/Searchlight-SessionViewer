using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Merges the Claude Code (<c>~/.claude</c>) and Copilot (<c>~/.copilot</c>)
/// stores into one session list. <see cref="LoadAll"/> concatenates both
/// sources (the view-model sorts); every per-session call routes back to the
/// source that owns the session, decided structurally by whether the session's
/// <see cref="SessionInfo.FolderPath"/> lives under the Claude projects root.
/// Read-only, like the sources it wraps.
/// </summary>
public sealed class CompositeSessionDataSource : ISessionDataSource
{
    private readonly ISessionDataSource _claude;
    private readonly ISessionDataSource _copilot;

    /// <summary>Creates the composite over the two live sources.</summary>
    public CompositeSessionDataSource(ISessionDataSource claude, ISessionDataSource copilot)
    {
        _claude = claude;
        _copilot = copilot;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionInfo> LoadAll()
    {
        var all = new List<SessionInfo>(_claude.LoadAll());
        all.AddRange(_copilot.LoadAll());
        return all;
    }

    /// <inheritdoc />
    public SessionInfo EnrichWithEvents(SessionInfo session) =>
        Route(session).EnrichWithEvents(session);

    /// <inheritdoc />
    public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) =>
        Route(session).ReadCheckpoints(session);

    /// <summary>
    /// Status snapshots are a Copilot-only concept (the Claude source always
    /// returns an empty list), and the call is keyed by id alone, so it goes
    /// straight to the Copilot source.
    /// </summary>
    public IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId) =>
        _copilot.LoadSnapshots(sessionId);

    /// <inheritdoc />
    public IReadOnlyList<SessionTodo> ReadTodos(SessionInfo session) =>
        Route(session).ReadTodos(session);

    private ISessionDataSource Route(SessionInfo session) =>
        IsClaudeSession(session) ? _claude : _copilot;

    /// <summary>
    /// True when the session was loaded from the Claude Code store. Structural
    /// check on the folder path: Claude sessions always carry a project folder
    /// directly under <c>~/.claude/projects</c>.
    /// </summary>
    public static bool IsClaudeSession(SessionInfo session)
    {
        string rel = Path.GetRelativePath(ClaudePaths.Projects, session.FolderPath);
        return rel != "."
            && !rel.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }
}
