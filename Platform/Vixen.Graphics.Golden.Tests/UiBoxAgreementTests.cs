// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The two implementations of the box shader draw the same box, with no group in the frame.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap this closes: nothing compared the two on a frame that composited nothing.</b>
///         <c>ui-box.frag</c> and <c>SoftwareUiRasterizer.Box</c> are two hand-written implementations
///         of one specification, and until <see cref="UiCompositingTests" /> existed nothing put any
///         two renderers side by side at all — the golden suite compares each of them against a
///         committed <i>picture</i>, which lets two implementations drift apart indefinitely as long
///         as each stays inside its own tolerance. That file closed the hole for frames with a group
///         in them. This one closes it for the shader itself, which is where the drift actually was:
///         the derivative <c>SoftwareUiRasterizer.Box</c> takes for <c>fwidth</c> was wrong by a
///         2×2 quad — worth <b>86 levels of 255</b> on the corner arcs of the <c>elliptical</c>
///         fixture below, measured by putting the defect back — and the blur branch was
///         <i>missing outright</i>.
///     </para>
///     <para>
///         ⚠ <b>A comparison and not a committed picture, for <see cref="UiCompositingTests" />'
///         reason.</b> A reference image is made by one of the two renderers, so it cannot say they
///         agree. There is no baseline here to regenerate and so no way for a divergence to be
///         accepted by accident.
///     </para>
///     <para>
///         ⚠ <b>Integer coordinates throughout, and that is not tidiness.</b>
///         <c>SoftwareUiRasterizer.TopLeft</c> keeps a closed test on axis-aligned edges where a GPU
///         opens the right and bottom ones, which its own remark states as a deliberate departure with
///         the eleven committed screenshots it would move. A box whose edge lands exactly on a sample
///         centre — <c>x = 6.5</c> with a width of 45, say — therefore differs by a whole opaque
///         column: measured at 54 pixels of 16384 differing by up to 107 levels, 42 of them by the
///         full 107. That is a known divergence with a decision on file and it is not this file's;
///         putting a half-pixel coordinate in the fixtures below would assert against it by accident.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class UiBoxAgreementTests {
    const int Side = Fixture.Side;

    static readonly Rectangle Viewport = new(0, 0, Side, Side);

    /// <summary>What both paths start from. Opaque, so the clear and the software fill are the same.</summary>
    static readonly Color4 Background = new(0.08f, 0.09f, 0.11f, 1f);

    /// <summary>
    ///     How far the two may be apart: a shade either way, and no pixel allowed to be wholly wrong.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Far tighter than <c>UiCompositingTests.Agreement</c>, because there is nothing
    ///         here for that allowance to absorb.</b> That one is sized for a composited pixel, which
    ///         is blended into an <c>Rgba8UNorm</c> surface, quantised, and blended again into an
    ///         <c>Rgba8UNorm</c> frame — three roundings against the software path's one. Nothing in
    ///         this file opens a group, so every pixel is stored once on both sides.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two rather than one, and one is what this device measures.</b> All six fixtures
    ///         below come out with a worst channel of exactly <b>1</b> on MoltenVK — on 0.45 % to
    ///         47.5 % of their pixels, which are the software rasterizer's own barycentric weights
    ///         rounding a constant vertex colour a level low, not anything either shader decided. One
    ///         is what a second conformant driver's <c>UNorm</c> store might not reproduce; two is not
    ///         enough room to hide what this file is for, since the two defects it was written after
    ///         measure <b>27</b> and <b>86</b> levels here.
    ///     </para>
    /// </remarks>
    static ImageTolerance Agreement => ImageTolerance.Slight;

    /// <summary>Every branch of the box shader, drawn twice and required to be the same picture.</summary>
    /// <param name="fixture">Which frame to draw. See <see cref="Frame" />.</param>
    [Theory]
    [InlineData("corners")]
    [InlineData("elliptical")]
    [InlineData("bordered")]
    [InlineData("blurred")]
    [InlineData("gradient")]
    [InlineData("tiled")]
    public void TheDeviceAndTheSoftwareRendererDrawTheSameBox(string fixture) {
        if (!TryOpen(out var opened, out _)) {
            return;
        }

        using var owned = opened!;
        var colour = owned.ColourTarget($"ui-box-{fixture}");

        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var geometry = new UiGeometryBuilder().Build(Frame(fixture), cache, Viewport);

        // ⚠ The instrument, and both halves of it matter. No layer, so this is the plain box path and
        // not the compositing one next door — a fixture that opened a group would be asserting about
        // surfaces instead of about the shader. And something has to have drawn: two renderers that
        // both emitted nothing agree perfectly.
        Assert.Empty(geometry.Layers);
        Assert.NotEmpty(geometry.Draws);

        var renderer = new UiRenderer(
            owned.Device,
            new(
                owned.Shader("ui.vert.spv", ShaderStage.Vertex),
                owned.Shader("ui-box.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-text.frag.spv", ShaderStage.Fragment),
                owned.Shader("ui-solid.frag.spv", ShaderStage.Fragment)
            ),
            new Rendering.RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        owned.Graph.AddPass($"ui-box-{fixture}", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, Background);
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, geometry, new(Side, Side)));
        });

        var rendered = owned.Render(colour, commands => renderer.Upload(commands, geometry, cache.Atlas));
        var software = SoftwareUiRasterizer.Render(geometry, cache.Atlas, Side, Side, Background);

        var comparison = ImageComparer.Compare(rendered, software, Agreement);

        Assert.True(
            comparison.Matches,
            $"the device and the software renderer disagree about a '{fixture}' box, and one of them "
            + $"is wrong: {comparison}. A difference confined to the corner arcs is the derivative — "
            + "`fwidth` belongs to the 2×2 quad and not to the fragment, see `SoftwareUiRasterizer.Box`. "
            + "A soft edge on one side and a hard one on the other is the blur branch, which the port "
            + "did not have at all until it was compared here."
        );
    }

    /// <summary>
    ///     ⚠ A blurred box is not the same picture as a hard one, which is what makes the
    ///     <c>blurred</c> case above worth running.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Without this, "the two agree" is compatible with the software renderer ignoring the
    ///         blur</b> — and that is exactly the state it was in. <c>SoftwareUiRasterizer.Box</c> read
    ///         the record's size, radii, border thickness, gradient axis, stops and interpolation
    ///         space, and not its <c>axis.z</c>; every <c>box-shadow</c> in the engine came out as a
    ///         hard-edged box at full opacity, sitting exactly where the soft shadow belonged, with
    ///         nothing rendering blank. The comparison above catches that only because the device does
    ///         blur — so this asserts the property directly, and it says so on a machine with no Vulkan
    ///         at all, where the comparison above skips. Both were checked by deleting the branch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On the software renderer alone, deliberately.</b> What this asserts is a property
    ///         of the shader's specification, not of the device, and rendering it twice on the GPU
    ///         would need a second fixture and a second readback to answer a question the device has
    ///         nothing to say about.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ABlurredBoxIsNotTheSamePictureAsAHardOne() {
        var soft = new GlyphFieldCache(new GlyphAtlas(64, 64));
        var hard = new GlyphFieldCache(new GlyphAtlas(64, 64));

        var blurred = new UiGeometryBuilder().Build(Frame("blurred"), soft, Viewport);
        var sharp = new UiGeometryBuilder().Build(Frame("blurred", blur: false), hard, Viewport);

        var a = SoftwareUiRasterizer.Render(blurred, soft.Atlas, Side, Side, Background);
        var b = SoftwareUiRasterizer.Render(sharp, hard.Atlas, Side, Side, Background);

        var comparison = ImageComparer.Compare(a, b, ImageTolerance.Exact);

        // ⚠ <b>A magnitude and not "they differ at all", because "they differ at all" passes with the
        // branch deleted.</b> Growing the quad by twice the blur — which `UiGeometryBuilder.Shadow`
        // does whether or not anything reads `axis.z` — moves the barycentric weights, and that alone
        // was measured at 16 pixels of 16384 differing by one level. A renderer ignoring the blur
        // therefore satisfies an exact comparison, and this test would have been green throughout the
        // whole period the defect existed. With the branch in place the same pair comes out at
        // <b>2832 pixels (17.29 %) differing by up to 27 levels</b>; the bounds below sit between the
        // two with room on both sides.
        Assert.True(
            comparison.WorstChannel >= 12 && comparison.Fraction >= 0.05,
            "the blur barely changed the picture, so `SoftwareUiRasterizer.Box` is not reading "
            + "`axis.z` and the comparison next door would pass with every box-shadow drawn as a "
            + $"hard box: {comparison}."
        );
    }

    /// <summary>One frame per branch of the shader.</summary>
    /// <param name="fixture">Which frame.</param>
    /// <param name="blur">
    ///     Whether the <c>blurred</c> frame's boxes are actually blurred. False is the same frame with
    ///     the soft edge taken away, which is what the shader draws when nothing reads <c>axis.z</c>.
    /// </param>
    /// <returns>The draw list, ended so that it has batches.</returns>
    /// <remarks>
    ///     ⚠ <c>EndFrame</c> is what builds the batches, and a list without it produces geometry with
    ///     no draws in it — a frame both renderers agree perfectly about, having drawn nothing.
    /// </remarks>
    static DrawList Frame(string fixture, bool blur = true) {
        var list = new DrawList();
        list.BeginFrame();

        switch (fixture) {
            case "corners":
                // Two overlapping rounded boxes: the fixture the corner derivative was measured on.
                list.Add(new(DrawCommandKind.Rectangle, 12, 28, 56, 44, new Color4(0.2f, 0.6f, 0.95f, 1f), 10, 0));
                list.Add(new(DrawCommandKind.Rectangle, 40, 44, 56, 44, new Color4(0.95f, 0.3f, 0.4f, 1f), 10, 0));

                // ⚠ A radius well past the half size and a radius of one pixel, which are the two
                // ends of the clamp in `box_distance`. Both implementations clamp against the half
                // size *before* the `abs` fold, and these are what would separate them from one that
                // clamped after: the first arrives as a stadium only because the clamp ran first.
                list.Add(new(DrawCommandKind.Rectangle, 4, 4, 60, 20, new Color4(0.9f, 0.8f, 0.2f, 1f), 40, 0));
                list.Add(new(DrawCommandKind.Rectangle, 72, 4, 52, 18, new Color4(0.4f, 0.9f, 0.3f, 1f), 1, 0));
                break;

            case "elliptical":
                // ⚠ The branchy half of the distance: in a corner quadrant the ellipse is turned into
                // a circle by scaling and the distance scaled back by the *smaller* semi-axis, and
                // every straight-edge band is a separate early return. A uniform radius takes one of
                // those five paths.
                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 6, 6, 52, 44, new Color4(0.2f, 0.6f, 0.95f, 1f), 0, 0),
                    BoxStyle.Rounded(CornerRadii.Circular(2f, 12f, 0f, 20f))
                );

                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 66, 6, 52, 44, new Color4(0.95f, 0.3f, 0.4f, 1f), 0, 0),
                    BoxStyle.Rounded(new CornerRadii(
                        new Vector2(20f, 6f),
                        new Vector2(6f, 20f),
                        new Vector2(18f, 18f),
                        new Vector2(3f, 14f)
                    ))
                );

                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 6, 62, 112, 56, new Color4(0.4f, 0.9f, 0.3f, 1f), 0, 0),
                    BoxStyle.Rounded(new CornerRadii(
                        new Vector2(40f, 12f),
                        new Vector2(12f, 40f),
                        new Vector2(30f, 30f),
                        new Vector2(60f, 5f)
                    ))
                );

                break;

            case "bordered":
                // The band between the edge and the thickness inside it, which is the difference of
                // two coverages and so two evaluations of the corner arc rather than one.
                list.Add(new(DrawCommandKind.Border, 8, 8, 50, 40, new Color4(0.2f, 0.6f, 0.95f, 1f), 9, 3));
                list.Add(new(DrawCommandKind.Border, 66, 8, 50, 40, new Color4(0.95f, 0.3f, 0.4f, 1f), 16, 2));
                list.Add(new(DrawCommandKind.Border, 8, 60, 108, 52, new Color4(0.4f, 0.9f, 0.3f, 1f), 20, 6));
                break;

            case "blurred":
                // ⚠ `DrawCommandKind.Shadow` and not `UiDropShadow`, which is the trap this case is
                // for. A drop shadow is a *layer* and reaches the frame through `ShadowSurface` and
                // the colour pipeline; a `box-shadow` is a record with `axis.z` set that goes through
                // the box shader like any other box. `UiCompositingTests` has the first and not the
                // second, which is how the missing branch survived.
                list.Add(new(DrawCommandKind.Shadow, 20, 20, 40, 30, new Color4(0f, 0f, 0f, 0.6f), 8, blur ? 4 : 0));
                list.Add(new(DrawCommandKind.Shadow, 66, 60, 40, 40, new Color4(0.1f, 0.1f, 0.4f, 0.8f), 18, blur ? 9 : 0));
                break;

            case "gradient":
                // The gradient shapes and interpolation spaces, on rounded corners: the fill and the
                // coverage are computed from the same record and a shader that read the wrong lane of
                // it would draw a correct shape in the wrong colours.
                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 6, 6, 52, 40, new Color4(0.2f, 0.6f, 0.95f, 1f), 0, 0),
                    new BoxStyle(CornerRadii.Uniform(12f), new Color4(0.95f, 0.3f, 0.4f, 1f), new Vector2(0.6f, 0.8f)) {
                        Shape = GradientShape.Linear,
                        Space = GradientSpace.Srgb
                    }
                );

                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 66, 6, 52, 40, new Color4(0.9f, 0.8f, 0.2f, 1f), 0, 0),
                    new BoxStyle(CornerRadii.Uniform(12f), new Color4(0.1f, 0.2f, 0.8f, 1f), new Vector2(0f, 1f)) {
                        Shape = GradientShape.Radial,
                        Space = GradientSpace.Oklab
                    }
                );

                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 6, 60, 112, 56, new Color4(0.4f, 0.9f, 0.3f, 1f), 0, 0),
                    new BoxStyle(CornerRadii.Uniform(24f), new Color4(0.9f, 0.2f, 0.7f, 1f), new Vector2(1f, 0f)) {
                        Shape = GradientShape.Conic,
                        Space = GradientSpace.Linear
                    }
                );

                break;

            case "tiled":
                // ⚠ <b>The placement lanes, and all three readings of them in one frame — because the
                // failure they invite is a branch taken on one executor and not the other.</b> Both
                // sides guard the whole tiling block on `area.zw`, so a fixture that only exercised
                // the default would compare two fast paths and say nothing about the branch.
                //
                // Repeating on both axes: `wrap_tile` runs twice per fragment, and a `fract`-based
                // wrap mirrors instead of repeating — which is invisible on a tile centred in its box
                // and obvious on this one, whose tile sits in the corner.
                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 6, 6, 52, 40, new Color4(0.2f, 0.6f, 0.95f, 1f), 0, 0),
                    new BoxStyle(CornerRadii.Uniform(8f), new Color4(0.95f, 0.3f, 0.4f, 1f), new Vector2(1f, 0f)) {
                        Shape = GradientShape.Linear,
                        Space = GradientSpace.Srgb,
                        AreaCentre = new Vector2(-13f, -10f),
                        AreaHalf = new Vector2(13f, 10f),
                        PaintExtent = new Vector2(13f, 10f)
                    }
                );

                // ⚠ Clipped on one axis and tiled on the other, which is `background-repeat: repeat-y`
                // and the case a per-axis sign encoding can get exactly half right. The clip runs
                // through the same `coverage_of` the box's edge uses, so its antialiased boundary is
                // where the two executors' emulated derivatives have to agree.
                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 66, 6, 52, 40, new Color4(0.9f, 0.8f, 0.2f, 1f), 0, 0),
                    new BoxStyle(CornerRadii.Uniform(8f), new Color4(0.1f, 0.2f, 0.8f, 1f), Vector2.Zero) {
                        Shape = GradientShape.Radial,
                        Space = GradientSpace.Oklab,
                        AreaCentre = new Vector2(-13f, 0f),
                        AreaHalf = new Vector2(-13f, 10f),
                        PaintExtent = new Vector2(13f, 10f)
                    }
                );

                // ⚠ An off-centre ramp inside a tile that is not the box: `paint.xy` and `area.xy` are
                // two different origins, and a shader that subtracted one of them twice — or the wrong
                // one — still draws a plausible radial gradient. The reach is the farthest-side pair
                // from that centre, which is `tile + abs(centre)`.
                Styled(
                    list,
                    new(DrawCommandKind.Rectangle, 6, 60, 112, 56, new Color4(0.4f, 0.9f, 0.3f, 1f), 0, 0),
                    new BoxStyle(CornerRadii.Uniform(16f), new Color4(0.9f, 0.2f, 0.7f, 1f), Vector2.Zero) {
                        Shape = GradientShape.Radial,
                        Space = GradientSpace.Linear,
                        AreaCentre = new Vector2(-28f, 0f),
                        AreaHalf = new Vector2(-28f, -28f),
                        PaintCentre = new Vector2(-14f, 12f),
                        PaintExtent = new Vector2(42f, 40f)
                    }
                );

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "no such fixture");
        }

        list.EndFrame();
        return list;
    }

    /// <summary>One box with a style entry in the side buffer behind it.</summary>
    static void Styled(DrawList list, DrawCommand command, BoxStyle style) =>
        list.Add(command with { Offset = list.AddBox(style), Length = 1 });

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
