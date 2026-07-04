using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// No-op <see cref="IResumeLauncher"/> for mock/demo mode. Records the last resume
/// request (useful for tests) and reports success without launching anything, so
/// screenshots and demos never spawn a terminal.
/// </summary>
public sealed class MockResumeLauncher : IResumeLauncher
{
    /// <summary>The session id passed to the most recent <see cref="Resume"/> call, if any.</summary>
    public string? LastResumedSessionId { get; private set; }

    /// <inheritdoc />
    public string? Resume(string sessionId, string? tabTitle = null)
    {
        LastResumedSessionId = sessionId;
        return $"copilot --resume={sessionId}";
    }
}
