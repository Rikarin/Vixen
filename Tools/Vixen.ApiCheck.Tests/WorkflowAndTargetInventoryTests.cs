// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ The build's ledgers — <c>README.md</c>'s target list and <c>docs/plan/12</c>'s target graph
///     and CI tables — say what exists, and this is what makes them say it about the tree they are in.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every one of these lists has been wrong, and each was found by a person reading it
///         rather than by anything running.</b> <c>docs/plan/12</c> named eleven CI jobs of which none
///         corresponded ([#340](https://github.com/Rikarin/Vixen/issues/340)); it then named eight
///         names that did not exist and missed fourteen that did; the body of
///         [#327](https://github.com/Rikarin/Vixen/issues/327) counted six jobs in <c>ci.yml</c> and
///         six in <c>nightly.yml</c> when there were eight and seven, and had to be corrected twice in
///         its own comments. A document that drifts is not a documentation problem here — it is how
///         <c>CompileMobile</c> and <c>CheckAotIos</c> came to be targets that no workflow invokes and
///         nobody noticed, which is that issue.
///     </para>
///     <para>
///         ⚠ <b>What this does not do is decide which legs should exist.</b> It compares two committed
///         artefacts with each other. The <c>android</c>, <c>ios</c> and <c>release</c> rows of doc 10
///         § Platform CI matrix have no job and this stays green — a gap between a design document and
///         the build is a judgement, and gating it here would be inventing policy. What it catches is
///         the drift underneath: a job added and not written down, a job written down and deleted, a
///         target invoked by a leg its own row does not mention, a target with no entry in either
///         ledger.
///     </para>
///     <para>
///         Here, beside <c>CiConcurrencyTests</c> and <c>TestParallelismTests</c>, because this is the
///         assembly that walks the repository to ask what the build reads. Anchored on
///         <c>AppContext.BaseDirectory</c> and never on <c>[CallerFilePath]</c>, which CI rewrites to
///         <c>/_/</c> — <see cref="SourceAnchoredTestTests" /> holds that property.
///     </para>
/// </remarks>
public sealed class WorkflowAndTargetInventoryTests {
    /// <summary>How the two documents spell the counts they state in prose.</summary>
    /// <remarks>
    ///     A job count and a target count, so the small numbers and the band the build is in. A count
    ///     that leaves this range fails to be looked up, which is a red test asking for a word rather
    ///     than a silent pass.
    /// </remarks>
    static readonly Dictionary<int, string> Numbers = new() {
        [2] = "two",
        [3] = "three",
        [4] = "four",
        [5] = "five",
        [6] = "six",
        [7] = "seven",
        [8] = "eight",
        [9] = "nine",
        [10] = "ten",
        [11] = "eleven",
        [12] = "twelve",
        [36] = "thirty-six",
        [37] = "thirty-seven",
        [38] = "thirty-eight",
        [39] = "thirty-nine",
        [40] = "forty",
        [41] = "forty-one",
        [42] = "forty-two",
        [43] = "forty-three",
        [44] = "forty-four",
        [45] = "forty-five",
        [46] = "forty-six",
        [47] = "forty-seven",
        [48] = "forty-eight"
    };

