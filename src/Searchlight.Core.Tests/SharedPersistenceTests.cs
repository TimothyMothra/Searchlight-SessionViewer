using System.Text.Json.Nodes;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SharedPersistenceTests : IDisposable
{
    // Tests only create synthetic state beneath the checkout, never user storage.
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), $"shared-state-test-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_root, "shared", "settings.json");
    private string NotesPath => Path.Combine(_root, "shared", "notes");
    private SharedStatePaths Paths => new(Path.Combine(_root, "shared"), Path.Combine(_root, "legacy"));

    [Fact]
    public void ResumeTemplateMigratesPersistsAndMergesAcrossInstances()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{"AppendYolo":true}""");
        var first = new SettingsService(SettingsPath);
        var second = new SettingsService(SettingsPath);
        Assert.False(first.Current.UseCustomResumeCommand);
        Assert.Equal(ResumeCommandBuilder.DefaultTemplate, first.Current.CustomResumeCommand);
        Assert.EndsWith("--yolo", ResumeCommandBuilder.Preview(first.Current));
        first.Current.CustomResumeCommand = "echo {sessionId}";
        first.Current.UseCustomResumeCommand = true;
        second.Current.HideUnnamedSessions = true;
        Assert.True(second.Reload());
        Assert.True(second.Current.UseCustomResumeCommand);
        Assert.Equal("echo {sessionId}", second.Current.CustomResumeCommand);
        Assert.True(second.Current.AppendYolo);
        Assert.True(first.Reload());
        Assert.True(first.Current.HideUnnamedSessions);
        var restarted = new SettingsService(SettingsPath);
        Assert.Equal("echo 00000000-0000-0000-0000-000000000000", ResumeCommandBuilder.Preview(restarted.Current));
    }

    [Fact]
    public void Migration_CopiesWithoutOverwrites_AndNeverResurrectsDeletedNotes()
    {
        string legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(Path.Combine(legacy, "notes"));
        Directory.CreateDirectory(NotesPath);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), """{"AppendYolo":true}""");
        File.WriteAllText(Path.Combine(legacy, "notes", "old.md"), "original");
        File.WriteAllText(Path.Combine(legacy, "notes", "existing.md"), "legacy note");
        File.WriteAllText(Path.Combine(NotesPath, "existing.md"), "shared note");
        Parallel.For(0, 12, _ => Paths.MigrateLegacy());
        Assert.True(new SettingsService(Paths).Current.AppendYolo);
        Assert.Equal("original", File.ReadAllText(Path.Combine(NotesPath, "old.md")));
        Assert.Equal("shared note", File.ReadAllText(Path.Combine(NotesPath, "existing.md")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(legacy, "notes", "old.md")));
        File.Delete(Path.Combine(NotesPath, "old.md"));
        Paths.MigrateLegacy();
        Assert.False(File.Exists(Path.Combine(NotesPath, "old.md")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void Migration_RetriesPartialCopy_AndPreservesExistingSettings()
    {
        Directory.CreateDirectory(Path.Combine(_root, "legacy", "notes"));
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{"RunElevated":true,"Future":"keep"}""");
        File.WriteAllText(Path.Combine(_root, "legacy", "settings.json"), """{"AppendYolo":true}""");
        File.WriteAllText(Path.Combine(_root, "legacy", "notes", "a.md"), "note");
        // Simulate an interrupted copy before the completion marker.
        Directory.CreateDirectory(NotesPath);
        File.WriteAllText(Path.Combine(NotesPath, "a.md"), "already copied");
        Paths.MigrateLegacy();
        Assert.Equal("""{"RunElevated":true,"Future":"keep"}""", File.ReadAllText(SettingsPath));
        Assert.Equal("already copied", File.ReadAllText(Path.Combine(NotesPath, "a.md")));
    }

    [Fact]
    public void Settings_StaleInstancesMergeScalarsPinsRenamesAndUnknownFields()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{"Future":{"nested":[1,2]},"PinnedSessionIds":["old"],"CustomSessionNames":{"old":"Old"}}""");
        var first = new SettingsService(SettingsPath);
        var second = new SettingsService(SettingsPath);
        first.Current.AppendYolo = true;
        first.Current.PinnedSessionIds = ["first", "old"];
        first.Current.CustomSessionNames = new() { ["old"] = "Updated", ["first"] = "First" };
        second.Current.UseSharedTerminalWindow = false;
        second.Current.PinnedSessionIds = ["second", .. second.Current.PinnedSessionIds];
        second.Current.CustomSessionNames = new(second.Current.CustomSessionNames) { ["second"] = "Second" };
        var result = new SettingsService(SettingsPath).Current;
        Assert.True(result.AppendYolo);
        Assert.False(result.UseSharedTerminalWindow);
        Assert.Equal(["second", "first", "old"], result.PinnedSessionIds);
        Assert.Equal("Updated", result.CustomSessionNames["old"]);
        Assert.Equal("First", result.CustomSessionNames["first"]);
        Assert.Equal("Second", result.CustomSessionNames["second"]);
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(SettingsPath))!["Future"]!["nested"]![1]!.GetValue<int>());
    }

    [Fact]
    public void Settings_PerIdDeletesPreserveOtherWritersUnrelatedEdits()
    {
        var seed = new SettingsService(SettingsPath);
        seed.Current.PinnedSessionIds = ["old"];
        seed.Current.CustomSessionNames = new() { ["old"] = "Old" };
        var first = new SettingsService(SettingsPath);
        var second = new SettingsService(SettingsPath);
        first.Current.PinnedSessionIds = ["new", "old"];
        first.Current.CustomSessionNames = new() { ["old"] = "Old", ["new"] = "New" };
        second.Current.PinnedSessionIds = [];
        second.Current.CustomSessionNames = new(second.Current.CustomSessionNames.Where(p => p.Key != "old"));
        var result = new SettingsService(SettingsPath).Current;
        Assert.Equal(["new"], result.PinnedSessionIds);
        Assert.Single(result.CustomSessionNames);
        Assert.Equal("New", result.CustomSessionNames["new"]);
    }

    [Fact]
    public void Settings_ParallelInstancesRetainAllIndependentIds()
    {
        var instances = Enumerable.Range(0, 16).Select(_ => new SettingsService(SettingsPath)).ToArray();
        Parallel.For(0, instances.Length, i =>
        {
            instances[i].Current.PinnedSessionIds = [$"id-{i}"];
            instances[i].Current.CustomSessionNames = new(instances[i].Current.CustomSessionNames) { [$"id-{i}"] = $"name-{i}" };
        });
        var result = new SettingsService(SettingsPath).Current;
        Assert.Equal(16, result.PinnedSessionIds.Count);
        Assert.Equal(16, result.CustomSessionNames.Count);
        Assert.All(instances, instance => Assert.Empty(instance.PersistenceNotice));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(SettingsPath)!, "*.pending"));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("null")]
    [InlineData("""{"SchemaVersion":2,"AppendYolo":true}""")]
    [InlineData("""{"SchemaVersion":"1"}""")]
    [InlineData("""{"PinnedSessionIds":null}""")]
    [InlineData("""{"CustomSessionNames":{"x":null}}""")]
    [InlineData("""{"RunElevated":"true"}""")]
    [InlineData("""{"AppendYolo":true,"AppendYolo":false}""")]
    [InlineData("""{"CustomResumeCommand":null}""")]
    public void Settings_CorruptOrUnsupportedStateIsExplicitAndNeverOverwritten(string original)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, original);
        var settings = new SettingsService(SettingsPath);
        Assert.NotEmpty(settings.PersistenceNotice);
        settings.Current.AppendYolo = true;
        Assert.False(settings.Save());
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.True(settings.Current.AppendYolo);
    }

    [Fact]
    public void Settings_ReloadIsGuardedAndPreservesUnsavedChangesAfterFailure()
    {
        var first = new SettingsService(SettingsPath);
        var second = new SettingsService(SettingsPath);
        first.Current.RunElevated = true;
        bool guarded = false;
        second.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.RunElevated)) guarded = second.IsReloading;
        };
        Assert.True(second.Reload());
        Assert.True(guarded);
        Assert.False(second.IsReloading);
        File.WriteAllText(SettingsPath, "{bad");
        second.Current.AppendYolo = true;
        File.WriteAllText(SettingsPath, """{"RunElevated":true,"HideUnnamedSessions":true}""");
        Assert.True(second.Reload());
        Assert.True(second.Current.AppendYolo);
        Assert.True(second.Current.HideUnnamedSessions);
        Assert.True(second.Save());
        Assert.True(new SettingsService(SettingsPath).Current.AppendYolo);
    }

    [Fact]
    public void Settings_ReentrantObserversCanReadSharedNotesWithoutLockingOut()
    {
        var first = new SettingsService(Paths);
        var second = new SettingsService(Paths);
        first.Current.AppendYolo = true;
        var notes = new NotesService(Paths);
        second.Current.PropertyChanged += (_, _) => Assert.Empty(notes.Read("note"));
        Assert.True(second.Reload());
        Assert.Empty(notes.PersistenceNotice);
        first.Current.RunElevated = true;
        second.Current.HideUnnamedSessions = true;
        Assert.Empty(notes.PersistenceNotice);
    }

    [Fact]
    public async Task Settings_CrossProcessLockWaitsAndMergesAfterOtherWriterReleases()
    {
        // Production/Dev are Windows packages. This Windows-only integration uses
        // the OS PowerShell host as an independent cooperating filesystem writer.
        if (!OperatingSystem.IsWindows()) return;
        var settings = new SettingsService(SettingsPath);
        string directory = Path.GetDirectoryName(SettingsPath)!;
        string ready = Path.Combine(_root, "child-ready");
        string release = Path.Combine(_root, "child-release");
        string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $gate = [IO.File]::Open({{Quote(Path.Combine(directory, ".state.lock"))}}, 'OpenOrCreate', 'ReadWrite', 'None')
            try {
                [IO.File]::WriteAllText({{Quote(SettingsPath)}}, '{"RunElevated":true,"Future":"child"}')
                [IO.File]::WriteAllText({{Quote(ready)}}, 'ready')
                while (![IO.File]::Exists({{Quote(release)}})) { Start-Sleep -Milliseconds 20 }
            } finally { $gate.Dispose() }
            """;
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var child = System.Diagnostics.Process.Start(start)!;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(ready) || child.HasExited, TimeSpan.FromSeconds(10)));
            Assert.False(child.HasExited);
            var save = Task.Run(() => settings.Current.AppendYolo = true);
            await Task.Delay(100);
            Assert.False(save.IsCompleted);
            File.WriteAllText(release, "release");
            await save;
            Assert.True(settings.Current.RunElevated);
            Assert.True(new SettingsService(SettingsPath).Current.AppendYolo);
            Assert.Empty(settings.PersistenceNotice);
            Assert.Equal("child", JsonNode.Parse(File.ReadAllText(SettingsPath))!["Future"]!.GetValue<string>());
        }
        finally
        {
            File.WriteAllText(release, "release");
            if (!child.WaitForExit(5000)) child.Kill();
        }
    }

    [Fact]
    public void Notes_ConflictingEditsKeepSharedTextAndPersistentDraft()
    {
        var first = new NotesService(NotesPath);
        var second = new NotesService(NotesPath);
        first.Write("note", "initial");
        Assert.Equal("initial", second.Read("note"));
        first.Write("note", "first edit");
        var ex = Assert.Throws<NotesConflictException>(() => second.Write("note", "second edit"));
        Assert.Equal("first edit", new NotesService(NotesPath).Read("note"));
        Assert.Equal("second edit", File.ReadAllText(ex.RecoveryPath!));
        Assert.NotEmpty(second.PersistenceNotice);
        Assert.Equal(["note"], second.LoadNoteIds());
        Assert.Throws<NotesConflictException>(() => second.Write("note", "third edit"));
        Assert.Equal("third edit", File.ReadAllText(ex.RecoveryPath!));
    }

    [Fact]
    public void Notes_ConcurrentCreateDeleteAndUnobservedOverwriteAreConflicts()
    {
        var first = new NotesService(NotesPath);
        var second = new NotesService(NotesPath);
        first.Read("note");
        second.Read("note");
        first.Write("note", "created");
        Assert.Throws<NotesConflictException>(() => second.Write("note", "also created"));
        Assert.Throws<NotesConflictException>(() => new NotesService(NotesPath).Write("note", "unobserved"));
        second.Read("note");
        first.Write("note", "changed");
        var conflict = Assert.Throws<NotesConflictException>(() => second.Write("note", ""));
        Assert.Equal("", File.ReadAllText(conflict.RecoveryPath!));
        second.Read("note");
        second.Write("note", "");
        Assert.Throws<NotesConflictException>(() => first.Write("note", "stale after delete"));
    }

    [Fact]
    public void Notes_ParallelConflictsPreserveBothDraftsAndExcludeRecoveryFromIndex()
    {
        var first = new NotesService(NotesPath);
        var second = new NotesService(NotesPath);
        first.Write("note", "initial");
        first.Read("note");
        second.Read("note");
        new NotesService(NotesPath).Read("note");
        File.WriteAllText(Path.Combine(NotesPath, "note.md"), "external edit");
        Parallel.Invoke(
            () => Assert.Throws<NotesConflictException>(() => first.Write("note", "first")),
            () => Assert.Throws<NotesConflictException>(() => second.Write("note", "second")));
        Assert.NotEqual(first.LastRecoveryPath, second.LastRecoveryPath);
        Assert.Equal("first", File.ReadAllText(first.LastRecoveryPath!));
        Assert.Equal("second", File.ReadAllText(second.LastRecoveryPath!));
        Assert.Single(first.LoadNoteIds());
        var restarted = new NotesService(NotesPath);
        Assert.Single(restarted.LoadNoteIds());
        Assert.Contains("Recovered note drafts", restarted.PersistenceNotice);
    }

    [Fact]
    public void Notes_InvalidUtf8IsNotSilentlyReplaced()
    {
        Directory.CreateDirectory(NotesPath);
        byte[] invalid = [0xff, 0xfe, 0xfd, 0x41, 0xff];
        File.WriteAllBytes(Path.Combine(NotesPath, "note.md"), invalid);
        var notes = new NotesService(NotesPath);
        Assert.ThrowsAny<Exception>(() => notes.Read("note"));
        Assert.ThrowsAny<Exception>(() => notes.Write("note", "draft"));
        Assert.Equal(invalid, File.ReadAllBytes(Path.Combine(NotesPath, "note.md")));
        Assert.Equal("draft", File.ReadAllText(notes.LastRecoveryPath!));
    }

    [Fact]
    public void Migration_FailedCopyDoesNotMarkCompleteAndCanBeRetried()
    {
        string legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), """{"AppendYolo":true}""");
        File.WriteAllText(Path.Combine(legacy, "notes"), "obstruction");
        Assert.Throws<IOException>(() => Paths.MigrateLegacy());
        Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, ".legacy-copy-v1")));
        File.Delete(Path.Combine(legacy, "notes"));
        Directory.CreateDirectory(Path.Combine(legacy, "notes"));
        File.WriteAllText(Path.Combine(legacy, "notes", "note.md"), "legacy note");
        Paths.MigrateLegacy();
        Assert.Equal("legacy note", File.ReadAllText(Path.Combine(NotesPath, "note.md")));
        Assert.True(new SettingsService(Paths).Current.AppendYolo);
    }

    [Fact]
    public void StorageFailuresAreSurfacedWithoutClaimingRecovery()
    {
        Directory.CreateDirectory(_root);
        string obstruction = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(obstruction, "keep");
        var settings = new SettingsService(Path.Combine(obstruction, "settings.json"));
        Assert.False(settings.Save());
        Assert.NotEmpty(settings.PersistenceNotice);
        var notes = new NotesService(obstruction);
        Assert.ThrowsAny<IOException>(() => notes.Read("note"));
        Assert.ThrowsAny<IOException>(() => notes.Write("note", "draft"));
        Assert.Null(notes.LastRecoveryPath);
        Assert.Contains("Keep this window open", notes.PersistenceNotice);
        Assert.Equal("keep", File.ReadAllText(obstruction));
    }

    private MainViewModel CreateViewModel()
    {
        var source = new MockSessionDataSource();
        return new MainViewModel(source, new NullSessionWatcher(),
            new DetailsViewModel(source, new MockResumeLauncher(), new MockClipboardService()),
            new SettingsService(SettingsPath), new NotesService(NotesPath), new InlineUiDispatcher());
    }

    [Fact]
    public async Task ViewModel_RefreshReloadsSettingsAndCleanSelectedNotes()
    {
        using var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.SessionGroups.SelectMany(g => g).First();
        vm.SelectedSession = row;
        new NotesService(NotesPath).Write(row.Id, "external note");
        var external = new SettingsService(SettingsPath);
        external.Current.PinnedSessionIds = [row.Id];
        external.Current.CustomSessionNames = new() { [row.Id] = "External name" };
        external.Current.NotesPaneVisible = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("external note", vm.SelectedNotes);
        Assert.True(vm.SelectedHasNote);
        Assert.True(vm.SelectedIsPinned);
        Assert.True(vm.SelectedHasCustomName);
        Assert.Equal("External name", vm.SelectedSession!.DisplayName);
        Assert.True(vm.IsNotesPaneVisible);
    }

    [Fact]
    public async Task ViewModel_ConflictDraftSurvivesRefreshSelectionAndDispose()
    {
        var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        var rows = vm.SessionGroups.SelectMany(g => g).Take(2).ToArray();
        vm.SelectedSession = rows[0];
        vm.SelectedNotes = "my unsaved draft";
        new NotesService(NotesPath).Write(rows[0].Id, "other window");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("my unsaved draft", vm.SelectedNotes);
        vm.SelectedSession = rows[1];
        Assert.True(vm.HasPersistenceNotice);
        vm.SelectedSession = rows[0];
        Assert.Equal("my unsaved draft", vm.SelectedNotes);
        Assert.True(vm.TryFlushNotes());
        vm.Dispose();
        Assert.Equal("other window", new NotesService(NotesPath).Read(rows[0].Id));
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(NotesPath, "conflicts")),
            file => File.ReadAllText(file) == "my unsaved draft");
    }

    [Fact]
    public async Task ViewModel_UnrecoverableDraftVetoesDisposeAndCanBeRetried()
    {
        var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedSession = vm.SessionGroups.SelectMany(g => g).First();
        string id = vm.SelectedSession.Id;
        vm.SelectedNotes = "must not be lost";
        Directory.Delete(NotesPath, recursive: true);
        File.WriteAllText(NotesPath, "blocked storage");
        Assert.False(vm.TryFlushNotes());
        Assert.True(vm.HasPersistenceNotice);
        Assert.Throws<IOException>(() => vm.Dispose());
        Assert.Equal("must not be lost", vm.SelectedNotes);
        File.Delete(NotesPath);
        Assert.True(vm.TryFlushNotes());
        vm.Dispose();
        Assert.Equal("must not be lost", new NotesService(NotesPath).Read(id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
