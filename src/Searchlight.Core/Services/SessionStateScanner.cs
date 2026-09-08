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

    private readonly WorkspaceYamlReader _workspaceReader;
    private readonly string _root;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, (SummaryVersion Version, SessionInfo Session)> _cache = [];

    private readonly record struct SummaryVersion(long FolderTicks, FileVersion Workspace, FileVersion Checkpoints);

    /// <summary>Creates a scanner using the given workspace.yaml reader.</summary>
    public SessionStateScanner(WorkspaceYamlReader workspaceReader)
        : this(workspaceReader, CopilotPaths.SessionState) { }

    internal SessionStateScanner(WorkspaceYamlReader workspaceReader, string root)
    {
        _workspaceReader = workspaceReader;
        _root = root;
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
        if (!Directory.Exists(_root))
        {
            lock (_cacheGate) _cache.Clear();
            return [];
        }

        var results = new List<SessionInfo>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in Directory.EnumerateDirectories(_root))
        {
            SessionInfo? info = ScanFolderCheap(folder);
            if (info is not null)
            {
                present.Add(info.FolderPath);
                results.Add(info);
            }
        }

        lock (_cacheGate)
        {
            foreach (string deleted in _cache.Keys.Where(k => !present.Contains(k)).ToArray())
                _cache.Remove(deleted);
        }
        results.Sort(static (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
        return results;
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
            string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
            bool isChat = folderName.StartsWith(ChatPrefix, StringComparison.OrdinalIgnoreCase);
            string id = isChat ? folderName[ChatPrefix.Length..] : folderName;

            // Query the directory itself: NTFS parent enumeration can report a
            // stale timestamp immediately after a child file has been written.
            DateTimeOffset lastWrite = new DirectoryInfo(folderPath).LastWriteTimeUtc;

            lock (_cacheGate)
            {
                // Cold discovery needs no per-folder probes. Warm refreshes inspect
                // versions, but only changed sessions reparse YAML/enumerate flags.
                if (_cache.TryGetValue(folderPath, out var cached)
                    && cached.Version == VersionFor(folderPath, lastWrite))
                    return cached.Session;
            }

            return new SessionInfo
            {
                Id = id,
                FolderName = folderName,
                FolderPath = folderPath,
                Kind = isChat ? SessionKind.Chat : SessionKind.Project,
                LastWriteTime = lastWrite,
            };
        }
        catch (Exception)
        {
            return null;
        }
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
            DateTimeOffset lastWrite = new DirectoryInfo(folderPath).LastWriteTimeUtc;
            SummaryVersion version = VersionFor(folderPath, lastWrite);
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(folderPath, out var cached) && cached.Version == version)
                    return cached.Session;
            }
            WorkspaceMetadata? workspace = _workspaceReader.Read(folderPath);

            // One directory enumeration replaces separate lock/plan enumerations
            // and events/database existence probes.
            bool inUse = false, plan = false, database = false, events = false;
            foreach (string file in Directory.EnumerateFiles(folderPath))
            {
                string name = Path.GetFileName(file);
                inUse |= name.StartsWith("inuse.", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
                plan |= name.StartsWith("plan", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                database |= name.Equals("session.db", StringComparison.OrdinalIgnoreCase);
                events |= name.Equals("events.jsonl", StringComparison.OrdinalIgnoreCase);
            }
            SessionInfo enriched = session with
            {
                LastWriteTime = lastWrite,
                Workspace = workspace,
                IsInUse = inUse,
                HasPlan = plan,
                HasSessionDb = database,
                HasCheckpoints = HasCheckpointContent(folderPath),
                HasEvents = events,
                IsEnriched = true,
            };
            lock (_cacheGate) _cache[folderPath] = (version, enriched);
            return enriched;
        }
        catch (Exception)
        {
            return session;
        }
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
