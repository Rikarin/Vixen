// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace Vixen.Net.Fuzz;

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
    Retained = 3
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

/// <summary>What a run came to.</summary>
/// <param name="Target">Which decoder was run.</param>
/// <param name="Cases">How many inputs were pushed through it.</param>
/// <param name="CorpusSize">How many were worth keeping.</param>
/// <param name="Signatures">How many distinct behaviours were seen.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Findings">What broke, if anything.</param>
public sealed record FuzzOutcome(
    string Target,
    long Cases,
    int CorpusSize,
    int Signatures,
    TimeSpan Elapsed,
    IReadOnlyList<FuzzFinding> Findings
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
        );
}

/// <summary>Runs one target until it is told to stop, and holds it to its promises.</summary>
/// <remarks>
///     <para>
///         <b>The oracles are the point, not the loop.</b> Pushing bytes at a decoder proves nothing
///         on its own — the decoder has to be measured while it does it. Three things are measured:
///         that nothing was thrown, that the allocation was proportionate to the input, and that the
///         case finished quickly. Everything else about the decode is the target's business.
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
    readonly List<FuzzFinding> findings = [];
    readonly Stopwatch clock = new();

    int windowCases;
    long windowAllocated;
    long windowAllowance;
    long windowWorst;
    byte[]? windowWorstInput;

    /// <summary>How long one input may take to decode before that is itself the finding.</summary>
    /// <remarks>
    ///     <para>
    ///         Generous, because it is checked after the fact rather than enforced: this measures a
    ///         case that finished slowly, and a case that never finishes hangs the run. That is the
    ///         right trade — cancelling the decode would mean running it on another thread, and a
    ///         decoder is single-threaded code whose whole contract is about what it does on the
    ///         frame's thread. A hung fuzz run is a legible failure; a decoder that can be made to
    ///         loop forever is exactly the finding, and it will be sitting in the stack trace.
    ///     </para>
    ///     <para>
    ///         Wide enough that a machine under load does not report a finding it cannot reproduce.
    ///         Any real runaway here is orders of magnitude past it: the loops in these decoders are
    ///         bounded by a length taken from a packet, so the failure mode is seconds, not
    ///         milliseconds.
    ///     </para>
    /// </remarks>
    public TimeSpan CaseBudget { get; set; } = TimeSpan.FromSeconds(2);

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
        Prepare();

        long executed = 0;

        while (executed < cases && clock.Elapsed < budget && findings.Count < MaxFindings) {
            var input = mutator.Mutate(corpus.Pick(picker.Next()), corpus.Pick(picker.Next()));

            // One in sixteen from nothing. The corpus records shapes that have been reached, and a
            // run that only ever mutates it cannot reach a shape no seed was near.
            if (picker.Next() % 16 == 0) {
                input = mutator.Fresh();
            }

            Execute(input, ++executed > WarmUpCases, keep: true);
        }

        clock.Stop();

        return new(target.Name, executed, corpus.Count, corpus.SignatureCount, clock.Elapsed, findings);
    }

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

        // Replayed before anything is generated, and *measured* while it is: a seed that throws is
        // a defect in the encoder's own output, and a committed regression that throws again is the
        // reason the file is in the tree.
        foreach (var entry in corpus.Entries.ToArray()) {
            Execute(entry, measure: false, keep: false);
        }
    }

    void Execute(byte[] input, bool measure, bool keep) {
        target.Maintain();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = clock.Elapsed;
        long signature;

        try {
            signature = target.Run(input);
        }
#pragma warning disable CA1031 // Catching everything is the assertion: nothing may escape a decoder.
        catch (Exception exception) {
            Record(new(target.Name, FuzzFailure.Threw, input, Describe(exception)));

            return;
        }
#pragma warning restore CA1031

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

            if (took > CaseBudget) {
                Record(
                    new(
                        target.Name,
                        FuzzFailure.TookTooLong,
                        input,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{took.TotalMilliseconds:N1} ms on {input.Length:N0} B of input"
                        )
                    )
                );
            }
        }

        if (keep) {
            corpus.Offer(input, signature);
        }
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

    void Record(FuzzFinding finding) {
        // One example of each failure per target is what gets fixed; a thousand near-identical
        // inputs is a report nobody reads. The fingerprint of the *shape* — the failure and its
        // detail — is what distinguishes them, not the bytes, which differ by construction.
        foreach (var seen in findings) {
            if (seen.Failure == finding.Failure && string.Equals(seen.Detail, finding.Detail, StringComparison.Ordinal)) {
                return;
            }
        }

        findings.Add(finding);
    }
}
