using Searchlight.Abstractions;
using Searchlight.Services;

namespace Searchlight.Avalonia.Services;

/// <summary>
/// Resume launcher for the combined Claude + Copilot view: routes each resume
/// to the CLI that owns the session. Ownership is decided by
/// <see cref="ClaudeSessionDataSource.OwnsSession"/> (cache-only, populated by
/// the list load that always precedes a resume); anything the Claude store
/// does not know is treated as a Copilot session.
/// </summary>
public sealed class RoutingResumeLauncher : IResumeLauncher
{
    private readonly ClaudeSessionDataSource _claudeSource;
    private readonly ClaudeTerminalResumeLauncher _claude;
    private readonly CopilotTerminalResumeLauncher _copilot;

    /// <summary>Creates the router over the two concrete launchers.</summary>
    public RoutingResumeLauncher(
        ClaudeSessionDataSource claudeSource,
        ClaudeTerminalResumeLauncher claude,
        CopilotTerminalResumeLauncher copilot)
    {
        _claudeSource = claudeSource;
        _claude = claude;
        _copilot = copilot;
    }

    /// <inheritdoc />
    public string? Resume(string sessionId, string? tabTitle = null) =>
        _claudeSource.OwnsSession(sessionId)
            ? _claude.Resume(sessionId, tabTitle)
            : _copilot.Resume(sessionId, tabTitle);
}
