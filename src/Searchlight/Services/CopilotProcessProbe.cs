using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Searchlight.Abstractions;
using Searchlight.Diagnostics;

namespace Searchlight.Services;

public sealed class CopilotProcessProbe : ICopilotProcessProbe
{
    public DateTimeOffset? GetStartTimeUtc(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!IsCopilotName(process.ProcessName, processId))
            {
                CoreLog.Write($"Session activity: PID {processId} is not a recognized Copilot executable.");
                return null;
            }
            DateTimeOffset started = process.StartTime.ToUniversalTime();
            return process.HasExited ? null : started;
        }
        catch (ArgumentException) { return null; } // PID no longer exists.
        catch (InvalidOperationException) { return null; } // Exited during the probe.
        catch (Win32Exception ex)
        {
            CoreLog.Write($"Session activity: cannot inspect PID {processId}: {ex.Message}");
            return null;
        }
    }

    internal static bool IsCopilotName(string name, int processId)
    {
        if (name.Equals("copilot", StringComparison.OrdinalIgnoreCase)) return true;
        // ASSUMPTION: the native updater can rename a still-running executable to
        // copilot.exe.old-<PID>-<timestamp>. Do not reject that observed owner shape.
        string prefix = $"copilot.exe.old-{processId.ToString(CultureInfo.InvariantCulture)}-";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(name.AsSpan(prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out long timestamp) && timestamp > 0;
    }
}
