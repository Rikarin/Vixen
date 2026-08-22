// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Text that does not fit, and the ellipsis that says so.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is about glyphs that were drawn, and that is the whole point of
///         the file.</b> The defect this repository keeps finding is a property that resolves and
///         paints nothing — an unread diagnostics list, an inert <c>@apply</c>, four <c>place-*</c>
///         shorthands that parse and never reach the layout. A test that asserted
///         <c>StyleOf(element, "text-overflow") == "ellipsis"</c> would have passed on the day this
///         feature was one interned string and no reader, so no test here asks the cascade anything.
///     </para>
///     <para>
///         The two halves that make an ellipsis real are asserted separately, because each fails on
///         its own: the line has to <i>fit</i> (otherwise it is the old clip with extra steps) and it
///         has to <i>end in U+2026</i> (otherwise it is just a shorter clip). Sabotage confirms it —
///         returning the untruncated block fails the width assertions, and cutting without appending
///         the marker fails the glyph ones.
///     </para>
///     <para>
///         ⚠ <b>A real font, for the reason <c>TextWrapTests</c> gives.</b> Measured without a face
///         every glyph is zero wide, nothing ever overflows, and a test that an ellipsis appeared
///         would pass against an engine that never truncated anything.
///     </para>
/// </remarks>
public class TextOverflowTests {
    static readonly FontFace Font = LoadFont("TestShapeLana.ttf", "TestShapeLana");

    /// <summary>
    ///     ⚠ <b>The ellipsis comes out of a <i>fallback</i> face here, and that is deliberate.</b>
    ///     <c>TestShapeLana</c> — the face every other test in this assembly measures with — has no
    ///     U+2026 at all, which was found the hard way: the first draft of this file asserted against
    ///     <c>Font.GlyphFor('…')</c>, that returned <c>.notdef</c>, and seven of eight tests passed
    ///     while measuring the glyph id zero. The <c>Assert.NotEqual(0, Marker)</c> below is what
    ///     caught it and is why it stays.
    ///     <para>
    ///         Pairing the two faces fixes the instrument and buys the harder case at the same time:
    ///         a UI font that cannot draw the marker is the common situation, so the truncated line is
    ///         legitimately two <c>TextRun</c>s in two faces, and the width it has to fit into is the
    ///         sum across both. A single-face test would not have measured that at all.
    ///     </para>
    /// </summary>
    static readonly FontFace Marked = LoadFont("NotoSerifKannada-Regular.ttf", "Kannada");

