using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Exercises the Claude Code data source against a synthetic
/// <c>~/.claude/projects</c> tree on disk (temp directory per test).
/// </summary>
public sealed class ClaudeSessionDataSourceTests : IDisposable
{
    private readonly string _root;

    public ClaudeSessionDataSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "searchlight-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeSessionDataSource CreateDataSource() =>
        new(new ClaudeSessionIndexReader(), new ClaudeJsonlHeadReader(), _root);

    private string CreateProject(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string IndexJson = """
        {
          "version": 1,
          "entries": [
            {
              "sessionId": "56a048ce-7a66-4bb5-8f87-de5a34a7274b",
              "fullPath": "/tmp/whatever.jsonl",
              "firstPrompt": "what is next",
              "summary": "MAST Portal Integration Complete",
              "messageCount": 31,
              "created": "2026-01-11T01:26:09.633Z",
              "modified": "2026-01-11T03:17:06.241Z",
              "gitBranch": "main",
              "projectPath": "/Users/jane/Source/App",
              "isSidechain": false
            },
            {
              "sessionId": "0b325b6a-0000-4000-8000-000000000001",
              "firstPrompt": "review the auth flow",
              "messageCount": 4,
              "created": "2026-02-01T10:00:00.000Z",
              "modified": "2026-02-01T11:00:00.000Z",
              "isSidechain": true
            }
          ]
        }
        """;

    [Fact]
    public void LoadAll_MapsIndexEntriesOntoSessionInfo()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        File.WriteAllText(Path.Combine(dir, "sessions-index.json"), IndexJson);

        IReadOnlyList<SessionInfo> sessions = CreateDataSource().LoadAll();

        Assert.Equal(2, sessions.Count);

        SessionInfo first = sessions.Single(s => s.Id == "56a048ce-7a66-4bb5-8f87-de5a34a7274b");
        Assert.Equal("MAST Portal Integration Complete", first.DisplayName);
        Assert.Equal("what is next", first.FirstPromptPreview);
        Assert.Equal("main", first.Branch);
        Assert.Equal("/Users/jane/Source/App", first.Cwd);
        Assert.Equal("Claude", first.ClientLabel);
        Assert.Equal(SessionKind.Project, first.Kind);
        Assert.Equal("31 messages", first.JournalActivity);
        Assert.Equal(DateTimeOffset.Parse("2026-01-11T03:17:06.241Z"), first.UpdatedAt);

        SessionInfo sidechain = sessions.Single(s => s.Id == "0b325b6a-0000-4000-8000-000000000001");
        Assert.Equal(SessionKind.Chat, sidechain.Kind);
        // No summary → the first prompt stands in for a raw UUID display name.
        Assert.Equal("review the auth flow", sidechain.DisplayName);
    }

    [Fact]
    public void LoadAll_SurfacesJsonlFilesMissingFromIndex()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        File.WriteAllText(Path.Combine(dir, "sessions-index.json"), IndexJson);
        File.WriteAllText(
            Path.Combine(dir, "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee.jsonl"),
            """{"type":"user","message":{"role":"user","content":"hi"}}""");

        IReadOnlyList<SessionInfo> sessions = CreateDataSource().LoadAll();

        Assert.Equal(3, sessions.Count);
        SessionInfo stray = sessions.Single(s => s.Id == "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        Assert.Equal("Claude", stray.ClientLabel);
        Assert.True(stray.LastWriteTime > DateTimeOffset.MinValue);
    }

    [Fact]
    public void LoadAll_WithoutIndex_FallsBackToJsonlScan()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        File.WriteAllText(
            Path.Combine(dir, "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee.jsonl"),
            """{"type":"user","message":{"role":"user","content":"hi"}}""");

        IReadOnlyList<SessionInfo> sessions = CreateDataSource().LoadAll();

