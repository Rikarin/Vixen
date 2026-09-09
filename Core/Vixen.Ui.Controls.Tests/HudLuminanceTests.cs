// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The black-HUD defect, in pixels rather than in a counter.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#670's picture, and the reason it took nine months to get one.</b> The white level
///         has had a mechanism, a derivation and a counter (<c>UiRenderFeature.Dim</c>) since it
///         landed, and every one of those is a claim about a number the interface carries. None of
///         them is the symptom. The symptom is that a HUD authored white comes out darker than the
///         wall behind it, and until something rasterised the frame in the pass's own units nothing
///         in this repository could say so — which is exactly the standing photometric trap: a pass
///         lit by an authored 0–1 tint is pixel-identical to a pass that never ran.
///     </para>
///     <para>
///         ⚠ <b>What made the picture impossible was four lines of the rasteriser, not the absence
///         of a device.</b> <c>SoftwareUiRasterizer.Render</c> stores eight bits, so a scene at a
///         hundred candelas and a HUD at one both leave it as 255 — the two frames this file is
///         about are byte-identical through that door. <see cref="SoftwareUiRasterizer.RenderLinear" />
///         is the same frame with the store not taken, and it is what turns "the HUD is black" from
///         a paragraph into an inequality.
///     </para>
///     <para>
///         ⚠ <b>The assertion is an <em>order</em> and not a threshold</b>, for this repository's
///         usual reason: no display transform is invented anywhere below. A white panel that is a
///         hundredth of the grey wall it covers is wrong under every monotone transform there is,
///         and a white panel that is twice the wall is right under all of them. The magnitudes are
///         asserted too, because an order alone would also hold for a panel that came out at
///         99 cd/m².
///     </para>
///     <para>
///         <b>The software executor, not the device.</b> This runs the fragment arithmetic on the
///         CPU from the geometry the GPU would be given, so it proves the white level reaches the
///         pixels and not that the Vulkan path agrees — see that class's own remarks on the line it
///         cannot see below. ⚠ The device's half is
///         <c>Vixen.Graphics.Golden.Tests.HudLuminanceDeviceTests</c>, which asserts the same order
///         and the same two magnitudes through <c>UiRenderer</c> into an <c>Rgba32Float</c>
///         attachment, and it needed no reference image: a float target is that suite's
///         <see cref="SoftwareUiRasterizer.RenderLinear" />.
///     </para>
/// </remarks>
public class HudLuminanceTests {
    /// <summary>BT.2408's reference white, which is what <c>UiRenderer.WhiteLevelFor</c> hands a float pass.</summary>
    const float DiffuseWhite = 203f;

    /// <summary>The scene already in the buffer when the interface composites over it, in cd/m².</summary>
    /// <remarks>
    ///     ⚠ A mid-grey wall of a scene-referred frame, and deliberately not a highlight: at
    ///     3 000 cd/m² every claim below would be about a clamp instead of about the interface.
    ///     It is passed as the rasteriser's background rather than drawn, because a wall the
    ///     interface drew would be scaled by the white level too and the ratio could never move.
    /// </remarks>
    static readonly Color4 Wall = new(100f, 100f, 100f, 1f);

    /// <summary>How wide and tall the frame is, in pixels, which the rasteriser wants as an int.</summary>
    const int Side = 64;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>A white HUD panel over a lit scene is darker than the scene unless the frame has a white level.</summary>
    /// <remarks>
    ///     <para>
    ///         The fixture is the smallest thing that can carry the defect: one opaque white
    ///         rectangle over the middle of a wall at 100 cd/m², built twice from one draw list and
    ///         differing only in <see cref="UiGeometryBuilder.WhiteLevel" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The wall is asserted unmoved in both frames.</b> Without that, "the panel got
    ///         brighter" would also be satisfied by a change that lit the whole buffer — and the
    ///         thing under test is a scale spent on the interface's colours alone.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_white_panel_is_darker_than_the_wall_it_covers_until_the_frame_carries_a_white_level() {
        var dark = Frame(1f);
        var lit = Frame(DiffuseWhite);

