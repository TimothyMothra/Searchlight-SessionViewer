using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// Watches <c>~/.claude/projects</c> for session-level changes and raises a
/// single debounced <see cref="Changed"/> event. Mirrors <see cref="SessionWatcher"/>:
/// only structural signals count — a <c>.jsonl</c> transcript or a project folder
/// appearing/disappearing/renamed. LastWrite is deliberately excluded because
/// active sessions append to their transcript constantly, which would flood a
/// full reload every couple of seconds. Read-only; it only observes.
/// </summary>
public sealed class ClaudeSessionWatcher : ISessionWatcher
{
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounce;
    private readonly object _gate = new();

    /// <summary>Raised (debounced) when the Claude Code session store changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Creates a watcher over the projects root. If the root does not exist,
    /// the watcher stays inert and never raises events.
    /// </summary>
    /// <param name="debounceMilliseconds">Quiet period before a change is reported.</param>
    public ClaudeSessionWatcher(int debounceMilliseconds = 2000)
    {
        _debounce = new System.Timers.Timer(debounceMilliseconds)
        {
            AutoReset = false,
        };
        _debounce.Elapsed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        if (!Directory.Exists(ClaudePaths.Projects))
        {
            return;
        }

        _watcher = new FileSystemWatcher(ClaudePaths.Projects)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };

        _watcher.Created += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        // Buffer overflow drops notifications silently — force a refresh so the
        // list doesn't go stale until the next manual reload.
        _watcher.Error += (_, _) => Kick();
    }

    /// <summary>Begins raising change notifications.</summary>
    public void Start()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (IsStructural(e.FullPath))
        {
            Kick();
        }
    }

    /// <summary>
    /// True for changes worth a full list refresh: a project folder directly
    /// under the root, or a session <c>.jsonl</c> transcript. Everything else
    /// (memory files, index rewrites, subdirectories) is churn.
    /// </summary>
    private static bool IsStructural(string fullPath)
    {
        string rel = Path.GetRelativePath(ClaudePaths.Projects, fullPath);
        if (string.IsNullOrEmpty(rel) || rel == ".")
        {
            return false;
        }

        int sep = 0;
        foreach (char ch in rel)
        {
            if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
            {
                sep++;
            }
        }

        // Depth 0 => a project folder itself appearing/disappearing.
        if (sep == 0)
        {
            return true;
        }

        // Depth 1 => only new/removed session transcripts matter.
        return sep == 1
            && fullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private void Kick()
    {
        lock (_gate)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
