using Searchlight.Abstractions;
using Searchlight.Models;
using Searchlight.Services;

namespace Searchlight.Core.Tests;

/// <summary>
/// Runs posted callbacks inline on the calling thread — a headless stand-in for the
/// WinUI <c>DispatcherQueue</c> adapter so view-model tests need no UI thread.
/// </summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>
/// Minimal <see cref="ISessionDataSource"/> over a caller-supplied session list, for
/// tests that need a specific fixture shape (e.g. empty or unnamed sessions) rather
/// than the locked 15-row <see cref="MockSessionDataSource"/> demo dataset. Rows are
/// returned as-is: enrichment is a no-op because the caller already sets the flags
/// under test.
/// </summary>
internal sealed class StubSessionDataSource : ISessionDataSource
{
    private readonly IReadOnlyList<SessionInfo> _sessions;

    public StubSessionDataSource(params SessionInfo[] sessions) => _sessions = sessions;

    public IReadOnlyList<SessionInfo> LoadAll() => _sessions;

    public IReadOnlyList<SessionInfo> LoadCheap() => _sessions;

    public SessionInfo EnrichOne(SessionInfo session) => session;

    public SessionInfo EnrichWithEvents(SessionInfo session) => session;

    public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) => [];

    public IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId) => [];

    public IReadOnlyList<SessionTodo> ReadTodos(SessionInfo session) => [];
}
