// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Xunit;

namespace Vixen.Net.Fuzz.Tests;

/// <summary>The fuzzing exit criterion, run on every build.</summary>
/// <remarks>
///     <para>
///         <b>A fuzzer that runs nightly and nowhere else finds a regression the morning after
///         somebody has already built on it.</b> So the same harness runs twice: a fixed budget here
///         on every build, and an open-ended one under <c>VIXEN_FUZZ_SECONDS</c> for a nightly job
///         that has hours rather than seconds. A few hundred thousand cases a target is not a
///         thorough fuzz and is a very effective regression test.
///     </para>
///     <para>
///         <b>A case count, not a time slice, and that is the whole point.</b> A run bounded by the
///         clock executes a different number of cases on a loaded CI machine than on a laptop, which
///         means a defect can be found on one and not the other and a green build proves nothing in
///         particular. Bounded by count, every machine runs the same cases in the same order from
///         the same seed — so a failure here is reproduced by reading the seed out of the message.
///     </para>
///     <para>
///         The counts differ per target because a case does. The bit reader runs a couple of million
///         a second and the session runs a whole frame per case; one number would either waste the
///         build's time or prove nothing about the other.
///     </para>
/// </remarks>
public sealed class FuzzGateTests(ITestOutputHelper output) {
    /// <summary>How many cases each target gets, chosen from how fast it runs.</summary>
    /// <remarks>
    ///     A dictionary rather than a list of <c>[InlineData]</c>, and that is a fix rather than a
    ///     preference. Three targets were written, registered and named, and were <b>not in the
    ///     theory's rows</b> — so they existed, passed <see cref="EveryNameBuilds" />, and never ran.
    ///     The rows are now generated from <see cref="FuzzTargets.Names" />, so a target that exists
    ///     is a target the gate runs; forgetting to give it a budget makes
    ///     <see cref="EveryTargetHasABudget" /> fail rather than making it disappear.
    /// </remarks>
    static readonly Dictionary<string, long> Budgets = new(StringComparer.Ordinal) {
        ["packet"] = 1_200_000,
        ["bits"] = 1_200_000,
        ["handshake"] = 400_000,
        ["client"] = 700_000,
        ["snapshot"] = 700_000,
        ["inspect"] = 700_000,
        ["delta"] = 1_500_000,
        ["rpc"] = 1_500_000,
        ["synclist"] = 1_500_000,
        ["input"] = 700_000,

        // Smaller than the rest, because these three do far more per case — a transport poll walks a
        // connection table, an input run files several entries, and a handshake scan re-reads a request — and the gate's job is to catch a
        // regression quickly rather than to search deeply. Depth is the nightly's, which is bounded by
        // seconds rather than by cases and gives every target the same ten minutes.
        ["udp"] = 500_000,
        ["upgrade"] = 400_000,

        // The content formats, which are the slowest cases here by a distance: a bundle open walks
        // an index and a CRC over the whole payload, a chunk case runs an LZ4 or Zstd decode, and a
        // heightmap runs inflate and then an unfilter pass over every row. A hundred thousand of
        // those is seconds rather than the milliseconds a bit read costs, and the gate's job is to
        // notice a regression rather than to search — the nightly, bounded by time, searches.
        ["bundle"] = 150_000,
        ["chunk"] = 100_000,
        ["heightmap"] = 60_000
    };

    /// <summary>One row per registered target, so a new one cannot be left out.</summary>
    public static TheoryData<string, long> Targets {
        get {
            var data = new TheoryData<string, long>();

            foreach (var name in FuzzTargets.Names) {
                // A target with no budget still runs, at a figure small enough not to slow the build
                // while EveryTargetHasABudget says so out loud. Skipping it instead would reproduce
                // exactly the silence this replaced.
                data.Add(name, Budgets.GetValueOrDefault(name, 200_000));
            }

            return data;
        }
    }

