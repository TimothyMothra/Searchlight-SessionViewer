using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Exercises the manual session-rename feature: the <see cref="SessionInfo.CustomName"/>
/// projection precedence and the <see cref="MainViewModel"/> rename/reset commands that
/// persist overrides into <c>AppSettings.CustomSessionNames</c> (never mutating
/// <c>~/.copilot</c>). Mirrors the pin feature's override-in-settings pattern.
/// </summary>
public sealed class RenameTests
{
    private const string SampleId = "12345678-0000-4000-8000-000000000042";

    private static SessionInfo Session(string? name = null, string? customName = null) =>
        new()
        {
            Id = SampleId,
            FolderName = SampleId,
            FolderPath = $@"C:\x\{SampleId}",
            LastWriteTime = DateTimeOffset.UnixEpoch,
            Workspace = name is null ? null : new WorkspaceMetadata { Id = SampleId, Name = name },
            CustomName = customName,
        };

    [Fact]
    public void DisplayName_PrefersCustomName_OverWorkspaceName() =>
        Assert.Equal("My Rename", Session(name: "Workspace Name", customName: "My Rename").DisplayName);

    [Fact]
    public void DisplayName_PrefersCustomName_OverUuidFallback() =>
        Assert.Equal("My Rename", Session(name: null, customName: "My Rename").DisplayName);

    [Fact]
    public void DisplayName_IgnoresWhitespaceCustomName_FallsBackToWorkspaceName() =>
        Assert.Equal("Workspace Name", Session(name: "Workspace Name", customName: "   ").DisplayName);

    private static MainViewModel BuildViewModel(out SettingsService settings)
    {
        var dataSource = new MockSessionDataSource();
        var resume = new MockResumeLauncher();
        var watcher = new NullSessionWatcher();
        var details = new DetailsViewModel(dataSource, resume, new MockClipboardService());
        settings = new SettingsService(path: null);
        var dispatcher = new InlineUiDispatcher();

        return new MainViewModel(dataSource, watcher, details, settings, new NotesService(dir: null), dispatcher);
    }

    [Fact]
    public async Task RenameCommand_PersistsOverride_AndRowReflectsIt()
    {
        MainViewModel vm = BuildViewModel(out SettingsService settings);
        await vm.LoadCommand.ExecuteAsync(null);

        SessionInfo target = vm.SessionGroups.SelectMany(g => g).First();
        vm.SelectedSession = target;
        vm.RenameDraft = "Custom Title";
        vm.RenameCommand.Execute(null);

        Assert.Equal("Custom Title", settings.Current.CustomSessionNames[target.Id]);
        Assert.True(vm.SelectedHasCustomName);

        SessionInfo renamed = vm.SessionGroups.SelectMany(g => g).First(s => s.Id == target.Id);
        Assert.Equal("Custom Title", renamed.DisplayName);
        Assert.Equal("Custom Title", renamed.CustomName);
    }

    [Fact]
    public async Task RenameCommand_EmptyDraft_RemovesOverride()
    {
        MainViewModel vm = BuildViewModel(out SettingsService settings);
        await vm.LoadCommand.ExecuteAsync(null);

        SessionInfo target = vm.SessionGroups.SelectMany(g => g).First();
        vm.SelectedSession = target;
        vm.RenameDraft = "Temp";
        vm.RenameCommand.Execute(null);
        Assert.True(settings.Current.CustomSessionNames.ContainsKey(target.Id));

        vm.SelectedSession = vm.SessionGroups.SelectMany(g => g).First(s => s.Id == target.Id);
        vm.RenameDraft = "   ";
        vm.RenameCommand.Execute(null);

        Assert.False(settings.Current.CustomSessionNames.ContainsKey(target.Id));
        Assert.False(vm.SelectedHasCustomName);
    }

    [Fact]
    public async Task ResetNameCommand_RemovesOverride_AndRevertsDisplayName()
    {
        MainViewModel vm = BuildViewModel(out SettingsService settings);
        await vm.LoadCommand.ExecuteAsync(null);

        SessionInfo target = vm.SessionGroups.SelectMany(g => g).First();
        string autoName = target.DisplayName;
        vm.SelectedSession = target;
        vm.RenameDraft = "Renamed";
        vm.RenameCommand.Execute(null);
        Assert.True(settings.Current.CustomSessionNames.ContainsKey(target.Id));

        vm.SelectedSession = vm.SessionGroups.SelectMany(g => g).First(s => s.Id == target.Id);
        vm.ResetNameCommand.Execute(null);

        Assert.False(settings.Current.CustomSessionNames.ContainsKey(target.Id));
        Assert.False(vm.SelectedHasCustomName);
        SessionInfo reverted = vm.SessionGroups.SelectMany(g => g).First(s => s.Id == target.Id);
        Assert.Equal(autoName, reverted.DisplayName);
    }
}