        SessionInfo session = Assert.Single(sessions);
        Assert.Equal("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", session.Id);
    }

    [Fact]
    public void LoadAll_MissingRoot_ReturnsEmpty()
    {
        var dataSource = new ClaudeSessionDataSource(
            new ClaudeSessionIndexReader(),
            new ClaudeJsonlHeadReader(),
            Path.Combine(_root, "does-not-exist"));

        Assert.Empty(dataSource.LoadAll());
    }

    [Fact]
    public void EnrichWithEvents_FillsModelVersionAndPromptFromTranscript()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        string id = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        File.WriteAllLines(Path.Combine(dir, id + ".jsonl"),
        [
            """{"type":"ai-title","aiTitle":"Some title","sessionId":"x"}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"<system-reminder>noise</system-reminder>"},"version":"2.1.170","gitBranch":"main","cwd":"/Users/jane/Source/App","timestamp":"2026-06-09T20:57:35.471Z"}""",
            """{"type":"user","message":{"role":"user","content":"do a deep review of this project"},"version":"2.1.170","gitBranch":"main","cwd":"/Users/jane/Source/App","timestamp":"2026-06-09T20:57:35.471Z"}""",
            """{"type":"assistant","message":{"role":"assistant","model":"claude-fable-5","content":[{"type":"text","text":"ok"}]}}""",
        ]);

        ClaudeSessionDataSource dataSource = CreateDataSource();
        SessionInfo session = Assert.Single(dataSource.LoadAll());

        SessionInfo enriched = dataSource.EnrichWithEvents(session);

        Assert.Equal("claude-fable-5", enriched.Model);
        Assert.Equal("2.1.170", enriched.CopilotVersion);
        Assert.Equal("do a deep review of this project", enriched.FirstPromptPreview);
        Assert.Equal("main", enriched.Branch);
        Assert.Equal("/Users/jane/Source/App", enriched.Cwd);
        // The transcript's ai-title becomes the display name when the index
        // provided no summary.
        Assert.Equal("Some title", enriched.DisplayName);
    }

    [Fact]
    public void EnrichWithEvents_NoAiTitle_FallsBackToFirstPromptName()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        string id = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff";
        // A brand-new session: one prompt, no assistant reply, no ai-title yet.
        File.WriteAllLines(Path.Combine(dir, id + ".jsonl"),
        [
            """{"type":"user","message":{"role":"user","content":"fix the login redirect loop"},"version":"2.1.170","cwd":"/Users/jane/Source/App","timestamp":"2026-07-02T10:00:00.000Z"}""",
        ]);

        ClaudeSessionDataSource dataSource = CreateDataSource();
        // Eager naming runs inside LoadAll for un-indexed sessions.
        SessionInfo session = Assert.Single(dataSource.LoadAll());

        Assert.Equal("fix the login redirect loop", session.DisplayName);
    }

    [Fact]
    public void EnrichWithEvents_AlreadyEnriched_ReturnsSameInstance()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        File.WriteAllText(Path.Combine(dir, "sessions-index.json"), IndexJson);

        ClaudeSessionDataSource dataSource = CreateDataSource();
        SessionInfo session = dataSource.LoadAll()[0] with
        {
            Start = new SessionStartInfo { Model = "claude-sonnet-5" },
        };

        Assert.Same(session, dataSource.EnrichWithEvents(session));
    }

    [Fact]
    public void TryGetProjectCwd_IsCacheOnly_PopulatedByLoadAll()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        File.WriteAllText(Path.Combine(dir, "sessions-index.json"), IndexJson);

        ClaudeSessionDataSource dataSource = CreateDataSource();

        // Cache-only by contract: no synchronous store rescan on a miss.
        Assert.Null(dataSource.TryGetProjectCwd("56a048ce-7a66-4bb5-8f87-de5a34a7274b"));

        dataSource.LoadAll();

        Assert.Equal(
            "/Users/jane/Source/App",
            dataSource.TryGetProjectCwd("56a048ce-7a66-4bb5-8f87-de5a34a7274b"));
        Assert.Null(dataSource.TryGetProjectCwd("not-a-session"));
    }

    [Fact]
    public void DetailSources_HaveNoClaudeEquivalent_AndReturnEmpty()
    {
        string dir = CreateProject("-Users-jane-Source-App");
        File.WriteAllText(Path.Combine(dir, "sessions-index.json"), IndexJson);

        ClaudeSessionDataSource dataSource = CreateDataSource();
        SessionInfo session = dataSource.LoadAll()[0];

        Assert.Empty(dataSource.ReadCheckpoints(session));
        Assert.Empty(dataSource.LoadSnapshots(session.Id));
        Assert.Empty(dataSource.ReadTodos(session));
    }
}

/// <summary>Covers the lossy project-folder-name decoder.</summary>
public sealed class ClaudeProjectDirNameTests : IDisposable
{
    private readonly string _root;

    public ClaudeProjectDirNameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "searchlight-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "my-app", "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Encode(string path) => path.Replace(Path.DirectorySeparatorChar, '-');

    [Fact]
    public void TryDecode_ResolvesPlainSegments()
    {
        string expected = Path.Combine(_root, "plain");
        Assert.Equal(expected, ClaudeProjectDirName.TryDecode(Encode(expected)));
    }

    [Fact]
    public void TryDecode_PrefersLiteralDashesInDirectoryNames()
    {
        string expected = Path.Combine(_root, "my-app", "sub");
        Assert.Equal(expected, ClaudeProjectDirName.TryDecode(Encode(expected)));
    }

    [Fact]
    public void TryDecode_UnknownPath_ReturnsNull()
    {
        Assert.Null(ClaudeProjectDirName.TryDecode("-nope-nothing-here-at-all"));
        Assert.Null(ClaudeProjectDirName.TryDecode("not-prefixed"));
        Assert.Null(ClaudeProjectDirName.TryDecode(null));
    }
}
