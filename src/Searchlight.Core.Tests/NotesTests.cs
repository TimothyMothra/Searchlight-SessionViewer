using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Exercises the per-session notes feature: <see cref="NotesService"/> sidecar
/// round-trips (in-memory and on-disk) and the <see cref="MainViewModel"/>
/// integration that loads a session's note on selection, autosaves edits
/// (flushed on selection change / dispose), and toggles the optional Notes pane.
/// Notes live only in the app's own folder — never in <c>~/.copilot</c>.
/// </summary>
public sealed class NotesTests
{
    // ---- NotesService seam ----

    [Fact]
    public void NotesService_InMemory_RoundTrips()
    {
        var notes = new NotesService(dir: null);

        Assert.Equal(string.Empty, notes.Read("s1"));
        Assert.False(notes.HasNote("s1"));

        notes.Write("s1", "hello");

        Assert.Equal("hello", notes.Read("s1"));
        Assert.True(notes.HasNote("s1"));
    }

    [Fact]
    public void NotesService_InMemory_EmptyWriteRemovesNote()
    {
        var notes = new NotesService(dir: null);
        notes.Write("s1", "hello");

        notes.Write("s1", "   ");

        Assert.Equal(string.Empty, notes.Read("s1"));
        Assert.False(notes.HasNote("s1"));
    }

    [Fact]
    public void NotesService_OnDisk_RoundTripsAcrossInstances_AndDeletes()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "SearchlightNotesTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            new NotesService(dir).Write("abc", "disk note");

            // A fresh instance over the same folder reads the persisted sidecar.
            Assert.Equal("disk note", new NotesService(dir).Read("abc"));
            Assert.True(new NotesService(dir).HasNote("abc"));

            new NotesService(dir).Write("abc", string.Empty);
            Assert.False(new NotesService(dir).HasNote("abc"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- MainViewModel integration ----

    private static MainViewModel BuildViewModel(out NotesService notes, out SettingsService settings)
    {
        var dataSource = new MockSessionDataSource();
        var resume = new MockResumeLauncher();
        var watcher = new NullSessionWatcher();
        var details = new DetailsViewModel(dataSource, resume, new MockClipboardService());
        settings = new SettingsService(path: null);
        notes = new NotesService(dir: null);
        var dispatcher = new InlineUiDispatcher();

        return new MainViewModel(dataSource, watcher, details, settings, notes, dispatcher);
    }

    [Fact]
    public async Task SelectingSession_LoadsItsExistingNote()
    {
        MainViewModel vm = BuildViewModel(out NotesService notes, out _);
        await vm.LoadCommand.ExecuteAsync(null);

        SessionInfo target = vm.SessionGroups.SelectMany(g => g).First();
        notes.Write(target.Id, "preexisting");

        vm.SelectedSession = target;

        Assert.Equal("preexisting", vm.SelectedNotes);
    }

    [Fact]
    public async Task EditingNotes_FlushesToPreviousSession_OnSelectionChange()
    {
        MainViewModel vm = BuildViewModel(out NotesService notes, out _);
        await vm.LoadCommand.ExecuteAsync(null);

        var rows = vm.SessionGroups.SelectMany(g => g).ToList();
        SessionInfo first = rows[0];
        SessionInfo second = rows.First(s => s.Id != first.Id);

        vm.SelectedSession = first;
        vm.SelectedNotes = "typed note";

        // Switching selection persists the pending edit for the previous session
        // and loads the (absent) note for the new one.
        vm.SelectedSession = second;

        Assert.Equal("typed note", notes.Read(first.Id));
        Assert.Equal(string.Empty, vm.SelectedNotes);
    }

    [Fact]
    public async Task Dispose_FlushesPendingNote()
    {
        MainViewModel vm = BuildViewModel(out NotesService notes, out _);
        await vm.LoadCommand.ExecuteAsync(null);
        SessionInfo target = vm.SessionGroups.SelectMany(g => g).First();

        vm.SelectedSession = target;
        vm.SelectedNotes = "on dispose";
        vm.Dispose();

        Assert.Equal("on dispose", notes.Read(target.Id));
    }

    [Fact]
    public void ToggleNotesPane_FlipsVisibility_AndPersistsToSettings()
    {
        MainViewModel vm = BuildViewModel(out _, out SettingsService settings);
        Assert.False(vm.IsNotesPaneVisible);

        vm.ToggleNotesPaneCommand.Execute(null);
        Assert.True(vm.IsNotesPaneVisible);
        Assert.True(settings.Current.NotesPaneVisible);

        vm.ToggleNotesPaneCommand.Execute(null);
        Assert.False(vm.IsNotesPaneVisible);
        Assert.False(settings.Current.NotesPaneVisible);
    }

    [Fact]
    public void NotesPaneToggleLabel_TracksVisibility()
    {
        MainViewModel vm = BuildViewModel(out _, out _);

        Assert.False(vm.IsNotesPaneVisible);
        Assert.Equal("Show notes", vm.NotesPaneToggleLabel);

        vm.ToggleNotesPaneCommand.Execute(null);
        Assert.Equal("Hide notes", vm.NotesPaneToggleLabel);
    }

    [Fact]
    public async Task SelectingSession_SetsSelectedHasNote_FromExistingNote()
    {
        MainViewModel vm = BuildViewModel(out NotesService notes, out _);
        await vm.LoadCommand.ExecuteAsync(null);

        var rows = vm.SessionGroups.SelectMany(g => g).ToList();
        SessionInfo withNote = rows[0];
        SessionInfo without = rows.First(s => s.Id != withNote.Id);
        notes.Write(withNote.Id, "has one");

        vm.SelectedSession = withNote;
        Assert.True(vm.SelectedHasNote);

        vm.SelectedSession = without;
        Assert.False(vm.SelectedHasNote);
    }

    [Fact]
    public async Task EditingNote_TogglesHasNoteFlags_OnEmptyBoundary()
    {
        MainViewModel vm = BuildViewModel(out _, out _);
        await vm.LoadCommand.ExecuteAsync(null);
        SessionInfo target = vm.SessionGroups.SelectMany(g => g).First();

        vm.SelectedSession = target;
        Assert.False(vm.SelectedHasNote);
        Assert.False(target.HasNote);

        vm.SelectedNotes = "now has content";
        Assert.True(vm.SelectedHasNote);
        Assert.True(target.HasNote);

        vm.SelectedNotes = "   ";
        Assert.False(vm.SelectedHasNote);
        Assert.False(target.HasNote);
    }

    [Fact]
    public async Task ApplyFilter_SetsHasNote_ForSessionsWithNotesOnDisk()
    {
        MainViewModel vm = BuildViewModel(out NotesService notes, out _);
        await vm.LoadCommand.ExecuteAsync(null);

        var rows = vm.SessionGroups.SelectMany(g => g).ToList();
        SessionInfo noted = rows[0];
        SessionInfo plain = rows.First(s => s.Id != noted.Id);
        notes.Write(noted.Id, "stored");

        // Re-run grouping (e.g. via a refresh) so the row flags are recomputed.
        await vm.LoadCommand.ExecuteAsync(null);

        SessionInfo notedAfter = vm.SessionGroups.SelectMany(g => g).First(s => s.Id == noted.Id);
        SessionInfo plainAfter = vm.SessionGroups.SelectMany(g => g).First(s => s.Id == plain.Id);
        Assert.True(notedAfter.HasNote);
        Assert.False(plainAfter.HasNote);
    }
}
