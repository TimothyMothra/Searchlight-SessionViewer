using System.Collections.Concurrent;
using System.Collections.Specialized;
using Searchlight.Abstractions;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class LoadingPerformanceTests
{
    private static MainViewModel Create(CountingSource source, SettingsService? settings = null, NotesService? notes = null) =>
        new(source, new NullSessionWatcher(),
            new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService()),
            settings ?? new SettingsService(path: null), notes ?? new NotesService(dir: null),
            new InlineUiDispatcher());

    [Fact]
    public async Task FirstPublication_EnrichesThirtyRecentRowsAndAllOlderPins()
    {
        var source = new CountingSource(100);
        var settings = new SettingsService(path: null);
        settings.Current.PinnedSessionIds = ["s99", "s98"];
        using var vm = Create(source, settings);
        SessionInfo[]? published = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.VisibleCount) && published is null)
                published = vm.SessionGroups.SelectMany(g => g).ToArray();
        };
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotNull(published);
        Assert.Equal(32, published.Count(s => s.IsEnriched));
        Assert.All(published.Where(s => s.IsPinned), s => Assert.True(s.IsEnriched));
        Assert.Equal(30, MainViewModel.EagerEnrichCount);
        Assert.Equal(100, source.EnrichCalls);
        Assert.Equal(new[] { "s98", "s99" }, source.EnrichOrder.Take(2).Order().ToArray());
        Assert.All(vm.SessionGroups.SelectMany(g => g), s => Assert.True(s.IsEnriched));
    }

    [Fact]
    public async Task SummaryReads_AreBoundedAndKeepResultsInOrder()
    {
        var source = new CountingSource(100);
        int active = 0, maximum = 0;
        object gate = new();
        source.OnEnrich = _ =>
        {
            lock (gate) { active++; maximum = Math.Max(maximum, active); }
            // Model blocking filesystem latency without depending on disk speed.
            Thread.Sleep(5);
            lock (gate) active--;
        };
        using var vm = Create(source);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.InRange(maximum, 1, 4);
        Assert.Equal(100, source.EnrichCalls);
        Assert.Equal(source.Sessions.Select(s => s.Id),
            vm.SessionGroups.SelectMany(g => g).Select(s => s.Id));
    }

    [Fact]
    public async Task SummaryProducer_ReadsAheadWhileConsumerIsPaused_WithinBoundedCapacity()
    {
        var source = new CountingSource(180);
        var prefetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int limit = SessionSummaryReader.BatchSize * (SessionSummaryReader.BufferedBatches + 2);
        source.OnEnrich = _ =>
        {
            if (source.EnrichCalls == limit) prefetched.TrySetResult();
        };
        var reader = new SessionSummaryReader(source);
        await using var batches = reader.ReadBatchesAsync(source.Sessions, default).GetAsyncEnumerator();
        Assert.True(await batches.MoveNextAsync());
        // Do not request another UI batch. Reader work must continue independently,
        // stopping at the bounded queue plus delivered and in-flight batches.
        await prefetched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(limit, source.EnrichCalls);
    }

    [Fact]
    public async Task SummaryProducer_PropagatesReaderFailureWithoutHangingConsumer()
    {
        var source = new CountingSource(180);
        source.OnEnrich = _ => throw new IOException("synthetic read failure");
        var reader = new SessionSummaryReader(source);
        await using var batches = reader.ReadBatchesAsync(source.Sessions, default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<IOException>(() => batches.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BackgroundEnrichment_UpdatesOnlyChangedRows_WithoutGroupResets()
    {
        var source = new CountingSource(1000);
        using var vm = Create(source);
        int resets = 0, replacements = 0;
        vm.SessionGroups.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (SessionGroup group in e.NewItems)
                group.CollectionChanged += (_, change) =>
                {
                    if (change.Action == NotifyCollectionChangedAction.Reset) resets++;
                    if (change.Action == NotifyCollectionChangedAction.Replace) replacements++;
                };
        };
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(970, replacements);
        // ASSUMPTION: this fixture stays in one recency bucket. A reset would
        // invalidate all 1,000 rows, rather than just each changed item.
        Assert.Equal(0, resets);
    }

    [Fact]
    public async Task Search_DoesNotReloadDetailsOrProbeNotes_AndRetainsUnchangedGroups()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var notes = new NotesService(path);
            notes.Write("s0", "cached note");
            var source = new CountingSource(100);
            using var vm = Create(source, notes: notes);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedSession = vm.SessionGroups.SelectMany(g => g).First(s => s.Id == "s1");
            await vm.Details.CurrentLoad;
            int events = source.EventCalls, versions = source.VersionCalls;
            SessionGroup group = vm.SessionGroups[0];
            File.Delete(Path.Combine(path, "s0.md"));

            foreach (string query in new[] { "s", "sh", "sha", "shared", "" })
                vm.SearchText = query;
            await vm.Details.CurrentLoad;

            Assert.Equal(events, source.EventCalls);
            Assert.Equal(versions, source.VersionCalls);
            Assert.Same(group, vm.SessionGroups[0]);
            Assert.True(vm.SessionGroups.SelectMany(g => g).First(s => s.Id == "s0").HasNote);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.False(vm.SessionGroups.SelectMany(g => g).First(s => s.Id == "s0").HasNote);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task WatcherBursts_CoalesceWithoutAbandoningTheCurrentPass()
    {
        var source = new CountingSource(100);
        var watcher = new TestWatcher();
        using var vm = new MainViewModel(source, watcher,
            new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService()),
            new SettingsService(path: null), new NotesService(dir: null), new InlineUiDispatcher());
        bool raised = false;
        source.OnEnrich = session =>
        {
            if (session.Id != "s30" || raised) return;
            raised = true;
            for (int i = 0; i < 5; i++) watcher.Raise();
        };
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 0, 100 }, source.CatalogEnrichmentCounts.ToArray());
        Assert.Equal(200, source.EnrichCalls);
    }

    [Fact]
    public async Task Details_AreOffThread_AndSupersededSelectionCannotPublish()
    {
        var source = new CountingSource(2);
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.OnEvents = s =>
        {
            if (s.Id == "s0")
            {
                entered.TrySetResult();
                if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test reader was not released.");
            }
        };
        var vm = new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService());
        try
        {
            vm.Load(source.Sessions[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsLoading);
            Assert.Equal("s0", vm.Session!.Id);
            Task old = vm.CurrentLoad;
            vm.Load(source.Sessions[1]);
            gate.Set();
            await Task.WhenAll(old, vm.CurrentLoad);
            Assert.Equal("s1", vm.Session!.Id);
            Assert.False(vm.IsLoading);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task DetailsCache_ReusesUnchangedEntries_InvalidatesAndEvicts()
    {
        var source = new CountingSource(SessionDetailsLoader.Capacity + 1);
        var loader = new SessionDetailsLoader(source);
        await loader.LoadAsync(source.Sessions[0], default);
        await loader.LoadAsync(source.Sessions[0], default);
        Assert.Equal(1, source.EventCalls);
        source.Version = "changed";
        await loader.LoadAsync(source.Sessions[0], default);
        Assert.Equal(2, source.EventCalls);
        foreach (SessionInfo session in source.Sessions.Skip(1))
            await loader.LoadAsync(session, default);
        int before = source.EventCalls;
        await loader.LoadAsync(source.Sessions[0], default);
        Assert.Equal(before + 1, source.EventCalls);
    }

    [Fact]
    public async Task DetailsCache_MergesRefreshedSummaryWithoutRereadingHeavyDetails()
    {
        var source = new CountingSource(1);
        var vm = new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService());
        SessionInfo original = source.Sessions[0] with { Branch = "old", JournalActivity = "before" };
        vm.Load(original);
        await vm.CurrentLoad;

        SessionInfo updated = original with
        {
            IsEnriched = true,
            Branch = "new",
            JournalActivity = "after",
            SnapshotCount = 3,
            Workspace = new WorkspaceMetadata { Name = "updated summary" },
        };
        vm.Load(updated, refresh: true);
        await vm.CurrentLoad;

        Assert.Equal("new", vm.Session!.Branch);
        Assert.Equal("after", vm.Session.JournalActivity);
        Assert.Equal(3, vm.Session.SnapshotCount);
        Assert.Equal("updated summary", vm.Session.DisplayName);
        Assert.Equal(1, source.EventCalls);
    }

    [Fact]
    public async Task DetailsCache_DoesNotCacheInputsThatChangeDuringRead()
    {
        var source = new CountingSource(1);
        var loader = new SessionDetailsLoader(source);
        source.OnEvents = _ => source.Version = Guid.NewGuid().ToString();
        await loader.LoadAsync(source.Sessions[0], default);
        await loader.LoadAsync(source.Sessions[0], default);
        Assert.Equal(2, source.EventCalls);
    }

    [Fact]
    public void SummaryCache_ReusesUnchangedRows_AndObservesYamlAndFolderChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "session");
        Directory.CreateDirectory(folder);
        string yaml = Path.Combine(folder, "workspace.yaml");
        try
        {
            File.WriteAllText(yaml, "name: original\n");
            var scanner = new SessionStateScanner(new WorkspaceYamlReader(), root);
            SessionInfo initial = scanner.Scan().Single();
            SessionInfo warm = scanner.ScanCheap().Single();
            Assert.Equal(initial.LastWriteTime.UtcTicks, warm.LastWriteTime.UtcTicks);
            Assert.Same(initial, warm);
            File.WriteAllText(yaml, "name: changed-name\n");
            File.SetLastWriteTimeUtc(yaml, DateTime.UtcNow.AddMinutes(1));
            Assert.False(scanner.ScanCheap().Single().IsEnriched);
            SessionInfo changed = scanner.Scan().Single();
            Assert.Equal("changed-name", changed.DisplayName);
            File.WriteAllText(Path.Combine(folder, "events.jsonl"), "");
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddMinutes(2));
            Assert.True(scanner.Scan().Single().HasEvents);
            Directory.Delete(folder, recursive: true);
            Assert.Empty(scanner.ScanCheap());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CountingSource : ISessionDataSource
    {
        public readonly SessionInfo[] Sessions;
        public int EnrichCalls, EventCalls, VersionCalls;
        public readonly ConcurrentQueue<string> EnrichOrder = new();
        public readonly ConcurrentQueue<int> CatalogEnrichmentCounts = new();
        public string Version = "initial";
        public Action<SessionInfo>? OnEvents;
        public Action<SessionInfo>? OnEnrich;

        public CountingSource(int count) => Sessions = Enumerable.Range(0, count)
            .Select(i => new SessionInfo
            {
                Id = $"s{i}", FolderName = $"s{i}", FolderPath = $"fixture-{i}",
                LastWriteTime = DateTimeOffset.Now.AddSeconds(-i),
                Workspace = new WorkspaceMetadata { Name = $"shared {i}" },
                HasEvents = true,
            }).ToArray();

        public IReadOnlyList<SessionInfo> LoadAll() => Sessions;
        public IReadOnlyList<SessionInfo> LoadCheap()
        {
            CatalogEnrichmentCounts.Enqueue(EnrichCalls);
            return Sessions;
        }
        public SessionInfo EnrichOne(SessionInfo session)
        {
            Interlocked.Increment(ref EnrichCalls);
            EnrichOrder.Enqueue(session.Id);
            OnEnrich?.Invoke(session);
            return session with { IsEnriched = true };
        }
        public SessionInfo EnrichWithEvents(SessionInfo session)
        {
            Interlocked.Increment(ref EventCalls);
            OnEvents?.Invoke(session);
            return session;
        }
        public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) => [];
        public IReadOnlyList<SnapshotInfo> LoadSnapshots(string id) => [];
        public SessionTodosResult ReadTodos(SessionInfo session, CancellationToken token = default) => new();
        public string GetDetailsVersion(SessionInfo session)
        {
            Interlocked.Increment(ref VersionCalls);
            return Version;
        }
    }

    private sealed class TestWatcher : ISessionWatcher
    {
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
        public void Start() { }
        public void Dispose() { }
    }
}
