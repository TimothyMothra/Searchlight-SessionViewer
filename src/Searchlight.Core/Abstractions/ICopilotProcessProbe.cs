namespace Searchlight.Abstractions;

/// <summary>Read-only host probe for a local, live Copilot process.</summary>
public interface ICopilotProcessProbe
{
    /// <summary>Returns the process start time, or null when ownership cannot be confirmed.</summary>
    DateTimeOffset? GetStartTimeUtc(int processId);
}
