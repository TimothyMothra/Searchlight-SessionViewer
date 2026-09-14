using System.Diagnostics;
using System.Text;
using Searchlight.Models;
using Searchlight.Services;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

public sealed class ResumeCommandTests
{
    private const string Id = "00000000-0000-4000-8000-000000000001";

    [Theory]
    [InlineData(false, "copilot --resume=00000000-0000-0000-0000-000000000000")]
    [InlineData(true, "copilot --resume=00000000-0000-0000-0000-000000000000 --yolo")]
    public void DefaultPreviewMatchesActualCommand(bool yolo, string preview)
    {
        var settings = new AppSettings { AppendYolo = yolo };
        Assert.Equal(preview, ResumeCommandBuilder.Preview(settings));
        Assert.Equal(preview.Replace(ResumeCommandBuilder.PreviewSessionId, Id), ResumeCommandBuilder.Build(settings, Id));
    }

    [Fact]
    public void CustomModeOwnsAllArgumentsAndPreservesShellSyntax()
    {
        const string template = """ "C:\Tools With Spaces\resume.cmd" --id="{sessionId}" && echo "a;b & c" """;
        var settings = new AppSettings
        {
            UseCustomResumeCommand = true,
            CustomResumeCommand = template,
            AppendYolo = true,
        };
        Assert.Equal(template.Replace("{sessionId}", Id), ResumeCommandBuilder.Build(settings, Id));
        Assert.DoesNotContain("--yolo", ResumeCommandBuilder.Preview(settings));
        settings.UseCustomResumeCommand = false;
        Assert.EndsWith(" --yolo", ResumeCommandBuilder.Build(settings, Id));
        Assert.Equal(template, settings.CustomResumeCommand);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("copilot --resume=hardcoded")]
    [InlineData("copilot --resume={sessionid}")]
    [InlineData("echo {sessionId}\r\nexit")]
    [InlineData("echo {sessionId}\0")]
    public void InvalidTemplatesDoNotProduceCommands(string template)
    {
        var settings = new AppSettings { UseCustomResumeCommand = true, CustomResumeCommand = template };
        Assert.NotNull(ResumeCommandBuilder.Validate(settings));
        Assert.Empty(ResumeCommandBuilder.Preview(settings));
        Assert.Throws<ArgumentException>(() => ResumeCommandBuilder.Build(settings, Id));
    }

    [Theory]
    [InlineData("000000000")]
    [InlineData("anything & echo unexpected")]
    [InlineData("")]
    public void UntrustedIdsCannotBecomeShellSyntax(string id) =>
        Assert.Throws<ArgumentException>(() => ResumeCommandBuilder.Build(new(), id));

    [Fact]
    public void TemplateLimitBoundsEncodedWindowsCommandLine()
    {
        string template = "echo {sessionId}" + new string('x', ResumeCommandBuilder.MaxTemplateLength - 5 - Id.Length);
        var settings = new AppSettings { UseCustomResumeCommand = true, CustomResumeCommand = template };
        Assert.Null(ResumeCommandBuilder.Validate(settings));
        string transport = ResumeCommandBuilder.EncodeTerminalTransport(ResumeCommandBuilder.Build(settings, Id));
        Assert.True(transport.Length + 1024 < 32767);
        settings.CustomResumeCommand = new string('x', ResumeCommandBuilder.MaxTemplateLength + 1) + "{sessionId}";
        Assert.NotNull(ResumeCommandBuilder.Validate(settings));
    }

    [Fact]
    public void RepeatedTokensAreExpandedAndCountTowardTransportLimit()
    {
        var settings = new AppSettings { UseCustomResumeCommand = true, CustomResumeCommand = "echo {sessionId} {sessionId}" };
        Assert.Equal($"echo {Id} {Id}", ResumeCommandBuilder.Build(settings, Id));
        settings.CustomResumeCommand = string.Concat(Enumerable.Repeat("{sessionId}", 200));
        Assert.True(settings.CustomResumeCommand.Length < ResumeCommandBuilder.MaxTemplateLength);
        Assert.NotNull(ResumeCommandBuilder.Validate(settings));
    }

    [Fact]
    public void ViewModelUpdatesPreviewAndRestoresDefaultWithoutResettingYolo()
    {
        var settings = new SettingsService((string?)null);
        var source = new MockSessionDataSource();
        var launcher = new MockResumeLauncher(settings);
        using var vm = new MainViewModel(source, new NullSessionWatcher(),
            new DetailsViewModel(source, launcher, new MockClipboardService()),
            settings, new NotesService((string?)null), new InlineUiDispatcher());
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        settings.Current.AppendYolo = true;
        Assert.EndsWith("--yolo", vm.ResumeCommandPreview);
        settings.Current.UseCustomResumeCommand = true;
        settings.Current.CustomResumeCommand = "echo {sessionId}";
        Assert.Equal("echo 00000000-0000-0000-0000-000000000000", vm.ResumeCommandPreview);
        Assert.Contains(nameof(vm.ResumeCommandPreview), notifications);
        Assert.False(vm.IsDefaultResumeCommand);
        Assert.Equal($"echo {Id}", launcher.Resume(Id));
        settings.Current.CustomResumeCommand = "";
        Assert.True(vm.HasResumeCommandError);
        Assert.Null(launcher.Resume(Id));
        Assert.NotEmpty(launcher.LastError!);
        vm.ResetResumeCommandCommand.Execute(null);
        Assert.False(vm.HasResumeCommandError);
        Assert.True(vm.IsDefaultResumeCommand);
        Assert.True(settings.Current.AppendYolo);
        Assert.Equal(ResumeCommandBuilder.DefaultTemplate, settings.Current.CustomResumeCommand);
    }

    [Fact]
    public void TransportContainsOnlyEncodedUserText()
    {
        const string command = """echo "a;b & c" && echo 'quoted' """;
        string encoded = ResumeCommandBuilder.EncodeTerminalTransport(command);
        string script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.DoesNotContain(command, script);
        Assert.Contains(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)), script);
        Assert.All(encoded, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='));
    }

    [WindowsFact]
    public async Task TransportAndFallbackPreserveQuotedExecutablesAndShellOperators()
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        string powershell = Path.Combine(Path.GetDirectoryName(cmd)!, "WindowsPowerShell", "v1.0", "powershell.exe");
        // ASSUMPTION: these are fixed harmless test commands, never user settings.
        string command = $"\"{cmd}\" /d /c \"echo nested;value\" & echo \"A;B & C\" & echo {Id} & exit";
        // cmd's echo includes the space preceding each command separator.
        string expected = $"nested;value \n\"A;B & C\" \n{Id}";
        string fallback = await Run(cmd, ResumeCommandBuilder.CmdArguments(command));
        string transport = await Run(powershell,
            "-NoLogo -NoProfile -EncodedCommand " + ResumeCommandBuilder.EncodeTerminalTransport(command));
        Assert.Equal(expected, fallback);
        Assert.Equal(fallback, transport);
    }

    private static async Task<string> Run(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(await error), await error);
        return (await output).Replace("\r\n", "\n").Trim();
    }

    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows cmd.exe and Windows PowerShell.";
        }
    }
}
