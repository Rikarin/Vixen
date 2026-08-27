// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vixen.Fuzz;

/// <summary>Which promise an input broke.</summary>
public enum FuzzFailure {
    /// <summary>An exception escaped the decoder.</summary>
    /// <remarks>
    ///     The one that matters most. An exception out of a receive path is a denial of service if
    ///     it unwinds a frame and a crash if it does not, which is the argument
    ///     <c>PacketReader</c>'s remarks make for the whole never-throws design.
    /// </remarks>
    Threw = 0,

    /// <summary>The decode allocated far more than the input could justify.</summary>
    /// <remarks>
    ///     A packet that is an amplifier is a packet that is an attack: a thousand of them a second
    ///     from one connection is a server in permanent garbage collection, and nothing about that
    ///     looks like an error to any log.
    /// </remarks>
    Allocated = 1,

    /// <summary>The decode took long enough to be a weapon.</summary>
    TookTooLong = 2,

    /// <summary>The decoder can be talked into holding more and more, and never letting go.</summary>
    /// <remarks>
    ///     A different failure from <see cref="Allocated" /> and the one an allocation budget cannot
    ///     see: each packet costs a hundred bytes, which is proportionate and fine, and the hundred
    ///     bytes are never given back. It looks like nothing for an hour and like a server with no
    ///     memory left on the second day, and the only thing that catches it is asking the decoder
    ///     what it is holding.
    /// </remarks>
    Retained = 3,

    /// <summary>The case was still running when the guard gave up on it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The only one of these seen from outside the call.</b> The other four are computed
    ///         from readings taken either side of <see cref="IFuzzTarget.Run" />, so none of them can
    ///         be reported for an input that never lets <c>Run</c> return — and an input that loops, or
    ///         that grows the heap until there is none left, is exactly that input. What reported it
    ///         before this existed was the operating system killing the machine.
    ///     </para>
    ///     <para>
    ///         Distinct from <see cref="TookTooLong" /> and <see cref="Allocated" /> rather than folded
    ///         into them, because it is a different claim measured a different way. Those two are
    ///         per-thread, exact and proportionate to the input, and they say a decode was slower or
    ///         fatter than the input justifies. This one is coarse and process-wide and says only that
    ///         a case was still going when a ceiling nothing healthy approaches went past — see
    ///         <see cref="CaseGuard" /> for what it can and, more to the point, cannot do about that.
    ///     </para>
    /// </remarks>
    RanAway = 4
}

/// <summary>An input that broke a promise.</summary>
/// <param name="Target">Which decoder.</param>
/// <param name="Failure">Which promise.</param>
/// <param name="Input">The exact bytes, so it can be committed and replayed.</param>
/// <param name="Detail">What happened, for the report.</param>
public sealed record FuzzFinding(string Target, FuzzFailure Failure, byte[] Input, string Detail) {
    /// <summary>The bytes, as hex, so a report can be acted on without the corpus file.</summary>
    /// <remarks>
    ///     Truncated, because the interesting inputs are short — a decoder that can be broken by
    ///     forty bytes is broken by forty bytes, and a listing of a kilobyte is a listing nobody
    ///     reads. The committed corpus file has all of them.
    /// </remarks>
    public string Hex => Convert.ToHexString(Input.AsSpan(0, Math.Min(Input.Length, 64)));

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Target}: {Failure} on {Input.Length} bytes ({Corpus.Fingerprint(Input):x16}) — {Detail}\n      {Hex}"
        );
}

/// <summary>Why a run ended.</summary>
/// <remarks>
///     ⚠ <b>Internal, and a phrase rather than a type on <see cref="FuzzOutcome" />, because
///     <c>Vixen.Fuzz</c>'s public surface is gated by <c>CheckDocs</c> and a new public type there
///     owes a guide page the harness does not have.</b> The distinction is worth keeping in the code
///     regardless: the summary line has to name one of exactly four things, and a string built at
///     four call sites is a string that eventually says five.
/// </remarks>
enum FuzzStop {
    /// <summary>The time budget ran out — the only ending that means the run finished its work.</summary>
    Clock = 0,

