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
    public const int RetentionSamples = 16;

    /// <summary>How long one case may run before the run gives up on it and names it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is a liveness backstop, and it is not a claim about how long a case takes.</b>
    ///         Nothing in either suite asserts a duration: the criteria they serve end in "no
    ///         exceptions, no hangs", and a hang is the <i>absence of a return</i> rather than a large
    ///         number of seconds. What the clock buys is that the case is <b>named</b> — a runner
    ///         killed by its own <c>timeout-minutes</c> prints nothing about which input did it, and
    ///         "the nightly hung" is a rumour. So the only thing this number has to do is fire before
    ///         the job does, on a case that was never going to return.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It used to be sixty seconds, chosen as roughly six times the slowest healthy case,
    ///         and that is a wall-clock assertion wearing a watchdog's clothes.</b> It fired as one:
    ///         <c>RemeshPipelinePropertyTests.TimedCases</c> records <c>ConditioningPropertyTests</c>
    ///         breaching at <b>60.2 s against 60.0</b> — three parts in a thousand — on a seed whose
    ///         whole class finishes in 1 m 36 s when run alone. Its own words: "the case was never
    ///         slow; ten cores were."
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the measurement the sixty was derived from had already rotted, which is what
    ///         makes this urgent rather than tidy.</b> This paragraph used to read "the slowest
    ///         conditioning case is under four [seconds]", written 2026-08-06. Four runs of the
    ///         remeshing property suite on 2026-08-18 — one ten-core machine, one build, the
    ///         <i>build's</i> counts, and the three timed classes already serialised into one
    ///         collection:
    ///     </para>
    ///     <para>
    ///         <c>suite 309 s → slowest case 54.4 s · 187 s → 23.0 s · 138 s → 38.1 s · 93 s →
    ///         12.7 s</c>
    ///     </para>
    ///     <para>
    ///         The slowest single case swung by <b>4.3×</b> across one afternoon on one machine, and
    ///         its worst reading was <b>54.4 s against a cap of 60</b> — <c>new(ShapeKind.Box, 7, 1,
    ///         [], 3, 0f, 0.001f)</c>. Not the six-fold headroom this comment claimed: <b>1.10×</b>.
    ///         The suite total moved with it, 93 s to 309 s, which is what says the swing is the
    ///         machine rather than the input. It had not fired in six nights because it had been
    ///         lucky six times.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The obvious repair — measure the interference the way the heap half does — was
    ///         built, measured and refuted.</b> <see cref="RetentionSamples" /> works because heap
    ///         contamination is <i>transient</i> relative to a case: sixteen consecutive samples
    ///         cannot all be somebody else's allocation. Scheduling interference is not transient. It
    ///         lasts the whole run, so no number of consecutive samples separates a starved case from
    ///         a stuck one. The in-band meter that would — a watchdog reading <i>its own</i> lateness,
    ///         since a thread that cannot be scheduled is itself late — was written and run on a
    ///         ten-core machine: it reads <b>5.6×</b> at zero contention, because a 16 ms wait is
    ///         dominated by the timer's own coalescing, and <b>2.9×</b> at fourfold oversubscription.
    ///         It does not even move the right way. Process CPU time cannot do it either: it saturates
    ///         at <see cref="Environment.ProcessorCount" /> and is blind to a thread that is runnable
    ///         and not running. <b>So the clock cannot be made to mean what a tight cap needs it to
    ///         mean, and the answer is to stop asking it to.</b>
    ///     </para>
    ///     <para>
    ///         <b>Twenty minutes, and the arithmetic is against the runner rather than against a
    ///         stopwatch on one machine.</b> Upper: the nightly's <c>properties</c> leg has
    ///         <c>timeout-minutes: 90</c>, and its own comments measure the work at ~18 min
    ///         (remeshing, 15×) and ~17 min (uv, 20×) on a laptop; allow a hosted runner twice that
    ///         and six minutes for checkout, restore and build, and <b>~48 min</b> is left for a guard
    ///         to fire in. Twenty fits twice over — two runaways can both be named and the leg still
    ///         finishes — where forty-eight would leave the second one to the job's own timeout, which
    ///         is the outcome this exists to prevent. The per-commit gate has no
    ///         <c>timeout-minutes</c> at all, so GitHub's six-hour default is the bound there and
    ///         twenty is nothing against it. Lower: <b>22×</b> the worst healthy reading ever measured
    ///         (the 54.4 s above, itself taken on a run whose suite total was 3.4× its uncontended
    ///         one) and <b>13×</b> the worst healthy whole-pipeline case on record (93 s, in
    ///         <c>RemeshPipelinePropertyTests</c>' own remarks).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Raise it only against the leg's budget, never against a case that nearly touched
    ///         it.</b> A case that took nineteen minutes is a defect worth a morning; widening the
    ///         ceiling to accommodate it puts this comment back where it started. The cost of this
    ///         number being too large is bounded and boring — a genuine hang is reported twenty
    ///         minutes later than it could have been — and the cost of it being too small is a red
    ///         master that says nothing true.
    ///     </para>
    /// </remarks>
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(20);

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
    /// <param name="ceiling">
    ///     How far the heap may grow during this case, or null for <see cref="RetentionCeiling" />.
    /// </param>
    /// <returns>Whatever <paramref name="body" /> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body" /> is null.</exception>
    /// <exception cref="RunawayException">The case exceeded the clock or the heap ceiling.</exception>
    /// <remarks>
    ///     <para>
    ///         An exception thrown by the case is rethrown here with its original stack, so a property
    ///         that fails for an ordinary reason fails in the ordinary way and only a runaway is
    ///         dressed up differently.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="ceiling" /> exists for the same reason
    ///         <paramref name="cap" /> does, and its absence is why the half these remarks call "the
    ///         one that matters" had never once been shown to fire.</b> The clock could be sabotaged
    ///         with a fifth of a second; the heap could only be sabotaged by really retaining a
    ///         gigabyte, which is not a thing to do on a shared runner — so nobody did, and the
    ///         sixteen-sample logic, the message and the arithmetic were all untested. A few megabytes
    ///         against a few more proves the same code.
    ///     </para>
    /// </remarks>
    public static T Run<T>(string what, Func<T> body, TimeSpan? cap = null, long? ceiling = null) {
        ArgumentNullException.ThrowIfNull(body);

        var ticket = CaseTrace.Enter();
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

        try {
            Watch(what, finished, cap ?? Cap, ceiling ?? RetentionCeiling);
        } finally {
            CaseTrace.Leave(ticket, what);
        }

        finished.Dispose();
        failure?.Throw();

        return result!;
    }

    /// <summary>The same, for a case that produces nothing.</summary>
    /// <param name="what">The case, printed if it runs away.</param>
    /// <param name="body">The case.</param>
    /// <param name="cap">How long this case may run, or null for <see cref="Cap" />.</param>
    /// <param name="ceiling">
    ///     How far the heap may grow during this case, or null for <see cref="RetentionCeiling" />.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="body" /> is null.</exception>
    /// <exception cref="RunawayException">The case exceeded the clock or the heap ceiling.</exception>
    public static void Run(string what, Action body, TimeSpan? cap = null, long? ceiling = null) {
        ArgumentNullException.ThrowIfNull(body);

        Run(
            what,
            () => {
                body();

                return 0;
            },
            cap,
            ceiling
        );
    }

    /// <summary>A span in whichever unit a reader can hold in their head.</summary>
    /// <remarks>
    ///     <see cref="Cap" /> is twenty minutes and <c>RunawayGuardTests</c> passes a fifth of a
    ///     second, and "1,200.0 s" and "0.0 min" are each unreadable at the other's scale.
    /// </remarks>
    static string Readable(TimeSpan span) =>
        span.TotalSeconds < 120d
            ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:N1} s")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:N1} min");

    static void Watch(string what, ManualResetEventSlim finished, TimeSpan cap, long ceiling) {
        var clock = Stopwatch.StartNew();

        // Taken before the first wait rather than before the thread starts: the reading is a
        // baseline for the case, and a case that finishes inside one poll is never sampled at all.
        var held = GC.GetTotalMemory(false);
        var strikes = 0;

        while (!finished.Wait(PollMilliseconds)) {
            var grown = GC.GetTotalMemory(false) - held;

            if (grown > ceiling) {
                if (++strikes < RetentionSamples) {
                    continue;
                }

                throw new RunawayException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{what} was still running after {Readable(clock.Elapsed)}, holding "
                        + $"{grown:N0} B more than when it started, over {RetentionSamples} consecutive "
                        + $"samples — a ceiling of {ceiling:N0} B. The thread has been abandoned; it "
                        + $"cannot be taken back."
                    )
                );
            }

            strikes = 0;

            if (clock.Elapsed > cap) {
                // ⚠ The message says what the ceiling is for, because the ceiling is far enough above
                // any healthy case that "it was slow" is not one of the things this can mean. A
                // reader who sees it and reaches for a larger number has read it as the old cap.
                throw new RunawayException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{what} was still running after {Readable(clock.Elapsed)}, against a ceiling of "
                        + $"{Readable(cap)} — which is set to be unreachable by a case that returns at "
                        + $"all, so this is a case that does not. The thread has been abandoned; it cannot be "
                        + $"taken back. Raise the ceiling only against the leg's own timeout; see RunawayGuard.Cap."
                    )
                );
            }
        }
    }
}

