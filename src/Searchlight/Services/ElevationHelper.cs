using System;
using System.Diagnostics;
using System.Security.Principal;

namespace Searchlight.Services;

/// <summary>
/// Helpers for detecting and requesting process elevation. Used to opt into
/// running the tray app as Administrator so its <c>wt -w</c> resume calls can
/// share a tab with an elevated (Admin) Windows Terminal window — Windows blocks
/// cross-integrity-level window reuse, so both must run at the same level.
/// </summary>
public static class ElevationHelper
{
    /// <summary>True if the current process is running elevated (as Administrator).</summary>
    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // If we cannot determine the token, assume non-elevated (safer default).
            return false;
        }
    }

    /// <summary>
    /// Relaunches this same executable elevated via the ShellExecute <c>runas</c>
    /// verb (triggers a UAC consent prompt). Returns <c>true</c> if the elevated
    /// instance was started — the caller should then exit this instance. Returns
    /// <c>false</c> if the user cancelled the UAC prompt or the launch failed, in
    /// which case the caller stays non-elevated.
    /// </summary>
    public static bool RelaunchElevated()
    {
        // Environment.ProcessPath is the full path to the running .exe.
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true, // required for the runas verb / UAC
                Verb = "runas",
            };
            // ASSUMPTION: package elevation relaunches this same channel. Preserve
            // demo/no-tray arguments rather than silently changing its launch mode.
            foreach (string argument in Environment.GetCommandLineArgs().Skip(1))
                psi.ArgumentList.Add(argument);
            return Process.Start(psi) is not null;
        }
        catch (Exception)
        {
            // Win32Exception 1223 (ERROR_CANCELLED) = user declined the UAC prompt;
            // any other failure also means elevation did not happen.
            return false;
        }
    }

    /// <summary>
    /// Relaunches this same executable NON-elevated from an elevated process.
    /// A process cannot de-elevate itself, and a plain <see cref="Process.Start"/>
    /// would inherit this process's Administrator token. Launching through
    /// <c>explorer.exe</c> hands the request to the shell, which starts the target
    /// at the logged-on user's medium-integrity token instead. Returns <c>true</c>
    /// if the launch was dispatched (the caller should then exit this instance).
    /// </summary>
    public static bool RelaunchNonElevated()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        try
        {
            // UseShellExecute=false so we invoke explorer.exe directly; explorer
            // then spawns the exe de-elevated. Note: the returned Process is the
            // transient explorer invocation, not the target app, so we cannot
            // track the new instance's PID from here.
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{exePath}\"",
                UseShellExecute = false,
            };
            return Process.Start(psi) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