        // The frame ran and the panel is opaque, so neither reading below is of the wall showing
        // through a panel that drew nothing.
        Assert.Equal(1f, Alpha(dark, 32, 32), 3);
        Assert.Equal(1f, Alpha(lit, 32, 32), 3);

        // The scene is not the interface's to scale.
        Assert.Equal(100f, At(dark, 4, 4), 3);
        Assert.Equal(100f, At(lit, 4, 4), 3);

        // The defect. A panel CSS calls white leaves the builder at one candela, which is a
        // hundredth of the wall — and the fix puts it at diffuse white, twice the wall.
        Assert.Equal(1f, At(dark, 32, 32), 3);
        Assert.Equal(DiffuseWhite, At(lit, 32, 32), 3);

        Assert.True(At(dark, 32, 32) < At(dark, 4, 4), "a white panel came out darker than the wall");
        Assert.True(At(lit, 32, 32) > At(lit, 4, 4), "a white panel came out no brighter than the wall");
    }

    /// <summary>And what it looks like: one code value off black, where it belongs on white.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same two frames encoded, because "darker than the wall" understates it.</b>
    ///         A display whose diffuse white is 203 cd/m² stores a pixel as its fraction of that
    ///         white — the linear store <c>SoftwareUiRasterizer.Render</c> already does, with no
    ///         gamma curve invented here, because the engine applies none either. Under it the panel
    ///         of the unlit frame lands on <b>1 of 255</b>: one code value from a buffer nothing was
    ///         drawn into, which is what "renders black" means and is not a figure of speech.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Black is measured rather than assumed</b> — the same geometry over an empty scene
    ///         is rasterised for it, so the claim is that the two answers are a code value apart
    ///         rather than that 1 is nearly 0.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_unlit_hud_encodes_one_code_value_from_a_frame_that_was_never_drawn_into() {
        var dark = Code(At(Frame(1f), 32, 32));
        var lit = Code(At(Frame(DiffuseWhite), 32, 32));
        var nothing = Code(At(SoftwareUiRasterizer.RenderLinear(
            Empty(),
            new GlyphAtlas(64, 64),
            Side,
            Side,
            new(0f, 0f, 0f, 1f)
        ), 32, 32));

        Assert.Equal(0, nothing);
        Assert.Equal(1, dark);
        Assert.Equal(255, lit);
    }

    /// <summary>The frame, at one white level, over the wall.</summary>
    static float[] Frame(float white) {
        var list = new DrawList();
        list.BeginFrame();

        // Opaque and white: the colour whose whole meaning is "as bright as this frame's white".
        list.Add(new(DrawCommandKind.Rectangle, 16, 16, 32, 32, new Color4(1f, 1f, 1f, 1f), 0, 0));
        list.EndFrame();

        var atlas = new GlyphAtlas(64, 64);
        var geometry = new UiGeometryBuilder { Gamut = ColorGamut.Srgb, WhiteLevel = white }.Build(
            list,
            new GlyphFieldCache(atlas),
            Viewport
        );

        return SoftwareUiRasterizer.RenderLinear(geometry, atlas, Side, Side, Wall);
    }

    /// <summary>A frame with nothing in it, which is what the defect is indistinguishable from.</summary>
    static UiGeometry Empty() {
        var list = new DrawList();
        list.BeginFrame();
        list.EndFrame();

        return new UiGeometryBuilder().Build(list, new GlyphFieldCache(new GlyphAtlas(64, 64)), Viewport);
    }

    /// <summary>One pixel's green channel, which is grey here and so is the luminance.</summary>
    static float At(float[] frame, int x, int y) => frame[(((y * Side) + x) * 4) + 1];

    /// <summary>And its coverage, so a reading can be told apart from the wall behind it.</summary>
    static float Alpha(float[] frame, int x, int y) => frame[(((y * Side) + x) * 4) + 3];

    /// <summary>What a display whose white is 203 cd/m² stores for that luminance.</summary>
    static int Code(float luminance) =>
        (int)Math.Clamp(MathF.Round(luminance / DiffuseWhite * 255f), 0f, 255f);
}
