namespace Searchlight.Diagnostics;

/// <summary>Local diagnostics consent; never authorizes telemetry uploads.</summary>
public static class MonitoringPolicy
{
    /// <summary>Dev always monitors; other channels require an explicit opt-in.</summary>
    public static bool IsEnabled(string channel, bool? enableMonitoring) =>
        // ASSUMPTION: unpackaged/unknown channels follow Production's opt-in
        // policy. Null means consent is not yet loaded (or could not be read).
        string.Equals(channel, "Dev", StringComparison.Ordinal) || enableMonitoring == true;
}
