using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Enumerates the per-session folders under <c>~/.copilot/session-state</c> and
/// produces base <see cref="SessionInfo"/> records: folder facts,
/// <c>workspace.yaml</c>, and cheap file-presence flags. Heavy per-session
/// parsing (events.jsonl, SQLite) is deferred to the aggregator so the initial
/// scan of ~500 folders stays fast. Read-only throughout.
/// </summary>
public sealed class SessionStateScanner
{
    private const string ChatPrefix = "optimistic-chat-";
    internal const int CatalogConcurrency = 4;

    private readonly WorkspaceYamlReader _workspaceReader;
    private readonly SessionActivityMonitor? _activity;
    private readonly string _root;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, FolderState> _cache = [];
    private readonly SemaphoreSlim _readSlots = new(CatalogConcurrency);
    private readonly Action<string>? _beforeFolderRead;
    private long _generation;

    private readonly record struct SummaryVersion(long FolderTicks, FileVersion Workspace, FileVersion Checkpoints);
    private sealed record Summary(SummaryVersion Version, SessionInfo Session, string[] Locks);

    private sealed class FolderState
    {
        public readonly object Gate = new();
        public long LastAccess;
        public Summary? Cached;
    }

    /// <summary>Creates a scanner using the given workspace.yaml reader.</summary>
    public SessionStateScanner(WorkspaceYamlReader workspaceReader, SessionActivityMonitor? activity = null)
        : this(workspaceReader, CopilotPaths.SessionState, activity) { }

    internal SessionStateScanner(WorkspaceYamlReader workspaceReader, string root,
        SessionActivityMonitor? activity = null, Action<string>? beforeFolderRead = null)
    {
        _workspaceReader = workspaceReader;
        _root = root;
        _activity = activity;
        _beforeFolderRead = beforeFolderRead;
    }

    /// <summary>
    /// Scans every session folder, newest first. Missing root yields an empty
    /// sequence; individual folder failures are skipped rather than fatal.
    /// </summary>
    public IReadOnlyList<SessionInfo> Scan()
    {
        return ScanCheap().Select(s => s.IsEnriched ? s : EnrichFolder(s)).ToArray();
    }

    /// <summary>
    /// Fast first pass over every session folder, newest first. Reads only cheap
    /// folder facts (name, id, kind, last-write time) — NO <c>workspace.yaml</c>
    /// parse and NO per-folder sub-enumeration for presence flags. This lets the UI
    /// publish placeholder rows for all ~500 folders in a few hundred milliseconds;
    /// each row is upgraded later via <see cref="EnrichFolder"/>. Missing root yields
    /// an empty sequence; individual folder failures are skipped rather than fatal.
    /// </summary>
    public IReadOnlyList<SessionInfo> ScanCheap()
    {
        long generation;
        lock (_cacheGate) generation = ++_generation;
        if (!Directory.Exists(_root))
        {
            EvictAbsent([], generation);
            return [];
        }

        // Enumerate on the caller so root failures retain their original exception.
        // Indexed slots bound worker count; the final sort resolves equal-time ties.
        string[] folders = Directory.GetDirectories(_root);
        var rows = new SessionInfo?[folders.Length];
        Parallel.For(0, folders.Length,
            new ParallelOptions { MaxDegreeOfParallelism = CatalogConcurrency },
            i => rows[i] = ScanFolderCheap(folders[i]));

        var results = rows.OfType<SessionInfo>().ToList();
        EvictAbsent(results.Select(s => s.FolderPath).ToHashSet(StringComparer.Ordinal), generation);
        results.Sort(static (a, b) =>
        {
            int byTime = b.LastWriteTime.CompareTo(a.LastWriteTime);
            return byTime != 0 ? byTime : StringComparer.Ordinal.Compare(a.FolderPath, b.FolderPath);
        });
        return results;
    }

    private void EvictAbsent(HashSet<string> present, long generation)
    {
        lock (_cacheGate)
        {
            // A concurrent read may have discovered/published a folder after this
            // pass began. An older catalog must not evict that newer state.
            foreach (string deleted in _cache.Where(p =>
                p.Value.LastAccess <= generation && !present.Contains(p.Key)).Select(p => p.Key).ToArray())
                _cache.Remove(deleted);
        }
    }

