using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class TodosViewModelTests
{
    [Fact]
    public async Task DetailsSelectionAndRefresh_NeverReadTodos()
    {
        var source = new TodoSource();
        var details = CreateDetails(source);
        details.Load(source.Session);
        await details.CurrentLoad;
        details.Load(source.Session with { CustomName = "Refreshed" }, refresh: true);
        await details.CurrentLoad;
        Assert.Equal(0, source.Calls);
        Assert.Null(details.Todos.Result);

        using var main = new MainViewModel(source, new NullSessionWatcher(), details,
            new SettingsService(path: null), new NotesService(dir: null), new InlineUiDispatcher());
        await main.LoadCommand.ExecuteAsync(null);
        await details.CurrentLoad;
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task OpeningReopeningAndRefresh_EachReadOneFreshSnapshot()
    {
        var source = new TodoSource();
        var details = CreateDetails(source);
        details.Load(source.Session);
        Assert.True(details.IsDetailsTabSelected);
        Assert.False(details.IsAgentTasksTabSelected);
        details.SelectedTabIndex = 1;
        await details.Todos.CurrentLoad;
        Assert.False(details.IsDetailsTabSelected);
        Assert.True(details.IsAgentTasksTabSelected);
        Assert.Equal("Read 1", Assert.Single(details.Todos.Items).Description);

        details.SelectedTabIndex = 1;
        await details.Todos.CurrentLoad;
        Assert.Equal(1, source.Calls);

        details.SelectedTabIndex = 0;
        Assert.False(details.Todos.RefreshCommand.CanExecute(null));
        details.SelectedTabIndex = 1;
        await details.Todos.CurrentLoad;
        Assert.Equal("Read 2", Assert.Single(details.Todos.Items).Description);

        await details.Todos.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Read 3", Assert.Single(details.Todos.Items).Description);
        Assert.Equal(3, source.Calls);
        await details.CurrentLoad;
    }

    [Fact]
    public async Task SameSessionMetadataAndGlobalRefresh_KeepTabAndSnapshotWithoutReading()
    {
        var source = new TodoSource();
        var details = CreateDetails(source);
        details.Load(source.Session);
        details.SelectedTabIndex = 1;
        await details.Todos.CurrentLoad;
        var result = details.Todos.Result;
        details.Load(source.Session with { CustomName = "New title" }, refresh: true);
        await details.CurrentLoad;
        Assert.Equal(1, details.SelectedTabIndex);
        Assert.Same(result, details.Todos.Result);

        using var main = new MainViewModel(source, new NullSessionWatcher(), details,
            new SettingsService(path: null), new NotesService(dir: null), new InlineUiDispatcher());
        main.SelectedSession = source.Session;
        await main.LoadCommand.ExecuteAsync(null);
        await details.CurrentLoad;
        Assert.Equal(1, details.SelectedTabIndex);
        Assert.Equal(1, source.Calls);
        Assert.Same(result, details.Todos.Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedSessionIdOrPath_ResetsTabAndDoesNotRead(bool changeId)
    {
        var source = new TodoSource();
        var details = CreateDetails(source);
        details.Load(source.Session);
        details.SelectedTabIndex = 1;
        await details.Todos.CurrentLoad;
        details.Load(changeId ? source.Session with { Id = "next" } : source.Session with { FolderPath = "next" });
        await details.CurrentLoad;
        Assert.Equal(0, details.SelectedTabIndex);
        Assert.Null(details.Todos.Result);
        Assert.Empty(details.Todos.CountsText);
        Assert.Equal(1, source.Calls);
        details.SelectedTabIndex = 1;
        await details.Todos.CurrentLoad;
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingTabOrClearingSelection_CancelsObsoletePublication(bool clearSelection)
    {
        var source = new TodoSource();
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Read = (_, _) =>
        {
            entered.TrySetResult();
            if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reader was not released.");
            return new() { Todos = [new() { Title = "Obsolete" }] };
        };
        var details = CreateDetails(source);
        try
        {
            details.Load(source.Session);
            details.SelectedTabIndex = 1;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(details.Todos.IsLoading);
            Assert.False(details.Todos.RefreshCommand.CanExecute(null));
            Task old = details.Todos.CurrentLoad;
            if (clearSelection) details.Load(null);
            else details.SelectedTabIndex = 0;
            gate.Set();
            await old;
            Assert.False(details.Todos.IsLoading);
            Assert.Null(details.Todos.Result);
            Assert.Empty(details.Todos.Items);
            Assert.Empty(details.Todos.CountsText);
            await details.CurrentLoad;
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task SupersededReads_AreSerializedAndCannotPublishIntoNewSelection()
    {
        var source = new TodoSource();
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0;
        source.Read = (session, _) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try
            {
                if (session.Id == source.Session.Id)
                {
                    entered.TrySetResult();
                    if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reader was not released.");
                }
                return new() { Todos = [new() { Title = session.Id }] };
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var details = CreateDetails(source);
        try
        {
            details.Load(source.Session);
            details.SelectedTabIndex = 1;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task old = details.Todos.CurrentLoad;
            details.Load(source.Session with { Id = "next" });
            details.SelectedTabIndex = 1;
            gate.Set();
            await Task.WhenAll(old, details.Todos.CurrentLoad, details.CurrentLoad);
            Assert.Equal("next", Assert.Single(details.Todos.Items).Title);
            Assert.Equal(1, maximum);
            Assert.Equal(2, source.Calls);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task CountsIncludeDoneUnknownAndMissingStatuses_WithoutReorderingRows()
    {
        var source = new TodoSource
        {
            Read = (_, _) => new()
            {
                Todos = [
                    new() { Title = "First", Status = "done" },
                    new() { Title = "Second", Status = "future" },
                    new() { Title = "Third", Status = "" },
                    new() { Title = "Fourth", Status = " " },
                    new() { Title = "Fifth", Status = "done" },
                ],
                MissingFields = ["description"],
            },
        };
        var vm = new TodosViewModel(source);
        vm.SetSession(source.Session);
        vm.SetActive(true);
        await vm.CurrentLoad;
        Assert.Equal("5 total | done: 2 | future: 1 | (No status): 2", vm.CountsText);
        Assert.Equal(["First", "Second", "Third", "Fourth", "Fifth"], vm.Items.Select(todo => todo.Title));
        Assert.Contains("description", vm.Warning);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task FailureClearsOldRowsAndCounts_AndRefreshCanRetry()
    {
        var source = new TodoSource();
        var vm = new TodosViewModel(source);
        vm.SetSession(source.Session);
        vm.SetActive(true);
        await vm.CurrentLoad;
        Assert.Single(vm.Items);
        source.Read = (_, _) => throw new IOException("Synthetic I/O failure");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(SessionTodosStatus.Unavailable, vm.Result!.Status);
        Assert.Contains("Synthetic I/O failure", vm.Message);
        Assert.Empty(vm.Items);
        Assert.Empty(vm.CountsText);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        source.Read = (_, _) => new();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Copilot has not recorded any tasks for this session.", vm.Message);
        Assert.Equal("0 total", vm.CountsText);
    }

    [Theory]
    [InlineData(SessionTodosStatus.MissingDatabase)]
    [InlineData(SessionTodosStatus.MissingTable)]
    [InlineData(SessionTodosStatus.UnsupportedSchema)]
    [InlineData(SessionTodosStatus.Unavailable)]
    public async Task NonSuccessStates_KeepTheirMessageAndDoNotClaimZeroTodos(SessionTodosStatus status)
    {
        var source = new TodoSource { Read = (_, _) => new() { Status = status, Message = "Unavailable source" } };
        var vm = new TodosViewModel(source);
        vm.SetSession(source.Session);
        vm.SetActive(true);
        await vm.CurrentLoad;
        Assert.Equal("Unavailable source", vm.Message);
        Assert.Empty(vm.CountsText);
        Assert.Null(vm.Warning);
    }

    [Fact]
    public async Task LargeTodoTable_IsNotTruncated()
    {
        var rows = Enumerable.Range(0, 10000).Select(i => new SessionTodo { Title = $"Task {i}", Status = "pending" }).ToArray();
        var source = new TodoSource { Read = (_, _) => new() { Todos = rows } };
        var vm = new TodosViewModel(source);
        vm.SetSession(source.Session);
        vm.SetActive(true);
        await vm.CurrentLoad;
        Assert.Same(rows, vm.Items);
        Assert.Equal("10000 total | pending: 10000", vm.CountsText);
    }

    private static DetailsViewModel CreateDetails(TodoSource source) =>
        new(source, new MockResumeLauncher(), new MockClipboardService());

    private sealed class TodoSource : ISessionDataSource
    {
        public SessionInfo Session { get; } = new()
        {
            Id = "session", FolderName = "session", FolderPath = "synthetic-session", IsEnriched = true, HasEvents = true,
            Workspace = new WorkspaceMetadata { Name = "Synthetic", UpdatedAt = DateTimeOffset.Now },
        };
        public int Calls;
        public Func<SessionInfo, CancellationToken, SessionTodosResult>? Read;
        public IReadOnlyList<SessionInfo> LoadAll() => [Session];
        public IReadOnlyList<SessionInfo> LoadCheap() => [Session];
        public SessionInfo EnrichOne(SessionInfo session) => session;
        public SessionInfo EnrichWithEvents(SessionInfo session) => session;
        public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) => [];
        public IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId) => [];
        public SessionTodosResult ReadTodos(SessionInfo session, CancellationToken token = default)
        {
            int call = Interlocked.Increment(ref Calls);
            return Read?.Invoke(session, token) ?? new()
            {
                Todos = [new() { Title = "Task", Description = $"Read {call}", Status = "pending" }],
            };
        }
    }
}
