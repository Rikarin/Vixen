// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Bidi reordering across a font-fallback boundary.</summary>
/// <remarks>
///     <para>
///         <b>The two things that cut a line into runs are not the same thing, and this is where they
///         meet.</b> <c>FontRegistry.Cover</c> cuts where the <i>face</i> changes; UAX#9 cuts where
///         the <i>level</i> changes; and a run has to be a stretch over which both are constant,
///         because a run is what gets one shaping call and one position on the line. Splitting by
///         coverage alone gives runs that are laid down in logical order — so a line whose Arabic and
///         whose Latin come from different faces reorders <i>within</i> each face and not across the
///         boundary between them.
///     </para>
///     <para>
///         ⚠ <b>That failure renders plausibly.</b> Each face's own text is internally correct, the
///         line is the right width, and nothing throws — the words are simply in the wrong order,
///         which is invisible to a reader who does not read the script. So every assertion here is an
///         inequality between two x coordinates.
///     </para>
///     <para>
///         <b>The fixture is the same disjoint pair the fallback tests use</b>, for the same reason:
///         <c>TestShapeLana</c> has Latin and no Arabic, <c>TestShapeAran</c> has seventeen Arabic
///         letters and no Latin, and the only character both draw is the space. A mixed string
///         therefore has exactly one correct split, and the Arabic in it is *necessarily* on the far
///         side of a fallback boundary from the Latin — which is the condition this defect needs.
///     </para>
/// </remarks>
public class BidiFallbackTests {
    /// <summary>ALEF then TEH.</summary>
    const string Arabic = "ات";

    /// <summary>Latin, a space, then Arabic. Three bidi levels under an <c>rtl</c> base.</summary>
    const string Mixed = "AB " + Arabic;

    static readonly FontFace Lana = LoadFont("TestShapeLana.ttf", "lana");
    static readonly FontFace Aran = LoadFont("TestShapeAran.ttf", "aran");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>One glyph as it is actually drawn: where, which, and out of which face.</summary>
    readonly record struct Drawn(float X, ushort Glyph, FontFace Font);

    /// <summary>
    ///     Lays the text out with a Latin family and an Arabic fallback, and returns every glyph at
    ///     its position along the line.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Walked run by run rather than through <see cref="TextLine.Place" />.</b> A glyph id is
    ///     an index into <i>one</i> face, so the same number means different things in the two fonts
    ///     here and a flattened list could not tell a Lana glyph from an Aran one. Carrying the face
    ///     alongside is what makes "the leftmost thing on the line is Arabic" an assertion that can be
    ///     written at all.
    /// </remarks>
    static Drawn[] Laid(string direction, string text) {
        using var document = new UiDocument(400f, 200f);

        document.Fonts.Register("Test", Lana);
        document.Fonts.AddFallback(Aran);
        document.Load(
            "root { width: 400px; height: 200px; align-items: flex-start; } "
            + $"label {{ font-family: Test; direction: {direction}; }}"
        );

        var label = document.Root.Add("label");
        label.Text = text;
        document.Update();

        var line = label.Block()!.Lines[0];
        var drawn = new List<Drawn>();

        for (var i = 0; i < line.Runs.Length; i++) {
            var glyphs = new List<PositionedGlyph>();
            line.Runs[i].Place(glyphs, line.PenOf(i));

            foreach (var glyph in glyphs) {
                drawn.Add(new Drawn(glyph.X, glyph.GlyphId, line.Runs[i].Font));
            }
        }

        return [.. drawn];
    }

    static float Of(Drawn[] drawn, FontFace font, ushort glyph) =>
        drawn.Single(item => ReferenceEquals(item.Font, font) && item.Glyph == glyph).X;

    static float RightmostOf(Drawn[] drawn, FontFace font) =>
        drawn.Where(item => ReferenceEquals(item.Font, font)).Max(item => item.X);

    [Fact]
    public void The_fixture_faces_split_the_string_exactly_where_the_tests_assume() {
        Assert.True(Lana.Supports('A'));
        Assert.False(Lana.Supports('ا'));
        Assert.True(Aran.Supports('ا'));
        Assert.False(Aran.Supports('A'));

        // The Latin face is the declared family and covers the space, so the space is drawn by Lana
        // and the boundary falls between the space and the Arabic. Every inequality below is written
        // against that split.
        Assert.True(Lana.Supports(' '));
    }

    /// <summary>
    ///     <b>The control.</b> Under an <c>ltr</c> base the Arabic is the only right-to-left run on
    ///     the line, so L2 reverses it within itself and leaves the run order alone — Latin first.
    ///     This is the answer the old code gave for every direction, and it is right for this one.
    /// </summary>
    [Fact]
    public void An_ltr_line_keeps_its_Latin_on_the_left() {
        var drawn = Laid("ltr", Mixed);

        Assert.True(Of(drawn, Lana, Lana.GlyphFor('A')) < RightmostOf(drawn, Aran));
    }

    /// <summary>
    ///     <b>The defect.</b> Under an <c>rtl</c> base the Latin is level 2 and the Arabic is level 1,
    ///     so L2 reverses the whole line and the Arabic must be drawn <i>left</i> of the Latin — across
    ///     a font boundary that a coverage-only split has no reason to reorder over.
    /// </summary>
    [Fact]
    public void An_rtl_line_puts_its_Arabic_left_of_its_Latin_across_the_fallback_boundary() {
        var drawn = Laid("rtl", Mixed);

        Assert.True(
            RightmostOf(drawn, Aran) < Of(drawn, Lana, Lana.GlyphFor('A')),
            "every Arabic glyph must be drawn left of the Latin 'A' when the base level is 1"
        );
    }

    /// <summary>
    ///     And the neutral between the two opposite runs sits <i>between</i> them, which is the
    ///     assertion a fix that reordered whole faces rather than whole levels would fail.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the one that stops the fix being "reverse the runs".</b> The space is level 1
    ///     and the Latin beside it is level 2, so the Lana coverage span holds two levels and has to
    ///     be cut in two. A fix that split only where the face changes would give the whole Lana span
    ///     one level, swap it with the Aran span whole, and leave the space stranded at the right-hand
    ///     end of the line — a picture in which the words are in the right order and the gap between
    ///     them is not, which is exactly the sort of wrongness that survives a reviewer who does not
    ///     read the script.
    /// </remarks>
    [Fact]
    public void The_neutral_between_two_opposite_runs_is_drawn_between_them() {
        var drawn = Laid("rtl", Mixed);

        var space = Of(drawn, Lana, Lana.GlyphFor(' '));

        Assert.True(RightmostOf(drawn, Aran) < space, "the space belongs right of the Arabic");
        Assert.True(space < Of(drawn, Lana, Lana.GlyphFor('A')), "and left of the Latin");
    }
}
