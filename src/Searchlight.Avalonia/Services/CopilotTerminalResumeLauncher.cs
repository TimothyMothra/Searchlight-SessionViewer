using Searchlight.Abstractions;
using Searchlight.Diagnostics;
using Searchlight.Services;

namespace Searchlight.Avalonia.Services;

/// <summary>
/// Resumes a Copilot CLI session in the platform terminal:
/// <c>copilot --resume=&lt;id&gt;</c>. Copilot sessions are not scoped to a
/// working directory, so no <c>cd</c> is needed (mirroring the WinUI host's
/// launcher). Command construction is delegated to
/// <see cref="CopilotResumeCommand"/>, which rejects non-GUID session ids —
/// they originate from on-disk folder names and are not trusted. The terminal
/// itself is opened by <see cref="PlatformTerminal"/>.
/// </summary>
public sealed class CopilotTerminalResumeLauncher : IResumeLauncher
{
    private readonly SettingsService _settings;

    /// <summary>Creates the launcher over the app settings.</summary>
    public CopilotTerminalResumeLauncher(SettingsService settings) => _settings = settings;

    /// <inheritdoc />
    public bool Resume(string sessionId, string? tabTitle = null)
    {
        if (!CopilotResumeCommand.TryBuild(
                sessionId, _settings.Current.AppendYolo, out string command))
        {
            CoreLog.Write(
                $"CopilotTerminalResumeLauncher: rejected non-GUID session id '{sessionId}'");
            return false;
        }

        try
        {
            return PlatformTerminal.Open(command);
        }
        catch (Exception ex)
        {
            CoreLog.Write($"CopilotTerminalResumeLauncher: EXCEPTION {ex.Message}");
            return false;
        }
    }
}
