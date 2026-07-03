using Avalonia.Threading;
using Searchlight.Abstractions;

namespace Searchlight.Avalonia.Services;

/// <summary>Marshals actions onto the Avalonia UI thread.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
