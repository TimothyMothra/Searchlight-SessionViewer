using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// VM-level tests for the two session-list hide filters: "hide empty sessions"
/// (no <c>events.jsonl</c>, on by default) and "hide unnamed sessions" (renders as
/// a bare UUID, off by default). Covers the default state, each toggle, the
/// pinned/renamed exemptions, the un-enriched-placeholder guard, the interaction
/// with search, and the hidden-count notice. Drives the real
/// <see cref="MainViewModel"/> over a stub data source — no filesystem, no WinUI.
/// </summary>
public sealed class MainViewModelHideFilterTests
{
    // Fixed anchor keeps grouping deterministic; only membership is asserted.
    private static readonly DateTimeOffset s_now = DateTimeOffset.Now;

    private static SessionInfo Session(
        string id,
        string? name,
        bool hasEvents,
        bool isEnriched = true,
        int minutesAgo = 10) =>
        new()
        {
            Id = id,
            FolderName = id,
            FolderPath = $@"C:\stub\{id}",
            Kind = SessionKind.Project,
            LastWriteTime = s_now.AddMinutes(-minutesAgo),
            Workspace = new WorkspaceMetadata
            {
                Id = id,
                Name = name,
                UpdatedAt = s_now.AddMinutes(-minutesAgo),
            },
            HasEvents = hasEvents,
            IsEnriched = isEnriched,
        };

    /// <summary>
    /// Builds a view-model over the given rows. An optional pre-seeded
    /// <paramref name="settings"/> lets a test stage persisted state (pins, custom
    /// names) before construction reads it.
    /// </summary>
    private static (MainViewModel Vm, SettingsService Settings) BuildViewModel(
        SettingsService? settings,
        params SessionInfo[] sessions)
    {
        settings ??= new SettingsService(path: null);
        var dataSource = new StubSessionDataSource(sessions);
        var details = new DetailsViewModel(dataSource, new MockResumeLauncher(), new MockClipboardService());

        return (
            new MainViewModel(
                dataSource,
                new NullSessionWatcher(),
                details,
                settings,
                new NotesService(dir: null),
                new InlineUiDispatcher()),
            settings);
    }

    private static IReadOnlyList<string> VisibleIds(MainViewModel vm) =>
        [.. vm.SessionGroups.SelectMany(g => g).Select(s => s.Id)];

    [Fact]
    public void HideEmptyDefaultsOn_AndHideUnnamedDefaultsOff()
    {
        var settings = new AppSettings();

        Assert.True(settings.HideEmptySessions);
        Assert.False(settings.HideUnnamedSessions);
    }

