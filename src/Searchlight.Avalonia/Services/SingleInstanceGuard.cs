using System.IO.Pipes;
using Searchlight.Diagnostics;

namespace Searchlight.Avalonia.Services;

/// <summary>
/// Single-instance guard for normal tray mode, mirroring the WinUI host: the
/// first instance owns a named mutex and listens on a named pipe; a second
/// launch (e.g. run-at-login plus a shortcut both firing) signals the pipe so
/// the running instance surfaces its window, then exits — instead of a
/// duplicate tray icon appearing. Named pipes are cross-platform in .NET
/// (implemented over Unix domain sockets on macOS/Linux).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Searchlight.Avalonia.SingleInstance";
    private const string PipeName = "Searchlight.Avalonia.Show";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;

    private SingleInstanceGuard(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    /// <summary>True when this process is the first (owning) instance.</summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// Tries to become the primary instance. Check <see cref="IsPrimary"/> on
    /// the result; a secondary instance should call <see cref="SignalPrimary"/>
    /// and exit.
    /// </summary>
    public static SingleInstanceGuard Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return new SingleInstanceGuard(mutex, createdNew);
    }

    /// <summary>
    /// Starts the background pipe listener that raises <paramref name="onShow"/>
    /// (on a worker thread — marshal to the UI thread yourself) every time
    /// another launch signals this instance. Primary instance only.
    /// </summary>
    public void ListenForShowRequests(Action onShow)
    {
        if (!IsPrimary || _cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    onShow();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A broken pipe just means the client vanished; keep listening.
                    CoreLog.Write($"SingleInstanceGuard: listener EXCEPTION {ex.Message}");
                }
            }
        }, token);
    }

    /// <summary>
    /// Signals the primary instance to show its window. Returns false when no
    /// primary is listening (nothing to surface — e.g. it is shutting down).
    /// </summary>
    public static bool SignalPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 1000);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Write($"SingleInstanceGuard: signal failed {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Cancel();
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a different thread than acquired — ignore;
                // the OS reclaims it at process exit anyway.
            }
        }

        _mutex.Dispose();
    }
}