    /// <summary>Every Nuke target is named in the README's list, and the count it states is right.</summary>
    [Fact]
    public void TheReadmeNamesEveryTarget() {
        var targets = Targets();
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var list = Regex.Match(
            readme,
            @"There are (?<count>[a-z-]+) targets:\s*`(?<names>[^`]+)`",
            RegexOptions.Singleline
        );

        Assert.True(
            list.Success,
            "README.md no longer carries a \"There are <n> targets: `…`\" list, so the one place a "
            + "reader is told what ./build.sh can do is either gone or written in a shape this "
            + "cannot read. Say which here rather than leaving the list unasserted."
        );

        var named = list.Groups["names"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Word(targets.Count), list.Groups["count"].Value);
        Assert.Equal(targets, named.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    ///     And <c>docs/plan/12</c>'s target graph names every one of them too, with its own count.
    /// </summary>
    /// <remarks>
    ///     ⚠ Containment rather than equality, because the block is a drawing: it carries arrows,
    ///     a parenthetical and <c>CompileWeb</c> twice, on two branches. The claim being held is the
    ///     one the document makes about itself — that this is the whole set — and it is the direction
    ///     a new target breaks.
    /// </remarks>
    [Fact]
    public void DocTwelvesTargetGraphNamesEveryTarget() {
        var targets = Targets();
        var document = DocTwelve();
        var graph = Regex.Match(
            document,
            @"The (?<count>[a-z-]+) targets, by what they depend on.*?```(?<block>.*?)```",
            RegexOptions.Singleline
        );

        Assert.True(
            graph.Success,
            "docs/plan/12 no longer opens with a \"The <n> targets, by what they depend on\" graph. "
            + "That block is the only place the dependency edges are written down."
        );

        var drawn = Regex.Matches(graph.Groups["block"].Value, @"\b[A-Z][A-Za-z]+\b")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = targets.Where(target => !drawn.Contains(target)).ToList();

        Assert.True(
            missing.Count == 0,
            $"build/Build*.cs declares {string.Join(", ", missing)}, which docs/plan/12's target graph "
            + "names nowhere. A target no document names is one nobody types — which is what happened "
            + "to CheckPathCase, and to the four the ledger's own ⚠ below the graph records."
        );

        Assert.Equal(Word(targets.Count), graph.Groups["count"].Value);
    }

    /// <summary>The jobs <c>ci.yml</c> declares are the jobs doc 12's table names, and as many.</summary>
    [Fact]
    public void DocTwelveNamesEveryJobInCiYaml() {
        var document = DocTwelve();
        var table = Regex.Match(document, @"`ci\.yml`, (?<count>[a-z-]+) jobs:\s*\n\n(?<rows>(?:\|.*\n)+)");

        Assert.True(table.Success, "docs/plan/12 no longer carries a table of ci.yml's jobs.");

        var documented = Rows(table.Groups["rows"].Value);
        var declared = Jobs("ci.yml");

        Assert.Equal(declared, documented);
        Assert.Equal(Word(declared.Count), table.Groups["count"].Value);
    }

    /// <summary>And the same for <c>nightly.yml</c>, whose jobs doc 12 names in a sentence.</summary>
    /// <remarks>
    ///     ⚠ <c>ci-freshness</c> is why this exists. It is the job that fails when nothing has verified
    ///     master for two days, and it was in no document at all — a watchman nobody had written down,
    ///     which is one deletion away from the silence it was built to break.
    /// </remarks>
    [Fact]
    public void DocTwelveNamesEveryJobInNightlyYaml() {
        var document = DocTwelve();
        var sentence = Regex.Match(
            document,
            @"`nightly\.yml`, (?<count>[a-z-]+) jobs:(?<body>.*?)\n\n",
            RegexOptions.Singleline
        );

        Assert.True(
            sentence.Success,
            "docs/plan/12 no longer carries a \"`nightly.yml`, <n> jobs:\" paragraph, so what runs "
            + "overnight is written down nowhere."
        );

        var documented = Regex.Matches(sentence.Groups["body"].Value, @"`(?<name>[a-z][a-z0-9-]*)`")
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var declared = Jobs("nightly.yml");

        Assert.Equal(declared, documented);
        Assert.Equal(Word(declared.Count), sentence.Groups["count"].Value);
    }

    /// <summary>
    ///     Every Nuke target a <c>ci.yml</c> job invokes is named in that job's row of doc 12's table.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One direction only: a row may name a target it does not run and say why — <c>pack</c>'s
    ///         does, because <c>Pack</c> depends on <c>Test</c> in the graph and the leg skips it. The
    ///         direction that matters is the other one, where a leg quietly grew a target nobody wrote
    ///         down. <c>checks</c> had run <c>CheckDocsCoverage</c> for as long as that target has
    ///         existed and its row named five targets and not six.
    ///     </para>
    ///     <para>
    ///         ⚠ The instrument: a job with no <c>./build.sh</c> line at all would satisfy this by
    ///         invoking nothing, so the total number of invocations found across the workflow is
    ///         asserted first. A parse that stopped matching is otherwise a green run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryTargetACiJobRunsIsNamedInItsRow() {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var targets = Targets().ToHashSet(StringComparer.Ordinal);
        var document = DocTwelve();
        var rows = Regex.Matches(document, @"^\| `(?<job>[a-z][a-z0-9-]*)` \|(?<body>.*)$", RegexOptions.Multiline)
            .ToDictionary(match => match.Groups["job"].Value, match => match.Groups["body"].Value, StringComparer.Ordinal);

        var invoked = 0;
        var undocumented = new List<string>();

        foreach (var (job, body) in JobBodies(workflow)) {
            if (!rows.TryGetValue(job, out var row)) {
                continue;
            }

            foreach (var run in Regex.Matches(body, @"\./build\.sh (?<targets>[A-Za-z ]+)")) {
                foreach (var target in ((Match)run).Groups["targets"].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(targets.Contains)) {
                    invoked++;

                    if (!row.Contains(target, StringComparison.Ordinal)) {
                        undocumented.Add($"{job} runs {target}");
                    }
                }
            }
        }

        Assert.True(
            invoked > 0,
            "No ci.yml job was found invoking any Nuke target. Every job in that workflow calls one — "
            + "that is the point of having Nuke — so this is a parse that has stopped reading the "
            + "workflow rather than a workflow that stopped running the build."
        );

        Assert.True(
            undocumented.Count == 0,
            $"These targets are run by a CI job whose row in docs/plan/12 does not name them: "
            + $"{string.Join(", ", undocumented.Order(StringComparer.Ordinal))}. The table is what a "
            + "reader is told CI does, and a leg that quietly grew a target is how it stops being that."
        );
    }

    /// <summary>The targets <c>build/Build*.cs</c> declares, ordered.</summary>
    /// <returns>Every target name.</returns>
    static List<string> Targets() {
        var build = Path.Combine(RepositoryRoot(), "build");
        var declarations = Directory.EnumerateFiles(build, "Build*.cs")
            .SelectMany(file =>
                Regex.Matches(File.ReadAllText(file), @"^\s*Target (?<name>\w+) => ", RegexOptions.Multiline)
            )
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The instrument for every test above: a glob that reached no file, or a declaration shape
        // that has moved, leaves each of them comparing two empty lists and passing.
        Assert.True(
            declarations.Count > 20,
            $"build/Build*.cs declares {declarations.Count} targets, and this build has had more than "
            + "thirty for its whole history. Nothing below is asserting about the real set."
        );

        return declarations;
    }

    /// <summary>The job ids a workflow declares, ordered.</summary>
    /// <param name="file">The workflow's file name.</param>
    /// <returns>Its job ids.</returns>
    /// <remarks>
    ///     ⚠ Read out of the <c>jobs:</c> block rather than by indentation alone: <c>on:</c>'s own
    ///     children (<c>push:</c>, <c>pull_request:</c>) sit at the same two spaces and would read as
    ///     jobs.
    /// </remarks>
    static List<string> Jobs(string file) {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", file))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var block = Regex.Match(workflow, @"^jobs:\s*$(?<body>(?:\n(?:[ \t#].*)?)*)", RegexOptions.Multiline);

        Assert.True(block.Success, $".github/workflows/{file} has no top-level jobs: block.");

        var jobs = Regex.Matches(block.Groups["body"].Value, @"^  (?<name>[a-z][a-z0-9-]*):\s*$", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(jobs.Count > 0, $".github/workflows/{file} declares a jobs: block with no job in it.");

        return jobs;
    }

    /// <summary>Each job's own text, so a <c>./build.sh</c> line can be attributed to it.</summary>
    /// <param name="workflow">The whole workflow.</param>
    /// <returns>Job id and the lines under it.</returns>
    static IEnumerable<(string Job, string Body)> JobBodies(string workflow) {
        var block = Regex.Match(workflow, @"^jobs:\s*$(?<body>(?:\n(?:[ \t#].*)?)*)", RegexOptions.Multiline);
        var jobs = Regex
            .Matches(block.Groups["body"].Value, @"^  (?<name>[a-z][a-z0-9-]*):\s*$", RegexOptions.Multiline)
            .ToList();

        for (var index = 0; index < jobs.Count; index++) {
            var start = jobs[index].Index;
            var end = index + 1 < jobs.Count ? jobs[index + 1].Index : block.Groups["body"].Value.Length;

            yield return (jobs[index].Groups["name"].Value, block.Groups["body"].Value[start..end]);
        }
    }

    /// <summary>The first backticked cell of every row of a table.</summary>
    /// <param name="rows">The table's rows.</param>
    /// <returns>The names, ordered, without the header separator.</returns>
    static List<string> Rows(string rows) =>
        Regex.Matches(rows, @"^\| `(?<name>[a-z][a-z0-9-]*)` \|", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>How a count is spelled in prose.</summary>
    /// <param name="count">The number.</param>
    /// <returns>Its word.</returns>
    static string Word(int count) {
        Assert.True(
            Numbers.ContainsKey(count),
            $"This build has {count} of something a document states in words, and that number is "
            + "outside the range this test can spell. Widen Numbers rather than dropping the "
            + "assertion — the count is what a reader trusts without checking."
        );

        return Numbers[count];
    }

    /// <summary>The build-and-CI document, normalised.</summary>
    /// <returns>Its text.</returns>
    static string DocTwelve() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "plan", "12-build-ci-and-testing.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    ///     ⚠ Walked up from the assembly rather than from <c>[CallerFilePath]</c>, which CI rewrites
    ///     to <c>/_/</c>.
    /// </summary>
    /// <returns>The repository root.</returns>
    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
