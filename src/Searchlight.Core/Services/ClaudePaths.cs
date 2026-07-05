namespace Searchlight.Services;

/// <summary>
/// Central resolver for the well-known read-only paths under <c>~/.claude</c>
/// (Claude Code's session store). Nothing here writes; the app never mutates
/// the user's Claude Code data.
/// </summary>
public static class ClaudePaths
{
    /// <summary>Root <c>~/.claude</c> directory.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude");

    /// <summary>
    /// The <c>projects</c> directory: one folder per workspace path (the path is
    /// encoded into the folder name, e.g. <c>-Users-jane-Source-app</c>), each
    /// holding per-session <c>&lt;uuid&gt;.jsonl</c> transcripts and an optional
    /// <c>sessions-index.json</c> summary index.
    /// </summary>
    public static string Projects { get; } = Path.Combine(Root, "projects");

    /// <summary>Absolute path to a project folder's <c>sessions-index.json</c>.</summary>
    public static string SessionsIndex(string projectDir) =>
        Path.Combine(projectDir, "sessions-index.json");

    /// <summary>Absolute path to a session's transcript <c>&lt;id&gt;.jsonl</c>.</summary>
    public static string SessionJsonl(string projectDir, string sessionId) =>
        Path.Combine(projectDir, sessionId + ".jsonl");
}
