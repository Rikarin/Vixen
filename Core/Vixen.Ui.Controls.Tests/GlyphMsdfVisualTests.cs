// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Doc 09 § Testing's "MSDF glyph rendering golden images", and the field behind them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A golden of drawn text is <i>not</i> a golden of MSDF, and that distinction is the
///         whole of this file.</b> A picture of the letter <c>B</c> looks the same whether the atlas
///         holds a multi-channel field, a single-channel one, or a plain coverage bitmap — at the
///         size a control draws at, all three are a black shape on a white ground, and a committed
///         reference would go on matching after the multi-channel half was removed. So the golden
///         here is the second assertion, and the first is a closed-form property of the field itself.
///     </para>
///     <para>
///         ⚠ <b>The property is that the three channels disagree.</b> That is the definition of
///         multi-channel: <c>DistanceFieldBitmap.Median</c> takes the median of R, G and B precisely
///         because each carries the distance to a different subset of the outline's edges, which is
///         what keeps a corner sharp where one channel alone would round it. A single-channel field
///         written into three channels has <c>R == G == B</c> everywhere and would render a picture
///         nobody could tell apart — so this is the assertion that would go red on the day the
///         edge colouring was dropped, and the only one that could.
///     </para>
///     <para>
///         <b>And the golden is drawn large.</b> MSDF's claim is resolution independence: one field
///         per glyph, sampled at any size. Rendering at 96 pixels from a 32-pixel field is that claim
///         being exercised, and a reference at that size shows an edge artefact — a rounded corner, a
///         bleeding seam, a wrong pixel range — that the same glyph at 12 pixels would hide.
///     </para>
/// </remarks>
public class GlyphMsdfVisualTests {
    /// <summary>Letters with corners in them, which is what the multi-channel field is <i>for</i>.</summary>
    const string Text = "AVL";

    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>The field is multi-channel, which no picture of it could tell you.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted rather than sampled at a chosen texel.</b> Every field has texels where the
    ///     three channels agree — the deep interior and the far exterior are the same distance by
    ///     every measure — so a single probe is a coin toss. What is diagnostic is that a substantial
    ///     band of the field disagrees, which is the band along the outline where the edge colouring
    ///     did its work.
    /// </remarks>
    [Fact]
    public void A_glyph_field_carries_three_channels_that_do_not_agree() {
        var glyph = Font.GlyphFor('A');
        Assert.NotEqual(0, glyph);

        // ⚠ Through the cache and the atlas rather than through `DistanceField.Generate` directly,
        // because those are what a frame reads. A field generated here and never placed would prove
        // the generator multi-channel and say nothing about what the renderer samples.
        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));

        Assert.True(cache.TryGet(Font, 0, glyph, out var placement), "the glyph was not placed in the atlas");
        Assert.False(placement.IsEmpty);

        var atlas = cache.Atlas;
        var texels = 0;
        var disagreeing = 0;

        for (var y = placement.Region.Y; y < placement.Region.Y + placement.Region.Height; y++) {
            for (var x = placement.Region.X; x < placement.Region.X + placement.Region.Width; x++) {
                var offset = ((y * atlas.Width) + x) * 3;

                var r = atlas.Pixels[offset];
                var g = atlas.Pixels[offset + 1];
                var b = atlas.Pixels[offset + 2];

                texels++;

                if (MathF.Abs(r - g) > 1e-4f || MathF.Abs(g - b) > 1e-4f) {
                    disagreeing++;
                }
            }
        }

        Assert.True(texels > 0, "the placement covers no texels");

        Assert.True(
            disagreeing > texels / 10,
            $"only {disagreeing} of {texels} texels have channels that differ, so the field is single-channel"
        );
    }

    /// <summary>And the letters draw, from a field a third of the size they are drawn at.</summary>
    /// <remarks>
    ///     ⚠ <b>The guard before the reference is that anything drew at all.</b> A missing glyph, an
    ///     exhausted atlas or a face that failed to load all produce a blank frame — and a blank
    ///     frame accepted once as a reference is a golden that passes for ever while showing nothing.
    ///     This is the same argument <c>AccessibilitySnapshot.Unnamed</c> makes about an empty tree.
    /// </remarks>
    [Fact]
    public void Large_text_matches_its_reference() {
        using var ui = UiTest.Create(220f, 120f);

        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            """
            root  { width: 220px; height: 120px; background-color: #ffffff; }
            .word { position: absolute; left: 12px; top: 8px;
                    font-family: Test; font-size: 96px; color: #101216; }
            """
        );

        ui.Create("div", null, "word", "word").Text = Text;
        ui.Frame();

        var image = ui.Capture();
        var ink = 0;

        for (var i = 0; i < image.Width * image.Height; i++) {
            if (image.Pixels[i * 4] < 128) {
                ink++;
            }
        }

        var pixels = image.Width * image.Height;

        // ⚠ Bounded above as well as below, and the upper bound is the half that matters. A blank
        // frame is caught by the floor; a frame that went *entirely* dark — a clip that swallowed
        // the background, a colour resolved to the wrong end — is not, and it is the failure that
        // would otherwise be accepted as the reference and match itself for ever.
        Assert.True(ink > 200, $"only {ink} of {pixels} pixels are dark, so the glyphs did not draw");
        Assert.True(ink < pixels / 2, $"{ink} of {pixels} pixels are dark, which is not three letters");

        ui.Screenshot("glyph-msdf-large");
    }
}
