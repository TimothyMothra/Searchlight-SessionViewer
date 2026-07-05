using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// VM-level tests for the Pin feature: pinning floats a session into a dedicated
/// "Pinned" group at the top, sets the transient <see cref="SessionInfo.IsPinned"/>
/// flag, excludes the session from its recency bucket (appears once), and persists
/// the pinned id set through <see cref="SettingsService"/>. Drives the real
/// <see cref="MainViewModel"/> over the mock data source — no filesystem, no WinUI.
/// The locked mock fixture is never mutated (pins are VM/settings-level state).
/// </summary>
public sealed class MainViewModelPinTests
{
    private static (MainViewModel Vm, SettingsService Settings) BuildViewModel()
    {
        var dataSource = new MockSessionDataSource();
        var resume = new MockResumeLauncher();
        var watcher = new NullSessionWatcher();
        var details = new DetailsViewModel(dataSource, resume, new MockClipboardService());
        var settings = new SettingsService();
        var dispatcher = new InlineUiDispatcher();

        return (new MainViewModel(dataSource, watcher, details, settings, dispatcher), settings);
    }

    private static SessionInfo FirstRow(MainViewModel vm) =>
        vm.SessionGroups.SelectMany(g => g).First();

    [Fact]
    public async Task Pin_PrependsPinnedGroupAndFlagsSession()
    {
        (MainViewModel vm, _) = BuildViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        SessionInfo target = FirstRow(vm);

        vm.PinCommand.Execute(target);

        SessionGroup first = vm.SessionGroups[0];
        Assert.Equal("Pinned", first.Key);
        Assert.Contains(target, first);
        Assert.True(target.IsPinned);
    }

    [Fact]
    public async Task Pin_KeepsTotalCountAndShowsSessionOnce()
    {
        (MainViewModel vm, _) = BuildViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        SessionInfo target = FirstRow(vm);

        vm.PinCommand.Execute(target);

        // Pinning must not duplicate the row: it moves from its recency bucket into
        // the Pinned group, so the flattened total stays at the fixture's 15.
        Assert.Equal(15, vm.SessionGroups.Sum(g => g.Count));
        int occurrences = vm.SessionGroups.SelectMany(g => g).Count(s => s.Id == target.Id);
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task Pin_PersistsIdToSettings()
    {
        (MainViewModel vm, SettingsService settings) = BuildViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        SessionInfo target = FirstRow(vm);

        vm.PinCommand.Execute(target);

        Assert.Contains(target.Id, settings.Current.PinnedSessionIds);
    }

    [Fact]
    public async Task Unpin_RemovesGroupFlagAndPersistedId()
    {
        (MainViewModel vm, SettingsService settings) = BuildViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        SessionInfo target = FirstRow(vm);
        vm.PinCommand.Execute(target);

        vm.UnpinCommand.Execute(target);

        Assert.DoesNotContain(vm.SessionGroups, g => g.Key == "Pinned");
        Assert.False(target.IsPinned);
        Assert.DoesNotContain(target.Id, settings.Current.PinnedSessionIds);
        Assert.Equal(15, vm.SessionGroups.Sum(g => g.Count));
    }

    [Fact]
    public async Task Pin_IsIdempotent()
    {
        (MainViewModel vm, SettingsService settings) = BuildViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        SessionInfo target = FirstRow(vm);

        vm.PinCommand.Execute(target);
        vm.PinCommand.Execute(target);

        Assert.Single(settings.Current.PinnedSessionIds);
        Assert.Equal(1, vm.SessionGroups[0].Count(s => s.Id == target.Id));
    }
}
