using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// Fans one <see cref="Changed"/> event out of both store watchers, so a host
/// showing the combined Claude + Copilot list refreshes when either
/// <c>~/.claude/projects</c> or <c>~/.copilot/session-state</c> changes. Each
/// inner watcher keeps its own debounce; this wrapper only forwards.
/// </summary>
public sealed class CompositeSessionWatcher : ISessionWatcher
{
    private readonly ISessionWatcher[] _watchers;

    /// <summary>Raised when either underlying store reports a change.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates the composite over the given store watchers.</summary>
    public CompositeSessionWatcher(params ISessionWatcher[] watchers)
    {
        _watchers = watchers;
        foreach (ISessionWatcher watcher in _watchers)
        {
            watcher.Changed += OnInnerChanged;
        }
    }

    private void OnInnerChanged(object? sender, EventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void Start()
    {
        foreach (ISessionWatcher watcher in _watchers)
        {
            watcher.Start();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (ISessionWatcher watcher in _watchers)
        {
            watcher.Changed -= OnInnerChanged;
            watcher.Dispose();
        }
    }
}
