using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Searchlight.Composition;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class NativeMetadataTests
{
    private static SessionInfo Session() => new()
    {
        Id = "native", FolderName = "native", FolderPath = "synthetic-native", LastWriteTime = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void DisplayAllowlist_CoversEveryParsedWorkspaceAndEventField()
    {
        var groups = SessionMetadata.Create(Session());
        Assert.Equal(typeof(WorkspaceMetadata).GetProperties().Select(p => p.Name).Order(),
            groups.Single(g => g.Title == "Workspace metadata").Fields
                .Concat(groups.Single(g => g.Title == "Overview").Fields
                    .Where(f => f.Property is nameof(WorkspaceMetadata.CreatedAt) or nameof(WorkspaceMetadata.UpdatedAt)))
                .Select(f => f.Property).Order());
        Assert.Equal(typeof(SessionStartInfo).GetProperties().Select(p => p.Name).Order(),
            groups.Single(g => g.Title == "Event metadata").Fields
                .Concat(groups.Single(g => g.Title == "Overview").Fields
                    .Where(f => f.Property is nameof(SessionStartInfo.FirstUserPrompt) or nameof(SessionStartInfo.LastUserPrompt)))
                .Select(f => f.Property).Order());
    }

    [Fact]
    public void Overview_RestoresPrimaryFieldOrder_AndKeepsStorageLast()
    {
        var created = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.FromHours(-7));
        var updated = created.AddHours(1);
        var session = Session() with
        {
            Workspace = new()
            {
                Cwd = "workspace", Branch = "workspace-branch", ClientName = "github/cli",
                CreatedAt = created, UpdatedAt = updated,
            },
            Start = new()
            {
                Cwd = "start", Branch = "start-branch", Model = "model", ReasoningEffort = "high",
                CopilotVersion = "version", FirstUserPrompt = "first", LastUserPrompt = "last",
            },
        };
        var groups = SessionMetadata.Create(session);
        Assert.Equal(["Overview", "Workspace metadata", "Event metadata", "Session storage"],
            groups.Select(g => g.Title));
        var overview = groups[0];
        Assert.Equal(["Session ID", "Folder", "Branch", "Model", "Reasoning", "Version", "Created", "Updated", "First prompt", "Last prompt"],
            overview.Fields.Select(f => f.Label));
        Assert.Equal(["native", "workspace", "workspace-branch", "model", "high", "version",
            created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            "first", "last"], overview.Fields.Select(f => f.Value));
        var fallback = SessionMetadata.Create(session with { Workspace = null })[0];
        Assert.Equal("start", fallback.Fields.Single(f => f.Label == "Folder").Value);
        Assert.Equal("start-branch", fallback.Fields.Single(f => f.Label == "Branch").Value);
        Assert.Single(groups.SelectMany(g => g.Fields), f => f.Label == "Session ID");
        Assert.DoesNotContain(groups.Single(g => g.Title == "Session storage").Fields, f => f.Label == "Session ID");
        Assert.Equal("\u2014", fallback.Fields.Single(f => f.Property == nameof(WorkspaceMetadata.CreatedAt)).Value);
        Assert.Equal("\u2014", fallback.Fields.Single(f => f.Property == nameof(WorkspaceMetadata.UpdatedAt)).Value);
        var workspace = groups.Single(g => g.Title == "Workspace metadata");
        Assert.Equal("github/cli", workspace.Fields.Single(f => f.Property == nameof(WorkspaceMetadata.ClientName)).Value);
        Assert.DoesNotContain(overview.Fields, f => f.Property == nameof(WorkspaceMetadata.ClientName));
        Assert.DoesNotContain(workspace.Fields, f => f.Property is nameof(WorkspaceMetadata.CreatedAt) or nameof(WorkspaceMetadata.UpdatedAt));
        Assert.Single(groups.SelectMany(g => g.Fields), f => f.Property == nameof(SessionStartInfo.FirstUserPrompt));
        Assert.Single(groups.SelectMany(g => g.Fields), f => f.Property == nameof(SessionStartInfo.LastUserPrompt));
    }

    [Fact]
    public void MissingMetadata_IsUnknownRatherThanFalseOrZero()
    {
        Assert.Empty(SessionMetadata.Create(null));
        var groups = SessionMetadata.Create(Session());
        Assert.All(groups.Where(g => g.Title is "Workspace metadata" or "Event metadata").SelectMany(g => g.Fields),
            field => Assert.Equal("\u2014", field.Value));
        Assert.All(groups.Single(g => g.Title == "Session storage").Fields
                .Where(f => f.Property.StartsWith("Has") || f.Property == "IsInUse"),
            field => Assert.Equal("\u2014", field.Value));
    }

    [Fact]
    public void KnownFalseZeroAndTimestamps_AreDisplayedWithoutLosingTheirMeaning()
    {
        var timestamp = new DateTimeOffset(2026, 9, 15, 12, 34, 56, TimeSpan.FromHours(-7));
        var groups = SessionMetadata.Create(Session() with
        {
            IsEnriched = true,
            Workspace = new() { UserNamed = false, RemoteSteerable = true, SummaryCount = 0, CreatedAt = timestamp },
            Start = new() { AlreadyInUse = false },
        });
        var workspace = groups.Single(g => g.Title == "Workspace metadata");
        Assert.Equal("No", workspace.Fields.Single(f => f.Property == nameof(WorkspaceMetadata.UserNamed)).Value);
        Assert.Equal("Yes", workspace.Fields.Single(f => f.Property == nameof(WorkspaceMetadata.RemoteSteerable)).Value);
        Assert.Equal("0", workspace.Fields.Single(f => f.Property == nameof(WorkspaceMetadata.SummaryCount)).Value);
        Assert.Equal(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            groups.Single(g => g.Title == "Overview").Fields
                .Single(f => f.Property == nameof(WorkspaceMetadata.CreatedAt)).Value);
        Assert.Equal("No", groups.Single(g => g.Title == "Event metadata").Fields
            .Single(f => f.Property == nameof(SessionStartInfo.AlreadyInUse)).Value);
    }

    [Fact]
    public void RegisteredReaders_ContainOnlyNativeSessionSources()
    {
        var services = new ServiceCollection().AddCopilotCore(useMock: false);
        Assert.Equal(new[] { "CheckpointsReader", "EventsJsonlReader", "SessionDbReader", "WorkspaceYamlReader" },
            services.Select(s => s.ServiceType.Name).Where(n => n.EndsWith("Reader", StringComparison.Ordinal)).Order());
        Assert.Equal(typeof(LiveSessionDataSource),
            services.Single(s => s.ServiceType == typeof(ISessionDataSource)).ImplementationType);
    }

    [Fact]
    public void WorkspaceReader_PreservesNativeGitFieldsAndMissingOptionalValues()
    {
        WithFolder(folder =>
        {
            string path = CopilotPaths.WorkspaceYaml(folder);
            File.WriteAllText(path, """
                id: native
                cwd: 'C:\synthetic\worktree'
                git_root: 'C:\synthetic'
                repository: demo/repo
                host_type: github
                branch: feature/native
                client_name: github/cli
                mc_task_id: native-task
                mc_session_id: native-client-session
                future_field: ignored
                """);
            byte[] original = File.ReadAllBytes(path);
            var reader = new WorkspaceYamlReader();
            var workspace = reader.Read(folder)!;
            Assert.Equal(@"C:\synthetic", workspace.GitRoot);
            Assert.Equal("demo/repo", workspace.Repository);
            Assert.Equal("github", workspace.HostType);
            Assert.Equal("feature/native", workspace.Branch);
            Assert.Equal("native-task", workspace.McTaskId);
            Assert.Equal("native-client-session", workspace.McSessionId);
            Assert.Null(workspace.UserNamed);
            Assert.Null(workspace.RemoteSteerable);
            Assert.Null(workspace.SummaryCount);
            Assert.Equal(original, File.ReadAllBytes(path));
            File.AppendAllText(path, "\nuser_named: false\nremote_steerable: false\nsummary_count: 0\n");
            workspace = reader.Read(folder)!;
            Assert.False(workspace.UserNamed);
            Assert.False(workspace.RemoteSteerable);
            Assert.Equal(0L, workspace.SummaryCount);
        });
    }

    [Fact]
    public void NativeSources_WorkWithoutAnyExtensionFiles_AndRefreshOldEventMetadata()
    {
        WithFolder(folder =>
        {
            File.WriteAllText(CopilotPaths.WorkspaceYaml(folder), "id: native\nbranch: native-branch\n");
            string events = CopilotPaths.EventsJsonl(folder);
            File.WriteAllText(events, """{"type":"session.start","data":{"selectedModel":"first","context":{"branch":"event-branch","gitRoot":"root","repository":"demo/repo"}}}""");
            var scanner = new SessionStateScanner(new WorkspaceYamlReader(), Path.GetDirectoryName(folder)!);
            var aggregator = new SessionAggregator(scanner, new EventsJsonlReader());
            var source = new LiveSessionDataSource(aggregator, new CheckpointsReader(), new SessionDbReader());
            SessionInfo session = scanner.ScanFolder(folder)!;
            Assert.Equal("native-branch", session.Branch);
            Assert.Null(session.Start);
            Assert.Empty(source.ReadCheckpoints(session));
            Assert.Equal(SessionTodosStatus.MissingDatabase, source.ReadTodos(session).Status);
            SessionInfo first = source.EnrichWithEvents(session);
            Assert.Equal("first", first.Model);
            Assert.Equal("event-branch", first.Start!.Branch);
            Assert.Equal("root", first.Start.GitRoot);
            Assert.Equal("demo/repo", first.Start.Repository);
            Assert.Null(first.Start.AlreadyInUse);
            string previousVersion = source.GetDetailsVersion(session);
            File.WriteAllText(events, """{"type":"session.start","data":{"selectedModel":"second"}}""");
            File.SetLastWriteTimeUtc(events, DateTime.UtcNow.AddMinutes(1));
            Assert.NotEqual(previousVersion, source.GetDetailsVersion(session));
            Assert.Equal("second", source.EnrichWithEvents(first).Model);
            Assert.Null((session with { Workspace = null }).Branch);
            Assert.Equal("event-branch", (first with { Workspace = null }).Branch);
        });
    }

    [Fact]
    public void CheckpointVersion_TracksInPlaceEditsWithoutInvalidatingDetails()
    {
        WithFolder(folder =>
        {
            var source = new LiveSessionDataSource(
                new SessionAggregator(new SessionStateScanner(new WorkspaceYamlReader()), new EventsJsonlReader()),
                new CheckpointsReader(), new SessionDbReader());
            var session = Session() with { FolderPath = folder };
            Directory.CreateDirectory(CopilotPaths.CheckpointsDir(folder));
            string checkpoint = Path.Combine(CopilotPaths.CheckpointsDir(folder), "001-progress.md");
            File.WriteAllText(checkpoint, "before");
            string details = source.GetDetailsVersion(session);
            string before = source.GetCheckpointsVersion(session);
            File.WriteAllText(checkpoint, "after");
            File.SetLastWriteTimeUtc(checkpoint, DateTime.UtcNow.AddMinutes(1));
            Assert.NotEqual(before, source.GetCheckpointsVersion(session));
            Assert.Equal(details, source.GetDetailsVersion(session));
        });
    }

    private static void WithFolder(Action<string> test)
    {
        // Synthetic native files only; tests never depend on the signed-in user's Copilot store.
        string folder = Path.Combine(Path.GetTempPath(), "SearchlightNative_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { test(folder); }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
