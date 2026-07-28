// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Syntax.Text;
using Xunit;

namespace Vixen.Ui.Markup.Tests;

/// <summary>
///     Reparsing an edit by keeping the parts of the previous tree the edit did not touch.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracle is a full reparse, and it is the only assertion that matters.</b> Reuse is
///         a pure optimisation or it is a bug, and the only way to know which is to have something
///         that does the work every time and compare. Everything else here — the counter, the
///         round trip — says reuse <i>happened</i>; this says it was allowed to.
///     </para>
///     <para>
///         ⚠ <b>VXML's unit of reuse is a content node whose subtree reported nothing</b>, which is
///         the question that was written down as open when the shared <c>Blender</c> was built. The
///         worry was that an element is reusable only if nothing about its <i>enclosing</i> content
///         changed. That is true of where the node ends up and false of the node itself: the parser's
///         one piece of enclosing state is the list of elements currently open, every branch that
///         reads it reports a diagnostic, and so a subtree that parsed cleanly never consulted it.
///     </para>
/// </remarks>
public class IncrementalTests {
    const string Source = """
        @component Counter
        @using System
        <panel class="root">
            <label text="one" />
            <label text="two" />
            @if (count > 0) {
                <badge />
            }
        </panel>
        <footer>
            <label text="three" />
        </footer>
        """;

    [Fact]
    public void An_edit_reuses_the_elements_it_did_not_touch() {
        var before = Vxml.Parse(Source);
        var after = Edit(before, Source.IndexOf("one", StringComparison.Ordinal), 3, "ONE");

        Assert.True(after.ReusedNodes > 0, "nothing was reused");
        AssertSameAsFresh(after);
    }

    /// <summary>
    ///     ⚠ The change ranges answer exactly only for the text they were derived from. A caller who
    ///     hands over a <c>SourceText</c> built from a string rather than from the previous one gets
    ///     a conservative whole-document change, and therefore a full reparse — correct, and silently
    ///     not incremental, which is why the counter exists to say which happened.
    /// </summary>
    [Fact]
    public void Text_that_did_not_come_from_the_previous_text_reparses_whole() {
        var before = Vxml.Parse(Source);
        var unrelated = SourceText.From(Source.Replace("one", "ONE", StringComparison.Ordinal));

        Assert.Equal(0, before.WithChangedText(unrelated).ReusedNodes);
    }

    [Fact]
    public void A_fresh_parse_reuses_nothing() => Assert.Equal(0, Vxml.Parse(Source).ReusedNodes);

    [Fact]
    public void An_unchanged_text_returns_the_same_tree() {
        var before = Vxml.Parse(Source);
        Assert.Same(before, before.WithChangedText(before.Text!));
    }

    /// <summary>
    ///     ⚠ The whole file is re-lexed even though nodes are reused, and it has to be. VXML's lexer
    ///     carries a mode stack, so a change above a node decides what its text lexes <i>as</i> — and
    ///     a reused node spliced over a stale token stream is a tree that no longer reproduces its
    ///     file.
    /// </summary>
    [Fact]
    public void An_edit_that_opens_a_block_still_reparses_correctly() {
        var before = Vxml.Parse(Source);

        // A `@code` block swallows everything to its closing brace, so this changes what the text
        // after it *is*, not merely where it sits.
        AssertSameAsFresh(
            Edit(before, Source.IndexOf("<footer>", StringComparison.Ordinal), 0, "@code { var x = 1; }\n")
        );
    }

    /// <summary>
    ///     ⚠ A subtree that reported something is never offered, because the parse that produced it
    ///     consulted the enclosing state — and because its diagnostics would be lost, a reused node
    ///     not being re-parsed means not being re-reported.
    /// </summary>
    [Fact]
    public void A_broken_element_is_not_reused_and_keeps_its_diagnostic() {
        const string Broken = """
            @component Counter
            <panel>
                <label text="one" />
            </wrong>
            <footer />
            """;

        var before = Vxml.Parse(Broken);
        Assert.NotEmpty(before.Diagnostics);

        var after = Edit(before, Broken.IndexOf("<footer />", StringComparison.Ordinal), 10, "<footer/>");

        AssertSameAsFresh(after);
        Assert.Equal(
            before.Diagnostics.Select(one => one.Descriptor.Id),
            after.Diagnostics.Select(one => one.Descriptor.Id)
        );
    }

    /// <summary>
    ///     ⚠ <b>Untouched characters are not unchanged tokens.</b> The edit here leaves every
    ///     character of <c>&lt;panel&gt;</c> and everything under it exactly where it was, and still
    ///     none of it may be reused: the <c>&lt;/</c> in the middle of the <c>@using</c> name opens
    ///     a tag the file never closes, so the lexer reads the rest of the document in tag mode and
    ///     <c>&lt;panel class="root"&gt;</c> stops being a start tag at all.
    /// </summary>
    /// <remarks>
    ///     The property below found this by chance on one seed out of thousands, which is exactly
    ///     the argument for writing it down as a case of its own. The blender used to decide reuse
    ///     from spans alone — untouched text, one character of margin, ends on a token boundary —
    ///     and all three were true here while the tokens underneath were completely different.
    /// </remarks>
    [Fact]
    public void An_edit_that_changes_how_the_text_below_it_lexes_reuses_none_of_it() {
        var at = Source.IndexOf("System", StringComparison.Ordinal);
        var edited = Source[..at] + "c</}} @" + Source[(at + 2)..];

        var after = Edit(Vxml.Parse(Source), at, 2, "c</}} @");

        Assert.Equal(edited, after.GetDocument().ToFullString());
        Assert.Equal(Vxml.Dump(Vxml.Parse(edited).GetDocument()), Vxml.Dump(after.GetDocument()));
    }

