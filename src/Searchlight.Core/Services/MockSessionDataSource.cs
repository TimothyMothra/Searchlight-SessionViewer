using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Synthetic <see cref="ISessionDataSource"/> for demos, screenshots, and unit tests.
/// Produces a fixed set of deterministic sessions (stable ids and relative timestamps)
/// that exercise every recency bucket (≤2h/4h/8h/16h/32h/older), both clients
/// (CLI/App) plus Unknown, and both kinds (Project/Chat) — with checkpoints,
/// snapshots, and todos — so the UI can be exercised without touching the user's real
/// <c>~/.copilot</c> tree or leaking any proprietary content.
/// </summary>
public sealed class MockSessionDataSource : ISessionDataSource
{
    // Anchor "now" is captured once so a single render is internally consistent.
    // Timestamps are expressed as offsets back from this anchor.
    private readonly DateTimeOffset _now = DateTimeOffset.Now;

    private readonly List<SessionInfo> _sessions;
    private readonly Dictionary<string, IReadOnlyList<CheckpointInfo>> _checkpoints = new();
    private readonly Dictionary<string, IReadOnlyList<SnapshotInfo>> _snapshots = new();
    private readonly Dictionary<string, IReadOnlyList<SessionTodo>> _todos = new();

    /// <summary>Builds the fixed synthetic dataset.</summary>
    public MockSessionDataSource()
    {
        _sessions = BuildSessions();
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionInfo> LoadAll() => _sessions;

    /// <inheritdoc />
    public IReadOnlyList<SessionInfo> LoadCheap() => _sessions;

    /// <inheritdoc />
    public SessionInfo EnrichOne(SessionInfo session) => session;

    /// <inheritdoc />
    public SessionInfo EnrichWithEvents(SessionInfo session) => session;

    /// <inheritdoc />
    public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) =>
        _checkpoints.TryGetValue(session.Id, out var c) ? c : [];

