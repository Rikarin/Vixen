// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Engine.Renderer;
using Vixen.Rendering;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>Debug geometry and the diagnostic overlays, drawn.</summary>
/// <remarks>
///     <para>
///         <c>DebugGeometry</c>'s arithmetic is checked without a device, and what a picture adds is
///         the part that cannot be. Two things here are silently wrong in every passing unit test:
///         <b>whether the screen projection puts a pixel where it was named</b> — a y that is not
///         flipped draws the whole overlay upside down and every measurement still agrees — and
///         <b>whether the stroke font comes out as letters</b>, which no assertion about segment
///         counts can say.
///     </para>
///     <para>
///         ⚠ The properties are asserted before the picture is trusted, as this suite's README asks.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class DebugDrawImageTests {
    const int Side = Fixture.Side;

    /// <summary>
    ///     A screen-space overlay: a filled panel, its border, and text inside it.
    /// </summary>
    /// <remarks>
    ///     Anchored to the top-left corner, which is the assertion that matters — a projection that
    ///     agreed with Vulkan's raw clip space instead of with the engine's convention draws this at
    ///     the bottom, and the panel is placed off-centre precisely so that "upside down" cannot look
    ///     like "correct".
    /// </remarks>
    [Fact]
    public void Overlay() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("debug overlay");

        var renderer = Renderer(owned);
        owned.Owns(renderer.Dispose);

        var draw = new DebugDraw();

        // Deliberately not centred and not square: a panel in the top-left quarter says which way up
        // and which way round the projection is, and a centred one would say neither.
        draw.ScreenFill(new(8f, 8f), new(72f, 40f), new(0.10f, 0.14f, 0.30f, 1f));
        draw.ScreenRect(new(8f, 8f), new(72f, 40f), new(0.45f, 0.60f, 0.95f, 1f));
        draw.ScreenText(new(14f, 16f), "AB", new(1f, 0.85f, 0.35f, 1f), size: 22f);

        renderer.Upload(draw, Matrix4x4.Identity, new Vector2(Side, Side));

        owned.Graph.AddPass("debug overlay", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.05f, 0.05f, 0.06f, 1f));
            pass.SideEffect();
            pass.Execute(context => renderer.RecordScreen(context.CommandList));
        });

        var image = owned.Render(colour);

        Assert.Equal(0, renderer.Dropped);
        Assert.Equal(1, renderer.Draws);
        Assert.Equal(draw.ScreenCount * 2, renderer.ScreenCount);

        AssertThePanelIsInTheTopLeft(image);

        GoldenImage.Verify("debug-overlay", image, Tolerance.Edges);
    }

    /// <summary>World lines and a label that faces the camera.</summary>
    /// <remarks>
    ///     The label is what this is for. It goes through the whole world path — accumulated as text,
    ///     turned into strokes against the view's basis, projected with the view-projection — and a
    ///     basis taken from the view matrix's rows rather than its columns draws it in the wrong plane
    ///     and usually edge-on, which is invisible rather than wrong-looking.
    /// </remarks>
    [Fact]
    public void WorldLabel() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("debug world");

        var renderer = Renderer(owned);
        owned.Owns(renderer.Dispose);

        // ⚠ No depth attachment on this target, so the world half is drawn untested. The depth path
        // is LineRenderer's and the golden line fixtures already cover it.
        renderer.DepthTested = false;

        var draw = new DebugDraw();

        draw.Box(new(new(-0.7f, -0.7f, -0.7f), new(0.7f, 0.7f, 0.7f)), new(0.35f, 0.85f, 0.45f, 1f));
        draw.Arrow(Vector3.Zero, new(0f, 1.4f, 0f), new(0.95f, 0.4f, 0.4f, 1f));
        draw.Text(new(-1.5f, -1.3f, 0f), "OK", new(1f, 1f, 1f, 1f), size: 0.5f);

        var eye = new Vector3(0f, 0f, 4f);
        var view = Matrix4x4.LookAt(eye, Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverFour, 1f, 0.1f, 50f);

        renderer.Upload(draw, view, Vector2.Zero);

        owned.Graph.AddPass("debug world", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.05f, 0.05f, 0.06f, 1f));
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, view * projection));
        });

        var image = owned.Render(colour);

        Assert.Equal(0, renderer.Dropped);
        Assert.Equal(1, renderer.LabelCount);

        // One draw and not two: the viewport was zero, so there is no screen pass to record.
        Assert.Equal(1, renderer.Draws);

        GoldenImage.Verify("debug-world", image, Tolerance.Edges);
    }

    static DebugDrawRenderer Renderer(Fixture fixture) =>
        new(
            fixture.Device,
            new(
                fixture.Shader("line.vert.spv", ShaderStage.Vertex),
                fixture.Shader("line.frag.spv", ShaderStage.Fragment)
            ),
            new RenderOutput([PixelFormat.Rgba8UNorm])
        );

    /// <summary>
    ///     The panel is where it was asked for and the opposite corner is empty.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both halves are needed. "There are lit pixels in the top-left" passes for a picture drawn
    ///     the right way up and for one that happens to be symmetric; "and none in the bottom-right"
    ///     is what a flipped projection fails.
    /// </remarks>
    static void AssertThePanelIsInTheTopLeft(in Bitmap image) {
        Assert.True(Lit(image, 40, 28), "the panel's middle is not lit, so nothing was drawn there");

        Assert.False(
            Lit(image, Side - 40, Side - 28),
            "the opposite corner is lit, so the screen projection does not agree with the engine's +y up"
        );
    }

    static bool Lit(in Bitmap image, int x, int y) {
        var offset = image.Offset(x, y);
        return image.Pixels[offset] + image.Pixels[offset + 1] + image.Pixels[offset + 2] > 96;
    }

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
