// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Ui.Text;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.1's <c>Text</c>: a string, shaped and filled, as coverage.</summary>
/// <remarks>
///     <para>
///         <b>The oracle is area, not a picture.</b> A filled outline scaled by <c>k</c> covers
///         <c>k²</c> times the ink — that is a closed form, it is false for almost every way of
///         getting the arithmetic wrong, and it needs no golden image and no eye. Everything else
///         here is a displacement: where the ink starts, where it ends, how far tracking moved it.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day the rasteriser stops drawing.</b> Every measure
///         below is over the ink, so a field of zeros would divide by nothing and compare nothing —
///         <see cref="An_empty_string_draws_nothing_and_a_word_draws_something" /> is the guard, and
///         it is the first thing to read when the rest goes strange.
///     </para>
///     <para>
///         ⚠ <b>The face is committed and is never the machine's.</b> <c>TextShaper</c> reads no
///         <c>CultureInfo</c> and takes no default from the process precisely so that a paragraph
///         shapes the same everywhere; a test that asked the operating system for a sans-serif would
///         throw that away and would fail on one developer's laptop only.
///     </para>
/// </remarks>
public class TextureTextTests {
    /// <summary>The field these are drawn into.</summary>
    const int Side = 256;

    static FontFace? face;

    /// <summary>The editor's own UI face, embedded by this project rather than found.</summary>
    static FontFace Font {
        get {
            if (face is not null) {
                return face;
            }

            using var stream = typeof(TextureTextTests).Assembly.GetManifestResourceStream(
                "Fonts.OpenSans-Regular.ttf"
            );

            Assert.NotNull(stream);

            using var bytes = new MemoryStream();

            stream.CopyTo(bytes);

            return face = FontFace.Load(bytes.ToArray());
        }
    }

    /// <summary>An empty string is empty and a word is not, which is what the rest rests on.</summary>
    [Fact]
    public void An_empty_string_draws_nothing_and_a_word_draws_something() {
        Assert.All(Draw("", 64f), value => Assert.Equal(0f, value));

        var word = Draw("Vixen", 64f);

        Assert.True(Ink(word) > 100f, $"a word covered {Ink(word)} texels");
        Assert.All(word, value => Assert.InRange(value, 0f, 1f));
    }

    /// <summary>
    ///     ⚠ Twice the size is four times the ink, which is the property a rasteriser cannot pass by
    ///     accident.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The closed form this file is built on.</b> A filled shape scaled by <c>k</c> about
    ///         any point covers <c>k²</c> times the area, exactly, in the continuum — so the only
    ///         thing between the measurement and 4 is the boundary, whose contribution is one
    ///         antialiased texel per unit of perimeter and therefore falls as <c>1/k</c>. Four per
    ///         cent is the room that leaves at these sizes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it rules out is most of the plausible mistakes.</b> A scale applied to the
    ///         pen but not to the outline gives a ratio of 1; applied to the outline but not to the
    ///         box gives a clipped glyph and a ratio near 2; a size read as pixels-per-em where the
    ///         face is 2048 units gives a glyph too small to measure. None of those is 4.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("H")]
    [InlineData("o")]
    [InlineData("Vixen")]
    public void Twice_the_size_covers_four_times_the_area(string text) {
        var small = Ink(Draw(text, 32f));
        var large = Ink(Draw(text, 64f));

        Assert.True(small > 20f, $"'{text}' at 32 covered only {small} texels");
        Assert.InRange(large / small, 3.84f, 4.16f);
    }

    /// <summary>Alignment moves the line across the picture, in the order the enum names.</summary>
    /// <remarks>
    ///     ⚠ <b>Three cases and two comparisons, because either alone is passed by a constant.</b> A
    ///     rasteriser that ignored alignment entirely puts all three in the same place, which the
    ///     strict inequalities refuse; one that swapped left and right passes a test that only
    ///     checked centre.
    /// </remarks>
    [Fact]
    public void Alignment_moves_the_line_across_the_picture() {
        var left = FirstInkColumn(Draw("Vixen", 48f, TextureTextAlignment.Left));
        var centre = FirstInkColumn(Draw("Vixen", 48f, TextureTextAlignment.Centre));
        var right = FirstInkColumn(Draw("Vixen", 48f, TextureTextAlignment.Right));

        Assert.True(left < centre, $"left started at {left} and centre at {centre}");
        Assert.True(centre < right, $"centre started at {centre} and right at {right}");

        // And left really means the edge: the pen is at column zero, so the first ink is the first
        // glyph's own left side bearing and nothing more.
        Assert.InRange(left, 0, 8);
    }

