// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Composition;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     <c>Hello &lt;b&gt;world&lt;/b&gt; again</c> in a block container: one line, and the middle
///     stretch is the one in the bold face.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Rich text here is not a run list with styled spans in it, and this file is the pin
///         on the mechanism it is instead.</b> Markup already emits one <c>text</c> element per
///         literal stretch (<c>BuildContext.Text</c>) and a nested tag as an element of its own with
///         its own cascade, so "which stretch is bold" was answered the day <c>font-weight</c>
///         cascaded — what nobody had said was which of those elements is <i>inline</i>. Before the
///         <c>base</c> layer of <c>ControlTheme.vcss</c> said so, no stylesheet in the tree set
///         <c>display: inline</c> at all, and three block boxes in a block container are three lines.
///     </para>
///     <para>
///         ⚠ <b>The fixture is two distinct faces at two weights under one family, and both halves
///         are load-bearing.</b> One face registered at both weights draws identical runs either way,
///         and every assertion about boldness would pass against a cascade that never read the
///         property; and with no <c>font-family</c> named, <c>FontRegistry.Resolve</c> answers the
///         default face for every weight, so the container names the family. The faces are the same
///         bytes under two names — what is under test is whether the weight reached the registry and
///         picked the variant registered under it, not whether the glyphs are heavier.
///     </para>
///     <para>
///         ⚠ <b>A block container, deliberately, because in the flex container this store defaults
///         to, <c>display: inline</c> is a no-op.</b> A flex item, a grid item, a float, an absolutely
///         positioned box and the root are blockified — <c>BlockificationTests</c> measures that the
///         transformation moves no geometry here — so this default changes nothing about any existing
///         panel, which is what made it a default rather than a decision.
///     </para>
/// </remarks>
public class PhraseTagDefaultTests {
    const float Tolerance = 0.01f;

    static readonly FontFace Regular = LoadFont("regular");
    static readonly FontFace Bold = LoadFont("bold");

