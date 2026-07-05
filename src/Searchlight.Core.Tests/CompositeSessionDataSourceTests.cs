using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Covers the combined Claude + Copilot data source: the merged list contains
/// both stores' sessions, and every per-session call routes back to the source
/// that owns the session (decided by whether its folder lives under the Claude
/// projects root).
/// </summary>
public sealed class CompositeSessionDataSourceTests
{
    private static SessionInfo Session(string id, string folderPath) => new()
    {
        Id = id,
        FolderName = Path.GetFileName(folderPath),
        FolderPath = folderPath,
    };

    private static readonly SessionInfo ClaudeSession = Session(
        "11111111-1111-1111-1111-111111111111",
        Path.Combine(ClaudePaths.Projects, "-Users-jane-Source-app"));

    private static readonly SessionInfo CopilotSession = Session(
        "22222222-2222-2222-2222-222222222222",
        Path.Combine(CopilotPaths.SessionState, "22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void LoadAll_MergesBothStores()
    {
        var composite = new CompositeSessionDataSource(
            new StubSource { Sessions = [ClaudeSession] },
            new StubSource { Sessions = [CopilotSession] });

        IReadOnlyList<SessionInfo> all = composite.LoadAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Id == ClaudeSession.Id);
        Assert.Contains(all, s => s.Id == CopilotSession.Id);
    }

    [Fact]
    public void PerSessionCalls_RouteToOwningSource()
    {
        var claude = new StubSource();
        var copilot = new StubSource();
        var composite = new CompositeSessionDataSource(claude, copilot);

        composite.EnrichWithEvents(ClaudeSession);
        composite.ReadCheckpoints(ClaudeSession);
        composite.ReadTodos(ClaudeSession);
        Assert.Equal(3, claude.Calls);
        Assert.Equal(0, copilot.Calls);

        composite.EnrichWithEvents(CopilotSession);
        composite.ReadCheckpoints(CopilotSession);
        composite.ReadTodos(CopilotSession);
        Assert.Equal(3, claude.Calls);
        Assert.Equal(3, copilot.Calls);
    }

    [Fact]
    public void LoadSnapshots_IsCopilotOnly()
    {
        var claude = new StubSource();
        var copilot = new StubSource();
        var composite = new CompositeSessionDataSource(claude, copilot);

        composite.LoadSnapshots(ClaudeSession.Id);

        Assert.Equal(0, claude.Calls);
        Assert.Equal(1, copilot.Calls);
    }

    [Theory]
    // The projects root itself is not a session folder.
    [InlineData(false, "")]
    // A sibling of the projects root must not be treated as Claude's.
    [InlineData(false, "..")]
    public void IsClaudeSession_RejectsNonProjectPaths(bool expected, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(ClaudePaths.Projects, relative));
        Assert.Equal(expected, CompositeSessionDataSource.IsClaudeSession(Session("x", path)));
    }

    private sealed class StubSource : ISessionDataSource
    {
        public IReadOnlyList<SessionInfo> Sessions { get; init; } = [];

        public int Calls { get; private set; }

        public IReadOnlyList<SessionInfo> LoadAll() => Sessions;

        public IReadOnlyList<SessionInfo> LoadCheap() => Sessions;

        public SessionInfo EnrichOne(SessionInfo session)
        {
            Calls++;
            return session;
        }

        public SessionInfo EnrichWithEvents(SessionInfo session)
        {
            Calls++;
            return session;
        }

        public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session)
        {
            Calls++;
            return [];
        }

        public IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId)
        {
            Calls++;
            return [];
        }

        public IReadOnlyList<SessionTodo> ReadTodos(SessionInfo session)
        {
            Calls++;
            return [];
        }
    }
}
