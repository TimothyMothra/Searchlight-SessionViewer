namespace Searchlight.Models;

/// <summary>
/// Lightweight projection of the head of a session's <c>events.jsonl</c> stream:
/// the <c>session.start</c> event, the most recent <c>session.model_change</c>,
/// and the first <c>user.message</c> content (for a prompt preview), plus the
/// last prompt from a separate bounded tail scan. The full log is never materialized.
/// </summary>
public sealed record SessionStartInfo
{
    /// <summary>Copilot CLI version recorded at session start.</summary>
    public string? CopilotVersion { get; init; }

    /// <summary>Context tier, e.g. <c>default</c> or <c>long_context</c>.</summary>
    public string? ContextTier { get; init; }

    /// <summary>Event producer, e.g. <c>copilot-agent</c>.</summary>
    public string? Producer { get; init; }

    /// <summary>UTC start time from the <c>session.start</c> event.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Working directory captured in the start event context.</summary>
    public string? Cwd { get; init; }

    /// <summary>True if the session was already in use when started.</summary>
    public bool AlreadyInUse { get; init; }

    /// <summary>
    /// Effective model — the <c>selectedModel</c> from <c>session.start</c> if
    /// present, otherwise the <c>newModel</c> of the latest
    /// <c>session.model_change</c> seen while head-parsing.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>Reasoning effort associated with <see cref="Model"/>.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>First user message content, trimmed for a one-line preview.</summary>
    public string? FirstUserPrompt { get; init; }

    /// <summary>Latest complete nonempty user message preview, or null if unavailable.</summary>
    public string? LastUserPrompt { get; init; }
}
