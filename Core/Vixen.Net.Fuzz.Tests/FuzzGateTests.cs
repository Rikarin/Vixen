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
    /// <summary>Every target survives everything the mutator can make of its own seeds.</summary>
    /// <param name="name">Which decoder.</param>
    /// <param name="cases">How many inputs to push through it.</param>
    [Theory]
    [InlineData("packet", 1_200_000)]
    [InlineData("bits", 1_200_000)]
    [InlineData("handshake", 400_000)]
    [InlineData("client", 700_000)]
    [InlineData("snapshot", 700_000)]
    [InlineData("inspect", 700_000)]
    [InlineData("delta", 1_500_000)]
    [InlineData("rpc", 1_500_000)]
    [InlineData("synclist", 1_500_000)]
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
