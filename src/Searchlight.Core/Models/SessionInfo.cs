namespace Searchlight.Models;

/// <summary>
/// Aggregate view of a single Copilot session, keyed by its UUID and merged
/// from every available source: the session-state folder, <c>workspace.yaml</c>,
/// the head of <c>events.jsonl</c>, the status-snapshot index, and the monthly
/// journal. Every enrichment field is null-safe — sessions vary wildly in which
/// files exist (some <c>optimistic-chat-*</c> folders are empty).
/// </summary>
public sealed record SessionInfo
{
    /// <summary>Session UUID (without any <c>optimistic-chat-</c> prefix).</summary>
    public required string Id { get; init; }

    /// <summary>Raw folder name under <c>session-state</c> (may carry a prefix).</summary>
    public required string FolderName { get; init; }

    /// <summary>Absolute path to the session-state folder.</summary>
    public required string FolderPath { get; init; }

    /// <summary>Project vs chat, inferred from the folder-name prefix.</summary>
    public SessionKind Kind { get; init; }

    /// <summary>Folder last-write time — the primary sort key for recency.</summary>
    public DateTimeOffset LastWriteTime { get; init; }

    // --- workspace.yaml ---

    /// <summary>Parsed <c>workspace.yaml</c>, or null when absent.</summary>
    public WorkspaceMetadata? Workspace { get; init; }

    // --- events.jsonl head ---

    /// <summary>Parsed head of <c>events.jsonl</c>, or null when absent.</summary>
    public SessionStartInfo? Start { get; init; }

    // --- enrichment (best-effort) ---

    /// <summary>Most recent branch seen for this session, if any.</summary>
    public string? Branch { get; init; }

    /// <summary>Latest journal activity synopsis for this session, if any.</summary>
    public string? JournalActivity { get; init; }

    /// <summary>Number of status snapshots recorded for this session.</summary>
    public int SnapshotCount { get; init; }

    // --- file-presence flags / state ---

    /// <summary>True when an <c>inuse.&lt;PID&gt;.lock</c> file is present.</summary>
    public bool IsInUse { get; init; }

    /// <summary>True when a <c>plan.md</c> (or variant) exists.</summary>
    public bool HasPlan { get; init; }

    /// <summary>True when a <c>session.db</c> exists.</summary>
    public bool HasSessionDb { get; init; }

    /// <summary>True when a <c>checkpoints</c> folder with content exists.</summary>
    public bool HasCheckpoints { get; init; }

    // --- convenience projections ---

    /// <summary>
    /// Best display name: the workspace name if set, else the full UUID.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Workspace?.Name)
            ? Workspace!.Name!
            : Id;

    /// <summary>First 8 characters of the UUID, for compact display.</summary>
    public string ShortId => Id.Length >= 8 ? Id[..8] : Id;

    // --- client source (which Copilot surface created the session) ---

    /// <summary>
    /// Which Copilot client created this session, derived from the
    /// <c>client_name</c> field in <c>workspace.yaml</c>:
    /// <c>github/cli</c> → "CLI", <c>github/autopilot</c> → "App", else "Unknown".
    /// Only newer sessions carry <c>client_name</c>; older ones read "Unknown".
    /// </summary>
    public string ClientLabel =>
        Workspace?.ClientName switch
        {
            "github/cli" => "CLI",
            "github/autopilot" => "App",
            "claude/code" => "Claude",
            _ => "Unknown",
        };

    /// <summary>True when this session was created by the Copilot CLI.</summary>
    public bool IsCliClient => Workspace?.ClientName == "github/cli";

    /// <summary>True when this session was created by the GitHub Copilot App.</summary>
    public bool IsAppClient => Workspace?.ClientName == "github/autopilot";

    /// <summary>
    /// Raw <c>client_name</c> string from <c>workspace.yaml</c> (e.g.
    /// <c>github/cli</c>, <c>github/autopilot</c>), or "Unknown" when absent. Used
    /// in the details pane where the full identifier is preferred over the short label.
    /// </summary>
    public string ClientNameRaw => Workspace?.ClientName ?? "Unknown";

    /// <summary>Working directory from workspace.yaml, falling back to start-event cwd.</summary>
    public string? Cwd => Workspace?.Cwd ?? Start?.Cwd;

    /// <summary>Effective model, from the events head.</summary>
    public string? Model => Start?.Model;

    /// <summary>Reasoning effort, from the events head.</summary>
    public string? ReasoningEffort => Start?.ReasoningEffort;

    /// <summary>Copilot version, from the events head.</summary>
    public string? CopilotVersion => Start?.CopilotVersion;

    /// <summary>First user prompt preview, from the events head.</summary>
    public string? FirstPromptPreview => Start?.FirstUserPrompt;

    /// <summary>
    /// Effective updated time — workspace <c>updated_at</c> if known, else the
    /// folder last-write time.
    /// </summary>
    public DateTimeOffset UpdatedAt => Workspace?.UpdatedAt ?? LastWriteTime;
}
