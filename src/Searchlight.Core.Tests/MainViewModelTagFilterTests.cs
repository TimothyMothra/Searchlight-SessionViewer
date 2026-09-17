using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class MainViewModelTagFilterTests
{
    private static SessionInfo Session(string id, string? client, bool inUse) => new()
    {
        Id = id, FolderName = id, FolderPath = $@"C:\synthetic\{id}",
        LastWriteTime = DateTimeOffset.UtcNow,
        Workspace = new() { Name = id, ClientName = client },
        IsInUse = inUse, IsEnriched = true, HasEvents = true,
    };

    private static SessionInfo[] Fixture() =>
    [
        Session("cli-live", "github/cli", true),
        Session("cli-idle", "github/cli", false),
        Session("app-live", "github/autopilot", true),
        Session("app-idle", "github/autopilot", false),
        Session("unknown-live", null, true),
        Session("unknown-idle", null, false),
    ];

    private static MainViewModel Create(Source source, SettingsService? settings = null) =>
        new(source, new NullSessionWatcher(),
            new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService()),
            settings ?? new SettingsService(path: null), new NotesService(dir: null), new InlineUiDispatcher());

    private static string[] Visible(MainViewModel vm) =>
        vm.SessionGroups.SelectMany(g => g).Select(s => s.Id).Order().ToArray();

    [Theory]
    [InlineData(false, false, false, "app-idle,app-live,cli-idle,cli-live,unknown-idle,unknown-live")]
    [InlineData(true, false, false, "app-live,cli-live,unknown-live")]
    [InlineData(false, true, false, "cli-idle,cli-live")]
    [InlineData(false, false, true, "app-idle,app-live")]
    [InlineData(true, true, false, "cli-live")]
    [InlineData(true, false, true, "app-live")]
    [InlineData(false, true, true, "")]
    [InlineData(true, true, true, "")]
    public async Task EveryTagCombination_UsesAnd(bool inUse, bool cli, bool app, string expected)
    {
        using var vm = Create(new Source(Fixture()));
        Assert.False(vm.FilterInUse);
        Assert.False(vm.FilterCli);
        Assert.False(vm.FilterApp);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.FilterInUse = inUse;
        vm.FilterCli = cli;
        vm.FilterApp = app;
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), Visible(vm));
        Assert.Equal(Visible(vm).Length, vm.VisibleCount);
        Assert.Equal(6, vm.TotalCount);
        Assert.Equal(0, vm.HiddenCount);
    }

    [Fact]
    public async Task Tags_CombineWithSearch_AndClearSearchDoesNotClearTags()
    {
        var source = new Source(Fixture());
        using var vm = Create(source);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.FilterInUse = true;
        vm.SearchText = "cli";
        Assert.Equal(["cli-live"], Visible(vm));
        vm.FilterApp = true;
        Assert.Empty(Visible(vm));
        vm.ClearSearchCommand.Execute(null);
        Assert.True(vm.FilterInUse);
        Assert.True(vm.FilterApp);
        Assert.Equal(["app-live"], Visible(vm));
        vm.FilterInUse = false;
        vm.FilterApp = false;
        Assert.Equal(6, vm.VisibleCount);
        Assert.Equal(1, source.CatalogReads);
        Assert.Equal(0, source.EventReads);
        Assert.Equal(0, source.SummaryReads);
    }

    [Fact]
    public async Task PinsAndRenames_DoNotOverrideExplicitTagRequirements()
    {
        var settings = new SettingsService(path: null);
        settings.Current.PinnedSessionIds = ["cli-idle"];
        settings.Current.CustomSessionNames = new() { ["app-idle"] = "Renamed" };
        SessionInfo[] rows = Fixture().Select(s => s.Id.EndsWith("idle")
            ? s with { HasEvents = false } : s).ToArray();
        using var vm = Create(new Source(rows), settings);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Contains("cli-idle", Visible(vm));
        Assert.Contains("app-idle", Visible(vm));
        vm.FilterApp = true;
        vm.FilterInUse = true;
        Assert.Equal(["app-live"], Visible(vm));
        Assert.Equal(1, vm.HiddenCount);
        Assert.Equal("1 hidden (not searched)", vm.HiddenNoticeText);
    }

    [Fact]
    public async Task TogglingTags_PreservesMatchingSelectionWithoutReloadingDetails()
    {
        var source = new Source(Fixture());
        using var vm = Create(source);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSession = source.Sessions.Single(s => s.Id == "cli-live");
        await vm.Details.CurrentLoad;
        int eventReads = source.EventReads;
        vm.FilterCli = true;
        vm.FilterInUse = true;
        await vm.Details.CurrentLoad;
        Assert.Equal("cli-live", vm.SelectedSession?.Id);
        Assert.Equal(eventReads, source.EventReads);
        vm.FilterApp = true;
        Assert.Null(vm.SelectedSession);
        Assert.False(vm.Details.HasSession);
    }

    [Fact]
    public async Task Refresh_ReevaluatesActivity_WithoutResettingTagChoices()
    {
        var source = new Source(Fixture());
        using var vm = Create(source);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.FilterCli = true;
        vm.FilterInUse = true;
        Assert.Equal(["cli-live"], Visible(vm));
        int index = Array.FindIndex(source.Sessions, s => s.Id == "cli-live");
        source.Sessions[index] = source.Sessions[index] with { IsInUse = false };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(Visible(vm));
        Assert.True(vm.FilterCli);
        Assert.True(vm.FilterInUse);
    }

    [Fact]
    public async Task Enrichment_PublishesNewTagMatchesBeforeTheEntireCatalogFinishes()
    {
        // ASSUMPTION: unknown metadata cannot satisfy an enabled tag. Once a
        // background batch establishes a match, it must enter the list promptly.
        SessionInfo[] rows = Enumerable.Range(0, 70)
            .Select(i => Session($"s{i}", i == 30 ? "github/cli" : null, false)).ToArray();
        using var release = new ManualResetEventSlim();
        var source = new Source(rows)
        {
            UsePlaceholders = true,
            OnEnrich = session =>
            {
                if (int.Parse(session.Id.AsSpan(1)) >= 60 && !release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Final batch was not released.");
            },
        };
        using var vm = Create(source);
        vm.FilterCli = true;
        var appeared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.VisibleCount) && vm.VisibleCount == 1)
                appeared.TrySetResult();
        };
        Task loading = vm.LoadCommand.ExecuteAsync(null);
        try
        {
            await appeared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsLoading);
            Assert.Equal(["s30"], Visible(vm));
        }
        finally
        {
            release.Set();
            await loading;
        }
        Assert.Equal(["s30"], Visible(vm));
    }

    private sealed class Source(SessionInfo[] sessions) : ISessionDataSource
    {
        public SessionInfo[] Sessions { get; } = sessions;
        public bool UsePlaceholders { get; init; }
        public Action<SessionInfo>? OnEnrich { get; init; }
        public int CatalogReads, SummaryReads, EventReads;

        public IReadOnlyList<SessionInfo> LoadAll() => Sessions;
        public IReadOnlyList<SessionInfo> LoadCheap()
        {
            CatalogReads++;
            return UsePlaceholders
                ? Sessions.Select(s => s with { Workspace = null, IsInUse = false, IsEnriched = false }).ToArray()
                : Sessions;
        }
        public SessionInfo EnrichOne(SessionInfo session)
        {
            Interlocked.Increment(ref SummaryReads);
            OnEnrich?.Invoke(session);
            return Sessions.Single(s => s.Id == session.Id);
        }
        public SessionInfo EnrichWithEvents(SessionInfo session)
        {
            Interlocked.Increment(ref EventReads);
            return session;
        }
        public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) => [];
        public SessionTodosResult ReadTodos(SessionInfo session, CancellationToken token = default) => new();
    }
}
