// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Build;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/866">#866</a>: the defect no gate could see,
///     with the gate that sees it run here over the two comments that landed stapled.
/// </summary>
/// <remarks>
///     <para>
///         <b>The rule is <c>build/DocCommentRule.cs</c> and this assembly compiles it</b> — the same
///         arrangement <see cref="PluginReferenceRuleTests" /> made for the plugin rule, and for the
///         same reason. A gate whose only observable behaviour is hypothetical has not answered the
///         question this repository asks of a gate, and a rule written to catch a defect nobody can
///         reproduce is exactly the kind that turns out to catch nothing.
///     </para>
///     <para>
///         ⚠ <b>So the two batch-9 stapleings are re-introduced verbatim, from the merge that removed
///         them</b> (<c>e6a94c8c</c>). If either fixture went quiet the rule would be decoration, and
///         the fixtures are the real text rather than a reduction of it because a reduction is a
///         claim about what the defect looked like.
///     </para>
///     <para>
///         ⚠ <b>The other half is the false-positive fixture, and it is the half that decides whether
///         this gate survives.</b> A regular-expression draft of this rule reported 544 findings on
///         this tree and every one sampled was the parser failing to see an <c>operator ==</c>, a
///         tuple return type or an indexer body containing <c>this[</c>. That rule would have been
///         switched off within a week. <see cref="Shapes_that_are_not_defects_are_left_alone" /> is
///         what says the parser is a parser.
///     </para>
/// </remarks>
public class DocCommentRuleTests {
    /// <summary>Where this file was compiled from.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The repository tree this assembly was compiled from.</summary>
    /// <remarks>
    ///     Anchored at the compiled path rather than climbing for a <c>.git</c>, because
    ///     <c>.claude/worktrees</c> holds a whole checkout per agent and a climb from there reads
    ///     somebody else's copy of these files. <see cref="DocCommentRule.Sources" /> excludes that
    ///     directory as well, so both halves have to be wrong before another session's tree is read.
    /// </remarks>
    static string Repository() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));

    /// <summary>
    ///     ⚠ Every doc comment in this repository outside the exemption list describes the member it
    ///     is attached to, and every file on that list still needs to be on it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the finding, and everything before the last two assertions is the
    ///         instrument.</b> A walk that found no files, a parser that stopped producing
    ///         documentation trivia, a rule that lost its checks — each of those reports "no findings"
    ///         and means nothing, so each is refused by name first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Fifty-one files were already wrong the day the rule was written, and one of them
    ///         is a live production staple</b>: <c>KeyChord.cs</c> carries <c>MacGlyphs</c>' whole
    ///         block above <c>MacWords</c>, so one public formatter is undocumented and the other is
    ///         described twice. None is in doc 48's own files — batch 9's two were the only ones there
    ///         and the merge fixed them — so the list is other people's work to shrink (#879) rather
    ///         than a reason to hold the rule back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which makes the exemption list itself the strongest instrument here.</b> Every
    ///         file on it is a file this run has to have flagged, so a clean sweep with a non-empty
    ///         list is proof that the checks stopped firing rather than proof that the tree is clean.
    ///         That is the assertion four green instruments could not make in #866.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_repository_holds_no_stapled_doc_comment_outside_the_exemption_list() {
        var root = Repository();
        var sources = DocCommentRule.Sources(root);

        Assert.True(
            sources.Count > 3000,
            $"Only {sources.Count} C# files were found under {root}. This walk is anchored at this file's "
            + "compiled path; a run whose sources are not on the machine reads nothing and would otherwise "
            + "report a clean tree."
        );

        // The instrument: the rule fires on a file that is wrong, right now, in this process. A clean
        // sweep below is a measurement only while this is true.
        Assert.NotEmpty(DocCommentRule.Check("fixture.cs", StapledOntoResolve));

        var findings = sources
            .SelectMany(file => DocCommentRule.Check(file[(root.Length + 1)..], File.ReadAllText(file)))
            .ToList();

        var exempt = DocCommentRule.Exemptions(root);

        Assert.NotEmpty(exempt);
        Assert.NotEmpty(findings);

        var (unexpected, stale) = DocCommentRule.Review(findings, exempt);

        // Reported as one message rather than as a collection diff on purpose: a doc comment is fixed
        // by reading it, so the file, the line and the sentence have to survive into the failure.
        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} file(s) hold a doc comment block that describes a member other than the one it "
            + "is attached to:\n"
            + string.Join('\n', findings.Where(finding => unexpected.Contains(finding.File)))
        );

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} file(s) in {DocCommentRule.ExemptionsPath} no longer hold one. Delete their lines — "
            + "the list may only shrink: " + string.Join(", ", stale)
        );
    }

    /// <summary>
    ///     The first batch-9 staple: <c>Refused</c>'s block left heading <c>Resolve</c>.
    /// </summary>
    /// <remarks>
    ///     Reduced only in the bodies. The comment and both signatures are the ones that were on
    ///     master, so what the rule is being asked about is what shipped: two <c>&lt;summary&gt;</c>,
    ///     two <c>&lt;returns&gt;</c>, and a <c>&lt;param name="compilation"&gt;</c> on a method whose
    ///     four parameters are named something else.
    /// </remarks>
    const string StapledOntoResolve = """
        namespace Fixture;

        static class LayerStackPreview {
            /// <summary>What to say when the compilation refused.</summary>
            /// <param name="compilation">It.</param>
            /// <returns>The sentence.</returns>
            /// <remarks>Both lists, because they are two readers' problems.</remarks>
            /// <summary>The picture for one external image, or the sentence saying why there is none.</summary>
            /// <param name="project">The project the asset reference is resolved against.</param>
            /// <param name="uploads">Where the texture is made, and what owns it.</param>
            /// <param name="plan">The plan the image belongs to.</param>
            /// <param name="entry">The external the compilation could not fill.</param>
            /// <returns>Null when it was uploaded, or the sentence saying why it was not.</returns>
            static string? Resolve(
                EditorProject project,
                TextureUploads uploads,
                TexturePlan plan,
                TextureGraphExternal entry
            ) => null;

            static string Refused(LayerStackCompilation compilation) => "";
        }
        """;

    /// <summary>
    ///     The second: the <c>Painted</c> helper's block left heading a <c>[Fact]</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This one is the reason the rule needs the parameter half at all.</b> Its only
    ///     structural tell besides the second <c>&lt;summary&gt;</c> is a <c>&lt;param&gt;</c> on a
    ///     test method that takes nothing — the shape CS1572 names and that
    ///     <c>GenerateDocumentationFile</c>, off for this whole profile, was not there to report.
    /// </remarks>
    const string StapledOntoTheCautionTest = """
        namespace Fixture;

        public class LayerStackPanelDeviceTests {
            /// <summary>A stack whose one fill authors an ordered colour no default matches.</summary>
            /// <param name="side">How big to bake it.</param>
            /// <returns>The stack.</returns>
            /// <summary>A plan's caution reaches the pane's sentence rather than stopping at the bake.</summary>
            /// <remarks>The caution is the sentence, not the bake.</remarks>
            [Fact]
            public void A_plans_caution_reaches_the_panes_sentence() {
            }

            static LayerStackAsset Painted(int side) => null!;
        }
        """;

    /// <summary>
    ///     ⚠ Both comments that landed stapled in batch 9 are red, and both are green once unstapled.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The sabotage, and it is the whole evidence that the sweep above is a measurement.</b>
    ///         A rule that cannot fire and a tree with nothing wrong in it print the same thing. Each
    ///         fixture differs from its clean twin only in where the comment sits, so the difference
    ///         in the answer is the staple's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The clean halves matter as much.</b> <c>Resolve</c> unstapled documents four
    ///         parameters and <c>Refused</c> documents one; a rule that fired on those too would be
    ///         reporting every documented method in the repository, and its green sweep above would be
    ///         impossible rather than informative.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Both_batch_nine_stapleings_are_caught() {
        var resolve = DocCommentRule.Check("LayerStackPreview.cs", StapledOntoResolve);

        Assert.Contains(resolve, finding => finding.Message.Contains("<summary>", StringComparison.Ordinal));
        Assert.Contains(resolve, finding => finding.Message.Contains("<returns>", StringComparison.Ordinal));
        Assert.Contains(resolve, finding => finding.Message.Contains("`compilation`", StringComparison.Ordinal));

        var caution = DocCommentRule.Check("LayerStackPanelDeviceTests.cs", StapledOntoTheCautionTest);

        Assert.Contains(caution, finding => finding.Message.Contains("<summary>", StringComparison.Ordinal));
        Assert.Contains(caution, finding => finding.Message.Contains("`side`", StringComparison.Ordinal));
        Assert.Contains(caution, finding => finding.Message.Contains("takes no parameters at all", StringComparison.Ordinal));

        // And the same two files with each comment over the member it documents.
        Assert.Equal([], DocCommentRule.Check("LayerStackPreview.cs", Unstapled(StapledOntoResolve, "Refused")));
        Assert.Equal([], DocCommentRule.Check("LayerStackPanelDeviceTests.cs", Unstapled(StapledOntoTheCautionTest, "Painted")));
    }

    /// <summary>The same fixture with the misplaced block moved down onto the member it documents.</summary>
    /// <param name="fixture">The stapled text.</param>
    /// <param name="member">The member the misplaced block belongs to.</param>
    /// <returns>The fixture as it reads after the staple is undone.</returns>
    /// <remarks>
    ///     ⚠ <b>A move rather than a second literal, so that the two halves cannot drift.</b> What the
    ///     assertion compares is one text and the same text with the block relocated — an edited copy
    ///     could be made clean by accident, and then the green half would be evidence about the copy
    ///     instead of about the staple. The misplaced block is everything from the first
    ///     <c>///</c> line up to the second <c>&lt;summary&gt;</c>, which is what a staple is.
    /// </remarks>
    static string Unstapled(string fixture, string member) {
        var lines = fixture.Split('\n').ToList();
        var first = lines.FindIndex(line => line.TrimStart().StartsWith("///", StringComparison.Ordinal));
        var second = lines.FindIndex(first + 1, line => line.Contains("<summary>", StringComparison.Ordinal));

        Assert.True(first >= 0 && second > first, "The fixture is not stapled, so there is nothing to undo.");

        var moved = lines.GetRange(first, second - first);

        lines.RemoveRange(first, second - first);

        lines.InsertRange(
            lines.FindIndex(line => line.Contains($" {member}(", StringComparison.Ordinal)),
            moved
        );

        return string.Join('\n', lines);
    }

    /// <summary>
    ///     ⚠ The shapes a textual draft of this rule got wrong are not findings.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Every one of these was measured red by the regular-expression draft.</b> An
    ///         <c>operator ==</c> whose <c>(</c> follows an <c>=</c>; a method returning a named tuple,
    ///         whose first <c>(</c> is the return type; an indexer, whose parameters are in brackets
    ///         and whose body contains <c>this[</c>; a positional record, whose parameters are the
    ///         type's; a primary constructor on a class. The draft reported 544 of these and would
    ///         have been ignored or deleted, which is a worse outcome than the gap it filled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the last one is why the element walk is structural.</b> A
    ///         <c>&lt;summary&gt;</c> written inside a <c>&lt;code&gt;</c> sample is an example of a
    ///         summary, not a second one, and a count of the characters cannot tell those apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Shapes_that_are_not_defects_are_left_alone() {
        const string clean = """
            namespace Fixture;

            /// <summary>A record whose parameters are the type's.</summary>
            /// <param name="Name">Documented on the type, which is where a positional record's are.</param>
            /// <param name="Size">The other one.</param>
            public sealed record Entry(string Name, int Size);

            /// <summary>A class with a primary constructor.</summary>
            /// <param name="count">Which is a parameter of the type.</param>
            public class Holder(int count) {
                /// <summary>An indexer, whose parameters are in brackets.</summary>
                /// <param name="index">The one.</param>
                /// <returns>The value.</returns>
                public int this[int index] => index + count + this[0];

                /// <summary>Equality, whose parameter list follows an `=`.</summary>
                /// <param name="left">One.</param>
                /// <param name="right">The other.</param>
                /// <returns>Whether they are equal.</returns>
                public static bool operator ==(Holder left, Holder right) => ReferenceEquals(left, right);

                /// <summary>Inequality.</summary>
                /// <param name="left">One.</param>
                /// <param name="right">The other.</param>
                /// <returns>Whether they differ.</returns>
                public static bool operator !=(Holder left, Holder right) => !(left == right);

                /// <summary>A named tuple return, whose first paren is not the parameter list.</summary>
                /// <param name="pattern">The pattern.</param>
                /// <param name="width">How wide.</param>
                /// <returns>Two numbers.</returns>
                public static (double Below, double Above) Coverage(int pattern, double width) => (pattern, width);

                /// <summary>A generic method with a constraint and a default.</summary>
                /// <typeparam name="T">The element.</typeparam>
                /// <param name="items">The items.</param>
                /// <param name="seed">Where to start, defaulting to a call with a comma in it.</param>
                /// <returns>Nothing in particular.</returns>
                public static int Shuffle<T>(IReadOnlyList<T> items, Holder seed = null!) where T : notnull => 0;

                /// <summary>
                ///     A summary whose remarks contain an example of one.
                /// </summary>
                /// <remarks>
                ///     <code>
                ///     /// &lt;summary&gt;Like this.&lt;/summary&gt;
                ///     </code>
                /// </remarks>
                public void Example() {
                    /// <summary>A local function is a member here too.</summary>
                    /// <param name="value">Its parameter.</param>
                    static int Inner(int value) => value;

                    Inner(0);
                }
            }
            """;

        Assert.Equal([], DocCommentRule.Check("Clean.cs", clean).Select(finding => finding.ToString()).ToArray());
    }

    /// <summary>
    ///     ⚠ The rule's three checks each fail on their own, and each on a file the others call clean.
    /// </summary>
    /// <remarks>
    ///     <b>A predicate with no false case is worse than the gap it filled.</b> The two fixtures
    ///     above trip several checks at once, which is what a real staple does and also what would
    ///     hide a check that had stopped working. These are one defect each.
    /// </remarks>
    [Theory]
    [InlineData("/// <summary>One.</summary>\n/// <summary>Two.</summary>\npublic void M() { }", "<summary>")]
    [InlineData("/// <returns>One.</returns>\n/// <returns>Two.</returns>\npublic int M() => 0;", "<returns>")]
    [InlineData("/// <param name=\"a\">One.</param>\n/// <param name=\"a\">Again.</param>\npublic void M(int a) { }", "`a` 2 times")]
    [InlineData("/// <param name=\"b\">Not a parameter.</param>\npublic void M(int a) { }", "`b`")]
    [InlineData("/// <param name=\"b\">Not a parameter.</param>\npublic int Value => 0;", "takes no parameters at all")]
    public void Each_check_fails_on_its_own(string member, string expected) {
        var findings = DocCommentRule.Check("One.cs", "class Fixture {\n" + member + "\n}");

        Assert.Single(findings);
        Assert.Contains(expected, findings[0].Message, StringComparison.Ordinal);
    }
}
