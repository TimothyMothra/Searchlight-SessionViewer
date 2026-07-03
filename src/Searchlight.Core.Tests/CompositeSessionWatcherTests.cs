using Searchlight.Abstractions;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Covers the combined store watcher: a change in either store surfaces as one
/// <see cref="CompositeSessionWatcher.Changed"/> event, and lifecycle calls
/// reach every inner watcher.
/// </summary>
public sealed class CompositeSessionWatcherTests
{
    [Fact]
    public void EitherInnerWatcher_RaisesChanged()
    {
        var claude = new FakeWatcher();
        var copilot = new FakeWatcher();
        using var composite = new CompositeSessionWatcher(claude, copilot);

        int raised = 0;
        composite.Changed += (_, _) => raised++;

        claude.RaiseChanged();
        Assert.Equal(1, raised);

        copilot.RaiseChanged();
        Assert.Equal(2, raised);
    }

    [Fact]
    public void StartAndDispose_ReachAllInnerWatchers()
    {
        var claude = new FakeWatcher();
        var copilot = new FakeWatcher();
        var composite = new CompositeSessionWatcher(claude, copilot);

        composite.Start();
        Assert.True(claude.Started);
        Assert.True(copilot.Started);

        composite.Dispose();
        Assert.True(claude.Disposed);
        Assert.True(copilot.Disposed);
    }

    [Fact]
    public void AfterDispose_InnerEventsNoLongerForward()
    {
        var claude = new FakeWatcher();
        var composite = new CompositeSessionWatcher(claude);

        int raised = 0;
        composite.Changed += (_, _) => raised++;
        composite.Dispose();

        claude.RaiseChanged();
        Assert.Equal(0, raised);
    }

    private sealed class FakeWatcher : ISessionWatcher
    {
        public event EventHandler? Changed;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Start() => Started = true;

        public void Dispose() => Disposed = true;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
