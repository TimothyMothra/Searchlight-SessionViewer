using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SectionLoadingTests
{
    [Fact]
    public async Task TabOrderAndActivation_ReadOnlyTheOpenedNativeSection()
    {
        Assert.Equal([0, 1, 2], new[]
        {
            DetailsViewModel.DetailsTabIndex, DetailsViewModel.AgentTasksTabIndex,
            DetailsViewModel.CheckpointsTabIndex,
        });
        var source = new SectionSource();
        var vm = Create(source);
        vm.Load(source.Session);
        await vm.CurrentLoad;
        Assert.Equal([1, 0, 0], source.Reads);
        Assert.Equal([2, 0], source.VersionReads);
        Assert.Empty(vm.Checkpoints);
        Assert.Null(vm.Todos.Result);
        Assert.False(vm.ShowNoCheckpoints);
        _ = vm.MetadataGroups;
        Assert.Equal([1, 0, 0], source.Reads);

        vm.SelectedTabIndex = DetailsViewModel.AgentTasksTabIndex;
        await vm.Todos.CurrentLoad;
        Assert.Equal([1, 0, 1], source.Reads);
        Assert.Equal([2, 0], source.VersionReads);
        vm.SelectedTabIndex = DetailsViewModel.CheckpointsTabIndex;
        await vm.CurrentLoad;
        Assert.Equal([1, 1, 1], source.Reads);
        Assert.Equal([2, 2], source.VersionReads);
        Assert.Single(vm.Checkpoints);
        Assert.Equal("session", vm.Session!.Model);
    }

    [Fact]
    public async Task ReopeningSections_ReusesCachesAndInvalidatesOnlyChangedInputs()
    {
        var source = new SectionSource();
        var vm = Create(source);
        vm.Load(source.Session);
        await vm.CurrentLoad;
        vm.SelectedTabIndex = DetailsViewModel.CheckpointsTabIndex;
        await vm.CurrentLoad;
        source.Versions[1]++;
        vm.SelectedTabIndex = DetailsViewModel.DetailsTabIndex;
        await vm.CurrentLoad;
        Assert.Equal([1, 1, 0], source.Reads);
        vm.SelectedTabIndex = DetailsViewModel.CheckpointsTabIndex;
        await vm.CurrentLoad;
        Assert.Equal([1, 2, 0], source.Reads);
        vm.SelectedTabIndex = DetailsViewModel.DetailsTabIndex;
        await vm.CurrentLoad;
        vm.SelectedTabIndex = DetailsViewModel.CheckpointsTabIndex;
        await vm.CurrentLoad;
        Assert.Equal([1, 2, 0], source.Reads);
    }

    [Theory]
    [InlineData(DetailsViewModel.DetailsTabIndex, 0)]
    [InlineData(DetailsViewModel.CheckpointsTabIndex, 1)]
    [InlineData(DetailsViewModel.AgentTasksTabIndex, -1)]
    public async Task GlobalRefresh_ReadsOnlyTheActiveSection_AndLeavesTasksSnapshotAlone(int tab, int reader)
    {
        var source = new SectionSource();
        var vm = Create(source);
        vm.Load(source.Session);
        await vm.CurrentLoad;
        vm.SelectedTabIndex = tab;
        await Task.WhenAll(vm.CurrentLoad, vm.Todos.CurrentLoad);
        int[] before = [.. source.Reads];
        int[] versionsBefore = [.. source.VersionReads];
        for (int i = 0; i < source.Versions.Length; i++) source.Versions[i]++;

        vm.Load(source.Session with { CustomName = "Refreshed" }, refresh: true);
        await Task.WhenAll(vm.CurrentLoad, vm.Todos.CurrentLoad);
        Assert.Equal(tab, vm.SelectedTabIndex);
        Assert.Equal("Refreshed", vm.Session!.DisplayName);
        for (int i = 0; i < source.Reads.Length; i++)
            Assert.Equal(before[i] + (i == reader ? 1 : 0), source.Reads[i]);
        for (int i = 0; i < source.VersionReads.Length; i++)
            Assert.Equal(versionsBefore[i] + (i == reader ? 2 : 0), source.VersionReads[i]);
    }

    [Fact]
    public async Task RapidTabSwitch_CancelsQueuedSectionsAndDiscardsOldMetadata()
    {
        var source = new SectionSource();
        var vm = Create(source);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.OnRead[0] = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reader was not released.");
        };
        try
        {
            vm.Load(source.Session);
            Task metadata = vm.CurrentLoad;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.SelectedTabIndex = DetailsViewModel.CheckpointsTabIndex;
            Task checkpoints = vm.CurrentLoad;
            vm.SelectedTabIndex = DetailsViewModel.AgentTasksTabIndex;
            release.Set();
            await Task.WhenAll(metadata, checkpoints, vm.Todos.CurrentLoad);
            Assert.Equal([1, 0, 1], source.Reads);
            Assert.Null(vm.Session!.Start);
            Assert.Empty(vm.Checkpoints);
            Assert.False(vm.IsLoading);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ChangedSession_DiscardsInFlightCheckpointRowsAndReturnsToDetails()
    {
        var source = new SectionSource();
        var vm = Create(source);
        vm.Load(source.Session);
        await vm.CurrentLoad;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.OnRead[1] = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reader was not released.");
        };
        try
        {
            vm.SelectedTabIndex = DetailsViewModel.CheckpointsTabIndex;
            Task old = vm.CurrentLoad;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Load(source.Session with { Id = "next", FolderPath = "next" });
            release.Set();
            await Task.WhenAll(old, vm.CurrentLoad);
            Assert.Equal(DetailsViewModel.DetailsTabIndex, vm.SelectedTabIndex);
            Assert.Equal("next", vm.Session!.Model);
            Assert.Empty(vm.Checkpoints);
            Assert.False(vm.HasLoadedCheckpoints);
            Assert.False(vm.ShowNoCheckpoints);
            Assert.Equal([2, 1, 0], source.Reads);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SectionFailure_IsNotAnEmptyResult_AndRefreshRetries()
    {
        var source = new SectionSource();
        var vm = Create(source);
        vm.Load(source.Session);
        await vm.CurrentLoad;
        source.OnRead[1] = _ => throw new IOException("Synthetic checkpoint failure");
        vm.SelectedTabIndex = DetailsViewModel.CheckpointsTabIndex;
        await vm.CurrentLoad;
        Assert.Contains("Synthetic checkpoint failure", vm.SectionLoadError);
        Assert.False(vm.HasLoadedCheckpoints);
        Assert.False(vm.ShowNoCheckpoints);

        source.OnRead[1] = null;
        source.EmptyCheckpoints = true;
        vm.Load(source.Session, refresh: true);
        await vm.CurrentLoad;
        Assert.Null(vm.SectionLoadError);
        Assert.True(vm.HasLoadedCheckpoints);
        Assert.True(vm.ShowNoCheckpoints);
        Assert.Equal([1, 2, 0], source.Reads);
    }

    [Fact]
    public async Task CacheCapacity_IsSharedAcrossSessionAndSectionKeys()
    {
        var source = new SectionSource();
        var loader = new SessionDetailsLoader(source);
        for (int i = 0; i <= SessionDetailsLoader.Capacity; i++)
        {
            var session = source.Session with { Id = $"s{i / 2}", FolderPath = $"p{i / 2}" };
            await loader.LoadAsync(session, default, (SessionDetailSection)(i % 2));
        }
        int expected = SessionDetailsLoader.Capacity + 1;
        Assert.Equal(expected, source.Reads.Sum());
        int last = SessionDetailsLoader.Capacity;
        await loader.LoadAsync(source.Session with { Id = $"s{last / 2}", FolderPath = $"p{last / 2}" },
            default, (SessionDetailSection)(last % 2));
        Assert.Equal(expected, source.Reads.Sum());
        await loader.LoadAsync(source.Session with { Id = "s0", FolderPath = "p0" }, default);
        Assert.Equal(expected + 1, source.Reads.Sum());
    }

    [Fact]
    public async Task ChangingSectionInputsDuringRead_DoesNotCacheTheResult()
    {
        var source = new SectionSource();
        source.OnRead[1] = _ => source.Versions[1]++;
        var loader = new SessionDetailsLoader(source);
        await loader.LoadAsync(source.Session, default, SessionDetailSection.Checkpoints);
        await loader.LoadAsync(source.Session, default, SessionDetailSection.Checkpoints);
        Assert.Equal([0, 2, 0], source.Reads);
    }

    private static DetailsViewModel Create(SectionSource source) =>
        new(source, new MockResumeLauncher(), new MockClipboardService());

    // ASSUMPTION: independent counters model expensive reads and file-version probes,
    // so tests detect eager work even when the resulting collections would be empty.
    private sealed class SectionSource : ISessionDataSource
    {
        public SessionInfo Session { get; } = new()
        {
            Id = "session", FolderName = "session", FolderPath = "synthetic-section-session", IsEnriched = true,
        };
        public int[] Reads { get; } = new int[3];
        public int[] VersionReads { get; } = new int[2];
        public int[] Versions { get; } = new int[2];
        public Action<SessionInfo>?[] OnRead { get; } = new Action<SessionInfo>?[2];
        public bool EmptyCheckpoints { get; set; }
        public IReadOnlyList<SessionInfo> LoadAll() => [Session];
        public IReadOnlyList<SessionInfo> LoadCheap() => [Session];
        public SessionInfo EnrichOne(SessionInfo session) => session;
        public SessionInfo EnrichWithEvents(SessionInfo session)
        {
            Interlocked.Increment(ref Reads[0]);
            OnRead[0]?.Invoke(session);
            return session with { Start = new SessionStartInfo { Model = session.Id } };
        }
        public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session)
        {
            Interlocked.Increment(ref Reads[1]);
            OnRead[1]?.Invoke(session);
            return EmptyCheckpoints ? [] : [new() { Number = 1, Title = session.Id, Timestamp = DateTimeOffset.UnixEpoch }];
        }
        public SessionTodosResult ReadTodos(SessionInfo session, CancellationToken token = default)
        {
            Interlocked.Increment(ref Reads[2]);
            return new();
        }
        public string GetDetailsVersion(SessionInfo session) => Version(0);
        public string GetCheckpointsVersion(SessionInfo session) => Version(1);
        private string Version(int section)
        {
            Interlocked.Increment(ref VersionReads[section]);
            return Versions[section].ToString();
        }
    }
}