    /// <summary>The requested number of cases was reached.</summary>
    Cases = 1,

    /// <summary>Enough distinct failures were found that there was nothing left to learn.</summary>
    Findings = 2,

    /// <summary>A case would not come back, and the run gave up rather than lose the host.</summary>
    Runaway = 3
}

/// <summary>What a run came to.</summary>
/// <param name="Target">Which decoder was run.</param>
/// <param name="Cases">How many inputs were pushed through it.</param>
/// <param name="CorpusSize">How many were worth keeping.</param>
/// <param name="Signatures">How many distinct behaviours were seen.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Findings">What broke, if anything.</param>
/// <param name="Stopped">
///     Why the run ended, as a phrase for the summary line.
///     <para>
///         ⚠ <b>Not decoration.</b> A run that stopped on the finding cap and a run that used its
///         whole budget print the same line without this, and the two mean opposite things: raven's
///         nightly saturated <see cref="FuzzSession.MaxFindings" /> after five and a half minutes of
///         a two-hour budget for weeks, and the summary said <c>3 FINDING(S)</c> in exactly the tone
///         it would have said it after two hours. What caught it was somebody downloading an
///         artifact and counting the files in it.
///     </para>
/// </param>
/// <param name="Suppressed">
///     How many further failures matched a finding already held.
///     <para>
///         The other half of the same signal. One defect reached ten thousand ways is a number worth
///         printing, both because it says the finding list is short for a good reason and because a
///         suppressed count that dwarfs the run is a key that is still splitting one bug into many.
///     </para>
/// </param>
/// <param name="Acquitted">
///     How many cases read over <see cref="FuzzSession.CaseBudget" /> and then did not, when asked
///     again.
///     <para>
///         ⚠ <b>Printed because an oracle that quietly stops accusing anybody is indistinguishable
///         from an oracle that has nothing to accuse.</b> Each of these is a case the machine made look
///         like a defect and that re-measuring exonerated — so a run of a dozen is a loaded host and a
///         run of none is a quiet one, and neither is visible from "clean" alone. It is also the only
///         number that would move if <see cref="FuzzSession.CaseBudgetConfirmations" /> ever started
///         swallowing real findings: a target that acquits steadily, build after build, is one whose
///         budget is genuinely too tight for what it does.
///     </para>
/// </param>
public sealed record FuzzOutcome(
    string Target,
    long Cases,
    int CorpusSize,
    int Signatures,
    TimeSpan Elapsed,
    IReadOnlyList<FuzzFinding> Findings,
    string Stopped,
    long Suppressed,
    long Acquitted
) {
    /// <summary>Whether every promise held.</summary>
    public bool Clean => Findings.Count == 0;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Target,-10} {Cases,9:N0} cases  {Signatures,5:N0} behaviours  {CorpusSize,4:N0} kept  "
            + $"{Cases / Math.Max(0.001, Elapsed.TotalSeconds),9:N0}/s  "
            + $"{(Clean ? "clean" : $"{Findings.Count} FINDING(S)")}"
            + $"{(Suppressed > 0 ? $" (+{Suppressed:N0} repeats)" : "")}"
            + $"{(Acquitted > 0 ? $" ({Acquitted:N0} acquitted on a second reading)" : "")}"
            + $"  stopped on {Stopped}"
        );
}

