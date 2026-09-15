namespace Searchlight.Diagnostics;

/// <summary>
/// Minimal logging seam for the platform-neutral core. The core writes diagnostic
/// breadcrumbs through <see cref="Write"/>; the host front-end assigns <see cref="Sink"/>
/// to route them wherever it logs (e.g. the tray app's temp-file logger). When no sink
/// is set the breadcrumbs are silently dropped, so the core never depends on any host
/// logging type.
/// </summary>
public static class CoreLog
{
    private static Action<string>? s_sink;

    /// <summary>Host-supplied log target. Null (default) drops all breadcrumbs.</summary>
    public static Action<string>? Sink
    {
        get => Volatile.Read(ref s_sink);
        set
        {
            var previous = Interlocked.Exchange(ref s_sink, value);
            if ((previous is null) != (value is null))
                EnabledChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Whether a diagnostic sink is currently installed.</summary>
    public static bool IsEnabled => Sink is not null;

    /// <summary>
    /// Raised synchronously on the assigning thread when logging turns on or off.
    /// Hosts assign on the UI thread so render monitors can detach immediately.
    /// </summary>
    public static event EventHandler? EnabledChanged;

    /// <summary>Writes a breadcrumb to <see cref="Sink"/> if one is set.</summary>
    // ASSUMPTION: a host sink rechecks consent before I/O, since an in-flight
    // writer may have captured the previous sink just before it was disabled.
    public static void Write(string message) => Sink?.Invoke(message);
}