    /// <inheritdoc />
    public IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId) =>
        _snapshots.TryGetValue(sessionId, out var s) ? s : [];

    /// <inheritdoc />
    public IReadOnlyList<SessionTodo> ReadTodos(SessionInfo session) =>
        _todos.TryGetValue(session.Id, out var t) ? t : [];

    private List<SessionInfo> BuildSessions()
    {
        var list = new List<SessionInfo>
        {
            // --- ≤ 2h bucket ---
            Make("00000000-0000-4000-8000-000000000001", "Refactor payment retry logic",
                minutesAgo: 12, kind: SessionKind.Project, client: "github/cli",
                name: "Refactor payment retry logic", branch: "feature/payment-retry",
                model: "claude-opus-4.8", effort: "high",
                prompt: "Walk the payment service retry path and make the backoff jitter deterministic under test. The current exponential backoff uses Random.Shared which makes the retry unit tests flaky — introduce an injectable time/jitter provider and update the three affected tests.",
                withDetail: true),
            Make("00000000-0000-4000-8000-000000000002", "Explain the auth middleware",
                minutesAgo: 47, kind: SessionKind.Chat, client: "github/autopilot",
                name: null, branch: null,
                model: "gpt-5.4", effort: "medium",
                prompt: "How does the auth middleware decide when to refresh the bearer token versus reject the request outright?"),

            // --- ≤ 4h bucket ---
            Make("00000000-0000-4000-8000-000000000003", "Add dark-mode titlebar",
                minutesAgo: 165, kind: SessionKind.Project, client: "github/cli",
                name: "Add dark-mode titlebar", branch: "ui/dark-titlebar",
                model: "claude-sonnet-4.6", effort: "medium",
                prompt: "The WinUI titlebar renders bright white in dark mode. Extend content into the titlebar and theme the caption buttons to follow the OS AppsUseLightTheme setting.",
                withDetail: true),
            Make("00000000-0000-4000-8000-000000000004", "Investigate flaky CI run",
                minutesAgo: 210, kind: SessionKind.Project, client: null,
                name: null, branch: "main",
                model: "gpt-5.4", effort: "high",
                prompt: "The nightly integration job fails ~1 in 5 runs on the database seeding step. Bisect the seed ordering and find the race."),

            // --- ≤ 8h bucket ---
            Make("00000000-0000-4000-8000-000000000005", "Port CLI parser to spans",
                minutesAgo: 350, kind: SessionKind.Project, client: "github/cli",
                name: "Port CLI parser to spans", branch: "perf/span-parser",
                model: "claude-opus-4.8", effort: "high",
                prompt: "Rewrite the argument tokenizer to be allocation-free using ReadOnlySpan<char> and SearchValues. Keep behavior identical and add a BenchmarkDotNet before/after.",
                withDetail: true),
            Make("00000000-0000-4000-8000-000000000006", "Draft release notes",
                minutesAgo: 460, kind: SessionKind.Chat, client: "github/autopilot",
                name: null, branch: null,
                model: "gemini-3.1-pro-preview", effort: "low",
                prompt: "Summarize the merged PRs since the last tag into user-facing release notes grouped by feature area."),

            // --- ≤ 16h bucket ---
            Make("00000000-0000-4000-8000-000000000007", "Migrate to EF Core 10",
                minutesAgo: 720, kind: SessionKind.Project, client: "github/cli",
                name: "Migrate to EF Core 10", branch: "chore/efcore-10",
                model: "claude-sonnet-4.6", effort: "medium",
                prompt: "Bump EF Core to 10, regenerate the migrations, and resolve the breaking change around split queries defaulting off.",
                withDetail: true),
            Make("00000000-0000-4000-8000-000000000008", "Security review of upload path",
                minutesAgo: 900, kind: SessionKind.Project, client: "github/autopilot",
                name: "Security review of upload path", branch: "security/upload-audit",
                model: "claude-opus-4.8", effort: "high",
                prompt: "Audit the file-upload endpoint for path traversal and content-type spoofing. Flag anything above medium severity with a concrete exploit."),

            // --- ≤ 32h bucket ---
            Make("00000000-0000-4000-8000-000000000009", "Wire up telemetry dashboard",
                minutesAgo: 1500, kind: SessionKind.Project, client: "github/cli",
                name: "Wire up telemetry dashboard", branch: "feature/telemetry-dash",
                model: "gpt-5.4", effort: "medium",
                prompt: "Build a Grafana panel set over the new OpenTelemetry metrics and document the query for p99 request latency.",
                withDetail: true),
            Make("00000000-0000-4000-8000-000000000010", "Chat: naming a service",
                minutesAgo: 1800, kind: SessionKind.Chat, client: null,
                name: null, branch: null,
                model: "gpt-5.4-mini", effort: "low",
                prompt: "What's a good name for the service that reconciles inventory counts across warehouses?"),

            // --- older than 32h (calendar-day headers) ---
            Make("00000000-0000-4000-8000-000000000011", "Set up devcontainer",
                minutesAgo: 60 * 40, kind: SessionKind.Project, client: "github/cli",
                name: "Set up devcontainer", branch: "chore/devcontainer",
                model: "claude-sonnet-4.6", effort: "medium",
                prompt: "Create a devcontainer with .NET 10, Node 22, and the Azure CLI preinstalled; mount the nuget cache."),
            Make("00000000-0000-4000-8000-000000000012", "Debug memory leak in cache",
                minutesAgo: 60 * 55, kind: SessionKind.Project, client: "github/autopilot",
                name: "Debug memory leak in cache", branch: "fix/cache-leak",
                model: "claude-opus-4.8", effort: "high",
                prompt: "The in-memory response cache grows unbounded under load. Find the missing eviction and add a regression test.",
                withDetail: true),
            Make("00000000-0000-4000-8000-000000000013", "Explain regex in validator",
                minutesAgo: 60 * 72, kind: SessionKind.Chat, client: "github/cli",
                name: null, branch: null,
                model: "gpt-5-mini", effort: "low",
                prompt: "Break down what this email-validation regex matches and where it can catastrophically backtrack."),
            Make("00000000-0000-4000-8000-000000000014", "Add xUnit smoke tests",
                minutesAgo: 60 * 96, kind: SessionKind.Project, client: null,
                name: "Add xUnit smoke tests", branch: "test/smoke",
                model: "gpt-5.4", effort: "medium",
                prompt: "Stand up an xUnit project and add smoke tests for the grouping key logic and the session projections."),
            Make("00000000-0000-4000-8000-000000000015", "Legacy session (no client)",
                minutesAgo: 60 * 140, kind: SessionKind.Project, client: null,
                name: null, branch: "release/1.4",
                model: null, effort: null,
                prompt: "Older session predating the client_name field — should render as Unknown client."),
        };

        return list;
    }

    /// <summary>
    /// Constructs one synthetic <see cref="SessionInfo"/> and, when
    /// <paramref name="withDetail"/> is set, seeds matching checkpoints, snapshots,
    /// and todos so the details pane is fully populated for screenshots.
    /// </summary>
    private SessionInfo Make(
        string id,
        string title,
        int minutesAgo,
        SessionKind kind,
        string? client,
        string? name,
        string? branch,
        string? model,
        string? effort,
        string prompt,
        bool withDetail = false)
    {
        DateTimeOffset updated = _now.AddMinutes(-minutesAgo);
        string folderName = kind == SessionKind.Chat ? $"optimistic-chat-{id}" : id;
        string folderPath = $@"C:\Users\demo\.copilot\session-state\{folderName}";

        var workspace = new WorkspaceMetadata
        {
            Id = id,
            Name = name,
            ClientName = client,
            Cwd = @"C:\REPOS\DemoApp",
            CreatedAt = updated.AddMinutes(-30),
            UpdatedAt = updated,
            UserNamed = name is not null,
        };

        var start = new SessionStartInfo
        {
            Model = model,
            ReasoningEffort = effort,
            CopilotVersion = "1.0.67",
            ContextTier = "default",
            Producer = client == "github/autopilot" ? "copilot-autopilot" : "copilot-agent",
            StartTime = updated.AddMinutes(-30),
            Cwd = @"C:\REPOS\DemoApp",
            FirstUserPrompt = prompt,
        };

        var session = new SessionInfo
        {
            Id = id,
            FolderName = folderName,
            FolderPath = folderPath,
            Kind = kind,
            LastWriteTime = updated,
            Workspace = workspace,
            Start = start,
            Branch = branch,
            SnapshotCount = withDetail ? 3 : 0,
            HasCheckpoints = withDetail,
            HasSessionDb = withDetail,
            HasPlan = withDetail,
            IsInUse = minutesAgo < 30,
            JournalActivity = withDetail ? $"working on {title.ToLowerInvariant()}" : null,
        };

        if (withDetail)
        {
            _checkpoints[id] =
            [
                new CheckpointInfo { Number = 1, Title = $"Planning {title.ToLowerInvariant()}", FilePath = $@"{folderPath}\checkpoints\001-planning.md", Timestamp = updated.AddMinutes(-25) },
                new CheckpointInfo { Number = 2, Title = "Implementing the core change", FilePath = $@"{folderPath}\checkpoints\002-impl.md", Timestamp = updated.AddMinutes(-15) },
                new CheckpointInfo { Number = 3, Title = "Build-verified and cleaned up", FilePath = $@"{folderPath}\checkpoints\003-verify.md", Timestamp = updated.AddMinutes(-5) },
            ];

            _snapshots[id] =
            [
                new SnapshotInfo { SnapshotId = 1, SessionId = id, Branch = branch, Cwd = @"C:\REPOS\DemoApp", TimestampIso = updated.AddMinutes(-20).ToString("o"), Timestamp = updated.AddMinutes(-20), SourceTrigger = "ask_user" },
                new SnapshotInfo { SnapshotId = 2, SessionId = id, Branch = branch, Cwd = @"C:\REPOS\DemoApp", TimestampIso = updated.AddMinutes(-10).ToString("o"), Timestamp = updated.AddMinutes(-10), SourceTrigger = "long_turn" },
                new SnapshotInfo { SnapshotId = 3, SessionId = id, Branch = branch, Cwd = @"C:\REPOS\DemoApp", TimestampIso = updated.AddMinutes(-2).ToString("o"), Timestamp = updated.AddMinutes(-2), SourceTrigger = "task_complete" },
            ];

            _todos[id] =
            [
                new SessionTodo { Id = "t1", Title = "Reproduce the issue", Status = "done" },
                new SessionTodo { Id = "t2", Title = "Implement the fix", Status = "done" },
                new SessionTodo { Id = "t3", Title = "Add a regression test", Status = "in_progress" },
                new SessionTodo { Id = "t4", Title = "Update the docs", Status = "pending" },
            ];
        }

        return session;
    }
}
