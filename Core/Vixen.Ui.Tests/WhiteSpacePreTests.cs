// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     <c>white-space: pre</c> stops the wrapping, and the refusal that said it must not was
///     arithmetic about this engine that had stopped being true.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The refusal, verbatim, and why it fails.</b> <c>UiDocument.WrapsOf</c> read:
///         "<c>white-space</c> conflates three questions — whether to collapse runs of space, whether
///         to keep newlines, and whether to wrap — and only the third is answered here…so honouring
///         <c>pre</c> for wrapping alone would be honouring a third of it." Every clause of the
///         premise is true. The fraction is not, and it is the fraction the conclusion rests on: this
///         engine collapses nothing and already breaks at every mandatory opportunity, so an element
///         with no <c>white-space</c> declaration at all is already behaving as <c>pre-wrap</c>.
///         <c>pre</c> and <c>pre-wrap</c> differ in exactly one respect — wrapping — so the third
///         being honoured here is the only third that was ever missing.
///     </para>
///     <para>
///         ⚠ <b>So the first two tests below are the load-bearing ones and they assert the ENGINE
///         rather than the property.</b> They would have passed before this change and they are what
///         says the third one is a completion rather than a third of one; if either ever goes red —
///         the day CSS Text § 4's collapsing lands, which is the rest of what <c>white-space</c>
///         owes — then <c>pre</c> is back to being a fraction and this file has to be re-read rather
///         than re-run.
///     </para>
///     <para>
///         ⚠ <b>Two things in the tree write it and both are stack traces.</b>
///         <c>EditorTheme.vcss</c> gives <c>console-detail-stack</c> and <c>message-detail-body</c>
///         <c>white-space: pre</c>; <c>ConsoleView</c> fills the first with
///         <c>exception.ToString()</c>. Until this change a stack trace in a narrow panel wrapped
///         mid-frame, which is the one thing the declaration is written to prevent.
///     </para>
/// </remarks>
public class WhiteSpacePreTests {
    const float Tolerance = 0.05f;
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>A narrow box, so anything that may wrap does.</summary>
    static TextLayout Block(string text, string label) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: 60px; height: 300px; align-items: flex-start; }
              label { font-family: Test; font-size: 16px; {{label}} }
              """
        );

        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        var block = element.Block();
        Assert.NotNull(block);

        return block;
    }

    /// <summary>
    ///     The first half of the premise: a run of spaces survives, so this engine's collapsing is
    ///     already <c>preserve</c>.
    /// </summary>
    /// <remarks>
    ///     Measured rather than asserted from the source. Four spaces between two letters make a line
    ///     wider than one space does by three spaces' advance; a store that collapsed would report the
    ///     same width for both, which is the answer CSS gives for <c>white-space: normal</c> and the
    ///     answer this engine does not give.
    /// </remarks>
    [Fact]
    public void Nothing_collapses_a_run_of_spaces_so_the_default_is_already_preserve() {
        var one = Block("a b", "white-space: normal;").Lines[0].Width;
        var four = Block("a    b", "white-space: normal;").Lines[0].Width;
        var space = Block("a  b", "white-space: normal;").Lines[0].Width - one;

        Assert.True(space > 0f, "a second space has to widen the line for this measurement to mean anything");
        Assert.Equal(one + (3f * space), four, Tolerance);
    }

    /// <summary>
    ///     The second half: a newline breaks the line under <c>normal</c>, so newlines are already
    ///     kept.
    /// </summary>
    /// <remarks>
    ///     The same two words with a space and with a line feed. CSS's <c>normal</c> makes both one
    ///     line; this engine makes the second two, which is <c>pre-wrap</c>'s answer.
    /// </remarks>
    [Fact]
    public void A_newline_already_breaks_a_line_under_normal() {
        Assert.Single(Block("a b", "white-space: normal;").Lines);
        Assert.Equal(2, Block("a\nb", "white-space: normal;").Lines.Length);
    }

    /// <summary>
    ///     And the conclusion: <c>pre</c> does not wrap, while <c>pre-wrap</c> and <c>normal</c> do.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Three values in one assertion because the point is the difference between them.</b>
    ///     A test that only said "<c>pre</c> gives one line" would be satisfied by a box wide enough,
    ///     by a wrapper that had stopped working, or by text with no break opportunity in it. The
    ///     same string in the same 60-point box under <c>normal</c> and <c>pre-wrap</c> has to give
    ///     more than one line for the <c>pre</c> row to mean anything.
    /// </remarks>
    [Fact]
    public void Pre_does_not_wrap_where_normal_and_pre_wrap_do() {
        const string Text = "alpha beta gamma delta";

        Assert.True(Block(Text, "white-space: normal;").Lines.Length > 1);
        Assert.True(Block(Text, "white-space: pre-wrap;").Lines.Length > 1);
        Assert.Single(Block(Text, "white-space: pre;").Lines);
    }

    /// <summary>
    ///     A <c>pre</c> element still breaks at its newlines, which is the whole of what a stack
    ///     trace wants.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes "does not wrap" safe here.</b> Not wrapping is an infinite
    ///     available width, and a mandatory break does not consult the width — but an implementation
    ///     that reached the same "one line" answer by suppressing the line walk would satisfy
    ///     <see cref="Pre_does_not_wrap_where_normal_and_pre_wrap_do" /> and would put the editor's
    ///     whole stack trace on one line. Three lines of four words each: exactly three under
    ///     <c>pre</c>, and more than three under anything that wraps — which is what the sabotage
    ///     reported, so both halves of this assertion are load-bearing.
    /// </remarks>
    [Fact]
    public void Pre_keeps_its_newlines_while_refusing_to_wrap() {
        var block = Block(
            "alpha beta gamma delta\nepsilon zeta eta theta\niota kappa lambda mu",
            "white-space: pre;"
        );

        Assert.Equal(3, block.Lines.Length);
    }

    /// <summary>
    ///     <c>nowrap</c> is unchanged, so the new clause did not widen into the value beside it.
    /// </summary>
    [Fact]
    public void Nowrap_still_does_not_wrap() =>
        Assert.Single(Block("alpha beta gamma delta", "white-space: nowrap;").Lines);
}
