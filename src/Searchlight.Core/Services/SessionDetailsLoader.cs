using Searchlight.Models;
using Searchlight.Diagnostics;
using System.Diagnostics;

namespace Searchlight.Services;

internal enum SessionDetailSection
{
    Details,
    Checkpoints,
}

internal sealed record SessionDetails(
    SessionInfo Session,
    IReadOnlyList<CheckpointInfo> Checkpoints,
    bool FromCache = false);

internal sealed class SessionDetailsLoader(ISessionDataSource source)
{
    // ASSUMPTION: eight recent session/section payloads cover normal navigation.
    // The shared bound includes empty results and all cache work is worker-owned.
    internal const int Capacity = 8;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly LinkedList<(string Path, string Id, SessionDetailSection Section, string Version, SessionDetails Details)> _cache = [];

    public async Task<SessionDetails> LoadAsync(
        SessionInfo session, CancellationToken token, SessionDetailSection section = SessionDetailSection.Details,
        int requestId = 0)
    {
        bool monitoring = CoreLog.IsEnabled;
        long queued = monitoring ? Stopwatch.GetTimestamp() : 0;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long started = monitoring ? Stopwatch.GetTimestamp() : 0;
            SessionDetails result = await Task.Run(() => Read(session, section, token), token).ConfigureAwait(false);
            if (monitoring)
                CoreLog.Write(FormattableString.Invariant(
                    $"DetailsRead request={requestId} section={section} cached={result.FromCache} queue_ms={Stopwatch.GetElapsedTime(queued, started).TotalMilliseconds:F3} worker_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}"));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SessionDetails Read(SessionInfo session, SessionDetailSection section, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string version = GetVersion(session, section);
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Path != session.FolderPath || node.Value.Id != session.Id ||
                node.Value.Section != section) continue;
            var entry = node.Value;
            _cache.Remove(node);
            if (entry.Version == version)
            {
                _cache.AddFirst(entry);
                // Workspace summaries can change independently of cached section data.
                // Reuse only the heavy payload, never an old summary.
                SessionInfo current = section == SessionDetailSection.Details && !session.IsEnriched
                    ? source.EnrichOne(session) : session;
                return entry.Details with
                {
                    Session = current with { Start = entry.Details.Session.Start },
                    FromCache = true,
                };
            }
            break;
        }

        // Never enrich events or open another section's source to populate the active tab.
        SessionDetails details = section switch
        {
            SessionDetailSection.Details => new(
                source.EnrichWithEvents(session.IsEnriched ? session : source.EnrichOne(session)), []),
            SessionDetailSection.Checkpoints => new(session, source.ReadCheckpoints(session)),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
        token.ThrowIfCancellationRequested();
        // Todos are intentionally excluded: only explicit tab activation may read session.db.

        // Do not cache a torn read while an active session is appending/writing.
        if (version == GetVersion(session, section))
        {
            token.ThrowIfCancellationRequested();
            _cache.AddFirst((session.FolderPath, session.Id, section, version, details));
            if (_cache.Count > Capacity) _cache.RemoveLast();
        }
        return details;
    }

    private string GetVersion(SessionInfo session, SessionDetailSection section) => section switch
    {
        SessionDetailSection.Details => source.GetDetailsVersion(session),
        SessionDetailSection.Checkpoints => source.GetCheckpointsVersion(session),
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };
}
