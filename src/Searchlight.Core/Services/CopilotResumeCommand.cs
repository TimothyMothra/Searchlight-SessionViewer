namespace Searchlight.Services;

/// <summary>
/// Builds the <c>copilot --resume=&lt;id&gt;</c> command line safely for
/// terminal hosts. Session ids originate from on-disk folder names under
/// <c>~/.copilot/session-state</c>, so they are not trusted: ids must parse as
/// canonical dashed GUIDs before they are embedded in a shell command (the
/// Avalonia host hands the command to <c>osascript</c>/<c>bash -lc</c>/<c>cmd</c>).
/// Platform-neutral string logic so it can be unit-tested without a UI host.
/// </summary>
public static class CopilotResumeCommand
{
    /// <summary>
    /// True when <paramref name="sessionId"/> is a canonical dashed GUID.
    /// Exact-"D" only: <c>Guid.TryParse</c> also accepts brace/paren/hex forms
    /// whose <c>{},()</c> are shell-active, and real Copilot session ids are
    /// always canonical.
    /// </summary>
    public static bool IsValidSessionId(string? sessionId) =>
        Guid.TryParseExact(sessionId, "D", out _);

    /// <summary>
    /// Builds the <c>copilot --resume=&lt;id&gt;</c> invocation. Returns false
    /// (and no command) when the session id is not a GUID. Unlike Claude Code,
    /// Copilot sessions are not scoped to the directory they ran in, so no
    /// <c>cd</c> prefix is needed. Opt-in <c>--yolo</c> auto-approves all tool
    /// actions in the resumed session.
    /// </summary>
    public static bool TryBuild(string? sessionId, bool yolo, out string command)
    {
        if (!IsValidSessionId(sessionId))
        {
            command = string.Empty;
            return false;
        }

        command = $"copilot --resume={sessionId}";
        if (yolo)
        {
            command += " --yolo";
        }

        return true;
    }
}