/// <summary>Every case's wall clock, its overlap and its share of the process, written to a file.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Off unless <c>VIXEN_CASE_TRACE</c> names a file, and it exists because
///         <see cref="RunawayGuard.Cap" />'s remarks are a claim about a distribution that nothing was
///         in a position to measure.</b> The cap fired once on a case that "was never slow; ten cores
///         were", and the repair for that comment was to widen the ceiling — which is right, and which
///         leaves the distribution itself unknown. <c>RunawayGuard.Run</c> already brackets every
///         expensive call in both suites and already has the case's own name in its hand, so the
///         cheapest honest instrument is the one that writes down what it already knows.
///     </para>
///     <para>
///         ⚠ <b>Wall is not enough on its own, and the other columns are what make a row decide
///         anything.</b> Wall alone cannot tell a slow case from a starved one — the comment on
///         <see cref="RunawayGuard.Cap" /> records an in-band lateness meter being built and refuted
///         for exactly that reason. <c>cpu</c> is the whole process's processor time across the case,
///         in milliseconds: a case whose wall is 50 s and whose <c>cpu</c> is 400 s did not do fifty
///         seconds of anything, and one whose <c>cpu</c> is 50 s did. <c>entered</c> and <c>left</c>
///         count the guarded cases in flight at each end, exactly rather than by inference, because
///         xunit parallelises test classes and a case sharing the machine with nine others is a case
///         whose clock is a statement about the runner.
///     </para>
///     <para>
///         ⚠ <b>Buffered in memory and flushed at exit, not appended per case.</b> A file opened and
///         closed a thousand times inside the thing being timed is an instrument that changes its
///         reading — and these runs are minutes long, so nothing needs to be readable early.
///     </para>
/// </remarks>
static class CaseTrace {
    static readonly string? Path = Environment.GetEnvironmentVariable("VIXEN_CASE_TRACE");
    static readonly Process Self = Process.GetCurrentProcess();
    static readonly List<string> Rows = [];
    static readonly Stopwatch Since = Stopwatch.StartNew();
    static int inFlight;

