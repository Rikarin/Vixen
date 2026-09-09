// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>
///     <c>vixen trace record</c>: doc 13 § Trace export's own line, which until now was the one part
///     of that section with nothing behind it.
/// </summary>
/// <remarks>
///     ⚠ <b>What was missing was never the exporter.</b> <c>TraceExporter</c> has been finished since
///     it was written and <c>--vixen-trace</c> has written the file at shutdown since
///     <c>TraceExportTests</c> was; the two ways doc 13 says a person asks for a trace — this verb
///     and the editor's capture button — were the halves nobody had built. Everything asserted here
///     is about the composition and the refusals, because those are the parts a build cannot be
///     spent on: the run itself needs MSBuild and a window.
/// </remarks>
public sealed class TraceCommandTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-trace-tests", Guid.NewGuid().ToString("N"));

    public TraceCommandTests() => Directory.CreateDirectory(Path.Combine(root, "Assets"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>The verb exists, rather than parsing and apologising.</summary>
    [Fact]
    public void TheVerbIsPresent() {
        var trace = Assert.Single(VixenCommand.Create().Subcommands, command => command.Name == "trace");

        Assert.Contains(trace.Subcommands, command => command.Name == "record");
    }

    /// <summary>Every duration form doc 13's line could be written in.</summary>
    [Theory]
    [InlineData("10s", 10d)]
    [InlineData("10S", 10d)]
    [InlineData("500ms", 0.5d)]
    [InlineData("500MS", 0.5d)]
    [InlineData("2m", 120d)]
    [InlineData("1.5s", 1.5d)]
    [InlineData("30", 30d)]
    [InlineData("  10s  ", 10d)]
    public void ADurationIsReadInTheUnitItWasWrittenIn(string text, double seconds) {
        Assert.True(VixenCommand.TryParseDuration(text, out var duration));
        Assert.Equal(seconds, duration.TotalSeconds, 6);
    }

    /// <summary>
    ///     ⚠ <b>The one that is worth a test of its own: <c>ms</c> ends in <c>s</c>.</b> A suffix
    ///     check in the other order reads <c>500ms</c> as five hundred seconds, which is a run eight
    ///     minutes long where half a second was asked for — and nothing about the output would say
    ///     so.
    /// </summary>
    [Fact]
    public void MillisecondsAreNotSeconds() {
        Assert.True(VixenCommand.TryParseDuration("500ms", out var milliseconds));
        Assert.True(VixenCommand.TryParseDuration("500s", out var seconds));

        Assert.Equal(1000d, seconds / milliseconds, 6);
    }

    /// <summary>Nothing that cannot be recorded is accepted.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("0s")]
    [InlineData("-5s")]
    [InlineData("soon")]
    [InlineData("s")]
    [InlineData("")]
    [InlineData(null)]
    public void ADurationThatIsNotOneIsRefused(string? text) =>
        Assert.False(VixenCommand.TryParseDuration(text, out _));

    /// <summary>What the child is told, and in which order.</summary>
    [Fact]
    public void TheChildIsToldWhereToWriteAndHowLongToRun() {
        var composed = VixenCommand.TraceArguments("/tmp/run.json", TimeSpan.FromSeconds(2.5), ["--vixen-scene", "arena"]);

        Assert.Equal(["--vixen-trace", "/tmp/run.json", "--vixen-run-for", "2.5", "--vixen-scene", "arena"], composed);
    }

    /// <summary>
    ///     ⚠ The caller's arguments come last so that they win: <c>AppArguments</c> applies a command
    ///     line in order, so a person who passes their own <c>--vixen-run-for</c> is overriding this
    ///     verb rather than being silently overridden by it.
    /// </summary>
    [Fact]
    public void TheCallersOwnArgumentsComeLast() {
        var composed = VixenCommand.TraceArguments("/tmp/run.json", TimeSpan.FromSeconds(10), ["--vixen-run-for", "1"]);

        Assert.Equal("--vixen-run-for", composed[^2]);
        Assert.Equal("1", composed[^1]);
    }

    /// <summary>A default path that names the application and the moment, and ends in .json.</summary>
    [Fact]
    public void TheDefaultPathNamesTheApplicationAndTheMoment() {
        var path = VixenCommand.DefaultTracePath("/games/arena", "Arena", new(2026, 9, 10, 14, 5, 6, TimeSpan.Zero));

        Assert.Equal(Path.Combine("/games/arena", "Traces", "Arena-20260910-140506.json"), path);
    }

    /// <summary>
    ///     A duration that is not one is refused before anything is built, which is what makes the
    ///     refusal worth having: the alternative is a minute of MSBuild and then an apology.
    /// </summary>
    [Fact]
    public async Task ADurationThatIsNotOneFailsBeforeAnythingIsBuilt() {
        var (code, output, error) = await RunFull("trace", "record", "--project", root, "--duration", "soon");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("soon", error, StringComparison.Ordinal);
        Assert.Equal("", output);
    }

    /// <summary>And a project with nothing to run says so rather than recording nothing.</summary>
    [Fact]
    public async Task AProjectWithNoApplicationSaysSo() {
        var (code, _, error) = await RunFull("trace", "record", "--project", root);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("nothing to record", error, StringComparison.Ordinal);
    }

    static async Task<(ExitCode Code, string Output, string Error)> RunFull(params string[] args) {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };

        var parsed = VixenCommand.Create(output, error).Parse(args);

        if (parsed.Errors.Count > 0) {
            return (ExitCode.UsageError, output.ToString(), string.Join("\n", parsed.Errors.Select(e => e.Message)));
        }

        var code = await parsed.InvokeAsync(null, TestContext.Current.CancellationToken);

        return ((ExitCode)code, output.ToString(), error.ToString());
    }
}