    static FontFace LoadFont(string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>A block paragraph holding three stretches, the middle one under a phrase tag.</summary>
    static (ControlFixture Fixture, UiElement Before, UiElement Tagged, UiElement Inner, UiElement After) Paragraph(
        string tag,
        string css = ""
    ) {
        var fixture = new ControlFixture(css: $"#p {{ display: block; width: 400px; font-family: Phrase; }} {css}");
        fixture.Document.Fonts.Register("Phrase", Regular, 400);
        fixture.Document.Fonts.Register("Phrase", Bold, 700);

        var paragraph = fixture.Document.Root.Add("div", "p");

        var before = paragraph.Add("text");
        before.Text = "Hello ";

        var tagged = paragraph.Add(tag);
        var inner = tagged.Add("text");
        inner.Text = "world";

        var after = paragraph.Add("text");
        after.Text = " again";

        fixture.Update();

        return (fixture, before, tagged, inner, after);
    }

    static FontFace FaceOf(UiElement element) {
        var lines = element.Block()!.Lines;
        var line = Assert.Single(lines);
        var run = Assert.Single(line.Runs.ToArray());

        return run.Font;
    }

    /// <summary>The three stretches share one line, each starting where the last ended.</summary>
    [Theory]
    [InlineData("b")]
    [InlineData("strong")]
    [InlineData("i")]
    [InlineData("em")]
    [InlineData("span")]
    public void A_tagged_stretch_shares_the_line_with_the_text_around_it(string tag) {
        var built = Paragraph(tag);
        using var fixture = built.Fixture;
        var (before, tagged, after) = (built.Before, built.Tagged, built.After);

        var first = before.Bounds;
        var middle = tagged.Bounds;
        var last = after.Bounds;

        // The fixture has to have measured something, or three empty boxes agree about everything.
        Assert.True(first.Width > 0f && middle.Width > 0f && last.Width > 0f, $"widths {first.Width}, {middle.Width}, {last.Width}");

        Assert.Equal(first.Y, middle.Y, Tolerance);
        Assert.Equal(first.Y, last.Y, Tolerance);
        Assert.Equal(first.X + first.Width, middle.X, Tolerance);
        Assert.Equal(middle.X + middle.Width, last.X, Tolerance);
    }

    /// <summary>The bold stretch, and only the bold stretch, draws in the face registered at 700.</summary>
    [Theory]
    [InlineData("b")]
    [InlineData("strong")]
    public void The_bold_stretch_is_the_one_in_the_bold_face(string tag) {
        var built = Paragraph(tag);
        using var fixture = built.Fixture;
        var (before, inner, after) = (built.Before, built.Inner, built.After);

        Assert.Same(Bold, FaceOf(inner));
        Assert.Same(Regular, FaceOf(before));
        Assert.Same(Regular, FaceOf(after));
    }

    /// <summary>
    ///     <c>i</c> and <c>em</c> ask for the italic, which this family does not have — so the
    ///     upright is what CSS Fonts 4 § 5.2's search ends on, and the weight is untouched.
    /// </summary>
    /// <remarks>
    ///     The half that can be asserted without an italic font in the repository: an italic tag does
    ///     not embolden. <c>FontSlantPixelTests</c> shows the slant reaching the registry.
    /// </remarks>
    [Theory]
    [InlineData("i")]
    [InlineData("em")]
    public void An_italic_stretch_keeps_the_regular_weight(string tag) {
        var built = Paragraph(tag);
        using var fixture = built.Fixture;
        var inner = built.Inner;

        Assert.Same(Regular, FaceOf(inner));
    }

    /// <summary>
    ///     The same thing from a <c>.vxml</c>: eleven stretches on one line, and the two under
    ///     <c>b</c> and <c>strong</c> are the ones in the bold face.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the test the issue asked for — "which stretch is bold" from markup — and it
    ///     goes through the compiler rather than through <c>Add</c>.</b> The three tests above pin
    ///     the mechanism; this one pins that the markup an author writes reaches it: the literal
    ///     stretches arrive as <c>text</c> elements (with the spaces the whitespace policy keeps), the
    ///     tags arrive as elements of their own, and the theme's defaults do the rest. The count is
    ///     asserted so that a compiler which dropped a stretch or merged two could not pass by having
    ///     fewer boxes to line up.
    /// </remarks>
    [Fact]
    public void From_markup_every_stretch_shares_the_line_and_the_bold_ones_are_bold() {
        using var fixture = new ControlFixture(css: "phrase-paragraph { display: block; width: 780px; font-family: Phrase; }");
        fixture.Document.Fonts.Register("Phrase", Regular, 400);
        fixture.Document.Fonts.Register("Phrase", Bold, 700);

        var sheet = new PhraseSheet();
        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        var stretches = sheet.Paragraph.Children.ToArray();

        Assert.Equal(
            ["text", "b", "text", "strong", "text", "i", "text", "em", "text", "span", "text"],
            stretches.Select(static stretch => stretch.Tag)
        );

        var top = stretches[0].Bounds.Y;
        var pen = stretches[0].Bounds.X;

        foreach (var stretch in stretches) {
            var bounds = stretch.Bounds;

            Assert.True(bounds.Width > 0f, $"<{stretch.Tag}> measured no width");
            Assert.Equal(top, bounds.Y, Tolerance);
            Assert.Equal(pen, bounds.X, Tolerance);

            pen = bounds.X + bounds.Width;
        }

        // The line fits, or the assertions above were about a wrap rather than a flow.
        Assert.True(pen <= 780f, $"the line ran to {pen}");

        foreach (var stretch in stretches) {
            var inner = stretch.Tag == "text" ? stretch : Assert.Single(stretch.Children);
            var expected = stretch.Tag is "b" or "strong" ? Bold : Regular;

            Assert.Same(expected, FaceOf(inner));
        }
    }

    /// <summary>
    ///     The same paragraph in a flex container stacks its stretches as it always did, because a
    ///     flex item is blockified whatever its <c>display</c> says.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the regression half: the default is only safe because it is inert everywhere a
    ///     panel in the tree puts text today, and a store that stopped blockifying would move every
    ///     one of them. Column direction, so that the stacking is the flex algorithm's and not a
    ///     coincidence of the row it would otherwise share.
    /// </remarks>
    [Fact]
    public void In_a_flex_column_the_stretches_still_stack() {
        var built = Paragraph("b", "#p { display: flex; flex-direction: column; }");
        using var fixture = built.Fixture;
        var (before, tagged, after) = (built.Before, built.Tagged, built.After);

        Assert.True(before.Bounds.Y < tagged.Bounds.Y, $"{before.Bounds.Y} then {tagged.Bounds.Y}");
        Assert.True(tagged.Bounds.Y < after.Bounds.Y, $"{tagged.Bounds.Y} then {after.Bounds.Y}");
        Assert.Equal(before.Bounds.X, tagged.Bounds.X, Tolerance);
    }
}
