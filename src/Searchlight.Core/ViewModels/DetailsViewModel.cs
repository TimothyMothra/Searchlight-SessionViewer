using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Searchlight.Abstractions;
using Searchlight.Models;
using Searchlight.Services;

namespace Searchlight.ViewModels;

/// <summary>
/// Backs the details pane for the currently-selected session. Lazily loads the
/// per-session heavy data (events head, checkpoints, snapshots, session.db) on
/// demand and exposes the headline <see cref="ResumeCommand"/>.
/// </summary>
public sealed partial class DetailsViewModel : ObservableObject
{
    private readonly ISessionDataSource _dataSource;
    private readonly IResumeLauncher _resume;
    private readonly IClipboardService _clipboard;

    /// <summary>Creates a details view-model bound to the data source, resume launcher, and clipboard.</summary>
    public DetailsViewModel(ISessionDataSource dataSource, IResumeLauncher resume, IClipboardService clipboard)
    {
        _dataSource = dataSource;
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
    /// </summary>
    [ObservableProperty]
    private string? _lastActionText;

    /// <summary>
    /// Loads and enriches the given session into the pane. Passing null clears it.
    /// </summary>
    public void Load(SessionInfo? session)
    {
        StatusMessage = null;
        Checkpoints.Clear();
        Snapshots.Clear();
        Todos.Clear();

        if (session is null)
        {
            Session = null;
            return;
        }

        // Lazily parse events.jsonl only when needed for this session.
        SessionInfo enriched = _dataSource.EnrichWithEvents(session);
        Session = enriched;

        foreach (CheckpointInfo checkpoint in _dataSource.ReadCheckpoints(enriched))
        {
            Checkpoints.Add(checkpoint);
        }

        foreach (SnapshotInfo snapshot in _dataSource.LoadSnapshots(enriched.Id))
        {
            Snapshots.Add(snapshot);
        }

        foreach (SessionTodo todo in _dataSource.ReadTodos(enriched))
        {
            Todos.Add(todo);
        }
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
