using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// End-to-end grouping test: wires a real <see cref="MainViewModel"/> over the mock
/// data source (no filesystem, no WinUI) and drives its load pipeline to prove the
/// full-fixture rows land in contiguous recency buckets. Demonstrates the app is
/// testable through the same DI seams the WinUI host uses.
/// </summary>
public sealed class MainViewModelGroupingTests
{
    private static MainViewModel BuildViewModel()
    {
        var dataSource = new MockSessionDataSource();
        var resume = new MockResumeLauncher();
        var watcher = new NullSessionWatcher();
        var details = new DetailsViewModel(dataSource, resume, new MockClipboardService());
        var settings = new SettingsService(path: null);
        var dispatcher = new InlineUiDispatcher();

        return new MainViewModel(dataSource, watcher, details, settings, new NotesService(dir: null), dispatcher);
    }

    [Fact]
    public async Task LoadCommand_PublishesAllFifteenRowsIntoGroups()
    {
        MainViewModel vm = BuildViewModel();

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(15, vm.TotalCount);
        Assert.Equal(15, vm.VisibleCount);
        Assert.Equal(15, vm.SessionGroups.Sum(g => g.Count));
        Assert.NotEmpty(vm.SessionGroups);
    }

    [Fact]
    public async Task LoadCommand_ProducesTheExpectedRecencyBuckets()
    {
        MainViewModel vm = BuildViewModel();

        await vm.LoadCommand.ExecuteAsync(null);

        var keys = vm.SessionGroups.Select(g => g.Key).ToArray();

        // The fixture seeds one session in each doubling window (≤2h…≤32h).
        Assert.Contains("Last 2 hours", keys);
        Assert.Contains("Last 4 hours", keys);
        Assert.Contains("Last 8 hours", keys);
        Assert.Contains("Last 16 hours", keys);
        Assert.Contains("Last 32 hours", keys);
        // Plus at least one absolute-date header for the >32h sessions.
        Assert.Contains(keys, k => !k.StartsWith("Last ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadCommand_OrdersGroupsNewestFirst()
    {
        MainViewModel vm = BuildViewModel();

        await vm.LoadCommand.ExecuteAsync(null);

        // Every row's UpdatedAt should be non-increasing across the flattened groups,
        // confirming the newest-first ordering that keeps buckets contiguous.
        var updates = vm.SessionGroups.SelectMany(g => g).Select(s => s.UpdatedAt).ToArray();
        for (int i = 1; i < updates.Length; i++)
        {
            Assert.True(updates[i] <= updates[i - 1], $"row {i} is newer than row {i - 1}");
        }
    }

    [Fact]
    public async Task Filtering_ByBranch_NarrowsTheVisibleRows()
    {
        MainViewModel vm = BuildViewModel();
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = "payment-retry";

        Assert.Equal(1, vm.VisibleCount);
        Assert.Equal(15, vm.TotalCount);
        Assert.Single(vm.SessionGroups.SelectMany(g => g));
    }
}
