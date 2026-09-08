using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Merges every data source into <see cref="SessionInfo"/> records keyed by
/// session UUID. The <see cref="LoadAll"/> pass is cheap: folder scan +
/// <c>workspace.yaml</c> + bulk snapshot-index and journal enrichment. The
/// per-session heavy work (head-parsing <c>events.jsonl</c>) is deferred to
/// <see cref="EnrichWithEvents"/> so a 500-folder scan stays responsive.
/// All access is read-only.
/// </summary>
public sealed class SessionAggregator
{
    private readonly SessionStateScanner _scanner;
    private readonly EventsJsonlReader _eventsReader;
    private readonly SnapshotIndexReader _snapshotReader;
    private readonly JournalReader _journalReader;

    // Lazily-loaded bulk enrichment maps, cached on first EnrichOne call so the
    // background enrichment pass reads the snapshot index + journal exactly once
    // rather than per session. Populated together; guarded by _bulkLoaded.
    private IReadOnlyDictionary<string, SnapshotSummary>? _snapshotCache;
    private IReadOnlyDictionary<string, JournalEntry>? _journalCache;
    private bool _bulkLoaded;
    private readonly object _bulkGate = new();

    /// <summary>Creates an aggregator over the given readers.</summary>
    public SessionAggregator(
        SessionStateScanner scanner,
        EventsJsonlReader eventsReader,
        SnapshotIndexReader snapshotReader,
        JournalReader journalReader)
    {
        _scanner = scanner;
        _eventsReader = eventsReader;
        _snapshotReader = snapshotReader;
        _journalReader = journalReader;
    }

    /// <summary>
    /// Produces the full recent-sessions list, newest first, with bulk
    /// enrichment (branch, journal activity, snapshot count) applied. Events
    /// head-parsing is NOT performed here — call <see cref="EnrichWithEvents"/>
    /// lazily per selected/visible session.
    /// </summary>
    public IReadOnlyList<SessionInfo> LoadAll()
    {
        IReadOnlyList<SessionInfo> baseList = _scanner.Scan();
        if (baseList.Count == 0)
        {
            return baseList;
        }

        IReadOnlyDictionary<string, SnapshotSummary> snapshots = _snapshotReader.LoadSummaries();
        IReadOnlyDictionary<string, JournalEntry> journal = _journalReader.LoadLatestBySession();

        var enriched = new List<SessionInfo>(baseList.Count);
        foreach (SessionInfo session in baseList)
        {
            enriched.Add(ApplyBulkEnrichment(session, snapshots, journal));
        }

        return enriched;
    }

    /// <summary>
    /// Catalog pass: refresh bulk maps and discover every folder, reusing unchanged
    /// summaries. Only rows with IsEnriched=false need a subsequent EnrichOne call.
    /// </summary>
    public IReadOnlyList<SessionInfo> LoadCheap()
    {
        // A refresh must also observe new/removed bulk metadata, not retain the
        // first snapshot/journal maps for the lifetime of the singleton.
        var snapshots = _snapshotReader.LoadSummaries();
        var journal = _journalReader.LoadLatestBySession();
        lock (_bulkGate)
        {
            _snapshotCache = snapshots;
            _journalCache = journal;
            _bulkLoaded = true;
        }
        return _scanner.ScanCheap()
            .Select(s => s.IsEnriched ? ApplyBulkEnrichment(s, snapshots, journal) : s)
            .ToArray();
    }

    /// <summary>
    /// Fully enriches a single cheap placeholder (from <see cref="LoadCheap"/>):
    /// applies the expensive per-folder facts (<c>workspace.yaml</c> +
    /// lock/plan/session-db/checkpoint flags) via <see cref="SessionStateScanner.EnrichFolder"/>,
    /// then merges bulk branch / snapshot-count / journal-activity enrichment.
    /// The bulk snapshot-index and journal maps are loaded once and cached on
    /// first call. Events head-parsing is still deferred to
    /// <see cref="EnrichWithEvents"/>.
    /// </summary>
    public SessionInfo EnrichOne(SessionInfo session)
    {
        IReadOnlyDictionary<string, SnapshotSummary> snapshots;
        IReadOnlyDictionary<string, JournalEntry> journal;
        lock (_bulkGate)
        {
            if (!_bulkLoaded)
            {
                _snapshotCache = _snapshotReader.LoadSummaries();
                _journalCache = _journalReader.LoadLatestBySession();
                _bulkLoaded = true;
            }
            snapshots = _snapshotCache!;
            journal = _journalCache!;
        }

        SessionInfo enriched = _scanner.EnrichFolder(session);
        return ApplyBulkEnrichment(enriched, snapshots, journal);
    }

    /// <summary>
    /// Returns a copy of <paramref name="session"/> with its <c>events.jsonl</c>
    /// head parsed into <see cref="SessionInfo.Start"/>. If already parsed or the
    /// file is absent, the original (or a null-Start copy) is returned unchanged.
    /// </summary>
    public SessionInfo EnrichWithEvents(SessionInfo session)
    {
        if (session.Start is not null)
        {
            return session;
        }

        SessionStartInfo? start = _eventsReader.Read(session.FolderPath);
        return start is null ? session : session with { Start = start };
    }

    private static SessionInfo ApplyBulkEnrichment(
        SessionInfo session,
        IReadOnlyDictionary<string, SnapshotSummary> snapshots,
        IReadOnlyDictionary<string, JournalEntry> journal)
    {
        // These projections belong to the current bulk maps, not an older cached
        // row. Removed snapshots/journal entries must also clear stale values.
        snapshots.TryGetValue(session.Id, out SnapshotSummary? summary);
        journal.TryGetValue(session.Id, out JournalEntry? entry);
        string? branch = summary?.LatestBranch ?? entry?.Branch;
        int snapshotCount = summary?.Count ?? 0;
        string? journalActivity = entry?.Activity;

        if (session.Branch == branch && session.SnapshotCount == snapshotCount
            && session.JournalActivity == journalActivity)
            return session;

        return session with
        {
            Branch = branch,
            SnapshotCount = snapshotCount,
            JournalActivity = journalActivity,
        };
    }
}