    /// <summary>
    ///     ⚠ Tracking is spacing <em>between</em> glyphs, so it widens a pair and leaves a single
    ///     glyph alone.
    /// </summary>
    /// <remarks>
    ///     <b>The second half is what makes this an assertion about tracking</b> rather than about
    ///     any parameter that happens to move ink. A number added to the pen once per glyph
    ///     — the off-by-one this is easiest to write — moves a lone glyph too, and the width of a
    ///     one-glyph line would grow with it.
    /// </remarks>
    [Fact]
    public void Tracking_widens_a_pair_and_leaves_one_glyph_alone() {
        var pair = InkWidth(Draw("HH", 48f, TextureTextAlignment.Left));
        var tracked = InkWidth(Draw("HH", 48f, TextureTextAlignment.Left, tracking: 20f));

        Assert.InRange(tracked - pair, 19, 21);

        var one = InkWidth(Draw("H", 48f, TextureTextAlignment.Left));
        var oneTracked = InkWidth(Draw("H", 48f, TextureTextAlignment.Left, tracking: 20f));

        Assert.Equal(one, oneTracked);
    }

    /// <summary>The field is exactly the shape <see cref="TextureUploads.AddCoverage" /> takes.</summary>
    /// <remarks>
    ///     <c>float[]</c>, row-major, <c>width · height</c> long, every value in <c>[0, 1]</c> — which
    ///     is <c>CoverageBitmap.Coverage</c>'s own contract, carried across a seam where the two
    ///     assemblies deliberately share no type.
    /// </remarks>
    [Fact]
    public void The_field_is_what_an_upload_takes() {
        var coverage = TextureText.Rasterize(Font, "Vixen", 128, 64, 32f);

        Assert.Equal(128 * 64, coverage.Length);
        Assert.All(coverage, value => Assert.InRange(value, 0f, 1f));

        // ⚠ And the quantisation rounds rather than truncating, which is the half of the upload a
        // reader gets wrong: a half-covered edge texel is 128 and not 127, and the error of the other
        // spelling is entirely on the dark side — a glyph uploaded that way is uniformly thinner than
        // the one that was drawn, which reads as a font weight.
        Assert.Equal(255, TextureUploads.Quantize(1f));
        Assert.Equal(128, TextureUploads.Quantize(0.5f));
        Assert.Equal(0, TextureUploads.Quantize(0f));
    }

    /// <summary>A wider picture holds the same letters at the same size, further apart from the edges.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § D8's promise, in the one form it takes for a source node.</b> Every length
    ///     here is in texels of the picture being written, so a caller that scales the size the way
    ///     <see cref="TexturePlan.Resolve" /> scales a radius gets the same physical letter at every
    ///     bake — and a caller that does not gets a letter half the size, which is exactly what this
    ///     measures. The ink is identical because nothing in the rasteriser reads an extent except
    ///     for the alignment and the clip.
    /// </remarks>
    [Fact]
    public void The_size_is_in_texels_of_the_picture_and_not_a_fraction_of_it() {
        var small = Ink(TextureText.Rasterize(Font, "H", 128, 128, 48f, TextureTextAlignment.Centre));
        var large = Ink(TextureText.Rasterize(Font, "H", 256, 256, 48f, TextureTextAlignment.Centre));

        Assert.InRange(large / small, 0.98f, 1.02f);

        // Twice the picture *and* twice the size is four times the ink again, which is what a caller
        // scaling by the bake would ask for.
        var scaled = Ink(TextureText.Rasterize(Font, "H", 256, 256, 96f, TextureTextAlignment.Centre));

        Assert.InRange(scaled / small, 3.84f, 4.16f);
    }

    /// <summary>A face with no outlines is refused where the font is given.</summary>
    /// <remarks>
    ///     Rather than at the first glyph, where the message would be about a glyph. There is no
    ///     bitmap-only face committed to this tree to prove the branch against, so what is asserted is
    ///     the predicate this reads — a face that <em>does</em> carry outlines says so — and the
    ///     refusal is stated in the remarks it guards.
    /// </remarks>
    [Fact]
    public void A_face_carrying_outlines_is_what_this_needs() => Assert.True(Font.HasOutlines);

    static float[] Draw(
        string text,
        float size,
        TextureTextAlignment alignment = TextureTextAlignment.Centre,
        float tracking = 0f
    ) =>
        TextureText.Rasterize(Font, text, Side, Side, size, alignment, tracking);

    /// <summary>How many texels of ink there are, counting partial coverage as its fraction.</summary>
    static float Ink(float[] coverage) {
        var total = 0f;

        foreach (var value in coverage) {
            total += value;
        }

        return total;
    }

    /// <summary>The leftmost column carrying any ink, or the width when there is none.</summary>
    static int FirstInkColumn(float[] coverage) {
        for (var column = 0; column < Side; column++) {
            for (var row = 0; row < Side; row++) {
                if (coverage[(row * Side) + column] > 0.01f) {
                    return column;
                }
            }
        }

        return Side;
    }

    /// <summary>How many columns lie between the first and the last carrying ink.</summary>
    static int InkWidth(float[] coverage) {
        var first = FirstInkColumn(coverage);
        var last = -1;

        for (var column = Side - 1; column > first; column--) {
            for (var row = 0; row < Side; row++) {
                if (coverage[(row * Side) + column] > 0.01f) {
                    last = column;

                    break;
                }
            }

            if (last >= 0) {
                break;
            }
        }

        return last < 0 ? 0 : last - first;
    }
}
