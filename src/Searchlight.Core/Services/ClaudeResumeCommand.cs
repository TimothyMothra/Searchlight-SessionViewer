namespace Searchlight.Services;

/// <summary>
/// Builds the <c>claude --resume</c> command line safely. Session ids and
/// workspace paths originate from on-disk content (<c>sessions-index.json</c>
/// fields, transcript records, and raw <c>.jsonl</c> filenames), so nothing here
/// is trusted: ids must parse as GUIDs, workspace paths containing control
/// characters are rejected, and the cwd is quoted for the target shell.
/// Platform-neutral string logic so it can be unit-tested without a UI host.
/// </summary>
public static class ClaudeResumeCommand
{
    /// <summary>
    /// True when <paramref name="sessionId"/> is a canonical dashed GUID.
    /// Exact-"D" only: <c>Guid.TryParse</c> also accepts brace/paren/hex forms
    /// whose <c>{},()</c> are shell-active, and real Claude session ids are
    /// always canonical.
    /// </summary>
    public static bool IsValidSessionId(string? sessionId) =>
        Guid.TryParseExact(sessionId, "D", out _);

    /// <summary>
    /// Builds a POSIX shell command (<c>cd '&lt;cwd&gt;' &amp;&amp; claude --resume &lt;id&gt;</c>)
    /// for macOS/Linux terminals. Returns false (and no command) when the
    /// session id is not a GUID. A cwd that is unusable — control characters,
    /// or blank — is skipped rather than failing the resume.
    /// </summary>
    public static bool TryBuildPosix(
        string? sessionId, string? cwd, bool skipPermissions, out string command)
    {
        if (!TryBuildResumeInvocation(sessionId, skipPermissions, out string resume))
        {
            command = string.Empty;
            return false;
        }

        command = IsUsableCwd(cwd)
            ? $"cd {PosixQuote(cwd!)} && {resume}"
            : resume;
        return true;
    }

    /// <summary>
    /// Builds the bare <c>claude --resume &lt;id&gt;</c> invocation with no
    /// embedded cwd — for hosts that set the working directory on the process
    /// (e.g. Windows <c>cmd</c>) instead of emitting a <c>cd</c>.
    /// </summary>
    public static bool TryBuildResumeInvocation(
        string? sessionId, bool skipPermissions, out string command)
    {
        if (!IsValidSessionId(sessionId))
        {
            command = string.Empty;
            return false;
        }

        command = $"claude --resume {sessionId}";
        if (skipPermissions)
        {
            command += " --dangerously-skip-permissions";
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="cwd"/> is safe to embed in a quoted shell
    /// command: non-blank and free of control characters (a newline would
    /// otherwise survive single-quoting and break the AppleScript layer).
    /// </summary>
    public static bool IsUsableCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return false;
        }

        foreach (char ch in cwd)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>POSIX single-quote wrapping (<c>'</c> escaped as <c>'\''</c>).</summary>
    public static string PosixQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
