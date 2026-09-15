using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Searchlight.Abstractions;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.Diagnostics;

namespace Searchlight.ViewModels;

/// <summary>
/// Backs the details pane for the currently-selected session. Lazily loads the
/// per-section heavy data only while its tab is active.
/// Todos have a separate explicit activation boundary and fresh-snapshot reader.
/// </summary>
public sealed partial class DetailsViewModel : ObservableObject
{
    // ASSUMPTION: these indices match the section order in MainView's TabView.
    // Only Agent tasks activates its independent database reader.
    public const int DetailsTabIndex = 0;
    public const int AgentTasksTabIndex = 1;
    public const int CheckpointsTabIndex = 2;

    private readonly SessionDetailsLoader _loader;
    private readonly IResumeLauncher _resume;
    private readonly IClipboardService _clipboard;
    private SessionInfo? _requested;
    private CancellationTokenSource? _loadCancellation;
    private int _generation;
    private bool _updatingSession;

    /// <summary>Creates a details view-model bound to the data source, resume launcher, and clipboard.</summary>
    public DetailsViewModel(ISessionDataSource dataSource, IResumeLauncher resume, IClipboardService clipboard)
    {
        _loader = new SessionDetailsLoader(dataSource);
        Todos = new TodosViewModel(dataSource);
        _resume = resume;
        _clipboard = clipboard;
        Checkpoints.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowNoCheckpoints));
    }

    /// <summary>The session currently shown, enriched with events-head data.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    [NotifyPropertyChangedFor(nameof(ShowNoCheckpoints), nameof(MetadataGroups))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyIdCommand))]
    private SessionInfo? _session;

    /// <summary>True when a session is loaded (drives empty-state visibility).</summary>
    public bool HasSession => Session is not null;

    /// <summary>Checkpoints for the current session (newest first).</summary>
    public ObservableCollection<CheckpointInfo> Checkpoints { get; } = [];

    /// <summary>Presentation-only projection; it never reads another source or tab.</summary>
    public IReadOnlyList<SessionMetadataGroup> MetadataGroups => SessionMetadata.Create(Session, CopyIdCommand);

    /// <summary>Explicitly activated, independent todo snapshot.</summary>
    public TodosViewModel Todos { get; }

    /// <summary>Selected native section: Details, Agent tasks, or Checkpoints.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsTabSelected), nameof(IsCheckpointsTabSelected),
        nameof(IsAgentTasksTabSelected))]
    private int _selectedTabIndex;

    public bool IsDetailsTabSelected => SelectedTabIndex == DetailsTabIndex;
    public bool IsCheckpointsTabSelected => SelectedTabIndex == CheckpointsTabIndex;
    public bool IsAgentTasksTabSelected => SelectedTabIndex == AgentTasksTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_updatingSession) return;
        Todos.SetActive(value == AgentTasksTabIndex);
        LoadSelectedSection();
    }

    // Folder-presence flags do not prove a populated list; unopened/failed tabs are not empty.
    public bool ShowNoCheckpoints =>
        HasSession && HasLoadedCheckpoints && !IsLoading && SectionLoadError is null && Checkpoints.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoCheckpoints))]
    private bool _hasLoadedCheckpoints;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoCheckpoints))]
    private string? _sectionLoadError;

    /// <summary>Last status message from a resume attempt, if any.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Full text of the most recent user action (copy or resume), for the persistent
    /// bottom footer. Shows the action name plus the full command/string involved
    /// (e.g. the exact <c>copilot --resume=…</c> command, or the copied GUID).
    /// Seeded with "Initializing..." so the footer isn't blank during startup; the
    /// load stopwatch overwrites it with "Loaded N sessions in Xs" once ready.
    /// </summary>
    [ObservableProperty]
    private string? _lastActionText = "Initializing...";

    /// <summary>True while the selected session's details are being read off-thread.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoCheckpoints))]
    private bool _isLoading;

    /// <summary>The pending selection load; also lets headless callers await completion.</summary>
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Loads and enriches the given session into the pane. Passing null clears it.
    /// </summary>
    public void Load(SessionInfo? session, bool refresh = false)
    {
        if (!refresh && ReferenceEquals(_requested, session))
        {
            if (session is not null && Session is not null)
            {
                Session = Session with { CustomName = session.CustomName, IsPinned = session.IsPinned, HasNote = session.HasNote };
                // Row overrides are mutable; the record may already compare equal
                // after a rename, but headline bindings must still see that change.
                OnPropertyChanged(nameof(Session));
            }
            return;
        }

        CancelSectionLoad();
        bool changed = _requested?.Id != session?.Id || _requested?.FolderPath != session?.FolderPath;
        _requested = session;
        // ASSUMPTION: changing the session must reset the tab before any reader activates.
        // Suppress the tab callback until both the visible session and Todos are updated.
        _updatingSession = true;
        try
        {
            if (changed || session is null)
            {
                Todos.SetActive(false);
                SelectedTabIndex = DetailsTabIndex;
                StatusMessage = null;
                HasLoadedCheckpoints = false;
                Checkpoints.Clear();
                Session = session;
            }
            else
            {
                Session = session with { Start = Session?.Start };
            }
            Todos.SetSession(session);
        }
        finally { _updatingSession = false; }

        Todos.SetActive(IsAgentTasksTabSelected);
        LoadSelectedSection();
    }

    private void CancelSectionLoad()
    {
        ++_generation;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        IsLoading = false;
        SectionLoadError = null;
        CurrentLoad = Task.CompletedTask;
    }

    private void LoadSelectedSection()
    {
        CancelSectionLoad();
        SessionInfo? session = _requested;
        SessionDetailSection? section = SelectedTabIndex switch
        {
            DetailsTabIndex => SessionDetailSection.Details,
            CheckpointsTabIndex => SessionDetailSection.Checkpoints,
            // Agent tasks owns its reader; WinUI may also temporarily select no tab.
            _ => null,
        };
        if (session is null || section is null) return;

        IsLoading = true;
        if (section == SessionDetailSection.Checkpoints)
        {
            HasLoadedCheckpoints = false;
            Checkpoints.Clear();
        }

        _loadCancellation = new CancellationTokenSource();
        CurrentLoad = LoadCoreAsync(session, section.Value, _generation, _loadCancellation.Token);
    }

    private async Task LoadCoreAsync(
        SessionInfo session, SessionDetailSection section, int generation, CancellationToken token)
    {
        try
        {
            SessionDetails details = await _loader.LoadAsync(session, token, section).ConfigureAwait(true);
            if (generation != _generation || token.IsCancellationRequested) return;
            switch (section)
            {
                case SessionDetailSection.Details:
                    Session = details.Session with
                    {
                        CustomName = session.CustomName,
                        IsPinned = session.IsPinned,
                        HasNote = session.HasNote,
                    };
                    break;
                case SessionDetailSection.Checkpoints:
                    ReplaceItems(Checkpoints, details.Checkpoints);
                    HasLoadedCheckpoints = true;
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException ex)
        {
            if (generation == _generation)
                SectionLoadError = $"Could not load this section: {ex.Message} Use Refresh to retry.";
            CoreLog.Write($"Details load failed: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            if (generation == _generation)
                SectionLoadError = $"Could not load this section: {ex.Message} Use Refresh to retry.";
            CoreLog.Write($"Details load failed: {ex}");
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (T item in items) target.Add(item);
    }

    /// <summary>Resumes the current session via <c>copilot resume &lt;id&gt;</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        if (Session is null)
        {
            return;
        }

        string? command = _resume.Resume(Session.Id, Session.DisplayName);
        bool ok = !string.IsNullOrEmpty(command);
        string error = _resume.LastError ?? "Could not launch a terminal to resume this session.";
        StatusMessage = ok
            ? $"Resuming {Session.ShortId}…"
            : error;
        LastActionText = ok
            ? $"Resumed session: {command}"
            : $"Resume failed: {error}";
    }

    private bool CanResume() => Session is not null;

    /// <summary>Copies the current session id (full GUID) to the system clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private void CopyId()
    {
        if (Session is null)
        {
            return;
        }

        bool ok = _clipboard.SetText(Session.Id);
        StatusMessage = ok
            ? "Session id copied to clipboard."
            : "Could not copy the session id to the clipboard.";
        LastActionText = ok
            ? $"Copied to clipboard: {Session.Id}"
            : $"Copy failed: could not copy {Session.Id} to the clipboard";
    }
}
