using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Searchlight.Abstractions;
using Searchlight.Diagnostics;
using Searchlight.Models;
using Searchlight.Services;

namespace Searchlight.ViewModels;

/// <summary>
/// Root view-model for the main window. Owns the recent-sessions collection,
/// the current selection, the details pane, a text filter, and live refresh via
/// <see cref="ISessionWatcher"/>. Heavy loads run on the thread-pool and marshal
/// back to the UI thread through the injected <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISessionDataSource _dataSource;
    private readonly ISessionWatcher _watcher;
    private readonly IUiDispatcher _dispatcher;

    private readonly List<SessionInfo> _all = [];
    private readonly HashSet<string> _pinnedIds = [];
    private readonly Dictionary<string, string> _customNames = [];
    private bool _watcherHooked;
    private bool _suppressSelectionSideEffects;

    // Notes state. _notesSessionId is the id whose note is currently loaded into
    // SelectedNotes; _suppressNotesSave gates the autosave while we load a note
    // into the bound property; _notesDirty tracks unsaved edits so a selection
    // change can flush them; _notesDebounceCts coalesces rapid keystrokes into a
    // single delayed write.
    private readonly NotesService _notes;
    private string? _notesSessionId;
    private bool _suppressNotesSave;
    private bool _notesDirty;
    private CancellationTokenSource? _notesDebounceCts;

    // Fixed "visible + buffer" window enriched synchronously before the first paint.
    // A generous proxy for the on-screen rows (not viewport-measured); the rest fill
    // in asynchronously. Monotonic generation cancels stale background enrichment when
    // a newer load/refresh starts.
    private const int EagerEnrichCount = 60;
    private int _loadGeneration;

    /// <summary>Creates the main view-model with its services and UI dispatcher.</summary>
    public MainViewModel(
        ISessionDataSource dataSource,
        ISessionWatcher watcher,
        DetailsViewModel details,
        SettingsService settings,
        NotesService notes,
        IUiDispatcher dispatcher)
    {
        _dataSource = dataSource;
        _watcher = watcher;
        _dispatcher = dispatcher;
        _notes = notes;
        Details = details;
        Settings = settings;

        // Seed the Notes-pane visibility straight into the backing field so
        // construction doesn't trigger a redundant settings save.
        _isNotesPaneVisible = settings.Current.NotesPaneVisible;

        // Seed pinned ids from persisted settings so pins survive restarts.
        foreach (string id in settings.Current.PinnedSessionIds)
        {
            _pinnedIds.Add(id);
        }

        // Seed custom display-name overrides from persisted settings.
        foreach (KeyValuePair<string, string> entry in settings.Current.CustomSessionNames)
        {
            _customNames[entry.Key] = entry.Value;
        }
    }

    /// <summary>The details pane view-model (empty until a row is selected).</summary>
    public DetailsViewModel Details { get; }

    /// <summary>App settings, bound by the Settings flyout and used by resume.</summary>
    public SettingsService Settings { get; }

    /// <summary>Filtered sessions grouped into recency buckets, bound to the list.</summary>
    public ObservableCollection<SessionGroup> SessionGroups { get; } = [];

    /// <summary>The row currently selected in the list.</summary>
    [ObservableProperty]
    private SessionInfo? _selectedSession;

    /// <summary>Free-text filter over name, id, cwd, and branch.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>True while a full reload is in flight.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Count of sessions after filtering, for the status line.</summary>
    [ObservableProperty]
    private int _visibleCount;

    /// <summary>Total sessions discovered, for the status line.</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>
    /// Live pin state of the currently selected session, driving the Pin/Unpin
    /// toggle in the details pane. Kept observable (unlike <see cref="SessionInfo.IsPinned"/>,
    /// a plain record field) so the details button flips immediately on pin/unpin.
    /// </summary>
    [ObservableProperty]
    private bool _selectedIsPinned;

    /// <summary>
    /// True when the selected session currently has a custom name override, driving
    /// the "Reset to default" button's enabled state in the rename flyout.
    /// </summary>
    [ObservableProperty]
    private bool _selectedHasCustomName;

    /// <summary>
    /// Editable draft bound two-way to the rename flyout's text box. Seeded with the
    /// selected session's current display name whenever the selection changes.
    /// </summary>
    [ObservableProperty]
    private string _renameDraft = string.Empty;

    /// <summary>
    /// Free-form note text for the currently selected session, bound two-way to the
    /// Notes pane's multiline text box. Autosaved (debounced) to a per-session
    /// sidecar file via <see cref="NotesService"/>; empty when no row is selected.
    /// </summary>
    [ObservableProperty]
    private string _selectedNotes = string.Empty;

    /// <summary>
    /// True when the selected session currently has a (non-empty) note, driving the
    /// note-presence indicator in the details-pane header. Kept live so the badge
    /// appears/disappears the moment the note becomes non-empty/empty.
    /// </summary>
    [ObservableProperty]
    private bool _selectedHasNote;

    /// <summary>
    /// Whether the optional Notes pane is shown. Two-way from the toggle in the
    /// details-pane header; persisted to <c>AppSettings.NotesPaneVisible</c> so the
    /// open/closed state survives restarts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesPaneToggleLabel))]
    private bool _isNotesPaneVisible;

    /// <summary>Label for the header toggle: "Hide notes" when open, else "Show notes".</summary>
    public string NotesPaneToggleLabel => IsNotesPaneVisible ? "Hide notes" : "Show notes";

    partial void OnIsNotesPaneVisibleChanged(bool value) =>
        Settings.Current.NotesPaneVisible = value;

    /// <summary>Shows or hides the Notes pane. Backs the header toggle button.</summary>
    [RelayCommand]
    private void ToggleNotesPane() => IsNotesPaneVisible = !IsNotesPaneVisible;

    partial void OnSelectedNotesChanged(string value)
    {
        // Ignore the programmatic assignment we make while loading a session's note
        // into the bound property (see ReconcileNotesSelection); only real user
        // edits should mark the note dirty and schedule a save.
        if (_suppressNotesSave || _notesSessionId is null)
        {
            return;
        }

        _notesDirty = true;
        ScheduleNotesSave(_notesSessionId, value);

        // Drive the presence indicators from the live text. The details-pane badge
        // (SelectedHasNote) flips immediately; the left-pane row badge only needs a
        // refresh when the note crosses the empty <-> non-empty boundary, so gate
        // the (heavier) regroup on that transition rather than every keystroke.
        bool hasNow = !string.IsNullOrWhiteSpace(value);
        SelectedHasNote = hasNow;
        if (SelectedSession is not null && SelectedSession.HasNote != hasNow)
        {
            SelectedSession.HasNote = hasNow;
            ApplyFilter();
        }
    }

    partial void OnSelectedSessionChanged(SessionInfo? value)
    {
        // During a list rebuild the ListView transiently clears its SelectedItem
        // (Sessions.Clear -> SelectedItem=null) before we restore it. Ignore that
        // churn so the details pane doesn't flicker or clear on every refresh.
        if (_suppressSelectionSideEffects)
        {
            return;
        }

        SelectedIsPinned = value is not null && _pinnedIds.Contains(value.Id);
        SelectedHasCustomName = value is not null && _customNames.ContainsKey(value.Id);
        RenameDraft = value?.DisplayName ?? string.Empty;
        ReconcileNotesSelection();
        Details.Load(value);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Two-phase load. Phase 1 (blocking, fast): read cheap folder facts for every
    /// session, eagerly enrich the newest <see cref="EagerEnrichCount"/> window, and
    /// publish immediately so the first screenful renders fully. Phase 2 (background):
    /// enrich the remaining sessions in recency order, yielding periodically and
    /// updating each row in place on the UI thread. A generation guard cancels an
    /// in-flight Phase 2 when a newer load/refresh starts. Also wires the watcher.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        CoreLog.Write("LoadAsync: start");
        int generation = ++_loadGeneration;

        // Time the whole load through to the end of background enrichment ("fully
        // loaded"). The elapsed value is written to the footer status once, last-one-wins,
        // so it shows at startup but is replaced by the next copy/resume action.
        Stopwatch loadStopwatch = Stopwatch.StartNew();
        IsLoading = true;
        try
        {
            // Phase 1a: cheap placeholders (folder facts only) — fast blocking pass.
            IReadOnlyList<SessionInfo> cheap =
                await Task.Run(() => _dataSource.LoadCheap()).ConfigureAwait(true);

            CoreLog.Write($"LoadAsync: data source returned {cheap.Count} sessions (cheap)");

            // Phase 1b: eagerly enrich the newest window so the first screenful is
            // fully populated before we publish.
            List<SessionInfo> initial = [.. cheap];
            int eager = Math.Min(EagerEnrichCount, initial.Count);
            await Task.Run(() =>
            {
                for (int i = 0; i < eager; i++)
                {
                    initial[i] = _dataSource.EnrichOne(initial[i]);
                }
            }).ConfigureAwait(true);

            _all.Clear();
            _all.AddRange(initial);
            TotalCount = _all.Count;
            ApplyFilter();
            CoreLog.Write($"LoadAsync: published {VisibleCount} rows in {SessionGroups.Count} groups (total {TotalCount}, eager {eager})");

            // Phase 2: enrich the rest in the background, updating rows in place.
            if (initial.Count > eager)
            {
                EnrichRemainingInBackground(generation, eager, loadStopwatch);
            }
            else
            {
                // No background phase — this load is already "fully loaded".
                ReportLoadTime(loadStopwatch);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Write($"LoadAsync: EXCEPTION {ex}");
        }
        finally
        {
            IsLoading = false;
        }

        HookWatcher();
    }

    /// <summary>
    /// Enriches sessions beyond the eager window on a background thread, in recency
    /// order, marshaling each completed row back to the UI thread. Yields between
    /// batches so posted row updates render progressively rather than in one burst.
    /// Bails out as soon as a newer load supersedes this <paramref name="generation"/>.
    /// </summary>
    private void EnrichRemainingInBackground(int generation, int startIndex, Stopwatch loadStopwatch)
    {
        // Snapshot the pending placeholders on the UI thread before going async.
        List<SessionInfo> pending = [.. _all.Skip(startIndex)];

        _ = Task.Run(async () =>
        {
            const int BatchSize = 20;
            int done = 0;
            try
            {
                foreach (SessionInfo placeholder in pending)
                {
                    if (generation != _loadGeneration)
                    {
                        return;
                    }

                    SessionInfo enriched = _dataSource.EnrichOne(placeholder);
                    _dispatcher.Post(() =>
                    {
                        if (generation == _loadGeneration)
                        {
                            ReplaceRow(enriched);
                        }
                    });

                    if (++done % BatchSize == 0)
                    {
                        await Task.Yield();
                    }
                }

                CoreLog.Write($"LoadAsync: background enrichment complete ({done} rows)");

                // Phase 1 grouped the older rows by folder mtime (the cheap sort key),
                // which can be bulk-touched and wildly diverge from workspace updated_at.
                // Now that every row carries its real updated_at, recompute the groups
                // once so day/week/month buckets are accurate. Guarded by generation so
                // a newer load (Refresh) doesn't get clobbered by this late recompute.
                _dispatcher.Post(() =>
                {
                    if (generation == _loadGeneration)
                    {
                        ApplyFilter();
                        CoreLog.Write($"LoadAsync: regrouped after enrichment ({SessionGroups.Count} groups)");
                        ReportLoadTime(loadStopwatch);
                    }
                });
            }
            catch (Exception ex)
            {
                CoreLog.Write($"LoadAsync: background enrichment EXCEPTION {ex}");
            }
        });
    }

    /// <summary>
    /// Writes the total load time to the footer status once the app is fully loaded
    /// (all rows enriched and regrouped). Last-one-wins: it shows at startup and is
    /// overwritten by the next copy/resume action, so it never persists.
    /// </summary>
    private void ReportLoadTime(Stopwatch loadStopwatch)
    {
        loadStopwatch.Stop();
        double seconds = loadStopwatch.Elapsed.TotalSeconds;
        Details.LastActionText = $"Loaded {TotalCount} sessions in {seconds:0.0}s";
        CoreLog.Write($"LoadAsync: fully loaded in {seconds:0.00}s");
    }

    /// <summary>
    /// Replaces a placeholder row with its enriched copy in place — both in the
    /// backing list and in its visible group — without re-sorting or regrouping, so
    /// ordering stays stable while off-screen rows fill in. Refreshes the details
    /// pane if the enriched row is the current selection.
    /// </summary>
    private void ReplaceRow(SessionInfo enriched)
    {
        // Enrichment builds a fresh record; carry the transient pin flag and custom
        // name forward so a pinned/renamed row isn't visually reset on enrich.
        enriched.IsPinned = _pinnedIds.Contains(enriched.Id);
        enriched.CustomName = _customNames.GetValueOrDefault(enriched.Id);

        for (int i = 0; i < _all.Count; i++)
        {
            if (_all[i].Id == enriched.Id)
            {
                _all[i] = enriched;
                break;
            }
        }

        foreach (SessionGroup group in SessionGroups)
        {
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].Id == enriched.Id)
                {
                    bool isSelected = SelectedSession?.Id == enriched.Id;

                    _suppressSelectionSideEffects = true;
                    try
                    {
                        group[i] = enriched;
                        if (isSelected)
                        {
                            SelectedSession = enriched;
                        }
                    }
                    finally
                    {
                        _suppressSelectionSideEffects = false;
                    }

                    if (isSelected)
                    {
                        Details.Load(enriched);
                    }

                    return;
                }
            }
        }
    }

    /// <summary>Re-runs <see cref="LoadAsync"/> to pick up on-disk changes.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Pins a session to the top of the list and persists the pin.</summary>
    [RelayCommand]
    private void Pin(SessionInfo? session)
    {
        if (session is null || !_pinnedIds.Add(session.Id))
        {
            return;
        }

        PersistPins();
        ApplyFilter();
    }

    /// <summary>Removes a session's pin and persists the change.</summary>
    [RelayCommand]
    private void Unpin(SessionInfo? session)
    {
        if (session is null || !_pinnedIds.Remove(session.Id))
        {
            return;
        }

        PersistPins();
        ApplyFilter();
    }

    // Reassign the settings list (never mutate in place) so SettingsService's
    // PropertyChanged-driven auto-save fires.
    private void PersistPins() => Settings.Current.PinnedSessionIds = [.. _pinnedIds];

    /// <summary>
    /// Toggles the pin state of the currently selected session. Backs the single
    /// Pin/Unpin button in the details pane (the per-row buttons were removed).
    /// </summary>
    [RelayCommand]
    private void TogglePin()
    {
        SessionInfo? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        if (!_pinnedIds.Remove(session.Id))
        {
            _pinnedIds.Add(session.Id);
        }

        PersistPins();
        SelectedIsPinned = _pinnedIds.Contains(session.Id);
        ApplyFilter();
    }

    /// <summary>
    /// Applies <see cref="RenameDraft"/> as the selected session's custom display
    /// name. A blank draft removes the override (reverts to the auto-generated name).
    /// Backs the Save button in the details-pane rename flyout.
    /// </summary>
    [RelayCommand]
    private void Rename()
    {
        SessionInfo? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        string trimmed = (RenameDraft ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            // Empty input clears the override rather than storing an empty name.
            _customNames.Remove(session.Id);
        }
        else
        {
            _customNames[session.Id] = trimmed;
        }

        PersistCustomNames();
        ApplyFilter();
    }

    /// <summary>
    /// Removes the selected session's custom name override, reverting its title to
    /// the auto-generated workspace name / UUID. Backs the "Reset to default" button.
    /// </summary>
    [RelayCommand]
    private void ResetName()
    {
        SessionInfo? session = SelectedSession;
        if (session is null || !_customNames.Remove(session.Id))
        {
            return;
        }

        PersistCustomNames();
        ApplyFilter();
    }

    // Reassign the settings dictionary (never mutate in place) so SettingsService's
    // PropertyChanged-driven auto-save fires.
    private void PersistCustomNames() => Settings.Current.CustomSessionNames = new(_customNames);

    private void HookWatcher()
    {
        if (_watcherHooked)
        {
            return;
        }

        _watcher.Changed += OnWatcherChanged;
        _watcher.Start();
        _watcherHooked = true;
    }

    private void OnWatcherChanged(object? sender, EventArgs e)
    {
        // FileSystemWatcher/timer fire on thread-pool threads; marshal to UI.
        _dispatcher.Post(() =>
        {
            if (!RefreshCommand.IsRunning)
            {
                RefreshCommand.Execute(null);
            }
        });
    }

    private void ApplyFilter()
    {
        string query = SearchText?.Trim() ?? string.Empty;

        IEnumerable<SessionInfo> filtered = _all;
        if (query.Length > 0)
        {
            filtered = _all.Where(s => Matches(s, query));
        }

        // Explicit newest-first ordering so buckets stay contiguous even when
        // workspace updated_at diverges from the folder last-write sort key.
        List<SessionInfo> ordered = [.. filtered.OrderByDescending(s => s.UpdatedAt)];

        // Recompute the transient pin flag, custom name, and note-presence flag from
        // the authoritative stores every pass so grouping, DisplayName, and the
        // notes badge stay in sync. For the currently-loaded session use the live
        // editor text (the disk write is debounced and may lag); others read disk.
        foreach (SessionInfo session in ordered)
        {
            session.IsPinned = _pinnedIds.Contains(session.Id);
            session.CustomName = _customNames.GetValueOrDefault(session.Id);
            session.HasNote = string.Equals(session.Id, _notesSessionId, StringComparison.Ordinal)
                ? !string.IsNullOrWhiteSpace(SelectedNotes)
                : _notes.HasNote(session.Id);
        }

        string? keepId = SelectedSession?.Id;

        // Suppress the details-pane reload while the ListView churns its
        // SelectedItem through null during the group Clear/re-add rebuild.
        _suppressSelectionSideEffects = true;
        try
        {
            SessionGroups.Clear();

            // Pinned sessions float to the top in their own group (newest-first),
            // and are excluded from the recency buckets below so they appear once.
            List<SessionInfo> pinned = [.. ordered.Where(s => s.IsPinned)];
            if (pinned.Count > 0)
            {
                SessionGroup pinnedGroup = new("Pinned", "Pinned");
                foreach (SessionInfo session in pinned)
                {
                    pinnedGroup.Add(session);
                }

                SessionGroups.Add(pinnedGroup);
            }

            DateTimeOffset now = DateTimeOffset.Now;
            SessionGroup? current = null;
            string? currentKey = null;

            foreach (SessionInfo session in ordered)
            {
                if (session.IsPinned)
                {
                    continue;
                }

                (string key, string shortKey) = GroupLabelsFor(session.UpdatedAt, now);
                if (current is null || !string.Equals(key, currentKey, StringComparison.Ordinal))
                {
                    current = new SessionGroup(key, shortKey);
                    SessionGroups.Add(current);
                    currentKey = key;
                }

                current.Add(session);
            }

            VisibleCount = ordered.Count;

            // Preserve selection across a filter/refresh when the row survives.
            SessionInfo? match = keepId is null
                ? null
                : ordered.FirstOrDefault(s => s.Id == keepId);
            SelectedSession = match;
        }
        finally
        {
            _suppressSelectionSideEffects = false;
        }

        // Reload the details pane exactly once, reflecting the final selection.
        SelectedIsPinned = SelectedSession is not null && _pinnedIds.Contains(SelectedSession.Id);
        SelectedHasCustomName = SelectedSession is not null && _customNames.ContainsKey(SelectedSession.Id);
        RenameDraft = SelectedSession?.DisplayName ?? string.Empty;
        ReconcileNotesSelection();
        Details.Load(SelectedSession);
    }

    // Loads the note for the current selection into SelectedNotes, first flushing
    // any pending edits for the previously-selected session. A no-op when the
    // effective selected id is unchanged (e.g. a refresh that preserves selection),
    // so in-flight edits and the debounce timer are left intact.
    private void ReconcileNotesSelection()
    {
        string? newId = SelectedSession?.Id;
        if (string.Equals(newId, _notesSessionId, StringComparison.Ordinal))
        {
            return;
        }

        FlushPendingNotes();

        _notesSessionId = newId;
        _suppressNotesSave = true;
        SelectedNotes = newId is null ? string.Empty : _notes.Read(newId);
        _suppressNotesSave = false;
        _notesDirty = false;
        SelectedHasNote = !string.IsNullOrWhiteSpace(SelectedNotes);
    }

    // Cancels any pending debounce and schedules a delayed write so rapid typing
    // coalesces into a single save (the note for the still-current session).
    private void ScheduleNotesSave(string sessionId, string text)
    {
        _notesDebounceCts?.Cancel();
        CancellationTokenSource cts = new();
        _notesDebounceCts = cts;

        _ = SaveNotesAfterDelayAsync(sessionId, text, cts.Token);
    }

    private async Task SaveNotesAfterDelayAsync(string sessionId, string text, CancellationToken ct)
    {
        try
        {
            await Task.Delay(600, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer keystroke or a flush.
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        _notes.Write(sessionId, text);
        _notesDirty = false;
    }

    // Immediately persists the current note if it has unsaved edits, cancelling any
    // pending debounce. Called on selection change and dispose so no edit is lost.
    private void FlushPendingNotes()
    {
        _notesDebounceCts?.Cancel();
        _notesDebounceCts = null;

        if (_notesSessionId is not null && _notesDirty)
        {
            _notes.Write(_notesSessionId, SelectedNotes);
            _notesDirty = false;
        }
    }

    /// <summary>
    /// Maps a session's last-update time to a group header. Recent sessions fall
    /// into doubling relative windows ("Last 2 hours" … "Last 32 hours"). Older
    /// sessions coarsen over time: 32h–14d are grouped by calendar day, 14d–30d
    /// by calendar week ("Week of …"), and anything ≥30d by calendar month.
    /// Each session lands in the tightest matching window.
    /// </summary>
    internal static string GroupKeyFor(DateTimeOffset updatedAt, DateTimeOffset now)
        => GroupLabelsFor(updatedAt, now).Key;

    /// <summary>
    /// Short label for a session's group, used by the compact tick rail that lets
    /// the user jump straight to a group. Mirrors <see cref="GroupKeyFor"/>'s tiers.
    /// </summary>
    internal static string ShortKeyFor(DateTimeOffset updatedAt, DateTimeOffset now)
        => GroupLabelsFor(updatedAt, now).ShortKey;

    /// <summary>
    /// Single source of truth for the grouping ladder. Returns both the full
    /// header <c>Key</c> and the compact tick-rail <c>ShortKey</c> for a session's
    /// last-update time relative to <paramref name="now"/>.
    /// </summary>
    private static (string Key, string ShortKey) GroupLabelsFor(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        TimeSpan age = now - updatedAt;

        // Hour tiers keep their relative header ("Last N hours") but the compact tick label
        // also carries the weekday letter of the session's last update (e.g. "2h (T)"), matching
        // the day/week ticks. Grouping is keyed off the header, so a varying tick letter never
        // splits an hour bucket; the group shows its most-recent session's weekday.
        string hourDayLetter = " (" + DayLetter(updatedAt.ToLocalTime().DayOfWeek) + ")";

        if (age < TimeSpan.FromHours(2))
        {
            return ("Last 2 hours", "2h" + hourDayLetter);
        }

        if (age < TimeSpan.FromHours(4))
        {
            return ("Last 4 hours", "4h" + hourDayLetter);
        }

        if (age < TimeSpan.FromHours(8))
        {
            return ("Last 8 hours", "8h" + hourDayLetter);
        }

        if (age < TimeSpan.FromHours(16))
        {
            return ("Last 16 hours", "16h" + hourDayLetter);
        }

        if (age < TimeSpan.FromHours(32))
        {
            return ("Last 32 hours", "32h" + hourDayLetter);
        }

        if (age < TimeSpan.FromHours(64))
        {
            return ("Last 64 hours", "64h" + hourDayLetter);
        }

        DateTime local = updatedAt.ToLocalTime().Date;

        // 64h – <14d: group by the session's own calendar day. The tick label restores
        // the abbreviated month + day-number, e.g. "Jul (3) (T)", combined with the
        // single-letter weekday (M T W T F S S); the full date lives in the tooltip.
        if (age < TimeSpan.FromDays(14))
        {
            return (local.ToString("dddd, MMMM d, yyyy"), local.ToString("MMM d") + " (" + DayLetter(local.DayOfWeek) + ")");
        }

        // 14d – <30d: group by calendar week (that week's Monday).
        if (age < TimeSpan.FromDays(30))
        {
            int sinceMonday = ((int)local.DayOfWeek + 6) % 7;
            DateTime weekStart = local.AddDays(-sinceMonday);
            return ("Week of " + weekStart.ToString("MMM d, yyyy"), "Wk " + weekStart.ToString("MMM d"));
        }

        // ≥30d: group by calendar month.
        return (local.ToString("MMMM yyyy"), local.ToString("MMM yyyy"));
    }

    // Single-letter weekday for the compact tick rail: M T W T F S S.
    // There is no .NET format specifier for a one-letter weekday, so map it directly.
    private static string DayLetter(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => "M",
        DayOfWeek.Tuesday => "T",
        DayOfWeek.Wednesday => "W",
        DayOfWeek.Thursday => "T",
        DayOfWeek.Friday => "F",
        DayOfWeek.Saturday => "S",
        DayOfWeek.Sunday => "S",
        _ => "?",
    };

    private static bool Matches(SessionInfo session, string query)
    {
        return Contains(session.DisplayName, query)
            || Contains(session.Id, query)
            || Contains(session.Cwd, query)
            || Contains(session.Branch, query)
            || Contains(session.FirstPromptPreview, query);
    }

    private static bool Contains(string? value, string query) =>
        value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Detaches the watcher event.</summary>
    public void Dispose()
    {
        // Persist any unsaved note before teardown.
        FlushPendingNotes();

        if (_watcherHooked)
        {
            _watcher.Changed -= OnWatcherChanged;
        }
    }
}
