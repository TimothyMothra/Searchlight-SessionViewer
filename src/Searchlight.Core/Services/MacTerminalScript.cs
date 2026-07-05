namespace Searchlight.Services;

/// <summary>
/// Builds the AppleScript that opens a command in macOS Terminal.app. The
/// script embeds the shell command as an AppleScript string literal, so this
/// is the outermost escaping boundary on macOS — kept as pure string logic in
/// Core so it can be unit-tested (the Avalonia host only hands the result to
/// <c>osascript</c>).
/// </summary>
public static class MacTerminalScript
{
    /// <summary>
    /// The full <c>osascript -e</c> payload: activate Terminal.app and run
    /// <paramref name="command"/> in a new window.
    /// </summary>
    public static string Build(string command) =>
        $"tell application \"Terminal\"\nactivate\ndo script \"{EscapeStringLiteral(command)}\"\nend tell";

    /// <summary>
    /// Escapes <paramref name="value"/> for embedding in a double-quoted
    /// AppleScript string literal. Order is load-bearing: backslashes must be
    /// doubled before quotes are escaped, or the added escape backslashes
    /// would themselves be doubled. AppleScript literals give <c>$</c>,
    /// backticks, and single quotes no special meaning, so they pass through —
    /// neutralizing those for the shell is the command builders' job
    /// (<see cref="ClaudeResumeCommand"/> / <see cref="CopilotResumeCommand"/>).
    /// </summary>
    public static string EscapeStringLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
