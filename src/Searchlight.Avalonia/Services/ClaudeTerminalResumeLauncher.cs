using Searchlight.Abstractions;
using Searchlight.Diagnostics;
using Searchlight.Services;
using System.Runtime.InteropServices;

namespace Searchlight.Avalonia.Services;

/// <summary>
/// Resumes a Claude Code session in the platform terminal:
/// <c>cd &lt;workspace&gt; &amp;&amp; claude --resume &lt;id&gt;</c>. Claude Code
/// scopes sessions to the directory they ran in, so the launcher resolves the
/// workspace via <see cref="ClaudeSessionDataSource.TryGetProjectCwd"/> before
/// launching. Command construction is delegated to
/// <see cref="ClaudeResumeCommand"/>, which rejects non-GUID session ids and
/// unusable workspace paths — both originate from on-disk content and are not
/// trusted. The terminal itself is opened by <see cref="PlatformTerminal"/>.
/// </summary>
public sealed class ClaudeTerminalResumeLauncher : IResumeLauncher
{
    private readonly ClaudeSessionDataSource _dataSource;
    private readonly SettingsService _settings;

    /// <summary>Creates the launcher over the Claude data source and settings.</summary>
    public ClaudeTerminalResumeLauncher(ClaudeSessionDataSource dataSource, SettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    /// <inheritdoc />
    public bool Resume(string sessionId, string? tabTitle = null)
    {
        if (!ClaudeResumeCommand.IsValidSessionId(sessionId))
        {
            CoreLog.Write($"ClaudeTerminalResumeLauncher: rejected non-GUID session id '{sessionId}'");
            return false;
        }

        string? cwd = _dataSource.TryGetProjectCwd(sessionId);
        bool skipPermissions = _settings.Current.AppendYolo;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows takes the workspace as the process working directory
                // instead of an embedded `cd`. The builder cannot fail here:
                // the id was GUID-validated above.
                ClaudeResumeCommand.TryBuildResumeInvocation(
                    sessionId, skipPermissions, out string resume);
                return PlatformTerminal.Open(
                    resume, ClaudeResumeCommand.IsUsableCwd(cwd) ? cwd : null);
            }

            ClaudeResumeCommand.TryBuildPosix(
                sessionId, cwd, skipPermissions, out string command);
            return PlatformTerminal.Open(command);
        }
        catch (Exception ex)
        {
            CoreLog.Write($"ClaudeTerminalResumeLauncher: EXCEPTION {ex.Message}");
            return false;
        }
    }
}