/// <summary>Runs one target until it is told to stop, and holds it to its promises.</summary>
/// <remarks>
///     <para>
///         <b>The oracles are the point, not the loop.</b> Pushing bytes at a decoder proves nothing
///         on its own — the decoder has to be measured while it does it. Four things are measured:
///         that nothing was thrown, that the allocation was proportionate to the input, that nothing
///         was retained, and that the case finished quickly. Everything else about the decode is the
///         target's business — including, since <see cref="IFuzzTarget.Domain" />, how its inputs were
///         made. None of the four asks what an input <i>is</i>, which is what let that be added
///         without disturbing any of them.
///     </para>
///     <para>
///         ⚠ <b>All four are computed after <see cref="IFuzzTarget.Run" /> returns, and that is a hole
///         rather than a detail.</b> An input that makes a decoder loop, or grow the heap without
///         bound, never lets it return — so the readings that would have caught it are never taken and
///         the run is ended by the operating system killing the machine instead. A fifth,
///         <see cref="FuzzFailure.RanAway" />, is watched for <i>during</i> the call by
///         <see cref="CaseGuard" />, on another thread, so that such an input is a named finding with
///         its bytes on disk rather than a dead host and a guess. Read that type for what the guard
///         cannot do, which is take the thread back.
///     </para>
///     <para>
///         <b>Deterministic, and that is a requirement rather than a nicety.</b> The generator is
///         seeded, the mutations are a pure function of it, and the corpus grows in a fixed order,
///         so a failure on a CI machine is reproduced locally from the seed and the target name.
///         A fuzzer whose findings cannot be replayed has handed you a rumour.
///     </para>
///     <para>
///         <b>It does not stop at the first finding.</b> One malformed shape usually reaches several
///         decoders and several of a decoder's paths, and a run that stops at the first one turns a
///         morning's fixing into a week of one-a-day. It stops at <see cref="MaxFindings" />, which
///         is there so a target that throws on everything does not fill memory with the evidence.
///     </para>
///     <para>
///         ⚠ <b>That cap ends the run, and not only the collecting — reaffirmed rather than assumed,
///         because it is what hid a compiler bug for weeks.</b> The argument for bounding stored
///         evidence does not by itself argue for stopping, and while the dedup key was the whole
///         detail string it very nearly was not: one defect minted a fresh finding per byte offset,
///         thirty-two of them filled the cap in five and a half minutes, and <c>raven</c> never once
///         spent more than that of its two-hour nightly. The cap stays a stopping condition now that
///         a finding means a distinct defect again, for a reason the old arithmetic did not have:
///         <b>past thirty-two of those there is nothing left to learn, and going on makes the search
///         worse rather than longer</b> — every failing input is offered to the corpus, so the
///         mutator settles into the broken region and stops reaching anything else. What was
///         actually missing was not more running but the ability to see that it had stopped, which
///         is <see cref="FuzzOutcome.Stopped" />.
///     </para>
/// </remarks>
public sealed class FuzzSession {
    /// <summary>How many findings are collected before a run gives up.</summary>
    public const int MaxFindings = 32;

    /// <summary>How many cases the allocation budget is measured over. See <see cref="Weigh" />.</summary>
    public const int WindowCases = 512;

    readonly IFuzzTarget target;
    readonly Mutator mutator;
    readonly Corpus corpus = new();
    readonly FuzzRandom picker;
    readonly FuzzRandom shaper;
    readonly List<FuzzFinding> findings = [];
    readonly HashSet<string> keys = new(StringComparer.Ordinal);
    readonly Stopwatch clock = new();

    long suppressed;

    int windowCases;
    long windowAllocated;
    long windowAllowance;
    long windowWorst;
    byte[]? windowWorstInput;

    CaseGuard? guard;
    bool abandoned;
    long acquitted;

