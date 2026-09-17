using System.Collections.Concurrent;
using Searchlight.Abstractions;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class SessionActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"searchlight-activity-{Guid.NewGuid():N}");
    private readonly FakeProcesses _processes = new();
    private readonly DateTimeOffset _written = DateTimeOffset.UtcNow.AddDays(-2);
    private readonly SessionActivityMonitor _activity;
    private readonly string _folder;

    public SessionActivityTests()
    {
        _folder = Path.Combine(_root, "synthetic-session");
        Directory.CreateDirectory(_folder);
        _activity = new(_processes);
    }

    private string WriteLock(string name = "inuse.123.lock", string content = "123\n")
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, _written.UtcDateTime);
        return path;
    }

    [Fact]
    public void OldLock_WithLiveIdleOwner_IsConfirmedWithoutChangingTheFile()
    {
        string path = WriteLock();
        _processes.Starts[123] = _written.AddSeconds(-1);
        byte[] original = File.ReadAllBytes(path);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        Assert.True(_activity.HasLiveOwner(path));
        Assert.False(_activity.CheckForExitedOwners());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void DeadOrUnverifiableOwner_DoesNotQualify()
    {
        string path = WriteLock();
        Assert.False(_activity.HasLiveOwner(path));
        Assert.False(_activity.CheckForExitedOwners());
    }

    [Theory]
    [InlineData("github/cli")]
    [InlineData("github/autopilot")]
    public void IdleOrBackgroundOwners_QualifyWithoutReadingEventContent(string client)
    {
        WriteLock();
        _processes.Starts[123] = _written.AddSeconds(-1);
        File.WriteAllText(Path.Combine(_folder, "workspace.yaml"), $"client_name: {client}\n");
        string events = Path.Combine(_folder, "events.jsonl");
        File.WriteAllText(events, """{"type":"hook.start","timestamp":"2026-01-01T00:00:00Z","data":{}}""");
        File.SetLastWriteTimeUtc(events, _written.AddDays(-7).UtcDateTime);
        // ASSUMPTION: a quiet/stalled background owner still counts. The badge
        // is not a window count, turn status, or recency heuristic; logs stay lazy.
        using var unreadable = new FileStream(events, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, _activity);
        SessionInfo session = scanner.Scan().Single();
        Assert.Equal(client, session.ClientNameRaw);
        Assert.True(session.IsInUse);
        Assert.True(scanner.ScanCheap().Single().IsInUse);
        Assert.False(_activity.CheckForExitedOwners());
    }

    [Fact]
    public void PidReusedByAnotherCopilotProcess_DoesNotQualify()
    {
        string path = WriteLock();
        _processes.Starts[123] = _written.AddSeconds(1);
        Assert.False(_activity.HasLiveOwner(path));
    }

    [Theory]
    [InlineData("inuse.lock", "123")]
    [InlineData("inuse..lock", "123")]
    [InlineData("inuse.xyz.lock", "123")]
    [InlineData("inuse.0.lock", "0")]
    [InlineData("inuse.-1.lock", "-1")]
    [InlineData("inuse.2147483648.lock", "2147483648")]
    [InlineData("inuse.123.lock", "124")]
    [InlineData("inuse.123.lock", "")]
    [InlineData("inuse.123.lock", "{\"pid\":123}")]
    [InlineData("inuse.123.lock", "123\nxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("other.123.lock", "123")]
    public void InvalidLocks_AreNotEvidence(string name, string content)
    {
        _processes.Starts[123] = _written.AddSeconds(-1);
        Assert.False(_activity.HasLiveOwner(WriteLock(name, content)));
    }

    [Fact]
    public void RemovedOrUnreadableLocks_DoNotKeepAConfirmedOwner()
    {
        string path = WriteLock();
        _processes.Starts[123] = _written.AddSeconds(-1);
        Assert.True(_activity.HasLiveOwner(path));
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(_activity.CheckForExitedOwners());
        Assert.False(_activity.CheckForExitedOwners());
        Assert.True(_activity.HasLiveOwner(path));
        File.Delete(path);
        Assert.True(_activity.CheckForExitedOwners());
        Assert.False(_activity.HasLiveOwner(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedSummary_RechecksOwnerWithoutFilesystemChanges(bool enrichDirectly)
    {
        WriteLock();
        _processes.Starts[123] = _written.AddSeconds(-1);
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, _activity);
        SessionInfo initial = scanner.Scan().Single();
        Assert.True(initial.IsInUse);
        Assert.Same(initial, scanner.ScanCheap().Single());
        DateTime folderTimestamp = Directory.GetLastWriteTimeUtc(_folder);
        _processes.Starts.TryRemove(123, out _);
        SessionInfo refreshed = enrichDirectly ? scanner.EnrichFolder(initial) : scanner.ScanCheap().Single();
        Assert.False(refreshed.IsInUse);
        Assert.True(refreshed.IsEnriched);
        Assert.Equal(folderTimestamp, Directory.GetLastWriteTimeUtc(_folder));
        Assert.Same(refreshed, scanner.ScanCheap().Single());

        // ASSUMPTION: a failed inspection can later recover; a cached negative
        // must not suppress fresh confirmation on an explicit refresh.
        _processes.Starts[123] = _written.AddSeconds(-1);
        Assert.True(scanner.ScanCheap().Single().IsInUse);
    }

    [Fact]
    public void MultipleLocks_TrackAllOwners_AndOnlyNeedOneLiveOwner()
    {
        WriteLock();
        WriteLock("inuse.456.lock", "456\n");
        _processes.Starts[123] = _written.AddSeconds(-1);
        _processes.Starts[456] = _written.AddSeconds(-1);
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, _activity);
        Assert.True(scanner.Scan().Single().IsInUse);
        _processes.Starts.TryRemove(123, out _);
        Assert.True(_activity.CheckForExitedOwners());
        Assert.True(scanner.ScanCheap().Single().IsInUse);
        _processes.Starts.TryRemove(456, out _);
        Assert.True(_activity.CheckForExitedOwners());
        Assert.False(scanner.ScanCheap().Single().IsInUse);
        Assert.False(_activity.CheckForExitedOwners());
    }

    [Fact]
    public async Task Watcher_ReportsOwnerExitWithoutAnyFileEvent()
    {
        string path = WriteLock();
        _processes.Starts[123] = _written.AddSeconds(-1);
        Assert.True(_activity.HasLiveOwner(path));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new SessionWatcher(_activity, _root, 10, 20);
        watcher.Changed += (_, _) => changed.TrySetResult();
        watcher.Start();
        _processes.Starts.TryRemove(123, out _);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(path));
        Assert.False(_activity.HasLiveOwner(path));
    }

    [Theory]
    [InlineData("copilot", 123, true)]
    [InlineData("Copilot", 123, true)]
    [InlineData("copilot.exe.old-123-1789597067327", 123, true)]
    [InlineData("copilot.exe.old-456-1789597067327", 123, false)]
    [InlineData("copilot.exe.old-123-invalid", 123, false)]
    [InlineData("copilot-impostor", 123, false)]
    [InlineData("msedge", 123, false)]
    [InlineData("node", 123, false)]
    public void HostProbe_RecognizesOnlySupportedCopilotNames(string name, int pid, bool expected) =>
        Assert.Equal(expected, CopilotProcessProbe.IsCopilotName(name, pid));

    [Fact]
    public void HostProbe_RejectsTheNonCopilotTestProcess() =>
        Assert.Null(new CopilotProcessProbe().GetStartTimeUtc(Environment.ProcessId));

    [Fact]
    public void Metadata_DescribesConfirmationRatherThanMereLockPresence()
    {
        WriteLock();
        var scanner = new SessionStateScanner(new WorkspaceYamlReader(), _root, _activity);
        var field = SessionMetadata.Create(scanner.Scan().Single())
            .SelectMany(g => g.Fields).Single(f => f.Property == nameof(SessionInfo.IsInUse));
        Assert.Equal("Live lock owner confirmed", field.Label);
        Assert.Equal("No", field.Value);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeProcesses : ICopilotProcessProbe
    {
        public ConcurrentDictionary<int, DateTimeOffset> Starts { get; } = new();
        public DateTimeOffset? GetStartTimeUtc(int processId) =>
            Starts.TryGetValue(processId, out var started) ? started : null;
    }
}
