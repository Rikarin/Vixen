// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Editor.App;

/// <summary>What a child process said, and whether it finished in time.</summary>
/// <param name="Finished">Whether it exited, and its streams closed, before the patience ran out. If not, it was killed.</param>
/// <param name="Code">Its exit code, or -1 when it did not finish.</param>
/// <param name="Output">Everything it wrote to standard output.</param>
/// <param name="Error">Everything it wrote to standard error.</param>
readonly record struct ChildProcessResult(bool Finished, int Code, string Output, string Error);

/// <summary>Runs a child process to the end, reading both of its streams at once, and gives up on it after a while.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both streams at once, not one after the other (#1512).</b> <c>GitSourceControl.Run</c> and
///         <c>ProjectAssemblies.Run</c> each read standard output to the end and then standard error. A
///         child that fills the stderr pipe first — 64 KB on Windows and Linux — blocks writing it, so it
///         never closes stdout, so the first read never returns: a deadlock, one stream at a time. Both
///         had a comment saying "read before waiting, and both streams", which was true and was not
///         the fix.
///     </para>
///     <para>
///         ⚠ <b>And the reads cannot block the wait.</b> Because both reads came before
///         <c>WaitForExit(patience)</c>, the hang check its callers describe — git's 30 s, a build's five
///         minutes — could not fire for a child that never closed its streams. The reads run as tasks
///         and the patience bounds the exit and the reads together.
///     </para>
/// </remarks>
static class ChildProcess {
    /// <summary>Starts a process and waits for it, reading everything it says.</summary>
    /// <param name="start">What to run. Its streams are redirected here, whatever it says.</param>
    /// <param name="patience">How long to wait for it before killing it — a hang check, not a budget.</param>
    /// <returns>What it said; <see langword="null" /> when the platform started nothing.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">There is no such executable.</exception>
    public static ChildProcessResult? Run(ProcessStartInfo start, TimeSpan patience) {
        ArgumentNullException.ThrowIfNull(start);

        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;

        using var process = Process.Start(start);

        if (process is null) {
            return null;
        }

        var clock = Stopwatch.StartNew();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(patience) || !Task.WaitAll([output, error], Remaining(patience, clock))) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) {
                // It exited between the wait and the kill; its streams are what is still open.
            }

            return new(false, -1, Settled(output), Settled(error));
        }

        return new(true, process.ExitCode, output.Result, error.Result);
    }

    static TimeSpan Remaining(TimeSpan patience, Stopwatch clock) {
        var left = patience - clock.Elapsed;

        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>What a read had by the time the child was killed — everything, once the kill closed the pipe, or nothing.</summary>
    static string Settled(Task<string> read) {
        try {
            return read.Wait(TimeSpan.FromSeconds(5)) ? read.Result : string.Empty;
        } catch (AggregateException) {
            return string.Empty;
        }
    }
}
