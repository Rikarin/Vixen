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
    ///         was a live production staple</b>: <c>KeyChord.cs</c> carried <c>MacFormat</c>' whole
    ///         block above <c>MacWords</c>, so one public formatter was undocumented and the other was
    ///         described twice. All sixty-four blocks have since been moved onto the member they
    ///         describe and <c>docs/DocCommentExempt.txt</c> is empty (#879).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Emptying it took an instrument with it, which is why the fixture check above is
    ///         no longer a belt-and-braces line.</b> The exemption list used to be the strongest
    ///         evidence here — every file on it is one this run has to have flagged, so a clean sweep
    ///         with a non-empty list proved the checks had stopped firing rather than that the tree
    ///         was clean. An empty list cannot say that, and <c>Assert.NotEmpty(findings)</c> now
    ///         asserts the tree is <em>dirty</em>. What is left is the rule firing on a stapled
    ///         fixture in this process, which is the claim that was always the load-bearing one.
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

        // The instrument, and since #879 it is the only one: the rule fires on a file that is wrong,
        // right now, in this process. A clean sweep below is a measurement only while this is true.
        Assert.NotEmpty(DocCommentRule.Check("fixture.cs", StapledOntoResolve));

        var findings = sources
            .SelectMany(file => DocCommentRule.Check(file[(root.Length + 1)..], File.ReadAllText(file)))
            .ToList();

        var exempt = DocCommentRule.Exemptions(root);
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
    ///     <c>GenerateDocumentationFile</c>, off for this whole profile at the time, was not there
    ///     to report. ⚠ It is on now (#1218), so CS1572 would catch this fixture's second half in a
    ///     real project; the duplicated <c>&lt;summary&gt;</c> above it, still, nothing but this rule
    ///     would.
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
                    // ⚠ A `//` comment, because a `///` block here is CS1587 and would be a finding.
                    // This fixture asserted the opposite until #1219: a `///` block on a local
                    // function sat in the "shapes that are not defects" list, which is exactly the
                    // claim that issue refuted.
                    static int Inner(int value) => value;

                    Inner(0);
                }
            }
            """;

        Assert.Equal([], DocCommentRule.Check("Clean.cs", clean).Select(finding => finding.ToString()).ToArray());
    }

    /// <summary>
    ///     ⚠ <a href="https://github.com/Rikarin/Vixen/issues/1219">#1219</a>: a <c>///</c> block on
    ///     a local function is CS1587, and both of the ones this repository had are here.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Verbatim from <c>cf46fa247^</c>, and both were carrying a real argument.</b>
    ///         <c>SubGraphs.Held</c> explained what the local function answers; <c>Consolidate</c>'s
    ///         eleven lines explained why 5 500 fixtures are carried verbatim rather than translated.
    ///         The compiler threw both away, and the only reason anybody found out is that
    ///         <c>GenerateDocumentationFile</c> was switched on for those two projects.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Consolidate</c> is in a top-level-statements file, where <em>every</em>
    ///         function is a local function.</b> That is the shape a <c>dotnet new</c> template is
    ///         made of, and <c>Tools/Vixen.Templates/templates/**</c> is outside #821's ratchet — so
    ///         the projects most likely to hold this defect are the ones the compiler warning can
    ///         never reach, which is the argument for asking the question here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_doc_comment_on_a_local_function_is_reported() {
        const string flattener = """
            static class SubGraphs {
                static void Flatten() {
                    /// <summary>The constant behind a wire that runs back to an entry port nobody fed.</summary>
                    float[]? Held(PortRef upstream) =>
                        upstream.Node == entry && constants.TryGetValue(upstream.Port, out var value) ? value : null;
                }
            }
            """;

        const string topLevel = """
            Run();

            /// <summary>
            ///     One file per category, each fixture's XML embedded verbatim.
            /// </summary>
            /// <remarks>
            ///     ⚠ <b>Verbatim, and consolidated, are both deliberate.</b> Taffy's fixtures are
            ///     already language-neutral, so the honest move is to carry them unchanged.
            /// </remarks>
            static string Consolidate(string category, string version) => category + version;
            """;

        foreach (var (name, text) in new[] { ("SubGraphs.cs", flattener), ("Program.cs", topLevel) }) {
            var findings = DocCommentRule.Check(name, text);

            Assert.Single(findings);
            Assert.Contains("local function", findings[0].Message, StringComparison.Ordinal);
            Assert.Contains("CS1587", findings[0].Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     ⚠ The rule's four checks each fail on their own, and each on a file the others call clean.
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
    [InlineData("public void M() {\n/// <summary>Discarded.</summary>\nstatic int Inner() => 0;\nInner();\n}", "local function `Inner`")]
    [InlineData("/// <summary>A warning: \\u26a0.</summary>\npublic void M() { }", "the escape `\\u26a0` as prose")]
    public void Each_check_fails_on_its_own(string member, string expected) {
        var findings = DocCommentRule.Check("One.cs", "class Fixture {\n" + member + "\n}");

        Assert.Single(findings);
        Assert.Contains(expected, findings[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>TransformedText.cs</c>'s casing remarks as they stood before
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1347">#1347</a>, the ten escaped lines
    ///     with their members.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Written with every backslash doubled and halved at run time, and that is the defect's
    ///     own mechanism rather than a style.</b> The issue that filed these had its escapes resolved
    ///     into the characters they name twice on the way to the tracker, and the same happens to a
    ///     fixture typed through any tool that decodes JSON-style escapes: the file would then hold
    ///     the fixed text, and this test would be asserting the rule is silent on a file that was
    ///     never wrong. A doubled backslash survives that trip.
    /// </remarks>
    static readonly string EscapedCasingRemarks = """
        namespace Fixture;

        static class TransformedText {
            /// <summary>Whether a sigma at an offset is the last letter of its word.</summary>
            /// <param name="source">The untransformed text.</param>
            /// <returns>Whether it lowercases to \\u03c2 rather than to \\u03c3.</returns>
            /// <remarks>
            ///     <para>
            ///         terms. Both halves are needed and the second is the one an implementation forgets \\u2014
            ///         without it <c>\\u039f\\u0394\\u039f\\u03a3 \\u039c\\u039f\\u03a5</c> would end its first word correctly and <c>\\u03a3\\u039f\\u03a6\\u039f\\u03a3</c> would
            ///     </para>
            ///     <para>
            ///         \\u26a0 <b>Read against the source and not against what has been written so far.</b> The
            ///     </para>
            ///     <para>
            ///         \\u26a0 <b>No <c>CultureInfo</c> here either.</b> <c>Cased</c> and <c>Case_Ignorable</c>
            ///         both of which are the same on every machine \\u2014 see the remarks on
            ///     </para>
            /// </remarks>
            static bool IsFinalSigma(string source) => false;

            /// <summary>The <c>Cased</c> derived property.</summary>
            /// <remarks>
            ///     Uppercase, lowercase or titlecase. \\u26a0 <b>Titlecase is the third one and is a real
            ///     category</b> \\u2014 <c>\\u01c5</c> is neither <c>Lu</c> nor <c>Ll</c>, so a test written as
            /// </remarks>
            static bool IsCased(int rune) => false;

            /// <summary>The <c>Case_Ignorable</c> derived property.</summary>
            /// <remarks>
            ///     \\u26a0 <b>Five categories <i>and</i> three word-break classes</b>, which is DerivedCoreProperties'
            ///     <c>\\u039c.\\u039f.\\u03a3.</c> and an apostrophe inside a word behave: a full stop between two letters is
            /// </remarks>
            static bool IsCaseIgnorable(int rune) => false;

            /// <summary>LATIN CAPITAL LETTER I WITH DOT ABOVE, U+0130.</summary>
            const string DottedCapitalI = "\\u0130";
        }
        """.Replace(@"\\", @"\", StringComparison.Ordinal);

    /// <summary>
    ///     ⚠ <a href="https://github.com/Rikarin/Vixen/issues/1347">#1347</a>: every escaped line of
    ///     the casing remarks is a finding on its own line, and the string literal beneath them is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Ten lines, not twenty-five escapes, is the count that says the positions are
    ///         right.</b> The rule reports each escape; grouping by line is what the issue counted and
    ///         what a reader fixes, and a rule reporting every escape at the block's first line would
    ///         give one line where this asserts ten.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>const string</c> at the bottom is the other half.</b> An escape there is the
    ///         correct spelling — the issue warned that a blanket sweep of the file would break exactly
    ///         those — so a rule that read the whole text rather than the documentation trivia would
    ///         report an eleventh line.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Each_escaped_line_of_the_casing_remarks_is_reported() {
        var findings = DocCommentRule.Check("TransformedText.cs", EscapedCasingRemarks);
        var lines = EscapedCasingRemarks.Split('\n');
        var escaped = Enumerable
            .Range(1, lines.Length)
            .Where(line => lines[line - 1].TrimStart().StartsWith("///", StringComparison.Ordinal) && lines[line - 1].Contains('\\'))
            .ToArray();

        Assert.Equal(10, escaped.Length);
        Assert.Equal(escaped, findings.Select(finding => finding.Line).Distinct().ToArray());
        Assert.All(findings, finding => Assert.Contains("as prose", finding.Message, StringComparison.Ordinal));
        Assert.Equal(25, findings.Count);

        // And the same text written the way #1347 fixed it: the characters, not their escapes.
        var fixedText = string.Join(
            '\n',
            lines.Select(line => line.TrimStart().StartsWith("///", StringComparison.Ordinal) ? Unescape(line) : line)
        );

        Assert.Equal([], DocCommentRule.Check("TransformedText.cs", fixedText).Select(finding => finding.ToString()).ToArray());
    }

    /// <summary>Resolves every backslash-u escape in a line into the character it names.</summary>
    /// <param name="line">One line of a fixture.</param>
    /// <returns>The line with its escapes written as characters.</returns>
    static string Unescape(string line) =>
        System.Text.RegularExpressions.Regex.Replace(
            line,
            @"\\u([0-9a-fA-F]{4})",
            match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString()
        );

    /// <summary>
    ///     ⚠ An escape the prose means as an escape is left alone.
    /// </summary>
    /// <remarks>
    ///     A <c>&lt;code&gt;</c> sample is source, where the escape is how the character is written; a
    ///     doubled backslash describes a literal whose value is the backslash; an attribute is read by
    ///     the compiler, not drawn; and a backslash-u with fewer than four hex digits after it is not
    ///     an escape at all. A rule that flagged these would be asking a sample to be wrong.
    /// </remarks>
    [Fact]
    public void An_escape_the_prose_means_as_an_escape_is_left_alone() {
        var sample = """
            class Fixture {
                /// <summary>Writes a sign.</summary>
                /// <remarks>
                ///     <code>
                ///     var sign = "\\u26a0";
                ///     </code>
                ///     The value of <c>"\\\\u26a0"</c> is six characters, and <c>\\uffz</c> is no escape.
                /// </remarks>
                /// <param name="x">See <see cref="M(string)" /> and <a href="\\u26a0">this</a>.</param>
                public void M(string x) { }
            }
            """.Replace(@"\\", @"\", StringComparison.Ordinal);

        Assert.Contains(@"\u26a0""", sample, StringComparison.Ordinal);
        Assert.Equal([], DocCommentRule.Check("Sample.cs", sample).Select(finding => finding.ToString()).ToArray());
    }
}
