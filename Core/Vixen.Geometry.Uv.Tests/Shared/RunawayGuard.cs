// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Vixen.Geometry.Testing;

/// <summary>What a property test needs that no assertion after the call can give it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41's robustness criterion and docs/plan/42's exit criterion 2 both end in the
///         same three words — "no exceptions, no hangs" — and only the first half of that is testable
///         by asserting things about a returned value.</b> <c>Vixen.Fuzz</c>'s <c>CaseGuard</c> makes
///         the argument in full: every other oracle is post-hoc, so "an input that makes a decoder
///         loop or grow without bound never reaches them: the call does not return, the second reading
///         is never taken." A property test that hangs is not a failing property test. It is a suite
///         that never finishes, with nothing on the console naming the case that did it.
///     </para>
///     <para>
///         ⚠ <b>This is a deliberate second copy of that shape rather than a reference to it, and the
///         reason is not layering.</b> <c>build/Build.ArchitectureRules.cs</c> exempts test projects
///         outright — "a test project may reference anything: it is not shipped" — so a
///         <c>ProjectReference</c> to <c>Vixen.Fuzz</c> from here would pass the gate. Two other things
///         stop it. <c>CaseGuard</c> is <c>internal</c> to an assembly that declares no
///         <c>InternalsVisibleTo</c> at all, so reusing it means widening the API surface of a harness
///         whose README is a statement about how narrow that surface is; and its whole interface is
///         <c>Enter(byte[] input, TimeSpan started)</c>, where the bytes are the finding. There are no
///         bytes here. A mesh is not a decoder's input, which is the same reason doc 41 and doc 42's
///         modules do not belong in <c>Vixen.Fuzz</c>'s target table in the first place.
///     </para>
///     <para>
///         ⚠ <b>The watchdog and the worker swap roles compared with <c>CaseGuard</c>, and that is
///         the one simplification a test suite is allowed.</b> There, the worker runs eleven million
///         cases a build and must not pay for the measurement, so a separate thread does the reading.
///         Here the case is the expensive thing and there are a few hundred of them, so the case runs
///         on its own thread and the <i>calling</i> thread — which has nothing else to do — takes the
///         readings. The cost is one thread per case, which is nothing next to conditioning a mesh.
///     </para>
///     <para>
///         ⚠ <b>What this cannot do is take the thread back.</b> .NET has no safe thread abort, so a
///         case wedged inside a loop stays wedged; the thread is a background thread, so it does not
///         hold the process open, and the run continues without it. The guarantee is the same one
///         <c>CaseGuard</c> offers — "a runaway is a named finding before anything dies" — which is
///         the difference between a defect and a rumour.
///     </para>
/// </remarks>
static class RunawayGuard {
    /// <summary>How often the case in flight is looked at.</summary>
    public const int PollMilliseconds = 16;

    /// <summary>How many consecutive samples over the retention ceiling are believed.</summary>
    /// <remarks>
    ///     ⚠ <b>The grace exists because <see cref="GC.GetTotalMemory(bool)" /> is process-wide.</b>
    ///     xunit runs test classes in parallel, so one sample over the line could be another class
    ///     allocating and being charged to whichever case happened to be in flight. Sixteen in a row,
    ///     all while the same case is still running, cannot be — and the quarter-second it costs is
    ///     nothing against a case that is doubling.
    /// </remarks>
    const int RetentionSamples = 16;

    /// <summary>How long one case may run before it is a finding rather than a slow test.</summary>
    /// <remarks>
    ///     Measured: the slowest healthy case in either suite is the packer's sixteen-attempt refusal
    ///     at just under ten seconds, and the slowest conditioning case is under four. Sixty is far
    ///     enough above both that a breach is a statement rather than a race with the machine.
    /// </remarks>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    /// <summary>How far the heap may grow during one case before that is the finding.</summary>
    /// <remarks>
    ///     ⚠ <b>Retention rather than churn, and <c>CaseGuard</c> records why: "a loop allocating and
    ///     dropping a kilobyte a thousand times a second never grows the heap, because the collector
    ///     keeps up; a loop that <i>keeps</i> what it allocates grows it until there is no more."</b>
    ///     Measured against the failure this exists for — an isotropic pre-remesh handed a target edge
    ///     length far below the mesh's own mean quadruples its triangle count every round, and the
    ///     tenth round alone allocates 763 MB — a ceiling of one gigabyte is crossed by round eleven
    ///     and by nothing healthy.
    /// </remarks>
    public static readonly long RetentionCeiling = 1L << 30;

