using System.Collections.Specialized;
using Searchlight.Models;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SessionGroupReconciliationTests
{
    private static SessionInfo Session(string id) => new()
    {
        Id = id, FolderName = id, FolderPath = $@"C:\synthetic\{id}",
        LastWriteTime = DateTimeOffset.UnixEpoch,
    };

    private static SessionGroup Group(params SessionInfo[] sessions)
    {
        var group = new SessionGroup("Recent", "Recent");
        group.SetItems(sessions);
        return group;
    }

    private static List<NotifyCollectionChangedEventArgs> Observe(SessionGroup group)
    {
        List<NotifyCollectionChangedEventArgs> changes = [];
        group.CollectionChanged += (_, e) => changes.Add(e);
        return changes;
    }

    [Fact]
    public void UnchangedRows_KeepTheirReferencesWithoutNotifications()
    {
        SessionInfo[] rows = [Session("a"), Session("b")];
        var group = Group(rows);
        var changes = Observe(group);
        group.SetItems(rows);
        Assert.Empty(changes);
        Assert.Same(rows[0], group[0]);
        Assert.Same(rows[1], group[1]);
    }

    [Fact]
    public void ChangedSummary_ReplacesOnlyThatRow()
    {
        SessionInfo a = Session("a"), b = Session("b"), c = Session("c");
        var group = Group(a, b, c);
        var changes = Observe(group);
        SessionInfo updated = b with { IsInUse = true };
        group.SetItems([a, updated, c]);
        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action);
        Assert.Equal(1, change.NewStartingIndex);
        Assert.Same(a, group[0]);
        Assert.Same(updated, group[1]);
        Assert.Same(c, group[2]);
    }

    [Fact]
    public void MembershipChanges_RemoveAndInsertWithoutReplacingSurvivors()
    {
        SessionInfo a = Session("a"), b = Session("b"), c = Session("c");
        SessionInfo d = Session("d"), e = Session("e");
        var group = Group(a, b, c);
        var changes = Observe(group);
        group.SetItems([a, d, c, e]);
        Assert.Equal(
            [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add],
            changes.Select(change => change.Action));
        Assert.Equal(["a", "d", "c", "e"], group.Select(s => s.Id));
        Assert.Same(a, group[0]);
        Assert.Same(c, group[2]);
    }

    [Fact]
    public void Reordering_UsesMovesAndRetainsRowInstances()
    {
        SessionInfo a = Session("a"), b = Session("b"), c = Session("c"), d = Session("d");
        var group = Group(a, b, c, d);
        var changes = Observe(group);
        group.SetItems([d, b, a, c]);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(NotifyCollectionChangedAction.Move, change.Action));
        Assert.Same(d, group[0]);
        Assert.Same(b, group[1]);
        Assert.Same(a, group[2]);
        Assert.Same(c, group[3]);
    }

    [Fact]
    public void ForcedRefresh_EmitsReplacementsForMutableRowChangesWithoutReset()
    {
        SessionInfo a = Session("a"), b = Session("b");
        var group = Group(a, b);
        var changes = Observe(group);
        a.CustomName = "Renamed";
        group.SetItems([a, b], force: true);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action));
        Assert.Equal("Renamed", group[0].DisplayName);
    }

    [Fact]
    public void EmptyTarget_RemovesRowsRatherThanClearingTheCollection()
    {
        var group = Group(Session("a"), Session("b"));
        var changes = Observe(group);
        group.SetItems([]);
        Assert.Empty(group);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(NotifyCollectionChangedAction.Remove, change.Action));
    }

    [Fact]
    public void RepeatedIds_StillProduceTheExactRequestedInstances()
    {
        SessionInfo a = Session("a"), anotherA = a with { IsInUse = true }, b = Session("b");
        var group = Group(a, anotherA, b);
        var changes = Observe(group);
        group.SetItems([anotherA, b, a]);
        Assert.Same(anotherA, group[0]);
        Assert.Same(b, group[1]);
        Assert.Same(a, group[2]);
        group.SetItems([a]);
        Assert.Same(a, Assert.Single(group));
        Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void MixedTransitions_AlwaysReachExactTargetWithoutReset()
    {
        // Fixed seed exercises insertion/removal, reordering, and replacement
        // together without depending on machine speed or real session files.
        var random = new Random(42);
        SessionInfo[] pool = Enumerable.Range(0, 30).Select(i => Session($"s{i}")).ToArray();
        var group = Group();
        var changes = Observe(group);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            SessionInfo[] target = pool.OrderBy(_ => random.Next()).Take(random.Next(pool.Length + 1))
                .Select(s => random.Next(2) == 0 ? s : s with { IsInUse = true }).ToArray();
            group.SetItems(target);
            Assert.Equal(target.Length, group.Count);
            for (int i = 0; i < target.Length; i++) Assert.Same(target[i], group[i]);
        }
        Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Reset);
    }
}
