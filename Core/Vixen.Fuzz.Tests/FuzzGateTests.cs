// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Fuzz.Targets;
using Vixen.Ui.Markup.Syntax;
using Xunit;

namespace Vixen.Fuzz.Tests;

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
        ["heightmap"] = 60_000,

        // The text grammars. A sidecar is the slowest case in the harness — it decodes the input to
        // a string, runs a line scanner over it, parses a YAML document into a node tree and then
        // binds that tree through reflected descriptors, which is four passes where a packet target
        // has one. The two VCSS readers are much cheaper: both are single scans over a short span,
        // and their budgets are set by how quickly the mutator exhausts a grammar this small rather
        // than by what a case costs.
        ["meta"] = 60_000,
        ["stylevalue"] = 400_000,
        ["layerrule"] = 300_000,

        // The smallest budget here by a distance, because a case is four parses rather than one: the
        // domain parses a corpus entry to choose an edit, the target parses the result, and then the
        // reparse oracle parses an edited copy twice so the incremental and full trees can be
        // compared. That is the point of the target and it is not cheap.
        ["vxml"] = 20_000,

        // And the smallest of all, because a case here is a whole compiler. Parse, print, reparse
        // twice for the incremental oracle, then bind and analyse flow — and for the rare mutant that
        // still compiles clean, lower, verify, emit SPIR-V and hand the module to spirv-val down a
        // pipe. Depth on this one belongs to the nightly, which is bounded by time and gives every
        // target the same ten minutes; the gate's job here is to notice that the pipeline still runs.
        ["raven"] = 1_500
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

    /// <summary>Every seed a grammar target offers is a document that grammar accepts.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one way a grammar seed fails that a byte seed does not: quietly.</b> A front
    ///         end is total — malformed text still produces a tree, with fabricated tokens and the
    ///         unusable source carried as trivia — so a seed with a typo in it goes on looking exactly
    ///         like a seed that works. It parses, it round-trips, it can be mutated, and every input
    ///         descended from it inherits the same error. The corpus is then a set of documents about
    ///         one mistake.
    ///     </para>
    ///     <para>
    ///         This is the grammar version of what the heightmap's seeds record — a PNG the decoder
    ///         refused at its IHDR, three branches before anything interesting, which seeded nothing
    ///         for as long as nobody measured it. That one at least failed loudly when measured. This
    ///         one has to be asked.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("vxml")]
    [InlineData("raven")]
    public void EveryGrammarSeedIsWellFormed(string name) {
        var target = FuzzTargets.Named(name);

        try {
            var seeds = new List<byte[]>();
            target.Seed(seeds);

            Assert.NotEmpty(seeds);

            foreach (var seed in seeds) {
                var source = Encoding.UTF8.GetString(seed);
                var (printed, reported) = Parse(name, source);

                Assert.True(
                    reported.Count == 0,
                    $"A {name} seed does not parse cleanly and therefore seeds nothing:\n{source}\n  "
                    + string.Join("\n  ", reported)
                );

                Assert.Equal(source, printed);
            }
        } finally {
            (target as IDisposable)?.Dispose();
        }
    }

    /// <summary>Parses a seed with whichever front end owns it.</summary>
    /// <remarks>
    ///     ⚠ <b>Raven looks enough like C from a distance to be written from memory and be wrong.</b>
    ///     Its statements end at a line break rather than a semicolon and its fields are
    ///     <c>var x: float</c> — so a seed typed out in the C shape parses into a tree full of
    ///     fabricated tokens, and because the parser is total it goes on looking exactly like a seed
    ///     that works. Every seed in this target's first draft was of that kind.
    /// </remarks>
    static (string Printed, IReadOnlyList<string> Reported) Parse(string name, string source) {
        if (string.Equals(name, "raven", StringComparison.Ordinal)) {
            var raven = Vixen.Raven.Syntax.SyntaxTree.ParseText(source, path: "Seed.rvn");

            return (raven.GetRoot().ToFullString(), [.. raven.Diagnostics.Select(d => d.ToString())]);
        }

        var markup = SyntaxTree.ParseText(source, "Seed.vxml");

        return (markup.GetRoot().ToFullString(), [.. markup.Diagnostics.Select(d => d.ToString())]);
    }

    /// <summary>The SPIR-V validator is installed, so the <c>raven</c> target means something.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A silent skip would make the codegen half of that target vacuous while the run
    ///         still printed "clean".</b> Every other oracle in this harness compares two things Vixen
    ///         wrote; <c>spirv-val</c> is the only one that asks somebody else whether the answer is
    ///         right, and it is therefore the only one that can catch a backend emitting a module that
    ///         is <i>valid and wrong</i> — the class an implicit-LOD substitution belonged to for four
    ///         months. Absent, the target goes on passing and stops checking that.
    ///     </para>
    ///     <para>
    ///         The same argument, and the same test, as
    ///         <c>SpirvBackendTests.The_validator_is_installed_so_these_tests_mean_something</c>. CI
    ///         installs <c>spirv-tools</c> on both legs.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSpirvValidatorIsInstalled() =>
        Assert.True(
            Spirv.Available,
            "spirv-val is not on PATH, so the raven target generates modules and validates nothing. "
            + "Install spirv-tools (brew install spirv-tools, or apt-get install spirv-tools)."
        );

    /// <summary>The validity oracle is switched off, and this is the note saying so out loud.</summary>
    /// <remarks>
    ///     ⚠ <b>It works, and it is off because of what it found.</b> Two one-token edits of
    ///     <c>Example2.rvn</c> compile with no diagnostic at all and emit modules a driver would
    ///     reject — a <c>bool</c> where SPIR-V forbids one, and an <c>OpConstantNull</c> of
    ///     <c>void</c>. Both are committed under <c>Corpus/raven</c> and both replay the moment
    ///     <c>VIXEN_FUZZ_SPIRV</c> is set. This test is here so that "off" is a fact somebody reads
    ///     rather than a silence, and it is <b>meant to be deleted</b> along with
    ///     <see cref="Spirv.Enabled" /> when the two are fixed.
    /// </remarks>
    [Fact]
    public void TheValidityOracleIsQuarantinedNotForgotten() =>
        Assert.SkipWhen(
            Spirv.Enabled,
            "VIXEN_FUZZ_SPIRV is set, so modules are being validated — delete this test and the switch."
        );

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