    /// <summary>Every target survives everything the mutator can make of its own seeds.</summary>
    /// <param name="name">Which decoder.</param>
    /// <param name="cases">How many inputs to push through it.</param>
    [Theory]
    [MemberData(nameof(Targets))]
    public void NothingEscapes(string name, long cases) {
        var target = FuzzTargets.Named(name);

        try {
            var seed = Corpus.Fingerprint(Encoding.UTF8.GetBytes(name));
            var session = new FuzzSession(target, seed) { RegressionDirectory = Regressions };
            var seconds = Seconds;
            var outcome = seconds is null ? session.Run(cases) : session.RunFor(seconds.Value);

            Keep(outcome);

            // Printed on every run, not only on a failure, because the ratio of kept to cases is
            // the health of the guidance and it is invisible from a green build. A target keeping
            // most of what it runs has a signature that cannot saturate — every case looks new,
            // nothing is being learnt, and the corpus is a list rather than a selection. That was
            // true of four of these until it was measured.
            output.WriteLine(outcome.ToString());

            Assert.True(
                outcome.Clean,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{outcome}\nSeed {seed:x16}. Findings:\n  {string.Join("\n  ", outcome.Findings)}"
                )
            );

            // A run that reached no behaviours reached no code. The number is low on purpose: it is
            // here to catch a target that was accidentally disconnected from what it decodes, not to
            // assert a coverage figure the harness cannot honestly measure.
            Assert.True(
                outcome.Signatures > 4,
                $"{name} produced {outcome.Signatures} distinct behaviours — is it wired to anything?"
            );
        } finally {
            (target as IDisposable)?.Dispose();
        }
    }

    /// <summary>Every registered target has a case budget somebody chose.</summary>
    /// <remarks>
    ///     The other half of running every target: one that runs on a default budget is one nobody
    ///     decided how hard to test, and a decoder's right number depends on how fast it is — the
    ///     spread here is four hundred thousand to one and a half million.
    /// </remarks>
    [Fact]
    public void EveryTargetHasABudget() {
        var missing = FuzzTargets.Names.Where(name => !Budgets.ContainsKey(name)).ToArray();

        Assert.True(missing.Length == 0, $"No case budget for: {string.Join(", ", missing)}.");
    }

    /// <summary>Every named target is one that can actually be built.</summary>
    /// <remarks>
    ///     The list and the factory are written twice, in the way a list of names and a list of
    ///     constructors always are, and this is the assertion that keeps them the same list.
    /// </remarks>
    [Fact]
    public void EveryNameBuilds() {
        var built = FuzzTargets.All();

        try {
            Assert.Equal(FuzzTargets.Names.Count, built.Count);

            for (var i = 0; i < built.Count; i++) {
                Assert.Equal(FuzzTargets.Names[i], built[i].Name);
                Assert.False(string.IsNullOrWhiteSpace(built[i].What));
            }
        } finally {
            foreach (var target in built) {
                (target as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>Writes the inputs that broke something, where CI can pick them up.</summary>
    /// <remarks>
    ///     <para>
    ///         A finding whose bytes only exist in an assertion message is a finding somebody has to
    ///         retype. These go to <c>artifacts/fuzz-findings</c>, which the workflow uploads, and the
    ///         fix for each one is to move the file into <c>Corpus/</c> and commit it — from then on it
    ///         is replayed before every run, which is the difference between fuzzing and having
    ///         fuzzed.
    ///     </para>
    ///     <para>
    ///         Deliberately not written straight into <c>Corpus/</c>. A test that commits its own
    ///         regressions would go green on the next run having changed nothing, which is the one
    ///         outcome worse than a red build.
    ///     </para>
    /// </remarks>
    static void Keep(FuzzOutcome outcome) {
        if (outcome.Clean) {
            return;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "fuzz-findings");

        foreach (var finding in outcome.Findings) {
            Corpus.WriteRegression(directory, finding.Target, finding.Input);
        }
    }

    /// <summary>Where the committed crashers live, next to this test.</summary>
    static string Regressions => Path.Combine(AppContext.BaseDirectory, "Corpus");

    /// <summary>
    ///     How long a nightly run gets, or null for the fixed per-build budget above.
    /// </summary>
    static TimeSpan? Seconds =>
        int.TryParse(
            Environment.GetEnvironmentVariable("VIXEN_FUZZ_SECONDS"),
            CultureInfo.InvariantCulture,
            out var seconds
        ) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