    /// <summary>Runs one case under the clock and the heap, and returns what it produced.</summary>
    /// <typeparam name="T">What the case produces.</typeparam>
    /// <param name="what">The case, printed if it runs away. Make it enough to reproduce from.</param>
    /// <param name="body">The case.</param>
    /// <param name="cap">How long this case may run, or null for <see cref="Cap" />.</param>
    /// <returns>Whatever <paramref name="body" /> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body" /> is null.</exception>
    /// <exception cref="RunawayException">The case exceeded the clock or the heap ceiling.</exception>
    /// <remarks>
    ///     An exception thrown by the case is rethrown here with its original stack, so a property
    ///     that fails for an ordinary reason fails in the ordinary way and only a runaway is dressed
    ///     up differently.
    /// </remarks>
    public static T Run<T>(string what, Func<T> body, TimeSpan? cap = null) {
        ArgumentNullException.ThrowIfNull(body);

        var result = default(T);
        var failure = default(ExceptionDispatchInfo);
        var finished = new ManualResetEventSlim(false);

        var worker = new Thread(
            () => {
                try {
                    result = body();
                }
#pragma warning disable CA1031 // Carried across the thread boundary rather than swallowed. See below.
                catch (Exception exception) {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
#pragma warning restore CA1031
                finally {
                    finished.Set();
                }
            }
        ) {
            // ⚠ Background, so a case that never returns cannot hold the test host open after the
            // run has already reported it. That is the whole of what is done about the wedged
            // thread, because it is the whole of what can be done.
            IsBackground = true,
            Name = "geometry-property-case"
        };

        worker.Start();
        Watch(what, finished, cap ?? Cap);
        finished.Dispose();
        failure?.Throw();

        return result!;
    }

    /// <summary>The same, for a case that produces nothing.</summary>
    /// <param name="what">The case, printed if it runs away.</param>
    /// <param name="body">The case.</param>
    /// <param name="cap">How long this case may run, or null for <see cref="Cap" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body" /> is null.</exception>
    /// <exception cref="RunawayException">The case exceeded the clock or the heap ceiling.</exception>
    public static void Run(string what, Action body, TimeSpan? cap = null) {
        ArgumentNullException.ThrowIfNull(body);

        Run(
            what,
            () => {
                body();

                return 0;
            },
            cap
        );
    }

    static void Watch(string what, ManualResetEventSlim finished, TimeSpan cap) {
        var clock = Stopwatch.StartNew();

        // Taken before the first wait rather than before the thread starts: the reading is a
        // baseline for the case, and a case that finishes inside one poll is never sampled at all.
        var held = GC.GetTotalMemory(false);
        var strikes = 0;

        while (!finished.Wait(PollMilliseconds)) {
            var grown = GC.GetTotalMemory(false) - held;

            if (grown > RetentionCeiling) {
                if (++strikes < RetentionSamples) {
                    continue;
                }

                throw new RunawayException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{what} was still running after {clock.Elapsed.TotalSeconds:N1} s, holding "
                        + $"{grown:N0} B more than when it started, over {RetentionSamples} consecutive "
                        + $"samples — a ceiling of {RetentionCeiling:N0} B. The thread has been abandoned; it "
                        + $"cannot be taken back."
                    )
                );
            }

            strikes = 0;

            if (clock.Elapsed > cap) {
                throw new RunawayException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{what} was still running after {clock.Elapsed.TotalSeconds:N1} s, against a cap of "
                        + $"{cap.TotalSeconds:N1} s. The thread has been abandoned; it cannot be taken "
                        + $"back."
                    )
                );
            }
        }
    }
}

/// <summary>A case that did not stop, named.</summary>
/// <remarks>
///     Its own type rather than a bare assertion failure, so that a property whose whole point is
///     "and never a hang" reports the hang as the hang rather than as "expected true, got false".
/// </remarks>
sealed class RunawayException : Exception {
    /// <summary>A runaway with nothing said about it.</summary>
    public RunawayException() { }

    /// <summary>A runaway, named.</summary>
    /// <param name="message">What ran away, and on which reading.</param>
    public RunawayException(string message) : base(message) { }

    /// <summary>A runaway, named, over something else.</summary>
    /// <param name="message">What ran away.</param>
    /// <param name="innerException">What it was doing.</param>
    public RunawayException(string message, Exception innerException) : base(message, innerException) { }
}
