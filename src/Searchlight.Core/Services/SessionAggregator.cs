using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Combines native Copilot session-folder and workspace metadata with lazy event previews.
/// Custom journal and status-snapshot extensions are not session-data dependencies.
/// </summary>
public sealed class SessionAggregator(SessionStateScanner scanner, EventsJsonlReader eventsReader)
{
    /// <summary>Loads native workspace metadata and file-presence flags, without events.</summary>
    public IReadOnlyList<SessionInfo> LoadAll() => scanner.Scan();

    /// <summary>Discovers folders and reuses unchanged native workspace summaries.</summary>
    public IReadOnlyList<SessionInfo> LoadCheap() => scanner.ScanCheap();

    /// <summary>Enriches a folder from workspace.yaml and native session-file presence.</summary>
    public SessionInfo EnrichOne(SessionInfo session) => scanner.EnrichFolder(session);

    /// <summary>Reads the event preview only when Details requests it.</summary>
    public SessionInfo EnrichWithEvents(SessionInfo session)
    {
        // ASSUMPTION: the details loader owns versioned caching; it requests a fresh
        // preview after an event-file change, even if its input has an older Start.
        return session with { Start = eventsReader.Read(session.FolderPath) };
    }
}
