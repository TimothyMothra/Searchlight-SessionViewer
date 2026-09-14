using System.Diagnostics;
using System.ComponentModel;
using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// Launches the configured session command in a terminal. This is the headline action
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
    public string? LastError { get; private set; }

    /// <summary>
    /// Creates the launcher. The <paramref name="settings"/> decides whether resumes
    /// share one tabbed window or open a fresh window each time.
    /// </summary>
    public ResumeLauncher(SettingsService settings) => _settings = settings;

    /// <summary>
    /// Runs the configured command for <paramref name="sessionId"/> in Windows Terminal,
    /// window as a new tab (creating that window on first use), falling back to a new
    /// <c>cmd.exe</c> window when Windows Terminal is unavailable. Returns the launched
    /// command when the process was started, or <c>null</c> on failure with
    /// <see cref="LastError"/> explaining validation or launch failures.
    /// </summary>
    /// <param name="sessionId">The session UUID to resume.</param>
    /// <param name="tabTitle">
    /// Optional friendly title for the terminal tab. Falls back to the session id.
    /// </param>
    public string? Resume(string sessionId, string? tabTitle = null)
    {
        LastError = null;
        if (!_settings.Reload())
        {
            LastError = _settings.PersistenceNotice;
            return null;
        }

        string command;
        try { command = ResumeCommandBuilder.Build(_settings.Current, sessionId); }
        catch (ArgumentException ex)
        {
            LastError = ex.Message;
            App.Log($"Resume rejected: {LastError}");
            return null;
        }
        string title = SanitizeTitle(
            string.IsNullOrWhiteSpace(tabTitle) ? sessionId : tabTitle!);

        var terminal = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
        foreach (string argument in new[] { "-w", _settings.Current.UseSharedTerminalWindow ? SharedWindowTarget : "new",
            "new-tab", "--title", title })
            terminal.ArgumentList.Add(argument);
        if (_settings.Current.UseCustomResumeCommand)
        {
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            foreach (string argument in new[] { powershell, "-NoLogo", "-NoProfile", "-EncodedCommand",
                ResumeCommandBuilder.EncodeTerminalTransport(command) })
                terminal.ArgumentList.Add(argument);
        }
        else
        {
            // Only the generated default command has whitespace-delimited tokens;
            // user-authored templates always take the encoded path above.
            foreach (string argument in new[] { "cmd.exe", "/k" }.Concat(command.Split(' ')))
                terminal.ArgumentList.Add(argument);
        }
        if (TryStart(terminal))
        {
            return command;
        }

        // Fallback: no Windows Terminal on this machine → a plain new cmd window.
        // (cmd.exe has no tab concept, so the single-window grouping is best-effort.)
        string fallback = _settings.Current.UseCustomResumeCommand
            ? ResumeCommandBuilder.CmdArguments(command) : $"/k {command}";
        if (TryStart(new ProcessStartInfo("cmd.exe", fallback) { UseShellExecute = true })) return command;
        LastError = "Could not launch Windows Terminal or Command Prompt. See the Searchlight log for details.";
        return null;
    }

    /// <summary>
    /// Strips characters that would break the <c>wt</c> command line (quotes and its
    /// <c>;</c> command delimiter) so an arbitrary workspace name is a safe tab title.
    /// </summary>
    private static string SanitizeTitle(string title) =>
        new(title.Take(200).Select(c => c == '"' ? '\'' : c == ';' || char.IsControl(c) ? ' ' : c).ToArray());

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            App.Log($"Resume: could not start {start.FileName}: {ex.Message}");
            return false;
        }
    }
}
