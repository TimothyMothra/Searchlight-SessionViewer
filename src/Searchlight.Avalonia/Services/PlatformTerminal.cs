using System.Diagnostics;
using System.Runtime.InteropServices;
using Searchlight.Diagnostics;
using Searchlight.Services;

namespace Searchlight.Avalonia.Services;

/// <summary>
/// Opens a command in the platform's terminal for the resume launchers.
/// macOS uses Terminal.app via <c>osascript</c> (script built and escaped by
/// <see cref="MacTerminalScript"/>); Linux tries <c>x-terminal-emulator</c>
/// then common terminals; Windows opens a new <c>cmd</c> window (cmd has no
/// safe inline quoting, so callers pass any working directory as the process
/// working directory instead of embedding a <c>cd</c>). Callers are
/// responsible for the command being shell-safe — both resume-command builders
/// only emit GUID-validated ids and quoted, control-character-free paths.
/// </summary>
internal static class PlatformTerminal
{
    /// <summary>
    /// Terminals tried in order on Linux. <c>x-terminal-emulator</c> is the
    /// Debian alternatives entry point; the rest cover distros without it.
    /// </summary>
    private static readonly string[] LinuxTerminals =
        ["x-terminal-emulator", "gnome-terminal", "konsole", "xterm"];

    /// <summary>
    /// Opens <paramref name="command"/> in a new terminal window and returns
    /// true when the launch was dispatched. On Windows
    /// <paramref name="windowsWorkingDirectory"/> (when non-null) becomes the
    /// new window's working directory; other platforms embed any needed
    /// <c>cd</c> in the command itself.
    /// </summary>
    public static bool Open(string command, string? windowsWorkingDirectory = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Launch(
                "cmd.exe", ["/c", "start", "cmd", "/k", command], windowsWorkingDirectory);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Launch("/usr/bin/osascript", ["-e", MacTerminalScript.Build(command)], null);
        }

        foreach (string terminal in LinuxTerminals)
        {
            // gnome-terminal's -e takes ONE command-string argument (and would
            // silently swallow the payload given the xterm-style vector while
            // Process.Start still succeeds); its modern spelling is `--` with
            // the command as the remaining argv. The others follow xterm's
            // `-e CMD ARGS…` convention.
            string[] args = terminal == "gnome-terminal"
                ? ["--", "bash", "-lc", command]
                : ["-e", "bash", "-lc", command];

            try
            {
                if (Launch(terminal, args, null))
                {
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed / not on PATH — try the next candidate.
            }
        }

        CoreLog.Write(
            "PlatformTerminal: no terminal emulator found "
            + $"(tried {string.Join(", ", LinuxTerminals)})");
        return false;
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
