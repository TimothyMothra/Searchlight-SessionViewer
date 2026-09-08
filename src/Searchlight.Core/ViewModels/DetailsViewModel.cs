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
/// per-session heavy data (events head, checkpoints, snapshots, session.db) on
/// demand and exposes the headline <see cref="ResumeCommand"/>.
/// </summary>
public sealed partial class DetailsViewModel : ObservableObject
{
    private readonly SessionDetailsLoader _loader;
    private readonly IResumeLauncher _resume;
    private readonly IClipboardService _clipboard;
    private SessionInfo? _requested;
    private CancellationTokenSource? _loadCancellation;
    private int _generation;

    /// <summary>Creates a details view-model bound to the data source, resume launcher, and clipboard.</summary>
    public DetailsViewModel(ISessionDataSource dataSource, IResumeLauncher resume, IClipboardService clipboard)
    {
        _loader = new SessionDetailsLoader(dataSource);
        _resume = resume;
        _clipboard = clipboard;
    }

    /// <summary>The session currently shown, enriched with events-head data.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyIdCommand))]
    private SessionInfo? _session;

    /// <summary>True when a session is loaded (drives empty-state visibility).</summary>
    public bool HasSession => Session is not null;

    /// <summary>Checkpoints for the current session (newest first).</summary>
    public ObservableCollection<CheckpointInfo> Checkpoints { get; } = [];

    /// <summary>Recent status snapshots for the current session (newest first).</summary>
    public ObservableCollection<SnapshotInfo> Snapshots { get; } = [];

    /// <summary>Todos read from the current session's <c>session.db</c>.</summary>
    public ObservableCollection<SessionTodo> Todos { get; } = [];

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

        int generation = ++_generation;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        bool changed = _requested?.Id != session?.Id || _requested?.FolderPath != session?.FolderPath;
        _requested = session;
        if (changed || session is null)
        {
            StatusMessage = null;
            Checkpoints.Clear();
            Snapshots.Clear();
            Todos.Clear();
            Session = session;
        }

        if (session is null)
        {
            IsLoading = false;
            CurrentLoad = Task.CompletedTask;
            return;
        }

        // Load is called on the UI context. Only publication runs there; all
        // filesystem/SQLite work, version checks and cache lookup run off-thread.
        _loadCancellation = new CancellationTokenSource();
        IsLoading = true;
        CurrentLoad = LoadCoreAsync(session, generation, _loadCancellation.Token);
    }

    private async Task LoadCoreAsync(SessionInfo session, int generation, CancellationToken token)
    {
        try
        {
            SessionDetails details = await _loader.LoadAsync(session, token).ConfigureAwait(true);
            if (generation != _generation || token.IsCancellationRequested) return;
            Session = details.Session with
            {
                CustomName = session.CustomName,
                IsPinned = session.IsPinned,
                HasNote = session.HasNote,
            };
            ReplaceItems(Checkpoints, details.Checkpoints);
            ReplaceItems(Snapshots, details.Snapshots);
            ReplaceItems(Todos, details.Todos);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException ex)
        {
            if (generation == _generation) StatusMessage = $"Could not read session details: {ex.Message}";
            CoreLog.Write($"Details load failed: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            if (generation == _generation) StatusMessage = $"Could not read session details: {ex.Message}";
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
        StatusMessage = ok
            ? $"Resuming {Session.ShortId}…"
            : "Could not launch a terminal to resume this session.";
        LastActionText = ok
            ? $"Resumed session: {command}"
            : $"Resume failed: could not launch a terminal for {Session.Id}";
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
