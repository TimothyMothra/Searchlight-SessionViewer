using System.Globalization;
using System.Text;
using Searchlight.Abstractions;
using Searchlight.Diagnostics;

namespace Searchlight.Services;

/// <summary>Confirms native lock ownership without modifying Copilot files.</summary>
public sealed class SessionActivityMonitor(ICopilotProcessProbe processes)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _activeLocks = new(StringComparer.Ordinal);

    /// <summary>Checks and tracks ownership, including idle or waiting background owners.</summary>
    public bool HasLiveOwner(string lockPath)
    {
        lock (_gate)
        {
            bool active = CheckOwner(lockPath);
            if (active) _activeLocks.Add(lockPath);
            else _activeLocks.Remove(lockPath);
            return active;
        }
    }

    /// <summary>Polls only previously confirmed locks; no catalog or event-log reads.</summary>
    public bool CheckForExitedOwners()
    {
        lock (_gate)
        {
            bool changed = false;
            foreach (string path in _activeLocks.ToArray())
            {
                if (CheckOwner(path)) continue;
                _activeLocks.Remove(path);
                changed = true;
            }
            return changed;
        }
    }

    internal static bool IsLockFile(string name) =>
        name.Length >= 11
        && name.StartsWith("inuse.", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    private bool CheckOwner(string path)
    {
        string name = Path.GetFileName(path);
        if (!IsLockFile(name)) return false;
        if (!int.TryParse(name.AsSpan(6, name.Length - 11), NumberStyles.None,
                CultureInfo.InvariantCulture, out int pid) || pid <= 0)
        {
            CoreLog.Write($"Session activity: malformed lock name: {name}");
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[33];
            int count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count == buffer.Length || !int.TryParse(Encoding.UTF8.GetString(buffer[..count]).AsSpan().Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int contentsPid) || contentsPid != pid)
            {
                CoreLog.Write($"Session activity: malformed or mismatched PID in {name}");
                return false;
            }

            var file = new FileInfo(path);
            if (!file.Exists) return false;
            DateTimeOffset written = file.LastWriteTimeUtc;
            DateTimeOffset? started = processes.GetStartTimeUtc(pid);
            // ASSUMPTION: native local locks contain their owner's PID and are written
            // at acquisition, not as heartbeats. A later process start means PID reuse.
            // File age is NOT activity: an idle owner can legitimately live for days.
            return started.HasValue && started.Value <= written && written <= DateTimeOffset.UtcNow;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Write($"Session activity: cannot verify {name}: {ex.Message}");
            return false;
        }
    }
}
