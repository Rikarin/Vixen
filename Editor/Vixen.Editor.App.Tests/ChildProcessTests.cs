// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The one way the editor runs git and <c>dotnet</c>: both streams at once, and a hang check that can fire (#1512).</summary>
/// <remarks>
///     ⚠ <b>A real child process, because the defect is the platform's pipe.</b> A child that writes more
///     than the pipe holds to stderr before closing stdout blocks on the write; a parent reading stdout to
///     the end first then waits for ever. A fake stream would have no buffer to fill.
/// </remarks>
public class ChildProcessTests {
    /// <summary>More than any platform's pipe buffer: 64 KB on Windows and Linux, 16–64 KB on macOS.</summary>
    const int Flood = 256 * 1024;

    /// <summary>
    ///     A child that fills stderr before it writes a word to stdout is read to the end, both streams,
    ///     rather than deadlocking.
    /// </summary>
    /// <remarks>
    ///     The ceiling is a hang check on a task the test does not trust to return; what is asserted is
    ///     every byte of both streams. Against reading stdout then stderr, it hangs and is red.
    /// </remarks>
    [Fact]
    public async Task A_child_that_fills_stderr_first_is_read_to_the_end() {
        var running = Task.Run(() => ChildProcess.Run(Shell($"{Stderr(Flood)}; {Stdout("done")}"), TimeSpan.FromMinutes(1)), TestContext.Current.CancellationToken);

        // A hang check: a deadlocked read throws `TimeoutException` here rather than hanging the suite.
        var result = Assert.NotNull(await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.True(result.Finished);
        Assert.Equal(0, result.Code);
        Assert.Equal("done", result.Output.Trim());
        Assert.Equal(Flood, result.Error.Count(character => character == 'x'));
    }

    /// <summary>A child that never finishes is killed when the patience runs out, though it holds its streams open.</summary>
    /// <remarks>
    ///     ⚠ <b>The reads came before the wait, so the wait's patience could not fire</b> for a child that kept
    ///     its streams open — git behind a credential prompt, a build that hangs. One second of patience
    ///     against a child that would sleep a minute; the ceiling is a hang check.
    /// </remarks>
    [Fact]
    public async Task A_child_that_does_not_finish_is_given_up_on() {
        var clock = Stopwatch.StartNew();
        var running = Task.Run(() => ChildProcess.Run(Shell($"{Stderr(Flood)}; {Sleep(60)}"), TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);

        // A hang check: patience that never fires throws `TimeoutException` here.
        var result = Assert.NotNull(await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.False(result.Finished);
        Assert.Equal(-1, result.Code);

        // And it was the patience, not the child finishing on its own a minute later.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
    }

    static ProcessStartInfo Shell(string script) {
        if (OperatingSystem.IsWindows()) {
            var start = new ProcessStartInfo("powershell") { CreateNoWindow = true };

            foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-Command", script]) {
                start.ArgumentList.Add(argument);
            }

            return start;
        }

        var posix = new ProcessStartInfo("/bin/sh");

        posix.ArgumentList.Add("-c");
        posix.ArgumentList.Add(script);
        return posix;
    }

    static string Stderr(int count) =>
        OperatingSystem.IsWindows()
            ? $"[Console]::Error.Write('x' * {count}); [Console]::Error.Flush()"
            : $"head -c {count} /dev/zero | tr '\\0' x >&2";

    static string Stdout(string text) =>
        OperatingSystem.IsWindows() ? $"[Console]::Out.Write('{text}')" : $"printf '{text}'";

    static string Sleep(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";
}
