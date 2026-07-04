namespace Searchlight.Abstractions;

/// <summary>
/// Launches a resumed agent session in a terminal. Abstracted so the
/// platform-neutral core can reference it without taking a dependency on any
/// concrete terminal launcher. The WinUI front-end implements this for Copilot
/// over <c>wt.exe</c>/<c>cmd.exe</c>; the Avalonia front-end implements it for
/// Claude Code over the platform terminal; a mock implementation is used for
/// the demo/mock data source.
/// </summary>
public interface IResumeLauncher
{
    /// <summary>
    /// Resumes the session with id <paramref name="sessionId"/>, optionally titling
    /// the terminal tab <paramref name="tabTitle"/>. Returns the launched CLI command
    /// (e.g. <c>copilot --resume=&lt;id&gt; --yolo</c>) when the launch was dispatched
    /// successfully, or <c>null</c> when it failed. Returning the command lets callers
    /// surface the exact string that was run.
    /// </summary>
    string? Resume(string sessionId, string? tabTitle = null);
}
