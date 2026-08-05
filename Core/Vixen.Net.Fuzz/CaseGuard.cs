// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace Vixen.Net.Fuzz;

/// <summary>Watches the case that is running now, rather than the one that finished.</summary>
/// <remarks>
///     <para>
///         <b>Every other oracle in this harness is post-hoc, and a post-hoc oracle cannot see the
///         failure that matters most.</b> <see cref="FuzzFailure.Allocated" /> and
///         <see cref="FuzzFailure.TookTooLong" /> are both computed from readings taken either side of
///         <see cref="IFuzzTarget.Run" />, so an input that makes a decoder loop or grow without bound
///         never reaches them: the call does not return, the second reading is never taken, and the
///         process grows until the operating system takes the machine away from whoever was using it.
///         That is a failure with no failing test in it — the same shape
///         <see cref="Corpus.MaxEntries" /> records, and the same lesson one layer down: it was the
///         <i>corpus</i> that was unbounded there and it is the <i>case</i> that is unbounded here.
///     </para>
///     <para>
///         <b>What it honestly does.</b> It names the offending input, writes it to disk, stops the run
///         from scheduling anything further, and fails it. What it cannot do is take the thread back:
///         .NET has no safe thread abort, and a case wedged inside a decoder stays wedged. So the
///         guarantee is not "the run survives a runaway" — it is "a runaway is a named finding with its
///         bytes on disk before anything dies", which is the difference between a defect and a rumour.
///         For the one runaway that genuinely cannot be outlived — a case <i>retaining</i> without
///         bound — see <see cref="AbandonProcess" />.
///     </para>
///     <para>
///         <b>The watchdog takes the measurements, not the worker.</b> That is what keeps this free on
///         the healthy path, which runs eleven million times a build. Per case the worker publishes two
///         release stores, a <c>long</c> it had already computed and a reference it already
///         held; every reading — the clock, the allocation counters — is taken on the watchdog's own
///         thread. The cost of the measurement therefore falls entirely on cases slow enough to be
///         sampled at all, and a case that finishes inside <see cref="PollMilliseconds" /> is never
///         sampled once.
///     </para>
///     <para>
///         ⚠ <b><c>GC.GetAllocatedBytesForCurrentThread</c> is thread-local and therefore useless
///         here</b> — a watchdog cannot read the worker's counter, and making the worker poll its own
///         counter puts the check back inside the call that never returns. The two process-wide
///         counters are what a second thread can actually see, and they answer two different questions:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>GC.GetTotalAllocatedBytes(precise: false)</c> is <b>churn</b> — every byte ever
///             handed out, garbage included. It catches a case allocating in a loop, which costs a core
///             and a lot of collector time.
///         </item>
///         <item>
///             <c>GC.GetTotalMemory(forceFullCollection: false)</c> is <b>what is being held</b>, and it
///             is the one that describes a dying host. A loop allocating and dropping a kilobyte a
///             thousand times a second never grows the heap, because the collector keeps up; a loop
///             that <i>keeps</i> what it allocates grows it until there is no more, and that is the
///             failure that ended a developer's afternoon. Churn is a performance finding; retention is
///             the emergency.
///         </item>
///     </list>
///     <para>
///         Both are process-wide, which is the price of being readable from another thread. It is paid
///         with the ceilings rather than with precision: they sit orders of magnitude above anything a
///         healthy case does, and a breach must persist across consecutive samples of the <i>same</i>
///         case before it is believed. A tight, proportionate allocation figure is still
///         <c>FuzzSession.Weigh</c>'s job, measured per thread and over a window — this is not a second
///         copy of that oracle and is not trying to be.
///     </para>
/// </remarks>
sealed class CaseGuard : IDisposable {
    /// <summary>How often the case in flight is looked at.</summary>
    /// <remarks>
    ///     The floor on how fast a runaway can be caught and therefore on how much it can take with it
    ///     before it is. Sixty-odd wakeups a second is nothing next to what is being watched, and the
    ///     interval is also the resolution of the measurement: a case shorter than this is never
    ///     sampled, which is the whole healthy population.
    /// </remarks>
    public const int PollMilliseconds = 16;

    /// <summary>How many consecutive samples over the retention ceiling end the process.</summary>
    /// <remarks>
    ///     ⚠ <b>The grace exists because the counters are process-wide.</b> One sample over the line
    ///     could be a parallel test class allocating, charged to whichever case happened to be in
    ///     flight; sixteen in a row, all while the same case is still running, cannot be. It costs
    ///     about a quarter of a second of further growth, which a machine with any memory left
    ///     survives — and the alternative is killing a test host over a spike.
    /// </remarks>
    const int AbandonSamples = 16;

