using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Searchlight.Diagnostics;
using Searchlight.ViewModels;

namespace Searchlight.Services;

/// <summary>Opt-in UI realization/layout/render-tick milestones, never GPU presentation timing.</summary>
internal sealed class DetailsRenderMonitor : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<FrameworkElement, PendingRender> _pending = new();
    private bool _renderingHooked;
    private bool _disposed;
    private bool _active = true;
    private long _activatedAt;

    public DetailsRenderMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _activatedAt = CoreLog.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        CoreLog.EnabledChanged += OnMonitoringChanged;
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active) _activatedAt = CoreLog.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        else Clear();
    }

    public void TrackLoaded(FrameworkElement element, SessionMetadataGroup group)
    {
        long requested = group.TakeMaterializationTimestamp();
        if (_disposed || !_active || !CoreLog.IsEnabled) return;
        Remove(element);
        long loaded = Stopwatch.GetTimestamp();
        // A retained group can reattach after a tab change. Do not count time spent
        // on another tab as rendering work, or reuse an already-observed request.
        long started = requested == 0 ? loaded : Math.Max(requested, _activatedAt);
        var pending = new PendingRender(group,
            started != 0 ? started : loaded,
            loaded, requested == 0 ? "reattach" : group.IsOverview ? "initial" : "viewport",
            (_, _) => ObserveLayout(element), (_, _) => Cancel(element, "unloaded"));
        _pending.Add(element, pending);
        element.LayoutUpdated += pending.LayoutHandler;
        element.Unloaded += pending.UnloadedHandler;
        ObserveLayout(element);
    }

    public void CancelGroup(SessionMetadataGroup group)
    {
        foreach (var element in _pending.Where(p => ReferenceEquals(p.Value.Group, group)).Select(p => p.Key).ToArray())
            Cancel(element, "left_viewport");
    }

    private void ObserveLayout(FrameworkElement element)
    {
        if (!_pending.TryGetValue(element, out var pending)) return;
        if (!CoreLog.IsEnabled) { Clear(); return; }
        pending.LayoutUpdates++;
        if (pending.LayoutObservedAt == 0 && element.IsLoaded && element.ActualWidth > 0 && element.ActualHeight > 0)
            pending.LayoutObservedAt = Stopwatch.GetTimestamp();
        UpdateRenderingHook();
    }

    private void OnRendering(object? sender, object args)
    {
        if (!CoreLog.IsEnabled) { Clear(); return; }
        long rendered = Stopwatch.GetTimestamp();
        foreach (var (element, pending) in _pending.ToArray())
        {
            if (pending.LayoutObservedAt == 0) continue;
            if (!element.IsLoaded || element.XamlRoot?.IsHostVisible != true)
            {
                Cancel(element, "not_visible");
                continue;
            }

            // All three durations share one origin and overlap. Rendering is a XAML
            // scheduling milestone, not proof that the GPU has presented the pixels.
            CoreLog.Write(FormattableString.Invariant(
                $"DetailsRender presentation={pending.Group.PresentationId} group={pending.Group.Title.Replace(' ', '_')} trigger={pending.Trigger} fields={pending.Group.Fields.Count} loaded_ms={Elapsed(pending.StartedAt, pending.LoadedAt):F3} layout_observed_ms={Elapsed(pending.StartedAt, pending.LayoutObservedAt):F3} next_render_tick_ms={Elapsed(pending.StartedAt, rendered):F3} layout_updates={pending.LayoutUpdates} width={element.ActualWidth:F1} height={element.ActualHeight:F1} outcome=render_tick"));
            Remove(element);
        }
        UpdateRenderingHook();
    }

    private static double Elapsed(long start, long end) => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private void Cancel(FrameworkElement element, string reason)
    {
        if (_pending.TryGetValue(element, out var pending) && CoreLog.IsEnabled)
            CoreLog.Write($"DetailsRender presentation={pending.Group.PresentationId} group={pending.Group.Title.Replace(' ', '_')} outcome={reason}");
        Remove(element);
        UpdateRenderingHook();
    }

    private void Remove(FrameworkElement element)
    {
        if (!_pending.Remove(element, out var pending)) return;
        element.LayoutUpdated -= pending.LayoutHandler;
        element.Unloaded -= pending.UnloadedHandler;
    }

    private void UpdateRenderingHook()
    {
        bool needed = !_disposed && _active && CoreLog.IsEnabled && _pending.Values.Any(p => p.LayoutObservedAt != 0);
        if (needed == _renderingHooked) return;
        if (needed) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = needed;
    }

    private void OnMonitoringChanged(object? sender, EventArgs args)
    {
        if (CoreLog.IsEnabled || _disposed) return;
        if (_dispatcher.HasThreadAccess) Clear();
        else _dispatcher.TryEnqueue(() => { if (!_disposed && !CoreLog.IsEnabled) Clear(); });
    }

    private void Clear()
    {
        foreach (var element in _pending.Keys.ToArray()) Remove(element);
        UpdateRenderingHook();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CoreLog.EnabledChanged -= OnMonitoringChanged;
        Clear();
    }

    private sealed class PendingRender(SessionMetadataGroup group, long startedAt, long loadedAt, string trigger,
        EventHandler<object> layoutHandler, RoutedEventHandler unloadedHandler)
    {
        public SessionMetadataGroup Group { get; } = group;
        public long StartedAt { get; } = startedAt;
        public long LoadedAt { get; } = loadedAt;
        public string Trigger { get; } = trigger;
        public long LayoutObservedAt { get; set; }
        public int LayoutUpdates { get; set; }
        public EventHandler<object> LayoutHandler { get; } = layoutHandler;
        public RoutedEventHandler UnloadedHandler { get; } = unloadedHandler;
    }
}
