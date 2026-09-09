using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Locks in the shape of the deterministic mock dataset so demos, screenshots, and
/// the grouping/projection tests all rest on a stable, leak-free fixture.
/// </summary>
public sealed class MockSessionDataSourceTests
{
    private const string DetailId = "00000000-0000-4000-8000-000000000001";
    private const string PlainId = "00000000-0000-4000-8000-000000000002";

    private static readonly string[] DetailIds =
    [
        "00000000-0000-4000-8000-000000000001",
        "00000000-0000-4000-8000-000000000003",
        "00000000-0000-4000-8000-000000000005",
        "00000000-0000-4000-8000-000000000007",
        "00000000-0000-4000-8000-000000000009",
        "00000000-0000-4000-8000-000000000012",
    ];

    private readonly MockSessionDataSource _sut = new();

    [Fact]
    public void LoadAll_ReturnsFifteenSessions() =>
        Assert.Equal(15, _sut.LoadAll().Count);

    [Fact]
    public void LoadAll_ReturnsExactlySixDetailedSessions()
    {
        int detailed = _sut.LoadAll().Count(s => s.HasCheckpoints);
        Assert.Equal(6, detailed);
    }

    [Fact]
    public void DetailedSessions_HaveTheExpectedIds()
    {
        var ids = _sut.LoadAll().Where(s => s.HasCheckpoints).Select(s => s.Id).OrderBy(x => x);
        Assert.Equal(DetailIds.OrderBy(x => x), ids);
    }

    [Fact]
    public void DetailSession_SeedsCheckpointsSnapshotsAndTodos()
    {
        SessionInfo detail = _sut.LoadAll().First(s => s.Id == DetailId);

        Assert.Equal(3, _sut.ReadCheckpoints(detail).Count);
        Assert.Equal(3, _sut.LoadSnapshots(detail.Id).Count);
        SessionTodosResult result = _sut.ReadTodos(detail);
        Assert.Equal(SessionTodosStatus.Success, result.Status);
        Assert.Equal(4, result.Todos.Count);
        Assert.All(result.Todos, todo => Assert.False(string.IsNullOrWhiteSpace(todo.Description)));
        Assert.Equal(3, detail.SnapshotCount);
        Assert.True(detail.HasSessionDb);
        Assert.True(detail.HasPlan);
    }

    [Fact]
    public void DetailSession_TodoStatuses_AreDoneDoneInProgressPending()
    {
        SessionInfo detail = _sut.LoadAll().First(s => s.Id == DetailId);
        var statuses = _sut.ReadTodos(detail).Todos.Select(t => t.Status).ToArray();
        Assert.Equal(["done", "done", "in_progress", "pending"], statuses);
    }

    [Fact]
    public void PlainSession_HasNoDetailContent()
    {
        SessionInfo plain = _sut.LoadAll().First(s => s.Id == PlainId);

        Assert.False(plain.HasCheckpoints);
        Assert.Empty(_sut.ReadCheckpoints(plain));
        Assert.Empty(_sut.LoadSnapshots(plain.Id));
        Assert.Empty(_sut.ReadTodos(plain).Todos);
        Assert.Equal(SessionTodosStatus.MissingDatabase, _sut.ReadTodos(plain).Status);
        Assert.Equal(0, plain.SnapshotCount);
    }

    [Fact]
    public void Fixture_CoversEveryClientKindAndBothSessionKinds()
    {
        var all = _sut.LoadAll();

        Assert.Contains(all, s => s.IsCliClient);
        Assert.Contains(all, s => s.IsAppClient);
        Assert.Contains(all, s => s.ClientLabel == "Unknown");
        Assert.Contains(all, s => s.Kind == SessionKind.Project);
        Assert.Contains(all, s => s.Kind == SessionKind.Chat);
    }
}
