// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
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
        // The nightly runs one target per job, so nineteen of these twenty rows are somebody else's
        // job and are skipped here rather than filtered off the command line — a skip with a reason
        // is visible in that job's results, and `--filter` on a display name is a second spelling of
        // the target list that goes wrong silently. Unset, which is what a build and a laptop get,
        // this selects nothing and every row runs. TheSelectionNamesRealTargets catches a typo, which
        // would otherwise be a green job that fuzzed nothing at all.
        Assert.SkipWhen(
            Only.Length > 0 && !string.Equals(name, Only, StringComparison.Ordinal),
            $"VIXEN_FUZZ_ONLY is {Only}, so this run is that target's and leaves {name} to another."
        );

        // Only the clock-bounded leg can be hung by a target, so only it honours the exclusion. The
        // per-build run is bounded by cases and finishes whatever it starts.
        Assert.SkipWhen(
            Seconds is not null && Excluded.Contains(name),
            $"{name} is named in VIXEN_FUZZ_SKIP, so this clock-bounded run leaves it out. It still "
            + "runs on every build, where the budget is a case count."
        );

        var target = FuzzTargets.Named(name);

        try {
            var seed = Corpus.Fingerprint(Encoding.UTF8.GetBytes(name));

            var session = new FuzzSession(target, seed) {
                RegressionDirectory = Regressions,

                // The same directory Keep writes to, given to the session as well, because the one
                // finding Keep can never write is the one that stops this method returning — a case
                // that does not come back. The guard writes that one from its own thread while the
                // case is still running.
                FindingDirectory = Findings
            };

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

    /// <summary>The committed crashers are on disk where the session looks for them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A corpus that stops resolving turns this whole gate green by finding nothing.</b>
    ///         <see cref="Corpus.ReadRegressions" /> returns an empty list for a directory that is not
    ///         there, which is right for a target nobody has broken yet and indistinguishable from a
    ///         path that has gone stale — a renamed project, a dropped <c>CopyToOutputDirectory</c>, a
    ///         publish layout that flattens the tree. Every replay then silently does not happen while
    ///         the run goes on printing "clean". Written when this project was renamed out of
    ///         <c>Vixen.Net.Fuzz</c>, which is exactly the move that could have done it.
    ///     </para>
    ///     <para>
    ///         The equality is the second half and catches the other direction: a <c>Corpus/</c>
    ///         subdirectory named after a target that has since been renamed is a set of regressions
    ///         nothing replays, and that is invisible from a count alone.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheCommittedCorpusIsFound() {
        var onDisk = Directory.Exists(Regressions)
            ? Directory.GetFiles(Regressions, "*.bin", SearchOption.AllDirectories).Length
            : 0;

        Assert.True(onDisk > 0, $"No committed corpus under {Regressions} — nothing is being replayed.");

        var replayed = FuzzTargets.Names.Sum(name => Corpus.ReadRegressions(Regressions, name).Count);

        Assert.Equal(onDisk, replayed);
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

    /// <summary>The nightly's matrix is this registry, in this order, with a budget each.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A target list written out in YAML is a second source of truth that goes stale in
    ///         silence.</b> The nightly fans out one job per target and cannot ask a running assembly
    ///         for the names, so it reads <c>nightly-budgets.json</c> — and this is what makes that
    ///         file the registry rather than a copy of it. Add a twenty-first target and the gate
    ///         fails here on the next build, in the same breath as
    ///         <see cref="EveryTargetHasABudget" />, rather than fuzzing it on every build and never
    ///         once overnight.
    ///     </para>
    ///     <para>
    ///         <b>The order is asserted as well as the set</b>, so the job list on a nightly reads in
    ///         the same order as <see cref="FuzzTargets.Names" /> and the two can be compared by eye.
    ///     </para>
    ///     <para>
    ///         <b>And the seconds are a whole number of minutes because the workflow divides by
    ///         sixty</b> to derive each job's <c>timeout-minutes</c>. That division is the arithmetic
    ///         that used to be done by hand against the target count and went stale twice; a budget
    ///         that is not a multiple of sixty would hand Actions a fractional cap.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheNightlyMatrixIsTheRegistry() {
        var path = Path.Combine(AppContext.BaseDirectory, "nightly-budgets.json");

        Assert.True(File.Exists(path), $"{path} is missing, so the nightly has no matrix to build.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var entries = document.RootElement.GetProperty("targets").EnumerateArray().ToArray();

        var named = entries.Select(entry => entry.GetProperty("name").GetString() ?? string.Empty).ToArray();

        Assert.Equal(FuzzTargets.Names, named);

        foreach (var entry in entries) {
            var name = entry.GetProperty("name").GetString();
            var seconds = entry.GetProperty("seconds").GetInt32();

            Assert.True(seconds > 0, $"{name} has a nightly budget of {seconds} seconds.");
            Assert.True(seconds % 60 == 0, $"{name}'s {seconds} seconds is not a whole number of minutes.");

            // ⚠ The runner's cap is stored beside the budget rather than derived from it, because
            // Actions expressions have no arithmetic: `${{ … seconds / 60 + 30 }}` is a workflow the
            // API refuses outright — `Unexpected symbol: '/'`, before a job runs — not a slow one.
            // Storing it means the two can drift, and this is the thing that stops them.
            var minutes = entry.GetProperty("minutes").GetInt32();

            Assert.True(
                minutes == (seconds / 60) + 30,
                $"{name} budgets {seconds}s and caps its job at {minutes} min, which must be "
                + $"{(seconds / 60) + 30} — the budget plus the runner's overhead. nightly.yml reads "
                + "`minutes` verbatim and cannot compute it."
            );

            // A budget nobody can explain is one nobody can revise, and these are the only place the
            // measurement behind a number is written down where CI also reads it.
            Assert.False(
                string.IsNullOrWhiteSpace(entry.GetProperty("why").GetString()),
                $"{name}'s nightly budget has no reason attached."
            );
        }
    }

    /// <summary>Whatever the two selection variables name is a target that exists.</summary>
    /// <remarks>
    ///     ⚠ <b>A misspelt <c>VIXEN_FUZZ_ONLY</c> skips every row and passes.</b> That is a job that
    ///     ran for its whole budget, reported clean and fuzzed nothing — the exact silence the
    ///     generated theory rows were introduced to end, arriving through the other door.
    ///     <c>VIXEN_FUZZ_SKIP</c> fails the same way in the safe direction, and is worth catching for
    ///     the same reason: a target somebody meant to exclude and did not is a nightly that dies on a
    ///     defect they already knew about.
    /// </remarks>
    [Fact]
    public void TheSelectionNamesRealTargets() {
        var selected = Only.Length > 0 ? new[] { Only } : Array.Empty<string>();

        var unknown = selected.Concat(Excluded)
            .Where(name => !FuzzTargets.Names.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            $"VIXEN_FUZZ_ONLY/VIXEN_FUZZ_SKIP name targets that do not exist: {string.Join(", ", unknown)}. "
            + $"Try one of: {string.Join(", ", FuzzTargets.Names)}."
        );
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

        foreach (var finding in outcome.Findings) {
            Corpus.WriteRegression(Findings, finding.Target, finding.Input);
        }
    }

    /// <summary>Where the inputs that broke something go, for the workflow to upload.</summary>
    static string Findings =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "fuzz-findings");

    /// <summary>Where the committed crashers live, next to this test.</summary>
    static string Regressions => Path.Combine(AppContext.BaseDirectory, "Corpus");

    /// <summary>Targets a clock-bounded run leaves out, named in <c>VIXEN_FUZZ_SKIP</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Nothing is in it, and it is kept for the case it was written for.</b> It held
    ///         <c>raven</c> while a binder recursion overflowed the stack: the CLR ends the process at
    ///         the guard page with no thread left to write a finding, so that target cost the other
    ///         nineteen their results every night and left no artifact saying why. The nightly runs one
    ///         target per job now, so a death takes only its own job — which retires the blast radius
    ///         this existed for, not the mechanism.
    ///     </para>
    ///     <para>
    ///         <b>What is left is a hand override</b>, for the night somebody needs a target out of the
    ///         way without editing a workflow. It is honoured *only* when the run is bounded by the
    ///         clock — the per-build gate is bounded by cases, finishes what it starts, and has never
    ///         been affected by it.
    ///     </para>
    ///     <para>
    ///         <b>A skip rather than a deleted row, because a skip is visible in the results.</b>
    ///         Filtering it off the command line would be a target that stops running with nothing
    ///         saying so — which is the same silence the generated theory rows were introduced to end.
    ///     </para>
    /// </remarks>
    static HashSet<string> Excluded { get; } =
        new(
            (Environment.GetEnvironmentVariable("VIXEN_FUZZ_SKIP") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal
        );

    /// <summary>The one target this run is for, named in <c>VIXEN_FUZZ_ONLY</c>, or empty for all.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The nightly's matrix is what this is for.</b> One job per target, each with its own
    ///         clock and its own artifacts, which is what makes a target that dies cost only itself.
    ///         Each job runs the whole class and skips the nineteen rows that are not its own.
    ///     </para>
    ///     <para>
    ///         <b>A selector rather than a filter, and it applies to both legs.</b>
    ///         <c>dotnet test --filter</c> would have to match a theory's display name, which is
    ///         generated from the argument list and is not something a workflow should be reading; and
    ///         a filtered-out row is absent from the results where a skipped one carries its reason.
    ///         Unset — a laptop, and every per-build run — it selects nothing.
    ///     </para>
    /// </remarks>
    static string Only => (Environment.GetEnvironmentVariable("VIXEN_FUZZ_ONLY") ?? string.Empty).Trim();

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
