using System.Text;
using Searchlight.Models;

namespace Searchlight.Services;

public static class ResumeCommandBuilder
{
    public const string SessionIdToken = "{sessionId}";
    // ASSUMPTION: previews use the canonical Guid.Empty shape, matching real UUID lengths.
    public const string PreviewSessionId = "00000000-0000-0000-0000-000000000000";
    public const string DefaultTemplate = AppSettings.DefaultResumeCommandTemplate;
    // ASSUMPTION: reserve space for encoded transport beneath Windows' command-line limit.
    public const int MaxTemplateLength = 3000;

    public static string? Validate(AppSettings settings)
    {
        if (!settings.UseCustomResumeCommand) return null;
        string template = settings.CustomResumeCommand;
        if (string.IsNullOrWhiteSpace(template)) return "Enter a custom command, or turn off custom command mode.";
        if (template.Length > MaxTemplateLength) return $"Keep the custom command within {MaxTemplateLength} characters.";
        if (template.Any(char.IsControl)) return "Use a single-line command without control characters.";
        if (!template.Contains(SessionIdToken, StringComparison.Ordinal))
            return $"Include {SessionIdToken} where the selected session ID should be inserted.";
        if (template.Replace(SessionIdToken, PreviewSessionId, StringComparison.Ordinal).Length > MaxTemplateLength)
            return $"Keep the expanded command within {MaxTemplateLength} characters after substituting the full session UUID.";
        return null;
    }

    public static string Preview(AppSettings settings) =>
        Validate(settings) is null ? Expand(settings, PreviewSessionId) : string.Empty;

    public static string Build(AppSettings settings, string sessionId)
    {
        if (Validate(settings) is { } error) throw new ArgumentException(error, nameof(settings));
        // Only the template is executable user-authored syntax. IDs loaded from
        // session metadata must never contribute shell operators or quoting.
        if (!Guid.TryParseExact(sessionId, "D", out _))
            throw new ArgumentException("The selected session does not have a valid session UUID.", nameof(sessionId));
        return Expand(settings, sessionId);
    }

    private static string Expand(AppSettings settings, string sessionId) =>
        settings.UseCustomResumeCommand
            ? settings.CustomResumeCommand.Replace(SessionIdToken, sessionId, StringComparison.Ordinal)
            : $"copilot --resume={sessionId}{(settings.AppendYolo ? " --yolo" : string.Empty)}";

    public static string CmdArguments(string command) => $"/s /k \"{command}\"";

    public static string EncodeTerminalTransport(string command)
    {
        // ASSUMPTION: custom commands are trusted cmd.exe syntax. Windows Terminal
        // parses semicolons and reconstructs quoting, so transport arbitrary user
        // text as base64, then give cmd.exe the original text without temp files.
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $command = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{encodedCommand}}'))
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = [IO.Path]::Combine([Environment]::GetFolderPath('System'), 'cmd.exe')
            $start.Arguments = '/s /k "' + $command + '"'
            $start.UseShellExecute = $false
            $process = [Diagnostics.Process]::Start($start)
            $process.WaitForExit()
            $result = $process.ExitCode
            $process.Dispose()
            exit $result
            """;
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
}