    /// <summary>How long one input may take to decode before that is itself the finding.</summary>
    /// <remarks>
    ///     <para>
    ///         Generous, because it is checked after the fact: this measures a case that finished
    ///         slowly, which is the ordinary reading of "too long" and the one worth reporting
    ///         precisely. A case that does not finish at all is a different failure and is
    ///         <see cref="CaseGuard" />'s, at <see cref="HardCaseCap" /> — an order of magnitude out,
    ///         so the two never report the same input and the cheap exact check stays the one that
    ///         normally speaks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The number is not what makes this survive a loaded machine —
    ///         <see cref="CaseBudgetConfirmations" /> is.</b> No wall-clock threshold can separate a
    ///         slow input from a stalled thread from one reading, and this one was set "generously"
    ///         twice on that assumption. Read that property before changing this one; raising this
    ///         alone is the same defect with a longer fuse.
    ///     </para>
    /// </remarks>
    public TimeSpan CaseBudget { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many further readings an over-budget case gets before it is called a finding.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A single reading of a wall clock is not evidence about an input, and treating it as
    ///         evidence is what made this oracle flaky.</b> "This input is slow" is a claim about the
    ///         input, so it is tested the way any claim about an input is tested — by running the input
    ///         again. A scheduler stall or a collector pause does not recur on demand and the next
    ///         reading collapses to what the decode actually costs; a decoder that has genuinely gone
    ///         superlinear costs the same seconds every time and every reading stays over the line. The
    ///         verdict is the <i>cheapest</i> reading, because timing noise is one-sided: the machine
    ///         can only ever make a case look slower than it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured, not assumed.</b> One Windows CI run reported six targets over a two-second
    ///         budget in the same job — 2.0 s to 6.2 s, one of them on a <i>four-byte</i> input. Replayed
    ///         from the printed seeds on an idle machine, the six inputs cost 0.06 µs, 0.10 µs, 0.12 µs,
    ///         0.47 µs, 2.8 µs and 21 µs a case: five to seven orders of magnitude under what was
    ///         reported. Three runs of the same seed on the same machine then nominated three
    ///         <i>different</i> inputs as the slowest, which is the whole argument in one line — in this
    ///         tail the reading is not a function of the input at all.
    ///     </para>
    ///     <para>
    ///         <b>It costs the healthy path nothing</b>, because it only runs for a case that has
    ///         already gone over the budget — about one in a million, and none at all on a quiet
    ///         machine, where a run is therefore identical to what it was before this existed. A
    ///         confirmed finding costs this many further decodes of an input already known to be
    ///         expensive, once, before the run says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this deliberately narrows.</b> An input that is expensive only the first time
    ///         it is seen — one that poisons a cache and is cheap afterwards — is now acquitted. That is
    ///         the right property rather than a gap: <see cref="FuzzFailure.TookTooLong" /> exists to
    ///         catch a decode slow enough to be a weapon, and a cost an attacker cannot make happen
    ///         twice is not one.
    ///     </para>
    ///     <para>
    ///         Zero restores the old behaviour exactly — one reading, believed. That is what
    ///         <c>CaseBudgetTests</c> uses to show the same one-off stall failing without this and
    ///         passing with it, so the property cannot quietly become one that never fires.
    ///     </para>
    /// </remarks>
    public int CaseBudgetConfirmations { get; set; } = 4;

    /// <summary>How long one case may run before the run is abandoned rather than measured.</summary>
    /// <remarks>
    ///     Enforced while the case runs, not after it, which is the only way it can be enforced at all
    ///     — see <see cref="CaseGuard" />. Far above <see cref="CaseBudget" /> on purpose: this is the
    ///     backstop for a case that is not coming back, not a second opinion on a slow one.
    /// </remarks>
    public TimeSpan HardCaseCap { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many bytes one case may be handed, garbage included, before it is abandoned.</summary>
    /// <remarks>Churn, process-wide and coarse. Proportionality is <see cref="Weigh" />'s question.</remarks>
    public long RunawayAllocation { get; set; } = 1L << 30;

    /// <summary>How far the heap may grow during one case before it is abandoned.</summary>
    /// <remarks>
    ///     The one that describes a host running out of memory, as opposed to a target that is merely
    ///     making a lot of garbage. See <see cref="CaseGuard" /> for why the two are separate numbers.
    /// </remarks>
    public long RunawayRetention { get; set; } = 512L << 20;

    /// <summary>Whether a case that will not stop growing ends the process. See the guard.</summary>
    public bool AbandonProcessOnRunaway { get; set; } = true;

    /// <summary>Where a runaway's input is written the moment it is seen, or null not to.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the same thing as the caller writing the findings out of the outcome.</b> A caller
    ///     only gets an outcome if the run returns, and the input this names is the one that might stop
    ///     it returning — so the guard writes that one itself, from the watchdog, while the case is
    ///     still running.
    /// </remarks>
    public string? FindingDirectory { get; set; }

    /// <summary>
    ///     How many cases run before allocation is held against the target, so that one-off
    ///     start-up costs are not reported as an amplifying packet.
    /// </summary>
    /// <remarks>
    ///     A first decode pays for static constructors, the first growth of every dictionary a
    ///     stateful target keeps, and the ECS's first chunk. None of that is a property of the
    ///     input, and all of it lands on whichever input happened to be first.
    /// </remarks>
    public int WarmUpCases { get; set; } = 64;

    /// <summary>Where committed regression inputs are read from, or null to skip them.</summary>
    public string? RegressionDirectory { get; set; }

    /// <summary>Creates a run over one target.</summary>
    /// <param name="target">The decoder.</param>
    /// <param name="seed">What to seed the generator with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    public FuzzSession(IFuzzTarget target, ulong seed) {
        ArgumentNullException.ThrowIfNull(target);

        this.target = target;
        mutator = new(seed);
        picker = new(seed ^ 0x9E3779B97F4A7C15ul);

        // Its own generator rather than the picker's, so that adding a domain to a target does not
        // change which corpus entries every *other* case draws. A run is reproduced from its seed,
        // and that only holds if the streams are independent.
        shaper = new(seed ^ 0xD1B54A32D192ED03ul);
        corpus.NoveltyGuides = target.NoveltyGuides;
    }

    /// <summary>Runs a fixed number of cases.</summary>
    /// <param name="cases">How many.</param>
    /// <returns>What it came to.</returns>
    public FuzzOutcome Run(long cases) => Run(cases, TimeSpan.MaxValue);

    /// <summary>Runs for a length of time.</summary>
    /// <param name="budget">How long.</param>
    /// <returns>What it came to.</returns>
    public FuzzOutcome RunFor(TimeSpan budget) => Run(long.MaxValue, budget);

    FuzzOutcome Run(long cases, TimeSpan budget) {
        clock.Restart();

        // Armed before the corpus is replayed, not after: a committed regression that hangs is a
        // regression, and Prepare runs it.
        using var watch = new CaseGuard(target.Name, clock) {
            Cap = HardCaseCap,
            AllocationCeiling = RunawayAllocation,
            RetentionCeiling = RunawayRetention,
            AbandonProcess = AbandonProcessOnRunaway,
            FindingDirectory = FindingDirectory
        };

        guard = watch;

        try {
            return Loop(cases, budget);
        } finally {
            guard = null;
        }
    }

    FuzzOutcome Loop(long cases, TimeSpan budget) {
        Prepare();

        long executed = 0;

        var domain = target.Domain;

        // A runaway stops the run rather than counting against MaxFindings. There is nothing to learn
        // from the cases after it, and the thread it was running on may still be busy losing the
        // machine — so the sooner this returns something with the input in it, the better.
        while (executed < cases && !abandoned && clock.Elapsed < budget && findings.Count < MaxFindings) {
            var first = corpus.Pick(picker.Next());
            var second = corpus.Pick(picker.Next());

            // The one line the seam changes. Everything below — the measurement, the oracles, the
            // corpus, the bytes a finding carries — is indifferent to which branch produced the input.
            var input = domain is null ? mutator.Mutate(first, second) : domain.Mutate(first, second, shaper);

            // One in sixteen from nothing. The corpus records shapes that have been reached, and a
            // run that only ever mutates it cannot reach a shape no seed was near.
            if (picker.Next() % 16 == 0) {
                input = domain is null ? mutator.Fresh() : domain.Fresh(shaper);
            }

            Execute(input, ++executed > WarmUpCases, keep: true);
        }

        clock.Stop();

        // In the order the loop would have left: a runaway abandons everything, the cap is checked
        // before the clock, and the clock is what a nightly is supposed to end on.
        var stop = abandoned ? FuzzStop.Runaway
            : findings.Count >= MaxFindings ? FuzzStop.Findings
            : executed >= cases ? FuzzStop.Cases
            : FuzzStop.Clock;

        return new(
            target.Name,
            executed,
            corpus.Count,
            corpus.SignatureCount,
            clock.Elapsed,
            findings,
            Phrase(stop),
            suppressed,
            acquitted
        );
    }

    static string Phrase(FuzzStop stop) =>
        stop switch {
            FuzzStop.Runaway => "a runaway",
            FuzzStop.Findings => "the finding cap",
            FuzzStop.Cases => "the case bound",
            _ => "the clock"
        };

    void Prepare() {
        var seeds = new List<byte[]>();
        target.Seed(seeds);

        foreach (var seed in seeds) {
            corpus.Add(seed);
        }

        if (RegressionDirectory is not null) {
            foreach (var regression in Corpus.ReadRegressions(RegressionDirectory, target.Name)) {
                corpus.Add(regression);
            }
        }

        // An empty input is the one every decoder is most likely to have got wrong and the one no
        // mutation of a seed produces except by accident.
        corpus.Add([]);

        // Everything above was chosen by somebody. Past the corpus cap, mutants replace mutants and
        // never these.
        corpus.Protect();

        // Replayed before anything is generated, and *measured* while it is: a seed that throws is
        // a defect in the encoder's own output, and a committed regression that throws again is the
        // reason the file is in the tree.
        foreach (var entry in corpus.Entries.ToArray()) {
            if (abandoned) {
                return;
            }

            Execute(entry, measure: false, keep: false);
        }
    }

    void Execute(byte[] input, bool measure, bool keep) {
        target.Maintain();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = clock.Elapsed;
        long signature;

        // Two release stores and a reading the line above already took. Everything the guard measures
        // is measured on its own thread, because this line runs eleven million times a build.
        guard?.Enter(input, started);

        try {
            signature = target.Run(input);
        }
#pragma warning disable CA1031 // Catching everything is the assertion: nothing may escape a decoder.
        catch (Exception exception) {
            guard?.Leave();
            Record(new(target.Name, FuzzFailure.Threw, input, Describe(exception)));

            return;
        }
#pragma warning restore CA1031

        guard?.Leave();

        // A case the guard gave up on came back after all, which happens for a slow one and not for
        // the kind that ends a process. Its finding is the guard's rather than the two below: those
        // would report the same input a second time under a name that describes it less well.
        if (guard?.Breach is { } runaway) {
            abandoned = true;
            Record(runaway);

            return;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var took = clock.Elapsed - started;

        if (measure) {
            Weigh(input, allocated);

            if (target.Held > target.HeldCap) {
                Record(
                    new(
                        target.Name,
                        FuzzFailure.Retained,
                        input,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"holding {target.Held:N0} things against a cap of {target.HeldCap:N0}"
                        )
                    )
                );
            }

            // Last of the three checks, so that the further decodes a confirmation runs are seen by
            // nothing else in this case's measurement: Weigh has already taken its reading and the
            // corpus is offered the signature the first run produced.
            if (took > CaseBudget) {
                var confirmed = Confirm(input, took);

                if (confirmed > CaseBudget) {
                    Record(
                        new(
                            target.Name,
                            FuzzFailure.TookTooLong,
                            input,
                            // How many readings agreed is part of the finding, because the number on
                            // its own is what somebody would otherwise read as a single sample — which
                            // is exactly the mistake this stopped making.
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{confirmed.TotalMilliseconds:N1} ms on {input.Length:N0} B of input"
                            )
                            + (CaseBudgetConfirmations > 0
                                ? string.Create(
                                    CultureInfo.InvariantCulture,
                                    $", the cheapest of {CaseBudgetConfirmations + 1:N0} readings — the "
                                    + $"first was {took.TotalMilliseconds:N1} ms"
                                )
                                : ", read once and not confirmed")
                        )
                    );
                } else {
                    acquitted++;
                }
            }
        }

        if (keep) {
            corpus.Offer(input, signature);
        }
    }