    readonly string target;
    readonly Stopwatch clock;
    readonly ManualResetEventSlim stopping = new(false);
    readonly Thread watchdog;

    // Written by the worker, read by the watchdog. Odd means a case is in flight; the value also
    // identifies which case, so the watchdog can tell "still the same one" from "a new one".
    long sequence;
    long startedTicks;
    byte[]? running;

    // The worker's own copy, so entering a case does not have to read back what it last wrote.
    long ticket;

    FuzzFinding? breach;

    /// <summary>Starts watching.</summary>
    /// <param name="target">Whose cases these are, for the finding.</param>
    /// <param name="clock">The run's clock, which the worker publishes readings from.</param>
    public CaseGuard(string target, Stopwatch clock) {
        this.target = target;
        this.clock = clock;

        // Its own thread rather than a timer, because a timer runs on the pool and the pool is the
        // first thing a runaway case starves. A watchdog that queues behind the thing it is watching
        // is not a watchdog.
        watchdog = new(Watch) {
            IsBackground = true,
            Name = $"fuzz-guard-{target}",
            Priority = ThreadPriority.AboveNormal
        };

        watchdog.Start();
    }

    /// <summary>How long one case may run before the run is abandoned rather than measured.</summary>
    public TimeSpan Cap { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many bytes one case may be handed, garbage included, before that is the finding.</summary>
    public long AllocationCeiling { get; init; } = 1L << 30;

    /// <summary>How far the heap may grow during one case before that is the finding.</summary>
    public long RetentionCeiling { get; init; } = 512L << 20;

    /// <summary>Where a breach's input is written the moment it is seen, or null not to.</summary>
    /// <remarks>
    ///     ⚠ <b>Written by the watchdog, not by the caller that collects the outcome.</b> A caller that
    ///     never regains control writes nothing, which is exactly the case this exists for — so the
    ///     bytes go to disk while the offending case is still running and before anything is decided
    ///     about whether the process has a future.
    /// </remarks>
    public string? FindingDirectory { get; init; }

    /// <summary>Whether a case that will not stop growing ends the process.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On by default, and it is the least bad of three options rather than a good one.</b>
    ///         A case retaining without bound cannot be outlived: the thread cannot be taken back, the
    ///         run cannot report anything the process does not live long enough to print, and the
    ///         ending is decided by the OOM killer — which takes the editor, the browser and whatever
    ///         else the developer had open with it. Ending deliberately, after the input is on disk and
    ///         the reason is on stderr, is the same outcome with a culprit attached.
    ///     </para>
    ///     <para>
    ///         Turned off for a test that means to breach the ceiling, and worth turning off for anyone
    ///         who would rather have a hung run they can attach a debugger to than a fast one they
    ///         cannot. Nothing about the finding depends on it — the input is written either way.
    ///     </para>
    ///     <para>
    ///         <see cref="Environment.FailFast(string)" /> rather than <see cref="Environment.Exit" />:
    ///         exit runs handlers and waits for foreground threads, one of which is by definition the
    ///         thread that will not stop, so it is the one call that can be blocked by the thing it is
    ///         escaping.
    ///     </para>
    /// </remarks>
    public bool AbandonProcess { get; init; } = true;

    /// <summary>What the guard saw, or null if every case so far behaved.</summary>
    public FuzzFinding? Breach => Volatile.Read(ref breach);

    /// <summary>Publishes the case about to run. Two stores, and no measurement.</summary>
    /// <param name="input">The bytes, so a breach can name them without the worker's help.</param>
    /// <param name="started">Where on the run's clock the case begins, which the caller already read.</param>
    public void Enter(byte[] input, TimeSpan started) {
        running = input;
        startedTicks = started.Ticks;

        // Release, so the two above are visible to the watchdog before the sequence that says to
        // trust them. Odd.
        Volatile.Write(ref sequence, ++ticket);
    }

    /// <summary>Publishes that the case is over.</summary>
    public void Leave() => Volatile.Write(ref sequence, ++ticket);

    /// <inheritdoc />
    public void Dispose() {
        stopping.Set();

        // Bounded, because the watchdog may be inside FailFast or writing a finding to disk, and a
        // harness that hangs waiting for its own watchdog has reproduced the defect it is fixing.
        watchdog.Join(TimeSpan.FromSeconds(5));
        stopping.Dispose();
    }

    void Watch() {
        long sampled = 0;
        long allocatedAt = 0;
        long heldAt = 0;
        long began = 0;
        byte[]? input = null;
        var strikes = 0;

        while (!stopping.Wait(PollMilliseconds)) {
            var one = Volatile.Read(ref sequence);

            if ((one & 1) == 0) {
                sampled = 0;
                strikes = 0;

                continue;
            }

            // Volatile on the way in as well as on the way out. The two are written plainly under the
            // release store above, which orders them; nothing orders a *reader* that the JIT has
            // decided may keep them in a register across the loop.
            var seenInput = Volatile.Read(ref running);
            var seenTicks = Volatile.Read(ref startedTicks);

            // The payload was read after the sequence and must be read before it is checked again;
            // without this the second read can be hoisted above them and the check proves nothing.
            Interlocked.MemoryBarrier();

            if (Volatile.Read(ref sequence) != one) {
                continue;
            }

            if (one != sampled) {
                // First sight of this case. The baselines are taken here rather than at Enter, which
                // is what the worker is being spared — so the window measured is (this sample, now]
                // and under-reports by up to one poll. That is the right direction: an oracle that
                // may not accuse a healthy case must round towards saying nothing.
                sampled = one;
                allocatedAt = GC.GetTotalAllocatedBytes();
                heldAt = GC.GetTotalMemory(false);
                began = seenTicks;
                input = seenInput;
                strikes = 0;

                continue;
            }

            var held = GC.GetTotalMemory(false) - heldAt;
            var allocated = GC.GetTotalAllocatedBytes() - allocatedAt;
            var took = TimeSpan.FromTicks(clock.Elapsed.Ticks - began);
            var payload = input ?? [];

            // Only this thread ever writes it, so the check is exact rather than optimistic. It is
            // here to stop a wedged case being formatted and written out sixty times a second for as
            // long as the process lasts, which is the wrong kind of loud.
            var reported = breach is not null;

            if (held > RetentionCeiling) {
                if (!reported) {
                    Report(
                        payload,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"still running after {took.TotalSeconds:N1} s, holding {held:N0} B more than when it "
                            + $"started — a ceiling of {RetentionCeiling:N0} B"
                        )
                    );
                }

                if (AbandonProcess && ++strikes >= AbandonSamples) {
                    Abandon(payload, held, took);
                }

                continue;
            }

            strikes = 0;

            if (reported) {
                continue;
            }

            if (allocated > AllocationCeiling) {
                Report(
                    payload,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"still running after {took.TotalSeconds:N1} s, {allocated:N0} B allocated so far "
                        + $"— a ceiling of {AllocationCeiling:N0} B"
                    )
                );
            } else if (took > Cap) {
                Report(
                    payload,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"still running after {took.TotalSeconds:N1} s, against a cap of {Cap.TotalSeconds:N1} s"
                    )
                );
            }
        }
    }

    /// <summary>Records a breach once, and puts its bytes somewhere they survive the process.</summary>
    void Report(byte[] input, string detail) {
        var finding = new FuzzFinding(target, FuzzFailure.RanAway, input, detail);

        if (Interlocked.CompareExchange(ref breach, finding, null) is not null) {
            return;
        }

        var written = "";

        if (FindingDirectory is not null) {
            try {
                written = $"\n  written to {Corpus.WriteRegression(FindingDirectory, target, input)}";
            }
#pragma warning disable CA1031 // A watchdog that throws on the way to reporting has reported nothing.
            catch (Exception exception) {
                written = $"\n  could not be written to {FindingDirectory}: {exception.Message}";
            }
#pragma warning restore CA1031
        }

        // On stderr as well as in the outcome, because the outcome is returned by a call that a
        // runaway case may never make.
        Console.Error.WriteLine($"fuzz: {finding}{written}");
        Console.Error.Flush();
    }

    void Abandon(byte[] input, long held, TimeSpan took) =>
        Environment.FailFast(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The fuzz target '{target}' has a case that will not stop growing: {took.TotalSeconds:N1} s in, "
                + $"holding {held:N0} B more than when it began, over {AbandonSamples} consecutive samples. "
                + $"The input is {input.Length:N0} B, fingerprint {Corpus.Fingerprint(input):x16}, and has been "
                + $"written to {FindingDirectory ?? "nowhere — no finding directory was set"}. Ending here rather "
                + $"than letting the host run out of memory; the thread cannot be taken back."
            )
        );
}