    static CaseTrace() {
        if (Path is null) {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>Marks a case as started, and returns what <see cref="Leave" /> needs to close it.</summary>
    /// <returns>When it started, the processor time then, and how many cases were in flight.</returns>
    public static (long Started, double Cpu, int Overlap) Enter() =>
        Path is null
            ? default
            : (Since.ElapsedMilliseconds, Self.TotalProcessorTime.TotalMilliseconds,
                Interlocked.Increment(ref inFlight));

    /// <summary>Writes the row and marks the case as finished.</summary>
    /// <param name="ticket">What <see cref="Enter" /> returned.</param>
    /// <param name="what">The case's own name, which is what makes the row worth having.</param>
    public static void Leave((long Started, double Cpu, int Overlap) ticket, string what) {
        if (Path is null) {
            return;
        }

        var wall = Since.ElapsedMilliseconds - ticket.Started;
        var cpu = Self.TotalProcessorTime.TotalMilliseconds - ticket.Cpu;
        var leaving = Interlocked.Decrement(ref inFlight) + 1;
        var flush = false;

        lock (Rows) {
            Rows.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ticket.Started}\t{wall}\t{cpu:F0}\t{ticket.Overlap}\t{leaving}\t{what.Replace('\t', ' ')}"
                )
            );

            // ⚠ Every so often rather than every row, and outside any case's own clock: a run that
            // is killed rather than allowed to exit still leaves most of its readings behind, and a
            // file rewritten once a case would be an instrument that changed the thing it measures.
            flush = Rows.Count % 256 == 0;
        }

        if (flush) {
            Flush();
        }
    }

    /// <summary>Writes everything gathered so far, for a run that will not reach process exit.</summary>
    public static void Flush() {
        if (Path is null) {
            return;
        }

        lock (Rows) {
            File.WriteAllLines(Path, Rows.Prepend("started\twall\tcpu\tentered\tleft\twhat"));
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