    /// <summary>Asks an over-budget case again, and answers with the cheapest reading it gave.</summary>
    /// <param name="input">The bytes that read slow.</param>
    /// <param name="first">What they read the first time.</param>
    /// <returns>The smallest of that reading and the confirmations. See <see cref="CaseBudgetConfirmations" />.</returns>
    /// <remarks>
    ///     <para>
    ///         It stops at the first reading under the budget, because one is already the whole answer:
    ///         an input that can be decoded inside the budget is not an input that costs more than the
    ///         budget. A genuine blowup therefore pays the full count and a stall pays one repeat.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The target sees these decodes, and for a stateful one that is a real perturbation.</b>
    ///         What it is not is a threat to reproducing a run from its seed: the mutator, the picker
    ///         and the shaper never learn that a confirmation happened, so the stream of inputs stays a
    ///         pure function of the seed and the corpus is offered the same signature either way. What
    ///         moves is how many times a target that accumulates has decoded one input — on a machine
    ///         quiet enough that no case goes over the budget, not at all.
    ///     </para>
    ///     <para>
    ///         The guard is armed around each reading for the reason it is armed around the first: a
    ///         repeat is as able to wedge as an original, and a confirmation nobody was watching would
    ///         be the one place in the harness where a case that never returns is not a named finding.
    ///     </para>
    /// </remarks>
    TimeSpan Confirm(byte[] input, TimeSpan first) {
        var best = first;

        for (var attempt = 0; attempt < CaseBudgetConfirmations && best > CaseBudget; attempt++) {
            target.Maintain();

            var started = clock.Elapsed;

            guard?.Enter(input, started);

            try {
                target.Run(input);
            }
#pragma warning disable CA1031 // Same reason as the call site: nothing may escape a decoder.
            catch (Exception) {
                // A repeat that throws where the first did not says the target's state has moved on,
                // not that the input is cheap. Stop asking, and let the reading already taken stand
                // rather than acquitting on a question that was not answered.
                guard?.Leave();

                return best;
            }
#pragma warning restore CA1031

            guard?.Leave();

            var again = clock.Elapsed - started;

            if (again < best) {
                best = again;
            }
        }

        return best;
    }

