// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What <c>direction</c> does to an element's text, judged by where the glyphs land.</summary>
/// <remarks>
///     <para>
///         <b>Every assertion here is on visual order — which glyph is leftmost — and never on
///         logical order.</b> That is the whole discipline of testing UAX#9: a wrong bidi result is
///         not a crash and not obviously wrong to a reader who does not read the script, it is text
///         in the wrong sequence. The algorithm itself is judged elsewhere, against 91 707 of the
///         Consortium's own cases in <c>Vixen.Ui.Text.Tests.BidiConformanceTests</c>; what is judged
///         here is whether <c>Vixen.Ui</c> ever asks it the right question.
///     </para>
///     <para>
///         <b>The observation every test turns on is a leading space</b>, because a space is the one
///         character whose side is decided by the base level and by nothing else. UAX#9 N2 gives a
///         neutral the embedding direction, so a space before the first word sits at the left end of
///         the line under a base level of 0 and at the <i>right</i> end under a base level of 1 — the
///         same glyph at opposite ends of the same string, which is the strongest form this assertion
///         can take.
///     </para>
///     <para>
///         ⚠ <b>The fixture needs a right-to-left face for half of it, and <c>TestShapeAran</c> is
///         the only one in the suite.</b> Its coverage is seventeen Arabic letters and the space — no
///         digits and no punctuation — which is the other reason the neutral here is a space.
///     </para>
/// </remarks>
public class BidiDirectionTests {
    /// <summary>ALEF then TEH. Two letters, two glyphs, and no joining between them.</summary>
    const string Arabic = "ات";

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

    /// <summary>The glyph id of whatever is drawn furthest left, under one face and one direction.</summary>
    /// <remarks>
    ///     One face, so that these tests say nothing about font fallback: crossing a fallback
    ///     boundary is a separate defect with separate tests, and a fixture that needed two faces
    ///     could not tell the two apart.
    /// </remarks>
    static ushort Leftmost(FontFace face, string direction, string text) {
        using var document = new UiDocument(400f, 200f);

        document.Fonts.Register("Test", face);
        document.Load(
            "root { width: 400px; height: 200px; align-items: flex-start; } "
            + $"label {{ font-family: Test; direction: {direction}; }}"
        );

        var label = document.Root.Add("label");
        label.Text = text;
        document.Update();

        var glyphs = new List<PositionedGlyph>();
        label.Block()!.Lines[0].Place(glyphs);

        return glyphs.OrderBy(glyph => glyph.X).First().GlyphId;
    }

    [Fact]
    public void The_fixture_faces_have_the_coverage_every_test_here_assumes() {
        // ⚠ Asserted rather than assumed. A subsetted replacement that lost the space, or an Arabic
        // face that gained Latin, would turn the tests below green by removing what they turn on.
        Assert.True(Aran.Supports('ا'));
        Assert.True(Aran.Supports(' '));
        Assert.False(Aran.Supports('A'));

        Assert.True(Lana.Supports('A'));
        Assert.True(Lana.Supports(' '));
        Assert.False(Lana.Supports('ا'));
    }

    /// <summary>
    ///     <b>Doc 46's first case.</b> An element styled <c>direction: rtl</c> whose text begins with
    ///     a Latin word must still lay out right to left — the style <i>states</i> the base level, and
    ///     P2/P3's first-strong-character guess is exactly what it overrides.
    /// </summary>
    /// <remarks>
    ///     Under a base level of 1 the leading space is level 1 and the Latin is level 2, so L2
    ///     reverses the pair and the space is drawn last, at the right end — leaving <c>A</c> leftmost.
    ///     Under a base level of 0 every character is level 0 and the space is drawn first. So the
    ///     first strong character being Latin decides the picture only if the style was ignored.
    /// </remarks>
    [Fact]
    public void An_rtl_paragraph_beginning_with_a_Latin_word_still_runs_right_to_left() {
        Assert.Equal(Lana.GlyphFor('A'), Leftmost(Lana, "rtl", " AB"));
    }

    /// <summary>
    ///     <b>Doc 46's second case, and the mirror that stops the first from being a coincidence.</b>
    ///     An element styled <c>direction: ltr</c> whose text begins with Arabic lays out left to
    ///     right, where the first-strong guess would have said otherwise.
    /// </summary>
    [Fact]
    public void An_ltr_paragraph_beginning_with_Arabic_still_runs_left_to_right() {
        Assert.Equal(Aran.GlyphFor(' '), Leftmost(Aran, "ltr", " " + Arabic));
    }

    /// <summary>
    ///     And the two halves of each pair differ, which is what makes them a flip rather than two
    ///     numbers that happen to be right.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Stated separately because a fixture that cannot show a flip and a flip that never
    ///     happens look identical.</b> If the face or the string were ever changed to something whose
    ///     picture the base level does not reach, the two tests above would still pass — against an
    ///     implementation that had gone back to ignoring the property.
    /// </remarks>
    [Fact]
    public void The_base_level_changes_which_glyph_is_leftmost() {
        Assert.NotEqual(Leftmost(Lana, "ltr", " AB"), Leftmost(Lana, "rtl", " AB"));
        Assert.NotEqual(Leftmost(Aran, "ltr", " " + Arabic), Leftmost(Aran, "rtl", " " + Arabic));
    }
}
