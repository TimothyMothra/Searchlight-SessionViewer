using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Searchlight.Diagnostics;

namespace Searchlight.Services;

/// <summary>Consent-gated pane navigation timings, not GPU presentation measurements.</summary>
internal sealed class PaneNavigationMonitor : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, int> _visits = new() { ["Home"] = 1 };
    private Measurement? _pending;
    private long _request;
    private bool _disposed;

    public PaneNavigationMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        CoreLog.EnabledChanged += OnMonitoringChanged;
    }

    public Measurement? Begin(string from, string to, string trigger, FrameworkElement target)
    {
        Cancel("superseded");
        int visit = _visits.GetValueOrDefault(to) + 1;
        _visits[to] = visit;
        if (_disposed || !CoreLog.IsEnabled) return null;

        _pending = new Measurement(++_request, from, to, trigger, visit, target, _dispatcher);
        return _pending;
    }

    private void OnMonitoringChanged(object? sender, EventArgs args)
    {
        if (CoreLog.IsEnabled || _disposed) return;
        if (_dispatcher.HasThreadAccess) Cancel("monitoring_disabled");
        else _dispatcher.TryEnqueue(() => { if (!_disposed && !CoreLog.IsEnabled) Cancel("monitoring_disabled"); });
    }

    private void Cancel(string outcome)
    {
        _pending?.Cancel(outcome);
        _pending = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CoreLog.EnabledChanged -= OnMonitoringChanged;
        Cancel("unloaded");
    }

    internal sealed class Measurement
    {
        private readonly FrameworkElement _target;
        private readonly DispatcherQueueTimer _timeout;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly string _prefix;
        private readonly List<string> _messages = [];
        private long _layoutAt;
        private int _layoutUpdates;
        private bool _rendered;
        private bool _workCompleted;
        private bool _cancelled;

        public Measurement(long request, string from, string to, string trigger, int visit,
            FrameworkElement target, DispatcherQueue dispatcher)
        {
            _target = target;
            _prefix = $"PaneNavigation process={Environment.ProcessId} request={request} from={from} to={to} trigger={trigger} visit={visit} first_visit={visit == 1} started_at={DateTimeOffset.Now:o}";
            _timeout = dispatcher.CreateTimer();
            _timeout.Interval = TimeSpan.FromSeconds(5);
            _timeout.IsRepeating = false;
            _timeout.Tick += OnTimeout;
            target.LayoutUpdated += OnLayoutUpdated;
            target.Unloaded += OnUnloaded;
            CompositionTarget.Rendering += OnRendering;
            _timeout.Start();
            Write($"phase=begin target_loaded={target.IsLoaded}");
        }

        public long StartPhase() => Stopwatch.GetTimestamp();

        public void RecordPhase(string phase, long started, string outcome = "completed")
        {
            if (_cancelled || !CoreLog.IsEnabled) return;
            Write(FormattableString.Invariant(
                $"phase={phase} duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} outcome={outcome}"));
        }

        public void CompleteWork()
        {
            if (_cancelled) return;
            _workCompleted = true;
            RecordPhase("handler", _started);
        }

        private void OnLayoutUpdated(object? sender, object args)
        {
            if (!CoreLog.IsEnabled) { Cancel("monitoring_disabled"); return; }
            _layoutUpdates++;
            // ASSUMPTION: retained controls can have old nonzero sizes. Only a
            // post-navigation layout callback counts, not the size at Begin.
            if (_layoutAt == 0 && IsVisible())
                _layoutAt = Stopwatch.GetTimestamp();
        }

        private bool IsVisible() => _target.IsLoaded &&
            _target.Visibility == Visibility.Visible &&
            _target.XamlRoot?.IsHostVisible == true &&
            _target.ActualWidth > 0 && _target.ActualHeight > 0;

        private void OnRendering(object? sender, object args)
        {
            if (!CoreLog.IsEnabled) { Cancel("monitoring_disabled"); return; }
            if (_layoutAt == 0) return;
            if (!IsVisible()) { Cancel("not_visible"); return; }
            long renderedAt = Stopwatch.GetTimestamp();
            _rendered = true;
            Detach();
            // These share an origin and overlap; a XAML tick is not GPU presentation.
            Write(FormattableString.Invariant(
                $"phase=render layout_observed_ms={Stopwatch.GetElapsedTime(_started, _layoutAt).TotalMilliseconds:F3} next_render_tick_ms={Stopwatch.GetElapsedTime(_started, renderedAt).TotalMilliseconds:F3} layout_updates={_layoutUpdates} width={_target.ActualWidth:F1} height={_target.ActualHeight:F1} outcome=render_tick"));
        }

        private void OnTimeout(DispatcherQueueTimer sender, object args) => Cancel("render_timeout");
        private void OnUnloaded(object sender, RoutedEventArgs args) => Cancel("target_unloaded");

        public void Cancel(string outcome)
        {
            if (_cancelled) return;
            if (!_rendered || !_workCompleted)
                Write(FormattableString.Invariant(
                    $"phase=end elapsed_ms={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3} outcome={outcome}"));
            _cancelled = true;
            Detach();
            Flush();
        }

        private void Write(string fields)
        {
            if (_cancelled || !CoreLog.IsEnabled) return;
            // Buffer until the render timestamp is captured so synchronous log
            // file writes are not mistaken for pane initialization/render cost.
            _messages.Add($"{_prefix} {fields}");
            if (_rendered) Flush();
        }

        private void Flush()
        {
            foreach (string message in _messages)
                CoreLog.Write(message);
            _messages.Clear();
        }

        private void Detach()
        {
            _target.LayoutUpdated -= OnLayoutUpdated;
            _target.Unloaded -= OnUnloaded;
            CompositionTarget.Rendering -= OnRendering;
            _timeout.Stop();
            _timeout.Tick -= OnTimeout;
        }
    }
}