    /// <summary>Adds a case to the allocation window, and reports the window if it went over.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Over a window rather than per case, for two reasons that both make a per-case check
    ///         lie.</b> The first is amortisation: a list that doubles pays for the next thousand
    ///         appends in one, and charging the whole doubling to whichever packet triggered it
    ///         reports a decoder that allocates four bytes an item as one that allocates four
    ///         kilobytes. The second is the measurement itself —
    ///         <c>GC.GetAllocatedBytesForCurrentThread</c> settles up the unused remainder of a
    ///         thread's allocation context when a collection happens, so a case that happens to
    ///         contain a Gen0 collection reads several kilobytes high through no fault of its own.
    ///     </para>
    ///     <para>
    ///         The property worth defending survives both: <b>a stream of hostile packets must not
    ///         cost memory out of proportion to the stream</b>. A window of
    ///         <see cref="WindowCases" /> is long enough that one doubling and one collection are
    ///         noise in it, and short enough that a decoder amplifying every packet is over the line
    ///         within a millisecond of running.
    ///     </para>
    /// </remarks>
    void Weigh(byte[] input, long allocated) {
        windowAllocated += allocated;
        windowAllowance += target.AllowanceFor(input.Length);

        if (allocated > windowWorst) {
            windowWorst = allocated;
            windowWorstInput = input;
        }

        if (++windowCases < WindowCases) {
            return;
        }

        if (windowAllocated > windowAllowance) {
            Record(
                new(
                    target.Name,
                    FuzzFailure.Allocated,
                    windowWorstInput ?? input,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{windowAllocated:N0} B over {windowCases:N0} cases, against an allowance of {windowAllowance:N0} B "
                        + $"— the worst single case was {windowWorst:N0} B and its input is the one below"
                    )
                )
            );
        }

