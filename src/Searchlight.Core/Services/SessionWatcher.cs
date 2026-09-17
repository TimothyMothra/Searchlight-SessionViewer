using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// Watches <c>~/.copilot/session-state</c> for folder-level changes and raises a
/// single debounced <see cref="Changed"/> event, so the UI can refresh the list
/// without reacting to every individual filesystem notification. The watcher is
/// read-only; it only observes. Debounce coalesces bursts (a session writing many
/// files) into one refresh.
/// </summary>
public sealed class SessionWatcher : ISessionWatcher
{
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounce;
    private readonly System.Timers.Timer _ownerPoll;
    private readonly object _gate = new();
    private readonly string _root;
    private bool _disposed;

    /// <summary>Raised (debounced) when session-state content changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Creates a watcher over the session-state root. If the root does not exist,
    /// the watcher stays inert and never raises events.
    /// </summary>
    /// <param name="debounceMilliseconds">Quiet period before a change is reported.</param>
    public SessionWatcher(SessionActivityMonitor activity, int debounceMilliseconds = 2000)
        : this(activity, CopilotPaths.SessionState, debounceMilliseconds, 10000) { }

    internal SessionWatcher(SessionActivityMonitor activity, string root,
        int debounceMilliseconds, int ownerPollMilliseconds)
    {
        _root = root;
        _debounce = new System.Timers.Timer(debounceMilliseconds)
        {
            AutoReset = false,
        };
        _debounce.Elapsed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _ownerPoll = new System.Timers.Timer(ownerPollMilliseconds);
        _ownerPoll.Elapsed += (_, _) =>
        {
            lock (_gate)
            {
                if (!_disposed && activity.CheckForExitedOwners()) Kick();
            }
        };

        if (!Directory.Exists(root))
        {
            return;
        }

        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            // Deliberately exclude LastWrite: active sessions append to events.jsonl
            // and .log files constantly, which would flood a full reload every couple
            // seconds and churn the list selection. We only care about structural
            // signals — a session folder appearing/disappearing/renamed, and the
            // inuse.<PID>.lock file being created/removed. Owner exits without file
            // changes are checked separately, without periodic catalog reloads.
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.CreationTime,
        };

        _watcher.Created += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
    }

    /// <summary>Begins raising change notifications.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = true;
                _ownerPoll.Start();
            }
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
    /// True only for changes worth a full list refresh: a session folder
    /// appearing / disappearing / being renamed directly under session-state
    /// (depth 0), or an <c>inuse.&lt;PID&gt;.lock</c> file (drives the "In use"
    /// badge). Everything deeper — SQLite <c>-wal</c>/<c>-shm</c>/journal files,
    /// checkpoints, events.jsonl, logs — is transient churn that active sessions
    /// rewrite constantly and must NOT trigger a re-scan of all session folders.
    /// </summary>
    private bool IsStructural(string fullPath)
    {
        string rel = Path.GetRelativePath(_root, fullPath);
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

        // Depth 0 => a direct child of session-state, i.e. a session folder itself.
        if (sep == 0)
        {
            return true;
        }

        // Otherwise only the in-use lock file matters for a live badge update.
        string name = Path.GetFileName(fullPath);
        return sep == 1 && SessionActivityMonitor.IsLockFile(name);
    }

    private void Kick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _ownerPoll.Dispose();
            _debounce.Dispose();
        }
    }
}