    static FontFace LoadFont(string file, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{file}")
            ?? throw new InvalidOperationException($"{file} is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>A document whose label is the three declarations <c>truncate</c> stands for.</summary>
    static UiDocument Documented(string declarations) {
        var document = new UiDocument(400f, 300f);
        Typeset(document);
        document.Load($"root {{ width: 400px; height: 300px; align-items: flex-start; }} label {{ {declarations} }}");

        return document;
    }

    static void Typeset(UiDocument document) {
        document.Fonts.Register("Test", Font);
        document.Fonts.AddFallback(Marked);
    }

    static UiElement Labelled(UiDocument document, string text) {
        var label = document.Create("label", document.Root, null);
        label.Text = text;
        document.Update();
        document.Draw();

        return label;
    }

    /// <summary>The glyph the fallback face uses for U+2026, which is what an ellipsis has to end in.</summary>
    static ushort Marker => Marked.GlyphFor('…');

    static List<PositionedGlyph> GlyphsOf(TextLine line) {
        var placed = new List<PositionedGlyph>();
        line.Place(placed);

        return placed;
    }

    const string Truncating = "width: 60px; overflow: hidden; white-space: nowrap; text-overflow: ellipsis;";
    const string Clipping = "width: 60px; overflow: hidden; white-space: nowrap;";

    /// <summary>
    ///     ⚠ <b>The test the finding is about: the line ends in an ellipsis and fits the box.</b>
    /// </summary>
    [Fact]
    public void An_overflowing_line_is_cut_to_the_box_and_ends_in_an_ellipsis() {
        using var document = Documented(Truncating);
        var label = Labelled(document, "aa bb cc dd ee ff gg hh");

        // The face has to have the character, or this test is measuring a fallback and not a feature.
        Assert.NotEqual(0, Marker);

        var drawn = label.Ellipsized(60f)!;

        Assert.Single(drawn.Lines);
        Assert.True(
            drawn.Lines[0].Width <= 60f,
            $"the truncated line is {drawn.Lines[0].Width} wide in a 60px box, so it was not truncated"
        );

        Assert.Equal(Marker, GlyphsOf(drawn.Lines[0])[^1].GlyphId);
    }

    /// <summary>
    ///     ⚠ <b>Without the property the old behaviour is untouched</b> — the pair that makes the
    ///     assertion above mean something, since a truncator that ran unconditionally would pass it.
    /// </summary>
    [Fact]
    public void Without_the_property_the_line_still_overflows_and_carries_no_marker() {
        using var document = Documented(Clipping);
        var label = Labelled(document, "aa bb cc dd ee ff gg hh");

        var drawn = label.Ellipsized(60f)!;

        Assert.Single(drawn.Lines);
        Assert.True(drawn.Lines[0].Width > 60f);
        Assert.DoesNotContain(GlyphsOf(drawn.Lines[0]), glyph => glyph.GlyphId == Marker);
    }

    /// <summary>A line that already fits is returned untouched, marker and all.</summary>
    [Fact]
    public void A_line_that_fits_is_not_given_an_ellipsis_it_does_not_need() {
        using var document = Documented(Truncating);
        var label = Labelled(document, "aa");

        var drawn = label.Ellipsized(60f)!;

        Assert.True(drawn.Lines[0].Width <= 60f);
        Assert.DoesNotContain(GlyphsOf(drawn.Lines[0]), glyph => glyph.GlyphId == Marker);

        // ⚠ The same object, not merely an equal one. The block a fitting label draws is the one every
        // other reader already holds, so the common case allocates no second layout per frame.
        Assert.Same(label.Block(), drawn);
    }

    /// <summary>
    ///     ⚠ <b>The caret's block is the untruncated one, and this is the assertion that keeps a text
    ///     field working.</b> <c>TextField</c> and <c>CodeEditor</c> index into <see cref="UiElement.Block()" />;
    ///     had truncation happened there, a caret past the cut would land on the wrong character and
    ///     a single-line field would stop scrolling sideways.
    /// </summary>
    [Fact]
    public void Truncating_the_picture_leaves_the_text_the_caret_reads_alone() {
        using var document = Documented(Truncating);
        var label = Labelled(document, "aa bb cc dd ee ff gg hh");

        var caret = label.Block()!;
        var drawn = label.Ellipsized(60f)!;

        Assert.NotSame(caret, drawn);
        Assert.True(caret.Lines[0].Width > 60f);
        Assert.Equal("aa bb cc dd ee ff gg hh".Length, caret.Lines[0].Length);
    }

    /// <summary>
    ///     ⚠ <b>The property reaches a child's text, which is the shape every real use of
    ///     <c>truncate</c> has.</b> CSS gets there without inheriting, by putting the child's glyphs
    ///     on the container's own line box; Vixen has no shared line box, so it inherits instead —
    ///     see <c>UiDocument.EllipsisOf</c>. Either way the author's intent has to arrive.
    /// </summary>
    [Fact]
    public void The_container_the_class_is_written_on_truncates_the_span_inside_it() {
        using var document = new UiDocument(400f, 300f);
        Typeset(document);

        document.Load(
            """
            root { width: 400px; height: 300px; align-items: flex-start; }
            row   { text-overflow: ellipsis; }
            label { width: 60px; overflow: hidden; white-space: nowrap; }
            """
        );

        var row = document.Create("row", document.Root, null);
        var label = document.Create("label", row, null);
        label.Text = "aa bb cc dd ee ff gg hh";

        document.Update();
        document.Draw();

        var drawn = label.Ellipsized(60f)!;

        Assert.True(drawn.Lines[0].Width <= 60f);
        Assert.Equal(Marker, GlyphsOf(drawn.Lines[0])[^1].GlyphId);
    }

    /// <summary>
    ///     A box too narrow for even one character still says something was elided, rather than
    ///     drawing an empty line that reads as an element with no text at all.
    /// </summary>
    [Fact]
    public void A_box_narrower_than_one_cluster_draws_the_marker_alone() {
        using var document = Documented(Truncating);
        var label = Labelled(document, "aa bb cc dd ee ff gg hh");

        var drawn = label.Ellipsized(2f)!;
        var glyphs = GlyphsOf(drawn.Lines[0]);

        Assert.Single(glyphs);
        Assert.Equal(Marker, glyphs[0].GlyphId);
    }

    /// <summary>
    ///     ⚠ <b>The glyph reaches the draw list</b>, which is the only claim that survives somebody
    ///     deciding <c>EmitText</c> should read <see cref="UiElement.Block()" /> again. Everything
    ///     above measures a block; this measures the picture.
    /// </summary>
    [Fact]
    public void The_ellipsis_reaches_the_draw_list() {
        using var document = Documented(Truncating);
        Labelled(document, "aa bb cc dd ee ff gg hh");

        Assert.Contains(document.Drawing.Glyphs, glyph => glyph.GlyphId == Marker);
    }

    /// <summary>And is absent from it when the property is not set, for the same reason as above.</summary>
    [Fact]
    public void Without_the_property_no_ellipsis_reaches_the_draw_list() {
        using var document = Documented(Clipping);
        Labelled(document, "aa bb cc dd ee ff gg hh");

        Assert.DoesNotContain(document.Drawing.Glyphs, glyph => glyph.GlyphId == Marker);
    }
}
