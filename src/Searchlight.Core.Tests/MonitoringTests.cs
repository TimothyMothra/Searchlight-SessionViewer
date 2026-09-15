using System.Text.Json.Nodes;
using Searchlight.Diagnostics;
using Searchlight.Models;
using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

// ASSUMPTION: all tests assigning the process-wide sink share the existing
// non-parallel reader collection, including persistence diagnostics tests.
[Collection("Events reader")]
public sealed class MonitoringTests : IDisposable
{
    private readonly Action<string>? _previousSink = CoreLog.Sink;
    private readonly string _directory = Path.Combine(
        Directory.GetCurrentDirectory(), $".monitoring-test-data-{Guid.NewGuid():N}");

    public MonitoringTests() => CoreLog.Sink = null;

    [Theory]
    [InlineData("Dev", null, true)]
    [InlineData("Dev", false, true)]
    [InlineData("Dev", true, true)]
    [InlineData("Production", null, false)]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, true)]
    [InlineData("Unpackaged", null, false)]
    [InlineData("Unpackaged", false, false)]
    [InlineData("Unpackaged", true, true)]
    [InlineData("Unknown", null, false)]
    [InlineData("Unknown", false, false)]
    public void PolicyRequiresConsentExceptInDev(string channel, bool? preference, bool expected) =>
        Assert.Equal(expected, MonitoringPolicy.IsEnabled(channel, preference));

    [Fact]
    public void SinkGatesWritesAndNotifiesOnlyOnEnabledTransitions()
    {
        List<string> messages = [];
        List<bool> states = [];
        EventHandler changed = (_, _) => states.Add(CoreLog.IsEnabled);
        CoreLog.EnabledChanged += changed;
        try
        {
            Assert.False(CoreLog.IsEnabled);
            CoreLog.Write("before");
            CoreLog.Sink = null;
            CoreLog.Sink = messages.Add;
            CoreLog.Write("first");
            CoreLog.Sink = message => messages.Add($"replacement:{message}");
            CoreLog.Write("second");
            CoreLog.Sink = null;
            CoreLog.Write("after");
            CoreLog.Sink = null;

            Assert.Equal(new[] { true, false }, states);
            Assert.Equal(new[] { "first", "replacement:second" }, messages);
        }
        finally
        {
            CoreLog.EnabledChanged -= changed;
        }
    }

    [Fact]
    public void MissingPreferenceDefaultsOffAndCanBePersistedBothWays()
    {
        string path = WriteSettings("""{"RunElevated":true,"FutureSetting":{"value":42}}""");
        var service = new SettingsService(path);
        Assert.False(new AppSettings().EnableMonitoring);
        Assert.False(service.Current.EnableMonitoring);

        service.Current.EnableMonitoring = true;
        Assert.True(new SettingsService(path).Current.EnableMonitoring);
        service.Current.EnableMonitoring = false;
        var reloaded = new SettingsService(path);
        Assert.False(reloaded.Current.EnableMonitoring);
        Assert.True(reloaded.Current.RunElevated);
        Assert.Equal(42, JsonNode.Parse(File.ReadAllText(path))!["FutureSetting"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void ConcurrentPreferenceAndUnrelatedEditsMergeWithoutLosingFields()
    {
        string path = WriteSettings("""
            {"UseSharedTerminalWindow":false,"RunElevated":true,"AppendYolo":true,
             "UseCustomResumeCommand":true,"CustomResumeCommand":"custom {sessionId}",
             "NotesPaneVisible":true,"HideEmptySessions":false,"HideUnnamedSessions":true,
             "PinnedSessionIds":["pin"],"CustomSessionNames":{"session":"name"},"Future":"keep"}
            """);
        var first = new SettingsService(path);
        var second = new SettingsService(path);
        first.Current.EnableMonitoring = true;
        second.Current.CustomSessionNames = new(second.Current.CustomSessionNames) { ["other"] = "other name" };
        Assert.True(second.Current.EnableMonitoring);
        Assert.True(first.Reload());

        var current = new SettingsService(path).Current;
        Assert.True(current.EnableMonitoring);
        Assert.False(current.UseSharedTerminalWindow);
        Assert.True(current.RunElevated);
        Assert.True(current.AppendYolo);
        Assert.True(current.UseCustomResumeCommand);
        Assert.Equal("custom {sessionId}", current.CustomResumeCommand);
        Assert.True(current.NotesPaneVisible);
        Assert.False(current.HideEmptySessions);
        Assert.True(current.HideUnnamedSessions);
        Assert.Equal(new[] { "pin" }, current.PinnedSessionIds);
        Assert.Equal("name", current.CustomSessionNames["session"]);
        Assert.Equal("other name", current.CustomSessionNames["other"]);
        Assert.Equal("keep", JsonNode.Parse(File.ReadAllText(path))!["Future"]!.GetValue<string>());

        // The observed baseline must advance after reload/save so the stale
        // instance's next unrelated edit cannot resurrect an external opt-out.
        first.Current.EnableMonitoring = false;
        second.Current.NotesPaneVisible = false;
        Assert.False(second.Current.EnableMonitoring);
        Assert.False(new SettingsService(path).Current.EnableMonitoring);
    }

    [Fact]
    public void ReloadAppliesMonitoringBeforeOtherSettingsAndNotifiesDuringReload()
    {
        string path = WriteSettings("""{"EnableMonitoring":true}""");
        var service = new SettingsService(path);
        List<string?> changes = [];
        service.Current.PropertyChanged += (_, e) =>
        {
            Assert.True(service.IsReloading);
            changes.Add(e.PropertyName);
        };
        File.WriteAllText(path, """{"EnableMonitoring":false,"RunElevated":true}""");

        Assert.True(service.Reload());
        Assert.False(service.Current.EnableMonitoring);
        Assert.True(service.Current.RunElevated);
        Assert.False(service.IsReloading);
        Assert.Equal(nameof(AppSettings.EnableMonitoring), changes[0]);
        Assert.Contains(nameof(AppSettings.RunElevated), changes);
        Assert.DoesNotContain("SchemaVersion", File.ReadAllText(path));
    }

    [Fact]
    public void UnsavedMonitoringEditSurvivesReloadAndCanBeRetried()
    {
        string path = WriteSettings("{}");
        var service = new SettingsService(path);
        File.WriteAllText(path, "{");
        service.Current.EnableMonitoring = true;
        Assert.NotEmpty(service.PersistenceNotice);
        File.WriteAllText(path, """{"EnableMonitoring":false,"AppendYolo":true}""");

        Assert.True(service.Reload());
        Assert.True(service.Current.EnableMonitoring);
        Assert.True(service.Current.AppendYolo);
        Assert.True(service.Save());
        var saved = new SettingsService(path).Current;
        Assert.True(saved.EnableMonitoring);
        Assert.True(saved.AppendYolo);
    }

    [Fact]
    public void CorruptConsentStaysOffAndReportsNoticeWithoutLoggingOrOverwriting()
    {
        const string invalid = """{"EnableMonitoring":"yes","Future":"keep"}""";
        string path = WriteSettings(invalid);
        var service = new SettingsService(path);
        Assert.False(service.Current.EnableMonitoring);
        Assert.False(CoreLog.IsEnabled);
        Assert.Contains("Could not reload", service.PersistenceNotice);
        int failures = 0;
        service.PersistenceFailed += (_, _) => failures++;

        Assert.False(service.Save());
        Assert.Equal(1, failures);
        Assert.Contains("Could not save", service.PersistenceNotice);
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    [Fact]
    public void PropertyChangingAllowsOptOutBeforeAutoSaveReportsFailure()
    {
        string path = WriteSettings("""{"EnableMonitoring":true}""");
        var service = new SettingsService(path);
        List<string> messages = [];
        CoreLog.Sink = messages.Add;
        service.Current.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.EnableMonitoring)
                && service.Current.EnableMonitoring)
                CoreLog.Sink = null;
        };
        File.WriteAllText(path, "{");
        int failures = 0;
        service.PersistenceFailed += (_, _) => failures++;

        service.Current.EnableMonitoring = false;

        Assert.False(CoreLog.IsEnabled);
        Assert.Empty(messages);
        Assert.Equal(1, failures);
        Assert.NotEmpty(service.PersistenceNotice);
        Assert.False(service.Current.EnableMonitoring);
    }

    private string WriteSettings(string json)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        CoreLog.Sink = _previousSink;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
