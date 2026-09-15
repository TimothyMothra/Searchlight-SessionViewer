using Searchlight.Models;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Exercises the pure computed projections on <see cref="SessionInfo"/>: display-name
/// fallback, short id, the CLI/App/Unknown client mapping, the raw client string, cwd
/// fallback, and the effective updated-time preference. No I/O — construction only.
/// </summary>
public sealed class SessionInfoProjectionTests
{
    private const string SampleId = "12345678-0000-4000-8000-000000000042";

    [Fact]
    public void PromptPreviews_ProjectIndependentFirstAndLastMessages()
    {
        Assert.Null(Session().FirstPromptPreview);
        Assert.Null(Session().LastPromptPreview);
        var session = Session() with
        {
            Start = new SessionStartInfo { FirstUserPrompt = "first", LastUserPrompt = "last" },
        };
        Assert.Equal("first", session.FirstPromptPreview);
        Assert.Equal("last", session.LastPromptPreview);
    }

    private static SessionInfo Session(
        string? name = null,
        string? clientName = null,
        string? workspaceCwd = null,
        string? startCwd = null,
        DateTimeOffset? workspaceUpdatedAt = null,
        DateTimeOffset? lastWrite = null)
    {
        WorkspaceMetadata? ws =
            (name is null && clientName is null && workspaceCwd is null && workspaceUpdatedAt is null)
                ? null
                : new WorkspaceMetadata
                {
                    Id = SampleId,
                    Name = name,
                    ClientName = clientName,
                    Cwd = workspaceCwd,
                    UpdatedAt = workspaceUpdatedAt,
                };

        SessionStartInfo? start = startCwd is null ? null : new SessionStartInfo { Cwd = startCwd };

        return new SessionInfo
        {
            Id = SampleId,
            FolderName = SampleId,
            FolderPath = $@"C:\x\{SampleId}",
            LastWriteTime = lastWrite ?? DateTimeOffset.UnixEpoch,
            Workspace = ws,
            Start = start,
        };
    }

    [Fact]
    public void DisplayName_PrefersWorkspaceName() =>
        Assert.Equal("My Session", Session(name: "My Session").DisplayName);

    [Fact]
    public void DisplayName_FallsBackToFullId_WhenNoWorkspaceName()
    {
        // Regression guard: fallback must be the FULL 36-char UUID, not the 8-char ShortId.
        SessionInfo s = Session(name: null);
        Assert.Equal(SampleId, s.DisplayName);
        Assert.Equal(36, s.DisplayName.Length);
    }

    [Fact]
    public void DisplayName_FallsBackToId_WhenNameIsWhitespace() =>
        Assert.Equal(SampleId, Session(name: "   ").DisplayName);

    [Fact]
    public void ShortId_IsFirstEightChars() =>
        Assert.Equal("12345678", Session().ShortId);

    [Theory]
    [InlineData("github/cli", "CLI", true, false)]
    [InlineData("github/autopilot", "App", false, true)]
    [InlineData(null, "Unknown", false, false)]
    [InlineData("something/else", "Unknown", false, false)]
    public void ClientProjections_MapClientName(string? clientName, string label, bool isCli, bool isApp)
    {
        SessionInfo s = Session(clientName: clientName);
        Assert.Equal(label, s.ClientLabel);
        Assert.Equal(isCli, s.IsCliClient);
        Assert.Equal(isApp, s.IsAppClient);
    }

    [Fact]
    public void ClientNameRaw_PreservesFullString() =>
        Assert.Equal("github/cli", Session(clientName: "github/cli").ClientNameRaw);

    [Fact]
    public void ClientNameRaw_IsUnknown_WhenAbsent() =>
        Assert.Equal("Unknown", Session(clientName: null).ClientNameRaw);

    [Fact]
    public void Cwd_PrefersWorkspace_ThenStart()
    {
        Assert.Equal(@"C:\ws", Session(workspaceCwd: @"C:\ws", startCwd: @"C:\start").Cwd);
        Assert.Equal(@"C:\start", Session(workspaceCwd: null, startCwd: @"C:\start").Cwd);
        Assert.Null(Session().Cwd);
    }

    [Fact]
    public void UpdatedAt_PrefersWorkspaceUpdatedAt_OverLastWrite()
    {
        DateTimeOffset wsUpdated = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset folder = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(wsUpdated, Session(workspaceUpdatedAt: wsUpdated, lastWrite: folder).UpdatedAt);
    }

    [Fact]
    public void UpdatedAt_FallsBackToLastWrite_WhenNoWorkspaceTimestamp()
    {
        DateTimeOffset folder = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(folder, Session(lastWrite: folder).UpdatedAt);
    }
}