    /// <summary>
    ///     ⚠ <b>A node whose own span no diagnostic touches can still be a node that went wrong.</b>
    ///     The <c>&lt;style</c> here never closes its tag: the <c>"</c> opens an attribute value that
    ///     swallows the rest of the file, so the parser reports the missing <c>&gt;</c> at end of
    ///     file — nowhere near the handful of characters the node ended up covering. An edit at the
    ///     top of the file then shifts it into place, and a "nothing was reported here" test that
    ///     looks only at spans says it is fine to reuse.
    /// </summary>
    [Fact]
    public void A_node_whose_diagnostic_landed_elsewhere_is_not_reused() {
        const string Broken = """
            @component Counter
            <panel>
                <label text="one" />
                <style"
            </panel>
            """;

        var before = Vxml.Parse(Broken);
        var at = Broken.IndexOf("Counter", StringComparison.Ordinal);
        var edited = Broken[..at] + "C" + Broken[(at + 7)..];

        var after = Edit(before, at, 7, "C");

        Assert.Equal(edited, after.GetDocument().ToFullString());
        Assert.Equal(Vxml.Dump(Vxml.Parse(edited).GetDocument()), Vxml.Dump(after.GetDocument()));
    }

    /// <summary>
    ///     The property that holds whatever the edit: an incremental reparse and a full one produce
    ///     the same tree, character for character and node for node.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The alphabet is the test.</b> Every character it cannot type is a lexer path this
    ///     never reaches, and the property is only as strong as the shapes it can spell — an
    ///     unbalanced <c>@(</c> dropped the rest of the file for as long as <c>(</c> was missing
    ///     from this string. Anything the grammar treats specially belongs here.
    /// </remarks>
    [Fact]
    public void An_incremental_reparse_equals_a_full_one() {
        var generator =
            from at in Gen.Int[0, Source.Length]
            from removed in Gen.Int[0, 12]
            from inserted in Gen.String[Gen.Char["<>/ \nabc=\"@{}()[]!.:"], 0, 10]
            select (at, removed, inserted);

        generator.Sample(
            edit => {
                var start = Math.Min(edit.at, Source.Length);
                var length = Math.Min(edit.removed, Source.Length - start);
                var edited = Source[..start] + edit.inserted + Source[(start + length)..];

                var before = Vxml.Parse(Source);
                var incremental = Edit(before, start, length, edit.inserted);
                var fresh = Vxml.Parse(edited);

                return Vxml.Dump(incremental.GetDocument()) == Vxml.Dump(fresh.GetDocument())
                    && incremental.GetDocument().ToFullString() == edited;
            },
            iter: 2_000
        );
    }

    /// <summary>
    ///     ⚠ Several edits in a row, each against the tree the last one produced — which is the shape
    ///     a keystroke actually arrives in, and the one where a position shifted by the wrong delta
    ///     accumulates instead of showing up once.
    /// </summary>
    [Fact]
    public void A_run_of_edits_stays_equal_to_a_full_reparse() {
        var text = Source;
        var tree = Vxml.Parse(text);
        var reused = 0;

        foreach (var word in new[] { "one", "two", "three", "root" }) {
            var at = text.IndexOf(word, StringComparison.Ordinal);
            tree = Edit(tree, at, word.Length, word.ToUpperInvariant());
            text = text[..at] + word.ToUpperInvariant() + text[(at + word.Length)..];
            reused += tree.ReusedNodes;

            Assert.Equal(Vxml.Dump(Vxml.Parse(text).GetDocument()), Vxml.Dump(tree.GetDocument()));
            Assert.Equal(text, tree.GetDocument().ToFullString());
        }

        Assert.True(reused > 0, "four edits in a row and nothing was ever reused");
    }

    /// <summary>Applies one edit to a tree's own text, which is what keeps the change ranges exact.</summary>
    static Markup.Syntax.SyntaxTree Edit(Markup.Syntax.SyntaxTree before, int start, int length, string inserted) =>
        before.WithChangedText(before.Text!.WithChanges(new TextChange(new TextSpan(start, length), inserted)));

    static void AssertSameAsFresh(Markup.Syntax.SyntaxTree incremental) {
        var text = incremental.GetDocument().ToFullString();
        Assert.Equal(Vxml.Dump(Vxml.Parse(text).GetDocument()), Vxml.Dump(incremental.GetDocument()));
    }
}