    private FolderState GetFolderState(string path)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(path, out var state))
                _cache[path] = state = new FolderState();
            state.LastAccess = ++_generation;
            return state;
        }
    }

    /// <summary>
    /// Builds a base <see cref="SessionInfo"/> for a single folder, or returns
    /// <c>null</c> if the folder is unreadable. Handles empty/fileless
    /// <c>optimistic-chat-*</c> folders gracefully. Composes the cheap folder
    /// scan (<see cref="ScanFolderCheap"/>) with the expensive enrichment
    /// (<see cref="EnrichFolder"/>).
    /// </summary>
    public SessionInfo? ScanFolder(string folderPath)
    {
        SessionInfo? cheap = ScanFolderCheap(folderPath);
        return cheap is null ? null : EnrichFolder(cheap);
    }

    /// <summary>
    /// Cheap per-folder scan: folder name, session id, kind, and last-write time
    /// only. Skips <c>workspace.yaml</c> and the three presence-flag sub-enumerations
    /// (lock/plan/checkpoints), so the caller can materialise a placeholder row
    /// immediately. Returns <c>null</c> if the folder is unreadable.
    /// </summary>
    public SessionInfo? ScanFolderCheap(string folderPath)
    {
        try
        {
            FolderState state = GetFolderState(folderPath);
            // ASSUMPTION: serialize reads of the same folder, not all folders.
            // Publication cannot race a later summary/activity read. An evicted
            // state stays detached; a finishing old read never reinserts it.
            // Same-folder waiters must not occupy the shared reader slots.
            lock (state.Gate)
            {
                _readSlots.Wait();
                try
                {
                    _beforeFolderRead?.Invoke(folderPath);
                    return ScanFolderCheapCore(folderPath, state);
                }
                finally { _readSlots.Release(); }
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private SessionInfo? ScanFolderCheapCore(string folderPath, FolderState state)
    {
        string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        bool isChat = folderName.StartsWith(ChatPrefix, StringComparison.OrdinalIgnoreCase);
        string id = isChat ? folderName[ChatPrefix.Length..] : folderName;

        // Query the directory itself: NTFS parent enumeration can report a
        // stale timestamp immediately after a child file has been written.
        var directory = new DirectoryInfo(folderPath);
        if (!directory.Exists) return null;
        DateTimeOffset lastWrite = directory.LastWriteTimeUtc;

        // Cold discovery needs no per-folder probes. Warm refreshes inspect
        // versions, but only changed sessions reparse YAML/enumerate flags.
        if (state.Cached is { } cached && cached.Version == VersionFor(folderPath, lastWrite))
            return RefreshActivity(state, cached);

        return new SessionInfo
        {
            Id = id,
            FolderName = folderName,
            FolderPath = folderPath,
            Kind = isChat ? SessionKind.Chat : SessionKind.Project,
            LastWriteTime = lastWrite,
        };
    }

    /// <summary>
    /// Upgrades a cheap placeholder (from <see cref="ScanFolderCheap"/>) with the
    /// expensive per-folder facts: <c>workspace.yaml</c> metadata plus the
    /// lock/plan/session-db/checkpoint/events presence flags. Returns a copy; on
    /// failure returns the input unchanged so a bad folder never drops the row —
    /// which also leaves <see cref="SessionInfo.IsEnriched"/> false, so the list's
    /// hide filters treat it as "unknown" rather than empty.
    /// </summary>
    public SessionInfo EnrichFolder(SessionInfo session)
    {
        try
        {
            string folderPath = session.FolderPath;
            FolderState state = GetFolderState(folderPath);
            lock (state.Gate)
            {
                _readSlots.Wait();
                try
                {
                    _beforeFolderRead?.Invoke(folderPath);
                    return EnrichFolderCore(session, state);
                }
                finally { _readSlots.Release(); }
            }
        }
        catch (Exception)
        {
            return session;
        }
    }

    private SessionInfo EnrichFolderCore(SessionInfo session, FolderState state)
    {
        string folderPath = session.FolderPath;
        DateTimeOffset lastWrite = new DirectoryInfo(folderPath).LastWriteTimeUtc;
        SummaryVersion version = VersionFor(folderPath, lastWrite);
        if (state.Cached is { } cached && cached.Version == version)
            return RefreshActivity(state, cached);
        WorkspaceMetadata? workspace = _workspaceReader.Read(folderPath);

        // One directory enumeration replaces separate lock/plan enumerations
        // and events/database existence probes.
        bool plan = false, database = false, events = false;
        List<string> locks = [];
        foreach (string file in Directory.EnumerateFiles(folderPath))
        {
            string name = Path.GetFileName(file);
            if (SessionActivityMonitor.IsLockFile(name)) locks.Add(file);
            plan |= name.StartsWith("plan", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            database |= name.Equals("session.db", StringComparison.OrdinalIgnoreCase);
            events |= name.Equals("events.jsonl", StringComparison.OrdinalIgnoreCase);
        }
        SessionInfo enriched = session with
        {
            LastWriteTime = lastWrite,
            Workspace = workspace,
            IsInUse = HasLiveOwner(locks),
            HasPlan = plan,
            HasSessionDb = database,
            HasCheckpoints = HasCheckpointContent(folderPath),
            HasEvents = events,
            IsEnriched = true,
        };
        state.Cached = new(version, enriched, locks.ToArray());
        return enriched;
    }

    private SessionInfo RefreshActivity(FolderState state, Summary cached)
    {
        // ASSUMPTION: process exit need not touch the folder. Recheck ownership
        // even when all summary versions match, retaining unchanged row identities.
        bool inUse = HasLiveOwner(cached.Locks);
        if (cached.Session.IsInUse == inUse) return cached.Session;
        SessionInfo refreshed = cached.Session with { IsInUse = inUse };
        state.Cached = cached with { Session = refreshed };
        return refreshed;
    }

    private bool HasLiveOwner(IEnumerable<string> locks)
    {
        bool active = false;
        // Check every lock so multiple owners are monitored, not only the first.
        foreach (string path in locks) active |= _activity?.HasLiveOwner(path) == true;
        return active;
    }

    private static SummaryVersion VersionFor(string folderPath, DateTimeOffset lastWrite) =>
        new(lastWrite.UtcTicks, FileVersion.Read(CopilotPaths.WorkspaceYaml(folderPath)),
            FileVersion.ReadDirectory(CopilotPaths.CheckpointsDir(folderPath)));

    private static bool HasCheckpointContent(string folderPath)
    {
        string dir = CopilotPaths.CheckpointsDir(folderPath);
        try
        {
            return Directory.Exists(dir) &&
                Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
