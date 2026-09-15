namespace Searchlight.Models;

/// <summary>
/// Strongly-typed projection of a session's <c>workspace.yaml</c> file.
/// All members are nullable because older or partially-written sessions may
/// omit fields. Parsed read-only via YamlDotNet.
/// </summary>
public sealed record WorkspaceMetadata
{
    /// <summary>The session UUID (matches the folder name).</summary>
    public string? Id { get; init; }

    /// <summary>Working directory the session was started against.</summary>
    public string? Cwd { get; init; }

    /// <summary>Native workspace Git context, when recorded by the client.</summary>
    public string? GitRoot { get; init; }
    public string? Repository { get; init; }
    public string? HostType { get; init; }
    public string? Branch { get; init; }

    /// <summary>Client that created the session, e.g. <c>github/autopilot</c>.</summary>
    public string? ClientName { get; init; }

    /// <summary>Human-friendly session name (may be auto-generated).</summary>
    public string? Name { get; init; }

    /// <summary>True when the user explicitly named the session.</summary>
    public bool? UserNamed { get; init; }

    /// <summary>Number of summaries recorded for the session.</summary>
    public long? SummaryCount { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>UTC last-updated timestamp.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Whether the session is remotely steerable.</summary>
    public bool? RemoteSteerable { get; init; }

    /// <summary>Mission-control task id, when present.</summary>
    public string? McTaskId { get; init; }

    /// <summary>Mission-control session id, when present.</summary>
    public string? McSessionId { get; init; }
}
