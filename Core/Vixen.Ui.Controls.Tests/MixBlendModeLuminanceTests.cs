// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A <c>mix-blend-mode</c> in a frame that carries a white level, in pixels (#1209).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The black-HUD defect of #670 returning through the blend path, and the reason it
///         needed a file of its own.</b> <c>HudLuminanceTests</c> proves the white level reaches an
///         ordinary panel's pixels. It cannot see this one: <c>UiBlend.Apply</c> un-premultiplied
///         both operands and clamped them to <c>[0, 1]</c>, so its premultiplied result could never
///         exceed its own alpha — <b>one candela</b>, whatever the element was authored at. A
///         <c>multiply</c> panel in an HDR HUD was therefore two orders of magnitude darker than the
///         same panel with no declaration at all, which is this repository's standing photometric
///         trap in a third place.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the tree could see it, and it is worth saying exactly why.</b>
///         <c>MixBlendModeTests</c> and <c>UiCompositingTests</c> build every fixture at the default
///         <see cref="UiGeometryBuilder.WhiteLevel" /> of one, where the divisor is the identity;
///         <c>SoftwareUiRasterizer.Render</c> and the golden suite's <c>Rgba8UNorm</c> then store
///         eight bits, so even a fixture that did carry a white level would clamp both frames to 255.
///         <see cref="SoftwareUiRasterizer.RenderLinear" /> is the door that opens it, the same one
///         #670's picture went through.
///     </para>
///     <para>
///         <b>The oracle is closed-form and not a screenshot.</b> White is <c>multiply</c>'s
///         identity — <c>B(Cb, Cs) = Cb·1 = Cb</c> — so a white panel declaring
///         <c>mix-blend-mode: multiply</c> over an opaque wall must leave that wall's own luminance
///         exactly where it was, at any white level. That is a number worked out from § 5.1 rather
///         than a picture re-accepted, and it is 100 here and was 1 before the divisor landed.
///     </para>
/// </remarks>
public class MixBlendModeLuminanceTests {
    /// <summary>BT.2408's reference white, which is what <c>UiRenderer.WhiteLevelFor</c> hands a float pass.</summary>
    const float DiffuseWhite = 203f;

    /// <summary>The scene already in the buffer when the interface composites over it, in cd/m².</summary>
    /// <remarks>
    ///     Opaque, so § 5.1's weighting by the backdrop's alpha leaves the blend function's answer
    ///     alone and every number below is the function itself rather than a mix of it with the
    ///     source. Mid-grey rather than a highlight, for <c>HudLuminanceTests</c>' reason: at
    ///     3 000 cd/m² these would be claims about a clamp.
    /// </remarks>
    static readonly Color4 Wall = new(100f, 100f, 100f, 1f);

    /// <summary>How wide and tall the frame is, in pixels.</summary>
    const int Side = 64;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>A white <c>multiply</c> panel leaves the wall it covers where it was, rather than at one candela.</summary>
    /// <remarks>
    ///     <para>
    ///         Two groups in one frame, identical but for the declaration: the <c>normal</c> one is
    ///         the control and lands at diffuse white, and the <c>multiply</c> one must land on the
    ///         wall. Before the divisor it landed on 1 — a hundredth of the wall, and a two-hundredth
    ///         of the panel beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instruments are asserted before the pixels.</b> A frame in which neither
    ///         group composited at all would read the wall at both points and satisfy "multiply left
    ///         the wall alone" perfectly, so the two layers and the panels' coverage are checked
    ///         first, and the geometry's own recorded white level is checked so that the two frames
    ///         differ by something other than their pixels.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_white_multiply_panel_leaves_the_wall_it_covers_alone_rather_than_at_one_candela() {
        var (geometry, frame) = Frame(DiffuseWhite);

        // The frame ran, both groups composited, and the two readings below are of panels rather than
        // of the wall showing through a group that drew nothing.
        Assert.Equal(2, geometry.Layers.Count);
        Assert.Equal(DiffuseWhite, geometry.WhiteLevel);
        Assert.Equal(1f, Alpha(frame, Blended.X, Blended.Y), 3);
        Assert.Equal(1f, Alpha(frame, Plain.X, Plain.Y), 3);

        // The scene is not the interface's to scale, and this corner is under neither panel.
        Assert.Equal(100f, At(frame, 2, 60), 3);

        // The control: an undeclared group is #670's panel and lands on the frame's white.
        Assert.Equal(DiffuseWhite, At(frame, Plain.X, Plain.Y), 3);

        // The defect. White is multiply's identity, so this pixel is the wall — 100 cd/m², not the
        // 1 cd/m² a clamp against the number one returned.
        Assert.Equal(100f, At(frame, Blended.X, Blended.Y), 3);

