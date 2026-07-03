using System.Diagnostics;
using System.Runtime.InteropServices;
using Searchlight.Abstractions;
using Searchlight.Diagnostics;
using Searchlight.Services;

namespace Searchlight.Avalonia.Services;

/// <summary>
/// Resumes a Claude Code session in the platform terminal:
/// <c>cd &lt;workspace&gt; &amp;&amp; claude --resume &lt;id&gt;</c>. Claude Code
/// scopes sessions to the directory they ran in, so the launcher resolves the
/// workspace via <see cref="ClaudeSessionDataSource.TryGetProjectCwd"/> before
/// launching. Command construction is delegated to
/// <see cref="ClaudeResumeCommand"/>, which rejects non-GUID session ids and
/// unusable workspace paths — both originate from on-disk content and are not
/// trusted. macOS uses Terminal.app via <c>osascript</c>; Linux tries
/// <c>x-terminal-emulator</c>; Windows uses <c>cmd</c> with the workspace as
/// the process working directory (cmd has no safe inline quoting).
/// </summary>
public sealed class TerminalResumeLauncher : IResumeLauncher
{
    private readonly ClaudeSessionDataSource _dataSource;
    private readonly SettingsService _settings;

    /// <summary>Creates the launcher over the Claude data source and settings.</summary>
    public TerminalResumeLauncher(ClaudeSessionDataSource dataSource, SettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    /// <inheritdoc />
    public bool Resume(string sessionId, string? tabTitle = null)
    {
        if (!ClaudeResumeCommand.IsValidSessionId(sessionId))
        {
            CoreLog.Write($"TerminalResumeLauncher: rejected non-GUID session id '{sessionId}'");
            return false;
        }

        string? cwd = _dataSource.TryGetProjectCwd(sessionId);
        bool skipPermissions = _settings.Current.AppendYolo;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // cmd.exe has no safe inline quoting — pass the workspace as the
                // process working directory instead of embedding a `cd`. The
                // builder cannot fail here: the id was GUID-validated above.
                ClaudeResumeCommand.TryBuildResumeInvocation(
                    sessionId, skipPermissions, out string resume);
                return Launch(
                    "cmd.exe",
                    ["/c", "start", "cmd", "/k", resume],
                    ClaudeResumeCommand.IsUsableCwd(cwd) ? cwd : null);
            }

            ClaudeResumeCommand.TryBuildPosix(
                sessionId, cwd, skipPermissions, out string command);

            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? LaunchMac(command)
                : Launch("x-terminal-emulator", ["-e", "bash", "-lc", command], null);
        }
        catch (Exception ex)
        {
            CoreLog.Write($"TerminalResumeLauncher: EXCEPTION {ex.Message}");
            return false;
        }
    }

    private static bool LaunchMac(string command)
    {
        // AppleScript string literal: escape backslashes then double quotes. The
        // command itself is control-character-free by construction
        // (ClaudeResumeCommand rejects such cwds and ids are GUIDs).
        string escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string script =
            $"tell application \"Terminal\"\nactivate\ndo script \"{escaped}\"\nend tell";
        return Launch("/usr/bin/osascript", ["-e", script], null);
    }

    private static bool Launch(string fileName, string[] args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(psi);
        return process is not null;
    }
}
