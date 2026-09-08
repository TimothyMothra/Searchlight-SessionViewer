using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly Dictionary<string, int> _rowIndices = [];
    private readonly Dictionary<string, (SessionGroup Group, int Index)> _visibleRows = [];
    private HashSet<string> _notedIds = [];
    private bool _watcherHooked;
    private bool _suppressSelectionSideEffects;
    private Task? _activeLoad;
    private bool _reloadRequested;
    private readonly CancellationTokenSource _lifetime = new();

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

    // ASSUMPTION: 30 recent rows cover a screenful plus buffer. All pins and the
    // selection are additional priorities, even when older than this window.
    internal const int EagerEnrichCount = 30;
    internal const int EnrichmentBatchSize = 30;
    internal const int SummaryReaderConcurrency = 4;

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

        // Re-filter whenever a list-hiding setting is toggled so the list responds
        // to the Settings flyout immediately (no reload required).
        settings.Current.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.HideEmptySessions)
            or nameof(AppSettings.HideUnnamedSessions))
        {
            ApplyFilter(refreshRows: true);
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
    [NotifyCanExecuteChangedFor(nameof(ClearSearchCommand))]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    /// <summary>
    /// True when the search box holds any text. Drives both the Clear button's
    /// visibility and its command's CanExecute, so the two can never disagree.
    /// </summary>
    /// <remarks>
    /// Deliberately <c>IsNullOrEmpty</c>, not <c>IsNullOrWhiteSpace</c>: a
    /// whitespace-only query filters nothing (ApplyFilter trims), but the box is
    /// not empty, so the user still needs a way to clear it.
    /// </remarks>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

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
    /// How many sessions the hide filters removed from the list. Independent of the
    /// search text — these rows are excluded before the search runs, so they are
    /// also excluded from search results. Drives the footer notice.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenSessions))]
    [NotifyPropertyChangedFor(nameof(HiddenNoticeText))]
    private int _hiddenCount;

    /// <summary>True when at least one session is hidden, revealing the footer notice.</summary>
    public bool HasHiddenSessions => HiddenCount > 0;

    /// <summary>
    /// Footer notice text. Deliberately states that hidden sessions are excluded
    /// from search too, so a user who cannot find a session knows to check the
    /// filters rather than assume it is gone.
    /// </summary>
    public string HiddenNoticeText =>
        HiddenCount == 1
            ? "1 hidden (not searched)"
            : $"{HiddenCount} hidden (not searched)";

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
            if (hasNow) _notedIds.Add(SelectedSession.Id);
            else _notedIds.Remove(SelectedSession.Id);
            ApplyFilter(refreshRows: true);
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
    /// Publishes pins and the recent window first, then fills missing searchable
    /// summaries in bounded batches. Concurrent refresh requests coalesce into a
    /// follow-up pass rather than repeatedly abandoning unfinished enrichment.
    /// </summary>
    [RelayCommand]
    private Task LoadAsync()
    {
        if (_lifetime.IsCancellationRequested) return Task.CompletedTask;
        if (_activeLoad is { IsCompleted: false })
        {
            _reloadRequested = true;
            return _activeLoad;
        }

        return _activeLoad = LoadUntilCurrentAsync();
    }

    private async Task LoadUntilCurrentAsync()
    {
        IsLoading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                CoreLog.Write("LoadAsync: start");
                Stopwatch loadStopwatch = Stopwatch.StartNew();
                CancellationToken token = _lifetime.Token;
                var loaded = await Task.Run(() =>
                {
                    long started = Stopwatch.GetTimestamp();
                    token.ThrowIfCancellationRequested();
                    var sessions = _dataSource.LoadCheap();
                    var notes = _notes.LoadNoteIds();
                    return (Sessions: sessions, Notes: notes,
                        ReadMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                _notedIds = new(loaded.Notes);
                CoreLog.Write($"LoadAsync: data source returned {loaded.Sessions.Count} sessions (cached {loaded.Sessions.Count(s => s.IsEnriched)})");

                List<SessionInfo> initial = [.. loaded.Sessions];
                HashSet<string> priorities = initial.Take(EagerEnrichCount).Select(s => s.Id).ToHashSet();
                priorities.UnionWith(_pinnedIds);
                if (SelectedSession is not null) priorities.Add(SelectedSession.Id);
                // Snapshot mutable UI-owned pin state before entering the worker.
                HashSet<string> pins = [.. _pinnedIds];
                int[] eagerIndices = Enumerable.Range(0, initial.Count)
                    .Where(i => priorities.Contains(initial[i].Id) && !initial[i].IsEnriched)
                    .OrderByDescending(i => pins.Contains(initial[i].Id)).ToArray();
                double eagerMs = 0;
                await Task.Run(async () =>
                {
                    long started = Stopwatch.GetTimestamp();
                    // Finish pending pins before starting the ordinary recent
                    // window, even though reads within each tier can overlap.
                    foreach (int[] tier in new[]
                    {
                        eagerIndices.Where(i => pins.Contains(initial[i].Id)).ToArray(),
                        eagerIndices.Where(i => !pins.Contains(initial[i].Id)).ToArray(),
                    })
                    {
                        SessionInfo[] rows = await EnrichSessionsAsync(
                            tier.Select(i => initial[i]).ToArray(), token).ConfigureAwait(false);
                        for (int i = 0; i < tier.Length; i++) initial[tier[i]] = rows[i];
                    }
                    eagerMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();

                long initialUiStarted = Stopwatch.GetTimestamp();
                var previous = _all.ToDictionary(s => s.Id);
                _all.Clear();
                _rowIndices.Clear();
                foreach (SessionInfo input in initial)
                {
                    SessionInfo row = PrepareRow(input);
                    // Preserve unchanged record identities, bindings and selection.
                    SessionInfo current = previous.TryGetValue(row.Id, out var old) && old == row ? old : row;
                    _rowIndices[current.Id] = _all.Count;
                    _all.Add(current);
                }
                TotalCount = _all.Count;
                ApplyFilter();
                double initialUiMs = Stopwatch.GetElapsedTime(initialUiStarted).TotalMilliseconds;
                CoreLog.Write($"LoadAsync: published {VisibleCount} rows in {SessionGroups.Count} groups (total {TotalCount}, eager {eagerIndices.Length}) in {loadStopwatch.Elapsed.TotalSeconds:0.00}s");
                HookWatcher();

                SessionInfo[] pending = _all.Where(s => !s.IsEnriched).ToArray();
                double backgroundReadMs = 0, backgroundUiMs = 0, schedulingMs = 0;
                foreach (SessionInfo[] batch in pending.Chunk(EnrichmentBatchSize))
                {
                    long batchStarted = Stopwatch.GetTimestamp();
                    var result = await Task.Run(async () =>
                    {
                        long started = Stopwatch.GetTimestamp();
                        SessionInfo[] rows = await EnrichSessionsAsync(batch, token).ConfigureAwait(false);
                        return (Rows: rows, ReadMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }, token).ConfigureAwait(true);
                    backgroundReadMs += result.ReadMs;
                    schedulingMs += Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds - result.ReadMs;
                    token.ThrowIfCancellationRequested();
                    long uiStarted = Stopwatch.GetTimestamp();
                    ReplaceRows(result.Rows);
                    backgroundUiMs += Stopwatch.GetElapsedTime(uiStarted).TotalMilliseconds;
                }
                CoreLog.Write($"LoadAsync: background enrichment complete ({pending.Length} rows, {(pending.Length + EnrichmentBatchSize - 1) / EnrichmentBatchSize} batches)");
                long finalUiStarted = Stopwatch.GetTimestamp();
                ApplyFilter();
                Details.Load(SelectedSession, refresh: true);
                // ASSUMPTION: worker wall time and UI mutation time must be measured
                // separately; a headless reader benchmark cannot reveal WinUI costs.
                CoreLog.Write($"LoadAsync: timings(ms) catalog={loaded.ReadMs:0} eager={eagerMs:0} initialUI={initialUiMs:0} backgroundRead={backgroundReadMs:0} backgroundUI={backgroundUiMs:0} scheduling={schedulingMs:0} finalUI={Stopwatch.GetElapsedTime(finalUiStarted).TotalMilliseconds:0}");
                ReportLoadTime(loadStopwatch);
            } while (_reloadRequested && !_lifetime.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException ex)
        {
            CoreLog.Write($"LoadAsync: I/O failure {ex}");
            Details.LastActionText = $"Could not load sessions: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            CoreLog.Write($"LoadAsync: access failure {ex}");
            Details.LastActionText = $"Could not load sessions: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<SessionInfo[]> EnrichSessionsAsync(IReadOnlyList<SessionInfo> sessions, CancellationToken token)
    {
        var rows = new SessionInfo[sessions.Count];
        // ASSUMPTION: four readers overlap local filesystem latency without
        // launching one task per folder or saturating the machine's I/O queue.
        await Parallel.ForEachAsync(Enumerable.Range(0, sessions.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = SummaryReaderConcurrency,
                CancellationToken = token,
                TaskScheduler = TaskScheduler.Default,
            },
            (i, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                rows[i] = _dataSource.EnrichOne(sessions[i]);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        return rows;
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
    private void ReplaceRows(IReadOnlyList<SessionInfo> rows)
    {
        _suppressSelectionSideEffects = true;
        try
        {
            string? keepId = SelectedSession?.Id;
            foreach (SessionInfo input in rows)
            {
                SessionInfo row = PrepareRow(input);
                if (_rowIndices.TryGetValue(row.Id, out int index)) _all[index] = row;
                if (_visibleRows.TryGetValue(row.Id, out var position))
                {
                    // Batch dispatch, not collection resets. Resetting a whole
                    // group discards WinUI's realized containers even when only
                    // off-screen metadata changed.
                    position.Group[position.Index] = row;
                }
            }
            if (keepId is not null && _rowIndices.TryGetValue(keepId, out int selectedIndex))
                SelectedSession = _all[selectedIndex];
        }
        finally
        {
            _suppressSelectionSideEffects = false;
        }
        Details.Load(SelectedSession);
    }

    private SessionInfo PrepareRow(SessionInfo row)
    {
        bool pinned = _pinnedIds.Contains(row.Id);
        string? customName = _customNames.GetValueOrDefault(row.Id);
        bool hasNote = row.Id == _notesSessionId
            ? !string.IsNullOrWhiteSpace(SelectedNotes) : _notedIds.Contains(row.Id);
        // Cached summary objects may already be bound. A changed flag needs a new
        // row identity so one-time item-template bindings get refreshed.
        return row.IsPinned == pinned && row.CustomName == customName && row.HasNote == hasNote
            ? row : row with { IsPinned = pinned, CustomName = customName, HasNote = hasNote };
    }

    /// <summary>Re-runs <see cref="LoadAsync"/> to pick up on-disk changes.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Empties <see cref="SearchText"/>, which re-runs the filter through
    /// <c>OnSearchTextChanged</c>. Backs the labelled "Clear" button that replaces the
    /// TextBox's built-in icon-only inline clear glyph (which carries no visible text).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearSearch))]
    private void ClearSearch() => SearchText = string.Empty;

    // The button is hidden (not just disabled) when there is nothing to clear, so
    // CanExecute is belt-and-braces for non-UI invocations (automation, keyboard).
    private bool CanClearSearch() => HasSearchText;

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
        ApplyFilter(refreshRows: true);
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
        ApplyFilter(refreshRows: true);
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
            _ = LoadAsync();
        });
    }

    private void ApplyFilter(bool refreshRows = false)
    {
        string query = SearchText?.Trim() ?? string.Empty;

        // Refresh the two cheap in-memory transient flags across EVERY session before
        // filtering: the hide predicate consults them (a pinned/renamed session is
        // never hidden), so they must not be stale. Both reads are dictionary
        // lookups, so doing this over the full list costs nothing.
        foreach (SessionInfo session in _all)
        {
            session.IsPinned = _pinnedIds.Contains(session.Id);
            session.CustomName = _customNames.GetValueOrDefault(session.Id);
        }

        // Hide filters run BEFORE the search so a hidden session stays hidden even
        // when it would match the query. The footer notice tells the user that
        // hidden rows are excluded from search, so a fruitless search points at the
        // filters rather than looking like missing data.
        List<SessionInfo> candidates = [.. _all.Where(IsVisibleUnderHideFilters)];
        HiddenCount = _all.Count - candidates.Count;

        IEnumerable<SessionInfo> filtered = candidates;
        if (query.Length > 0)
        {
            filtered = candidates.Where(s => Matches(s, query));
        }

        // Explicit newest-first ordering so buckets stay contiguous even when
        // workspace updated_at diverges from the folder last-write sort key.
        List<SessionInfo> ordered = [.. filtered.OrderByDescending(s => s.UpdatedAt)];

        // Filtering is entirely in memory. External note edits are observed by the
        // next refresh; the selected editor's pending text always wins.
        foreach (SessionInfo session in ordered)
        {
            session.HasNote = string.Equals(session.Id, _notesSessionId, StringComparison.Ordinal)
                ? !string.IsNullOrWhiteSpace(SelectedNotes)
                : _notedIds.Contains(session.Id);
        }

        string? keepId = SelectedSession?.Id;

        // Suppress the details-pane reload while the ListView churns its
        // SelectedItem through null during the group Clear/re-add rebuild.
        _suppressSelectionSideEffects = true;
        try
        {
            List<SessionGroup> desiredGroups = [];

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

                desiredGroups.Add(pinnedGroup);
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
                    desiredGroups.Add(current);
                    currentKey = key;
                }

                current.Add(session);
            }

            ReconcileGroups(desiredGroups, refreshRows);
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

    private void ReconcileGroups(IReadOnlyList<SessionGroup> desired, bool refreshRows)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            SessionGroup target = desired[i];
            SessionGroup? existing = SessionGroups.FirstOrDefault(g =>
                g.Key == target.Key && g.ShortKey == target.ShortKey);
            if (existing is null)
            {
                SessionGroups.Insert(i, target);
            }
            else
            {
                int oldIndex = SessionGroups.IndexOf(existing);
                if (oldIndex != i) SessionGroups.Move(oldIndex, i);
                existing.SetItems(target, refreshRows);
            }
        }
        while (SessionGroups.Count > desired.Count) SessionGroups.RemoveAt(SessionGroups.Count - 1);
        _visibleRows.Clear();
        foreach (SessionGroup group in SessionGroups)
            for (int i = 0; i < group.Count; i++) _visibleRows[group[i].Id] = (group, i);
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

    /// <summary>
    /// Decides whether a session survives the list's hide filters (applied before
    /// the text search). Two independent opt-outs, both persisted in settings:
    /// "hide empty sessions" (no <c>events.jsonl</c> — a provisioned-but-unused
    /// folder) and "hide unnamed sessions" (renders as a bare UUID).
    /// </summary>
    private bool IsVisibleUnderHideFilters(SessionInfo session)
    {
        // A cheap placeholder has not had its files inspected yet, so "no events"
        // is unknown rather than false. Never hide a row the two-phase load has not
        // enriched — it would flicker out and then back in as enrichment lands.
        if (!session.IsEnriched)
        {
            return true;
        }

        // Explicit user intent outranks both filters: a session the user pinned or
        // renamed is always shown, however empty or unnamed it is.
        if (session.IsPinned || !string.IsNullOrWhiteSpace(session.CustomName))
        {
            return true;
        }

        if (Settings.Current.HideEmptySessions && !session.HasEvents)
        {
            return false;
        }

        return !Settings.Current.HideUnnamedSessions || !session.IsUnnamed;
    }

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

    /// <summary>Detaches the watcher and settings events.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        Details.Load(null);
        // Persist any unsaved note before teardown.
        FlushPendingNotes();

        Settings.Current.PropertyChanged -= OnSettingsPropertyChanged;

        if (_watcherHooked)
        {
            _watcher.Changed -= OnWatcherChanged;
        }
    }
}
