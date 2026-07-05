using Searchlight.Services;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Covers the macOS AppleScript boundary: the shell command is embedded in a
/// double-quoted AppleScript string literal, so backslashes and double quotes
/// must be escaped (in that order), while characters AppleScript gives no
/// meaning pass through untouched.
/// </summary>
public sealed class MacTerminalScriptTests
{
    [Fact]
    public void EscapesDoubleQuotes()
    {
        Assert.Equal("cd \\\"x\\\"", MacTerminalScript.EscapeStringLiteral("cd \"x\""));
    }

    [Fact]
    public void EscapesBackslashes()
    {
        Assert.Equal("a\\\\b", MacTerminalScript.EscapeStringLiteral("a\\b"));
    }

    [Fact]
    public void BackslashBeforeQuote_EscapesBothWithoutDoubleProcessing()
    {
        // \" must become \\\" (escaped backslash + escaped quote) — reversing
        // the Replace order would corrupt this into \\\\" and un-escape the quote.
        Assert.Equal("\\\\\\\"", MacTerminalScript.EscapeStringLiteral("\\\""));
    }

    [Theory]
    // AppleScript string literals give these no special meaning; the shell
    // sees them only inside the single-quoting applied by the command builders.
    [InlineData("$HOME")]
    [InlineData("`id`")]
    [InlineData("it's")]
    public void ShellMetacharacters_PassThrough(string value)
    {
        Assert.Equal(value, MacTerminalScript.EscapeStringLiteral(value));
    }

    [Fact]
    public void Build_WrapsCommandInTerminalDoScript()
    {
        string script = MacTerminalScript.Build("cd 'x' && claude --resume abc");

        Assert.Equal(
            "tell application \"Terminal\"\nactivate\n" +
            "do script \"cd 'x' && claude --resume abc\"\nend tell",
            script);
    }
}
