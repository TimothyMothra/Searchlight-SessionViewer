using Searchlight.Abstractions;

namespace Searchlight.Services;

/// <summary>
/// No-op <see cref="IResumeLauncher"/> for mock/demo mode. Records the last resume
/// request (useful for tests) and reports success without launching anything, so
/// screenshots and demos never spawn a terminal.
/// </summary>
public sealed class MockResumeLauncher : IResumeLauncher
{
    private readonly SettingsService _settings;

    public MockResumeLauncher() : this(new SettingsService((string?)null)) { }
    public MockResumeLauncher(SettingsService settings) => _settings = settings;
    public string? LastError { get; private set; }

    /// <summary>The session id passed to the most recent <see cref="Resume"/> call, if any.</summary>
    public string? LastResumedSessionId { get; private set; }

    /// <inheritdoc />
    public string? Resume(string sessionId, string? tabTitle = null)
    {
        LastError = null;
        string command;
        try { command = ResumeCommandBuilder.Build(_settings.Current, sessionId); }
        catch (ArgumentException ex)
        {
            LastError = ex.Message;
            return null;
        }
        LastResumedSessionId = sessionId;
        return command;
    }
}
