using System.Diagnostics;
using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// Launches <c>copilot resume &lt;id&gt;</c> in a terminal. This is the headline action
/// for a selected session. The app itself never resumes a session in-process; it hands
/// off to the Copilot CLI. When Windows Terminal is available every resume is routed as
/// a new tab into the user's most-recently-used Terminal window (<see cref="SharedWindowTarget"/>),
/// so resumed sessions land in the terminal the user already has open instead of a
/// separate app-owned window or a window per session.
/// </summary>
public sealed class ResumeLauncher : IResumeLauncher
{
    /// <summary>
    /// Windows Terminal reserved target meaning "the most-recently-used window". Using
    /// <c>last</c> (rather than a dedicated app-named window) makes resumed tabs attach to
    /// the terminal the user already has open; if no Terminal window exists one is created.
    /// </summary>
    private const string SharedWindowTarget = "last";

    private readonly SettingsService _settings;

    /// <summary>
    /// Creates the launcher. The <paramref name="settings"/> decides whether resumes
    /// share one tabbed window or open a fresh window each time.
    /// </summary>
    public ResumeLauncher(SettingsService settings) => _settings = settings;

    /// <summary>
    /// Opens <c>copilot resume &lt;sessionId&gt;</c> in the shared Windows Terminal
    /// window as a new tab (creating that window on first use), falling back to a new
    /// <c>cmd.exe</c> window when Windows Terminal is unavailable. Returns the launched
    /// CLI command when the process was started, or <c>null</c> on failure. A blank id
    /// is rejected.
    /// </summary>
    /// <param name="sessionId">The session UUID to resume.</param>
    /// <param name="tabTitle">
    /// Optional friendly title for the terminal tab. Falls back to the session id.
    /// </param>
    public string? Resume(string sessionId, string? tabTitle = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        // ASSUMPTION: `copilot` is on the user's PATH (it is, since sessions are
        // produced by the Copilot CLI).
        // The CLI resume syntax is `copilot --resume=<id>` (there is no bare
        // `resume` subcommand; that form errors with "Invalid command format").
        // Opt-in `--yolo` auto-approves all tool actions in the resumed session.
        string yolo = _settings.Current.AppendYolo ? " --yolo" : string.Empty;
        string command = $"copilot --resume={sessionId}{yolo}";
        string title = SanitizeTitle(
            string.IsNullOrWhiteSpace(tabTitle) ? sessionId : tabTitle!);

        // Shared (default): `-w last` targets the user's most-recently-used Terminal
        // window — so the resumed tab attaches to the terminal they already have open
        // (creating one only if none exists). Opt-out: `-w new` forces a brand-new
        // Windows Terminal window per resume. GUIDs contain no ';' so the copilot
        // command needs no wt delimiter escaping.
        string windowArg = _settings.Current.UseSharedTerminalWindow ? $"-w {SharedWindowTarget}" : "-w new";
        string wtArgs = $"{windowArg} new-tab --title \"{title}\" cmd /k {command}";
        if (TryStart("wt.exe", wtArgs))
        {
            return command;
        }

        // Fallback: no Windows Terminal on this machine → a plain new cmd window.
        // (cmd.exe has no tab concept, so the single-window grouping is best-effort.)
        return TryStart("cmd.exe", $"/k {command}") ? command : null;
    }

    /// <summary>
    /// Strips characters that would break the <c>wt</c> command line (quotes and its
    /// <c>;</c> command delimiter) so an arbitrary workspace name is a safe tab title.
    /// </summary>
    private static string SanitizeTitle(string title) =>
        title.Replace("\"", string.Empty).Replace(";", " ").Trim();

    private static bool TryStart(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
            };
            return Process.Start(psi) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
