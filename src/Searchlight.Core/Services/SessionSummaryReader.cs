using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Searchlight.Models;

namespace Searchlight.Services;

internal sealed record SummaryBatch(SessionInfo[] Rows, double ReadMilliseconds, long ReadyTimestamp);

internal sealed class SessionSummaryReader(ISessionDataSource source)
{
    internal const int BatchSize = 30;
    internal const int Concurrency = 4;
    internal const int BufferedBatches = 2;

    public async Task<SessionInfo[]> EnrichAsync(IReadOnlyList<SessionInfo> sessions, CancellationToken token)
    {
        var rows = new SessionInfo[sessions.Count];
        // ASSUMPTION: four readers overlap filesystem latency without launching
        // an unbounded task per folder. Result slots preserve catalog order.
        await Parallel.ForEachAsync(Enumerable.Range(0, sessions.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Concurrency,
                CancellationToken = token,
                TaskScheduler = TaskScheduler.Default,
            },
            (i, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                rows[i] = source.EnrichOne(sessions[i]);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        return rows;
    }

    public async IAsyncEnumerable<SummaryBatch> ReadBatchesAsync(
        IReadOnlyList<SessionInfo> sessions,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken producerToken = lifetime.Token;
        var channel = Channel.CreateBounded<SummaryBatch>(new BoundedChannelOptions(BufferedBatches)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

        // The producer never resumes on the UI context. It can read ahead while
        // WinUI renders, but retains at most two queued batches plus in-flight work.
        Task producer = Task.Run(async () =>
        {
            foreach (SessionInfo[] batch in sessions.Chunk(BatchSize))
            {
                long started = Stopwatch.GetTimestamp();
                SessionInfo[] rows = await EnrichAsync(batch, producerToken).ConfigureAwait(false);
                var result = new SummaryBatch(rows,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, Stopwatch.GetTimestamp());
                await channel.Writer.WriteAsync(result, producerToken).ConfigureAwait(false);
            }
        }, producerToken);

        // Complete the consumer on every producer outcome, including a task that
        // was cancelled before its delegate started. Observe and propagate faults.
        _ = producer.ContinueWith(completed =>
            channel.Writer.TryComplete(completed.IsFaulted ? completed.Exception!.InnerException
                : completed.IsCanceled ? new OperationCanceledException(producerToken) : null),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await foreach (SummaryBatch batch in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                yield return batch;
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (producerToken.IsCancellationRequested) { }
        }
    }
}
