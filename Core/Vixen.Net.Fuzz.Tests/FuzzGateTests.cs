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
public sealed class FuzzGateTests {
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