        Assert.True(
            At(frame, Blended.X, Blended.Y) > At(frame, Plain.X, Plain.Y) / 100f,
            "a multiplied panel came out two orders of magnitude below the unblended one beside it"
        );
    }

    /// <summary>The blend is homogeneous in the frame's units, over all sixteen modes.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The property that makes one divisor enough</b>, and the one #783 will transcribe
    ///         into GLSL and Raven: scaling both operands and the white level by the same factor
    ///         scales the answer by it. It holds by construction for the ratio-preserving modes, it
    ///         is what <i>gives</i> the quadratic ones (<c>multiply</c>, <c>screen</c>,
    ///         <c>exclusion</c>) a unit to be quadratic in, and it is what makes the clamp the five
    ///         undefined-above-one modes need a statement about the frame's white rather than about
    ///         the number one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sixteen modes rather than the three the picture above exercises</b>, because the
    ///         cheap wrong fix — dividing only where the arithmetic is obviously quadratic — passes
    ///         that picture and fails here on <c>color-dodge</c>, which clamps against one and would
    ///         keep returning a burnt-out white in a frame whose white is 203.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Modes))]
    public void Every_mode_scales_with_the_frames_white(UiBlendMode mode) {
        var source = new Color4(0.8f, 0.35f, 0.1f, 1f);
        var backdrop = new Color4(0.2f, 0.6f, 0.9f, 1f);

        var unit = UiBlend.Apply(mode, source, backdrop, 1f);
        var lit = UiBlend.Apply(
            mode,
            new Color4(source.R * DiffuseWhite, source.G * DiffuseWhite, source.B * DiffuseWhite, source.A),
            new Color4(
                backdrop.R * DiffuseWhite,
                backdrop.G * DiffuseWhite,
                backdrop.B * DiffuseWhite,
                backdrop.A
            ),
            DiffuseWhite
        );

        Assert.Equal(unit.R * DiffuseWhite, lit.R, 2);
        Assert.Equal(unit.G * DiffuseWhite, lit.G, 2);
        Assert.Equal(unit.B * DiffuseWhite, lit.B, 2);
        Assert.Equal(unit.A, lit.A, 4);
    }

    /// <summary>Every mode § 5 defines, so a seventeenth is covered the day it is added.</summary>
    public static TheoryData<UiBlendMode> Modes {
        get {
            var data = new TheoryData<UiBlendMode>();

            foreach (var mode in Enum.GetValues<UiBlendMode>()) {
                data.Add(mode);
            }

            return data;
        }
    }

    /// <summary>Where the multiplied panel's middle is.</summary>
    static readonly (int X, int Y) Blended = (18, 18);

    /// <summary>And the undeclared one's, which is the control.</summary>
    static readonly (int X, int Y) Plain = (46, 18);

    /// <summary>The frame, at one white level, over the wall.</summary>
    /// <remarks>
    ///     Both panels are opaque white — the colour whose whole meaning is "as bright as this
    ///     frame's white" — and each is its own composited group, which is what a
    ///     <c>mix-blend-mode</c> declaration produces and what the mode is a property of.
    /// </remarks>
    static (UiGeometry Geometry, float[] Pixels) Frame(float white) {
        var list = new DrawList();
        list.BeginFrame();

        Group(list, 8, UiBlendMode.Multiply);
        Group(list, 36, UiBlendMode.Normal);

        list.EndFrame();

        var atlas = new GlyphAtlas(64, 64);
        var geometry = new UiGeometryBuilder { Gamut = ColorGamut.Srgb, WhiteLevel = white }.Build(
            list,
            new GlyphFieldCache(atlas),
            Viewport
        );

        return (geometry, SoftwareUiRasterizer.RenderLinear(geometry, atlas, Side, Side, Wall));
    }

    /// <summary>One 20×20 white panel as a composited group, at a column, under a mode.</summary>
    static void Group(DrawList list, float x, UiBlendMode mode) {
        list.Add(
            new DrawCommand(DrawCommandKind.LayerPush, x, 8f, 20f, 20f, new Color4(1f, 1f, 1f, 1f), 0f, 0f) {
                Blend = mode
            }
        );

        list.Add(new DrawCommand(DrawCommandKind.Rectangle, x, 8f, 20f, 20f, new Color4(1f, 1f, 1f, 1f), 0f, 0f));
        list.Add(new DrawCommand(DrawCommandKind.LayerPop, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f));
    }

    /// <summary>One pixel's green channel, which is grey here and so is the luminance.</summary>
    static float At(float[] frame, int x, int y) => frame[(((y * Side) + x) * 4) + 1];

    /// <summary>And its coverage, so a reading can be told apart from the wall behind it.</summary>
    static float Alpha(float[] frame, int x, int y) => frame[(((y * Side) + x) * 4) + 3];
}
