// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>A run that does not gate does not call what it finds an error.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1018">#1018</a>.</b>
///         <c>./build.sh Docs</c> printed <c>[ERR] Docs: T:… has no guide page …</c>, reported
///         <c>Docs Succeeded</c> and exited 0, while <c>./build.sh CheckDocs</c> on the same tree
///         exited 255. Nuke tags a child process's standard error as <c>[ERR]</c>, so the word came
///         from the stream this tool chose — and it chose standard error whether or not the run
///         would fail on what it printed.
///     </para>
///     <para>
///         ⚠ <b>Ask what this gate prints on the day it does not run: it printed errors and
///         succeeded, which is the strongest possible version of that failure.</b> Sixteen batches of
///         the doc 48 workstream ran <c>Docs</c> in their pre-merge sweep believing it was the gate,
///         and every one of those sweeps would have passed a new undocumented public type.
///     </para>
/// </remarks>
public class ProblemReportTests {
    static readonly string[] One = ["T:Vixen.X has no guide page and no line in docs/DocsExempt.txt"];

    /// <summary>⚠ A generating run writes to standard output, so nothing tags it <c>[ERR]</c>.</summary>
    /// <remarks>
    ///     <b>The whole fix, and the assertion is the stream rather than the words.</b> A test that
    ///     only read the wording would stay green against a run that said "this does not gate" on
    ///     standard error and was therefore still printed as <c>[ERR] Docs: this does not gate</c>.
    /// </remarks>
    [Fact]
    public void A_run_that_does_not_gate_does_not_write_to_standard_error() {
        var (toStandardError, lines) = ProblemReport.Describe(One, gating: false);

        Assert.False(toStandardError);
        Assert.Contains(One[0], lines[^1], StringComparison.Ordinal);
    }

    /// <summary>And it names the target that does fail, in the form it is typed.</summary>
    /// <remarks>
    ///     ⚠ <b>The two target names differ by six characters, and CLAUDE.md was wrong about which
    ///     was which for sixteen batches.</b> A reader who is told only "this does not gate" has to
    ///     go and find out what does; the line carries the command, so the answer is where the
    ///     question is asked.
    /// </remarks>
    [Fact]
    public void A_run_that_does_not_gate_names_the_target_that_does() {
        var (_, lines) = ProblemReport.Describe(One, gating: false);

        Assert.Contains(lines, line => line.Contains("CheckDocs", StringComparison.Ordinal));
    }

    /// <summary>⚠ A gating run still writes to standard error, which is what makes it visible.</summary>
    /// <remarks>
    ///     <b>The half a fix could break silently.</b> Moving everything to standard output would
    ///     close #1018 by making the gate's own failure indistinguishable from its chatter — the
    ///     target would still exit non-zero, and the reason would be twenty lines up among the
    ///     counts. The stream is the difference between the two runs, and both directions matter.
    /// </remarks>
    [Fact]
    public void A_gating_run_writes_to_standard_error() {
        var (toStandardError, lines) = ProblemReport.Describe(One, gating: true);

        Assert.True(toStandardError);
        Assert.DoesNotContain(lines, line => line.Contains("CheckDocs", StringComparison.Ordinal));
    }

    /// <summary>Nothing found is nothing printed, on either stream.</summary>
    /// <remarks>
    ///     The instrument's own question: a reporter that always printed a header would put "0
    ///     problems in the written half" on stderr of every clean gating run, which is the defect
    ///     this fixes wearing the opposite sign.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nothing_found_prints_nothing(bool gating) {
        var (toStandardError, lines) = ProblemReport.Describe([], gating);

        Assert.False(toStandardError);
        Assert.Empty(lines);
    }

    /// <summary>Past the limit the rest are counted rather than listed, and the count is right.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted on the number and not on the presence of the word "more".</b> An off-by-one
    ///     in the remainder is invisible to a substring check and is exactly what a rewrite of this
    ///     loop would get wrong.
    /// </remarks>
    [Fact]
    public void Past_the_limit_the_remainder_is_counted() {
        var many = Enumerable.Range(0, 30).Select(index => $"problem {index}").ToList();
        var (_, lines) = ProblemReport.Describe(many, gating: true, limit: 25);

        // A blank line, the header, twenty-five problems, and the tally.
        Assert.Equal(28, lines.Count);
        Assert.Equal("  …and 5 more", lines[^1]);
        Assert.Contains("problem 24", lines[^2], StringComparison.Ordinal);
    }

    /// <summary>⚠ And the tool asks <see cref="ProblemReport" /> rather than printing for itself.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The wiring, which every assertion above is silent about.</b> <c>Describe</c> could
    ///         be perfect and unreached — <c>Program.cs</c> is top-level statements in an executable
    ///         and cannot be invoked from here, so what a unit test can settle is the decision and
    ///         not that anything takes it. This reads the production file, the way
    ///         <see cref="RealCoverageTests" /> reads the repository's own <c>docs/</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because either alone passes against the defect.</b> A file that
    ///         calls <c>ProblemReport.Write</c> <em>and</em> keeps the old loop prints everything
    ///         twice, once on each stream, and <c>[ERR]</c> is back; a file with neither prints
    ///         nothing at all and would satisfy a check for the absence of the loop.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_tool_reports_the_written_half_through_this_and_not_inline() {
        var source = File.ReadAllText(Path.Combine(Root, "Tools", "Vixen.DocGen", "Program.cs"));

        Assert.Contains("ProblemReport.Write(problems", source, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "foreach (var problem in problems",
            source
        );
    }

    /// <summary>The checkout this assembly was compiled in — the nearest root, never the outermost.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per parallel agent, so a walk that kept
    ///     going would leave a worktree's test reading the main tree's <c>Program.cs</c> — a file the
    ///     run cannot change, while missing the one it can. <see cref="RealCoverageTests" /> carries
    ///     the same walk for the same reason.
    /// </remarks>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null) {
                if (File.Exists(Path.Combine(directory.FullName, "Tools", "Vixen.DocGen", "Program.cs"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Tools/Vixen.DocGen/Program.cs above {AppContext.BaseDirectory}. This test reads "
                + "the repository it was compiled in, so an output directory outside the checkout "
                + "breaks it."
            );
        }
    }
}
