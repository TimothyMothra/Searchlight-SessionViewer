using Searchlight.Models;

namespace Searchlight.Services;

internal sealed record SessionDetails(
    SessionInfo Session,
    IReadOnlyList<CheckpointInfo> Checkpoints,
    IReadOnlyList<SnapshotInfo> Snapshots);

internal sealed class SessionDetailsLoader(ISessionDataSource source)
{
    // ASSUMPTION: eight recent selections cover normal navigation. This cache
    // never grows with the entire catalog, and all cache work is worker-owned.
    internal const int Capacity = 8;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly LinkedList<(string Path, string Id, string Version, SessionDetails Details)> _cache = [];

    public async Task<SessionDetails> LoadAsync(SessionInfo session, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Read(session, token), token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SessionDetails Read(SessionInfo session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string version = source.GetDetailsVersion(session);
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Path != session.FolderPath || node.Value.Id != session.Id) continue;
            var entry = node.Value;
            _cache.Remove(node);
            if (entry.Version == version)
            {
                _cache.AddFirst(entry);
                // Bulk journal/snapshot projections can change independently of
                // detail files. Reuse only the heavy payload, never an old summary.
                SessionInfo current = session.IsEnriched ? session : source.EnrichOne(session);
                return entry.Details with
                {
                    Session = current with { Start = entry.Details.Session.Start },
                };
            }
            break;
        }

        SessionInfo summary = source.EnrichOne(session);
        SessionInfo enriched = source.EnrichWithEvents(summary);
        token.ThrowIfCancellationRequested();
        var checkpoints = source.ReadCheckpoints(enriched);
        token.ThrowIfCancellationRequested();
        var snapshots = source.LoadSnapshots(enriched.Id);
        token.ThrowIfCancellationRequested();
        // Todos are intentionally excluded: only explicit tab activation may read session.db.
        var details = new SessionDetails(enriched, checkpoints, snapshots);

        // Do not cache a torn read while an active session is appending/writing.
        if (version == source.GetDetailsVersion(session))
        {
            _cache.AddFirst((session.FolderPath, session.Id, version, details));
            if (_cache.Count > Capacity) _cache.RemoveLast();
        }
        return details;
    }
}
