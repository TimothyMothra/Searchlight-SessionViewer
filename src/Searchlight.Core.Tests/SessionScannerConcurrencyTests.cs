using Searchlight.Abstractions;
using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SessionScannerConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"searchlight-scanner-{Guid.NewGuid():N}");

    public SessionScannerConcurrencyTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingCatalogs_BoundReadersToFour_AndReturnDeterministicOrder(bool warm)
    {
        Assert.Equal(4, SessionStateScanner.CatalogConcurrency);
        DateTime timestamp = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < 24; i++)
        {
            string folder = CreateFolder($"s{i:00}");
            Directory.SetLastWriteTimeUtc(folder, timestamp.AddMinutes(i / 2));
        }

        int active = 0, maximum = 0;
        bool measure = false;
        object gate = new();
        using var release = new ManualResetEventSlim();
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, beforeFolderRead: _ =>
        {
            if (!Volatile.Read(ref measure)) return;
            lock (gate)
            {
                maximum = Math.Max(maximum, ++active);
                // The scheduler need not fill all slots; require overlap, not
                // a particular worker utilization, and verify the cap throughout.
                if (active >= 2) full.TrySetResult();
            }
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Reader gate timed out.");
            }
            finally { lock (gate) active--; }
        });

        if (warm) scanner.Scan();
        Volatile.Write(ref measure, true);
        Task<IReadOnlyList<SessionInfo>> first = Task.Run(scanner.ScanCheap);
        Task<IReadOnlyList<SessionInfo>> second = Task.Run(scanner.ScanCheap);
        try
        {
            await full.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(maximum, 2, SessionStateScanner.CatalogConcurrency);
        }
        finally { release.Set(); await Task.WhenAll(first, second); }

        IReadOnlyList<SessionInfo> firstRows = await first;
        IReadOnlyList<SessionInfo> secondRows = await second;
        Assert.InRange(maximum, 2, SessionStateScanner.CatalogConcurrency);
        Assert.Equal(24, firstRows.Count);
        Assert.Equal(firstRows.Select(s => s.Id), secondRows.Select(s => s.Id));
        Assert.Equal(firstRows.OrderByDescending(s => s.LastWriteTime)
            .ThenBy(s => s.FolderPath, StringComparer.Ordinal).Select(s => s.Id),
            firstRows.Select(s => s.Id));
        Assert.All(firstRows, s => Assert.Equal(warm, s.IsEnriched));
        if (warm)
            Assert.All(firstRows.Zip(secondRows), pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public async Task ConcurrentEnrichmentOfOneFolder_PublishesOneStableSummary()
    {
        string folder = CreateFolder("session");
        File.WriteAllText(Path.Combine(folder, "workspace.yaml"), "name: shared-summary\n");
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root);
        SessionInfo cold = Assert.Single(scanner.ScanCheap());
        SessionInfo[] rows = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => scanner.EnrichFolder(cold))));
        Assert.All(rows, row =>
        {
            Assert.True(row.IsEnriched);
            Assert.Equal("shared-summary", row.DisplayName);
            Assert.Same(rows[0], row);
        });
        Assert.Same(rows[0], Assert.Single(scanner.ScanCheap()));
    }

    [Fact]
    public void ColdDiscovery_DefersMetadataAndFlags_WarmCatalogRetainsIdentity()
    {
        string folder = CreateFolder("session");
        File.WriteAllText(Path.Combine(folder, "workspace.yaml"), "name: synthetic\n");
        File.WriteAllText(Path.Combine(folder, "events.jsonl"), "not parsed by the catalog");
        File.WriteAllText(Path.Combine(folder, "plan.md"), "synthetic");
        Directory.CreateDirectory(Path.Combine(folder, "checkpoints"));
        File.WriteAllText(Path.Combine(folder, "checkpoints", "index.md"), "synthetic");
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root);

        SessionInfo cold = Assert.Single(scanner.ScanCheap());
        Assert.False(cold.IsEnriched);
        Assert.Null(cold.Workspace);
        Assert.False(cold.HasEvents);
        Assert.False(cold.HasPlan);
        Assert.False(cold.HasCheckpoints);
        SessionInfo enriched = scanner.EnrichFolder(cold);
        Assert.Equal("synthetic", enriched.DisplayName);
        Assert.True(enriched.HasEvents);
        Assert.True(enriched.HasPlan);
        Assert.True(enriched.HasCheckpoints);
        Assert.Same(enriched, Assert.Single(scanner.ScanCheap()));
        Assert.Same(enriched, scanner.EnrichFolder(cold));
    }

    [Fact]
    public void Cache_InvalidatesYamlFolderAndCheckpoints_AndEvictsDeletedFolders()
    {
        string folder = CreateFolder("session");
        string yaml = Path.Combine(folder, "workspace.yaml");
        string checkpoints = Path.Combine(folder, "checkpoints");
        File.WriteAllText(yaml, "name: first\n");
        Directory.CreateDirectory(checkpoints);
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root);
        SessionInfo initial = Assert.Single(scanner.Scan());
        DateTime originalFolderTime = Directory.GetLastWriteTimeUtc(folder);

        File.WriteAllText(yaml, "name: second-longer\n");
        File.SetLastWriteTimeUtc(yaml, DateTime.UtcNow.AddMinutes(1));
        Directory.SetLastWriteTimeUtc(folder, originalFolderTime);
        Assert.False(Assert.Single(scanner.ScanCheap()).IsEnriched);
        Assert.Equal("second-longer", Assert.Single(scanner.Scan()).DisplayName);

        File.WriteAllText(Path.Combine(folder, "events.jsonl"), "");
        Directory.SetLastWriteTimeUtc(folder, originalFolderTime.AddMinutes(2));
        Assert.False(Assert.Single(scanner.ScanCheap()).IsEnriched);
        Assert.True(Assert.Single(scanner.Scan()).HasEvents);

        DateTime folderTime = Directory.GetLastWriteTimeUtc(folder);
        File.WriteAllText(Path.Combine(checkpoints, "index.md"), "synthetic");
        Directory.SetLastWriteTimeUtc(checkpoints, DateTime.UtcNow.AddMinutes(3));
        Directory.SetLastWriteTimeUtc(folder, folderTime);
        Assert.False(Assert.Single(scanner.ScanCheap()).IsEnriched);
        Assert.True(Assert.Single(scanner.Scan()).HasCheckpoints);

        Directory.Delete(folder, recursive: true);
        Assert.Empty(scanner.ScanCheap());
        Assert.Null(scanner.ScanFolderCheap(folder));
        CreateFolder("session");
        Assert.False(Assert.Single(scanner.ScanCheap()).IsEnriched);
        Assert.NotSame(initial, Assert.Single(scanner.Scan()));
    }

    [Fact]
    public void WarmCatalog_RechecksLiveOwner_WithoutChangingSummaryVersions()
    {
        string folder = CreateFolder("session");
        string path = Path.Combine(folder, "inuse.123.lock");
        DateTime written = DateTime.UtcNow.AddHours(-1);
        File.WriteAllText(path, "123\n");
        File.SetLastWriteTimeUtc(path, written);
        var processes = new FakeProcesses { Started = new DateTimeOffset(written.AddMinutes(-1), TimeSpan.Zero) };
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, new SessionActivityMonitor(processes));
        SessionInfo initial = Assert.Single(scanner.Scan());
        Assert.True(initial.IsInUse);
        Assert.Same(initial, Assert.Single(scanner.ScanCheap()));
        DateTime folderTime = Directory.GetLastWriteTimeUtc(folder);

        processes.Started = null;
        SessionInfo exited = Assert.Single(scanner.ScanCheap());
        Assert.False(exited.IsInUse);
        Assert.True(exited.IsEnriched);
        Assert.NotSame(initial, exited);
        Assert.Same(exited, Assert.Single(scanner.ScanCheap()));
        Assert.Equal(folderTime, Directory.GetLastWriteTimeUtc(folder));
        Assert.Equal(4, processes.Calls);
    }

    [Fact]
    public void UnreadableFolder_IsSkippedWithoutDroppingOtherRows()
    {
        string unavailable = CreateFolder("unavailable");
        CreateFolder("available");
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root,
            beforeFolderRead: path =>
            {
                if (path == unavailable) throw new UnauthorizedAccessException("Synthetic access failure.");
            });
        Assert.Equal("available", Assert.Single(scanner.ScanCheap()).Id);
        var input = new SessionInfo { Id = "unavailable", FolderPath = unavailable, FolderName = "unavailable" };
        Assert.Same(input, scanner.EnrichFolder(input));
        Directory.Delete(_root, recursive: true);
        Assert.Empty(scanner.ScanCheap());
    }

    [Fact]
    public async Task OlderCatalog_DoesNotEvictNewlyDiscoveredFolder()
    {
        string original = CreateFolder("original");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int block = 1;
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, beforeFolderRead: path =>
        {
            if (path != original || Interlocked.Exchange(ref block, 0) != 1) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Catalog gate timed out.");
        });
        Task<IReadOnlyList<SessionInfo>> older = Task.Run(scanner.ScanCheap);
        SessionInfo newer;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string folder = CreateFolder("newer");
            File.WriteAllText(Path.Combine(folder, "workspace.yaml"), "name: synthetic-newer\n");
            newer = scanner.ScanFolder(folder)!;
            Assert.True(newer.IsEnriched);
        }
        finally { release.Set(); await older; }

        Assert.Single(await older);
        Assert.Same(newer, scanner.ScanCheap().Single(s => s.Id == "newer"));
    }

    [Fact]
    public async Task EvictedInflightRead_CannotReplaceRecreatedFoldersCache()
    {
        string folder = CreateFolder("session");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int block = 0;
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, beforeFolderRead: _ =>
        {
            if (Interlocked.Exchange(ref block, 0) != 1) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Enrichment gate timed out.");
        });
        SessionInfo cold = Assert.Single(scanner.ScanCheap());
        block = 1;
        Task<SessionInfo> older = Task.Run(() => scanner.EnrichFolder(cold));
        SessionInfo newer;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(folder, recursive: true);
            Assert.Empty(scanner.ScanCheap());
            CreateFolder("session");
            File.WriteAllText(Path.Combine(folder, "workspace.yaml"), "name: recreated\n");
            newer = scanner.ScanFolder(folder)!;
            Assert.True(newer.IsEnriched);
        }
        finally { release.Set(); await older; }

        // ASSUMPTION: the old read may return a snapshot to its caller, but must
        // not republish into the replacement state installed after eviction.
        Assert.NotSame(newer, await older);
        Assert.Same(newer, Assert.Single(scanner.ScanCheap()));
    }

    private string CreateFolder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeProcesses : ICopilotProcessProbe
    {
        public DateTimeOffset? Started;
        public int Calls;
        public DateTimeOffset? GetStartTimeUtc(int processId)
        {
            Interlocked.Increment(ref Calls);
            return Started;
        }
    }
}
