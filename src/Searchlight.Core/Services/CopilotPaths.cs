namespace Searchlight.Services;

/// <summary>
/// Central resolver for the well-known read-only paths under <c>~/.copilot</c>.
/// Nothing here writes; the app never mutates the user's Copilot data.
/// </summary>
public static class CopilotPaths
{
    /// <summary>Root <c>~/.copilot</c> directory.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot");

    /// <summary>The <c>session-state</c> directory holding per-session folders.</summary>
    public static string SessionState { get; } = Path.Combine(Root, "session-state");

    /// <summary>Absolute path to a session's <c>events.jsonl</c>.</summary>
    public static string EventsJsonl(string folderPath) =>
        Path.Combine(folderPath, "events.jsonl");

    /// <summary>Absolute path to a session's <c>workspace.yaml</c>.</summary>
    public static string WorkspaceYaml(string folderPath) =>
        Path.Combine(folderPath, "workspace.yaml");

    /// <summary>Absolute path to a session's <c>session.db</c>.</summary>
    public static string SessionDb(string folderPath) =>
        Path.Combine(folderPath, "session.db");

    /// <summary>Absolute path to a session's <c>checkpoints</c> folder.</summary>
    public static string CheckpointsDir(string folderPath) =>
        Path.Combine(folderPath, "checkpoints");
}