        windowCases = 0;
        windowAllocated = 0;
        windowAllowance = 0;
        windowWorst = 0;
        windowWorstInput = null;
    }

    /// <summary>Names an exception by where it came from, not only by what it said.</summary>
    /// <remarks>
    ///     The top frame is the whole value of the report. "ArgumentOutOfRangeException: Specified
    ///     argument was out of the range of valid values" names no method, no file and no line, and
    ///     a fuzzer that hands you that has handed you an afternoon. It is also what the findings are
    ///     deduplicated on, so two different throwing sites are two findings rather than one.
    /// </remarks>
    static string Describe(Exception exception) {
        var frames = exception.StackTrace?.Split('\n') ?? [];
        var top = frames.Length == 0 ? "" : $" at{frames[0].AsSpan().TrimStart().TrimStart("at".AsSpan())}".TrimEnd();

        return $"{exception.GetType().Name}: {exception.Message}{top}";
    }

    /// <summary>Holds the first example of each distinct failure, and counts the rest.</summary>
    /// <remarks>
    ///     <para>
    ///         One example of each failure per target is what gets fixed; a thousand near-identical
    ///         inputs is a report nobody reads. What distinguishes them is the <i>shape</i> of the
    ///         failure, never the bytes, which differ by construction — and the first example's bytes
    ///         are what is kept, because the reproducer is the entire value of a finding.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The shape is not the message, and this used to say it was.</b> A detail is written
    ///         for a person to read, so it quotes the input: an offset, a character, a byte count. Two
    ///         instances of one defect therefore have two details, and keying on the whole string mints
    ///         a fresh finding for each — which is not a tidiness problem, because
    ///         <see cref="MaxFindings" /> then ends the run. Raven's round-trip oracle names the offset
    ///         of the first difference and the character either side of it; one dropped attribute list
    ///         was thirty-two findings at thirty-two offsets, the cap filled in five and a half
    ///         minutes, and the target's two-hour nightly had never once run longer than that.
    ///     </para>
    ///     <para>
    ///         So the key is <b>the failure and the detail with the input's own values taken out of
    ///         it</b> — digit runs and quoted spans blanked. For the failure that matters, that is
    ///         where it was thrown: <see cref="Describe" /> puts the exception's type and top frame in
    ///         the detail precisely so two throwing sites stay two findings, and blanking values
    ///         leaves both standing. What collapses is only the part that varies per input, which is
    ///         what "the same bug twice" means.
    ///     </para>
    ///     <para>
    ///         It does over-collapse in one direction, deliberately: a message whose <i>only</i>
    ///         distinguishing content is a quoted value — an unexpected diagnostic id, say — is one
    ///         finding however many ids reach it. That is the same claim as "one throwing site is one
    ///         defect", and a report of thirty near-identical lines is the thing being fixed.
    ///     </para>
    /// </remarks>
    void Record(FuzzFinding finding) {
        if (!keys.Add(Key(finding))) {
            suppressed++;

            return;
        }

        findings.Add(finding);
    }

    /// <summary>The failure's shape: everything about it that is not this input's own values.</summary>
    static string Key(FuzzFinding finding) {
        var detail = finding.Detail.AsSpan();
        var key = new StringBuilder(detail.Length + 8);

        key.Append((int)finding.Failure).Append('|');

        for (var i = 0; i < detail.Length; i++) {
            var character = detail[i];

            // A digit run is an offset, a length or a count, and always this input's.
            if (char.IsAsciiDigit(character)) {
                while (i + 1 < detail.Length && (char.IsAsciiDigit(detail[i + 1]) || detail[i + 1] is ',' or '.')) {
                    i++;
                }

                key.Append('#');

                continue;
            }

            // A quoted span is a value lifted out of the input — the character a printer dropped, the
            // token a parser did not expect. The quotes stay, so a message *shaped* around a quote
            // still differs from one that is not.
            if (character == '\'' && detail[(i + 1)..].IndexOf('\'') is >= 0 and var end) {
                key.Append("''");
                i += end + 1;

                continue;
            }

            key.Append(character);
        }

        return key.ToString();
    }
}