    [Fact]
    public void SettingsWrittenBeforeTheseKeysExisted_StillGetTheDefaults()
    {
        // A settings.json from an earlier build has no Hide* keys at all. Absent
        // JSON properties must fall back to the field initializers (empty ON,
        // unnamed OFF) rather than deserializing to false/false.
        string path = Path.Combine(Path.GetTempPath(), $"searchlight-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "UseSharedTerminalWindow": true,
              "RunElevated": true,
              "AppendYolo": true,
              "NotesPaneVisible": false
            }
            """);

        try
        {
            var settings = new SettingsService(path);

            Assert.True(settings.Current.HideEmptySessions);
            Assert.False(settings.Current.HideUnnamedSessions);

            // Pre-existing values must survive the upgrade untouched.
            Assert.True(settings.Current.UseSharedTerminalWindow);
            Assert.True(settings.Current.AppendYolo);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Default_HidesEmptySessionsButKeepsUnnamedOnesWithEvents()
    {
        (MainViewModel vm, _) = BuildViewModel(
            null,
            Session("named-with-events", "Real work", hasEvents: true),
            Session("unnamed-with-events", null, hasEvents: true),
            Session("empty-stub", null, hasEvents: false));

        await vm.LoadCommand.ExecuteAsync(null);

        // The stub is hidden; the unnamed-but-real session survives, because a
        // missing name is not evidence that the conversation is worthless.
        Assert.Equal(["named-with-events", "unnamed-with-events"], [.. VisibleIds(vm).Order()]);
        Assert.Equal(2, vm.VisibleCount);
        Assert.Equal(3, vm.TotalCount);
    }

    [Fact]
    public async Task HiddenCountAndNotice_ReflectFilteredRows()
    {
        (MainViewModel vm, _) = BuildViewModel(
            null,
            Session("keep", "Real work", hasEvents: true),
            Session("stub-1", null, hasEvents: false),
            Session("stub-2", null, hasEvents: false));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.HiddenCount);
        Assert.True(vm.HasHiddenSessions);
        Assert.Equal("2 hidden (not searched)", vm.HiddenNoticeText);
    }

    [Fact]
    public async Task HiddenNotice_IsSuppressedWhenNothingIsHidden()
    {
        (MainViewModel vm, _) = BuildViewModel(
            null,
            Session("keep", "Real work", hasEvents: true));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.HiddenCount);
        Assert.False(vm.HasHiddenSessions);
    }

    [Fact]
    public async Task DisablingHideEmpty_RestoresTheStubsWithoutAReload()
    {
        (MainViewModel vm, SettingsService settings) = BuildViewModel(
            null,
            Session("keep", "Real work", hasEvents: true),
            Session("stub", null, hasEvents: false));

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Single(VisibleIds(vm));

        settings.Current.HideEmptySessions = false;

        Assert.Equal(2, VisibleIds(vm).Count);
        Assert.Equal(0, vm.HiddenCount);
    }

    [Fact]
    public async Task HideUnnamed_WhenEnabled_HidesUuidOnlyRowsThatHaveEvents()
    {
        (MainViewModel vm, SettingsService settings) = BuildViewModel(
            null,
            Session("named", "Real work", hasEvents: true),
            Session("unnamed", null, hasEvents: true));

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(2, VisibleIds(vm).Count);

        settings.Current.HideUnnamedSessions = true;

        Assert.Equal(["named"], VisibleIds(vm));
        Assert.Equal(1, vm.HiddenCount);
    }

    [Fact]
    public async Task PinnedSession_IsNeverHiddenByEitherFilter()
    {
        // Stage the pin before construction so the VM seeds it from settings — the
        // session would otherwise be hidden on the very first paint.
        var settings = new SettingsService(path: null);
        settings.Current.PinnedSessionIds = ["empty-and-unnamed"];

        (MainViewModel vm, _) = BuildViewModel(
            settings,
            Session("named", "Real work", hasEvents: true),
            Session("empty-and-unnamed", null, hasEvents: false));

        await vm.LoadCommand.ExecuteAsync(null);
        settings.Current.HideUnnamedSessions = true;

        Assert.Contains("empty-and-unnamed", VisibleIds(vm));
        Assert.Equal(0, vm.HiddenCount);
    }

    [Fact]
    public async Task RenamedSession_IsNeverHiddenByEitherFilter()
    {
        var settings = new SettingsService(path: null);
        settings.Current.CustomSessionNames = new() { ["empty-and-unnamed"] = "Keep me" };

        (MainViewModel vm, _) = BuildViewModel(
            settings,
            Session("named", "Real work", hasEvents: true),
            Session("empty-and-unnamed", null, hasEvents: false));

        await vm.LoadCommand.ExecuteAsync(null);
        settings.Current.HideUnnamedSessions = true;

        Assert.Contains("empty-and-unnamed", VisibleIds(vm));
        Assert.Equal(0, vm.HiddenCount);
    }

    [Fact]
    public async Task PinningAHiddenSessionAtRuntime_BringsItBack()
    {
        SessionInfo stub = Session("empty-and-unnamed", null, hasEvents: false);
        (MainViewModel vm, _) = BuildViewModel(
            null,
            Session("named", "Real work", hasEvents: true),
            stub);

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.DoesNotContain("empty-and-unnamed", VisibleIds(vm));

        vm.PinCommand.Execute(stub);

        Assert.Contains("empty-and-unnamed", VisibleIds(vm));
        Assert.Equal(0, vm.HiddenCount);
    }

    [Fact]
    public async Task UnenrichedPlaceholder_IsNeverHidden()
    {
        // A cheap placeholder reads HasEvents=false only because its files have not
        // been inspected yet. Hiding it would make rows flicker out during load.
        (MainViewModel vm, SettingsService settings) = BuildViewModel(
            null,
            Session("placeholder", null, hasEvents: false, isEnriched: false));

        await vm.LoadCommand.ExecuteAsync(null);
        settings.Current.HideUnnamedSessions = true;

        Assert.Equal(["placeholder"], VisibleIds(vm));
        Assert.Equal(0, vm.HiddenCount);
    }

    [Fact]
    public async Task Search_DoesNotResurrectHiddenSessions()
    {
        (MainViewModel vm, _) = BuildViewModel(
            null,
            Session("keep", "Real work", hasEvents: true),
            Session("hidden-stub", null, hasEvents: false));

        await vm.LoadCommand.ExecuteAsync(null);

        // Searching the hidden session's exact id still yields nothing: the hide
        // filters run before the query. The footer notice is what tells the user why.
        vm.SearchText = "hidden-stub";

        Assert.Empty(VisibleIds(vm));
        Assert.True(vm.HasHiddenSessions);
    }

    [Fact]
    public async Task MockFixture_ShowsAllRowsByDefaultAndNineWhenHidingUnnamed()
    {
        // Guards the demo dataset against the default filter: every synthetic row
        // represents a real conversation, so none may be hidden out of the box.
        // Six of the fifteen are deliberately unnamed, which the opt-in filter drops.
        var dataSource = new MockSessionDataSource();
        var details = new DetailsViewModel(dataSource, new MockResumeLauncher(), new MockClipboardService());
        var settings = new SettingsService(path: null);
        var vm = new MainViewModel(
            dataSource,
            new NullSessionWatcher(),
            details,
            settings,
            new NotesService(dir: null),
            new InlineUiDispatcher());

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(15, VisibleIds(vm).Count);
        Assert.Equal(0, vm.HiddenCount);

        settings.Current.HideUnnamedSessions = true;

        Assert.Equal(9, VisibleIds(vm).Count);
        Assert.Equal(6, vm.HiddenCount);
    }
}
