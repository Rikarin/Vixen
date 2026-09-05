// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>World-space lines, drawn.</summary>
/// <remarks>
///     <para>
///         <c>LineRenderer</c> is what the editor's grid, its entity markers and its gizmo all come
///         out of, and what <c>DebugDraw</c> has been producing lines for since Phase 2 with nothing
///         to draw them. The arithmetic above it is checked without a device; what a picture adds is
///         the part that cannot be: <b>whether the pipeline agrees with the vertex layout</b>. A
///         colour read at the wrong offset draws the right lines in the wrong colours, a stride that
///         is off by a float draws every second vertex at the origin, and both pass every unit test.
///     </para>
///     <para>
///         ⚠ The properties are asserted before the picture is trusted, as this suite's README asks.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class LineImageTests {
    const int Side = Fixture.Side;

    /// <summary>
    ///     A red horizontal, a green vertical and a blue diagonal that fades along its length.
    /// </summary>
    /// <remarks>
    ///     Three colours in three directions, because a vertex layout that read the colour at the
    ///     wrong offset would still draw three lines — and the fade is what says the colour is per
    ///     <i>vertex</i> rather than per segment, which is the whole reason a grid can disappear into
    ///     the distance.
    /// </remarks>
    [Fact]
    public void Lines() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var colour = owned.ColourTarget("lines");

        var renderer = new LineRenderer(
            owned.Device,
            owned.Lines(),
            new RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);

        var red = new Color4(1f, 0.2f, 0.2f, 1f);
        var green = new Color4(0.2f, 1f, 0.2f, 1f);
        var blue = new Color4(0.2f, 0.4f, 1f, 1f);

        LineVertex[] segments = [
            // Horizontal across the middle.
            new(new Vector3(-0.8f, 0f, 0f), red),
            new(new Vector3(0.8f, 0f, 0f), red),

            // Vertical down the middle.
            new(new Vector3(0f, -0.8f, 0f), green),
            new(new Vector3(0f, 0.8f, 0f), green),

            // A diagonal that fades to nothing, which only a per-vertex colour can do.
            new(new Vector3(-0.8f, -0.8f, 0f), blue),
            new(new Vector3(0.8f, 0.8f, 0f), new Color4(blue.R, blue.G, blue.B, 0f))
        ];

        renderer.Upload(segments);

        // Clip space directly: the shader multiplies by this and the points above are already where
        // they should end up, so a projection bug cannot hide behind a camera's arithmetic.
        var identity = Matrix4x4.Identity;

        owned.Graph.AddPass("lines", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0.05f, 0.05f, 0.06f, 1f));
            pass.SideEffect();
            pass.Execute(context => renderer.Record(context.CommandList, identity, depthTested: false));
        });

        var image = owned.Render(colour);

        Assert.Equal(6, renderer.Count);
        Assert.Equal(0, renderer.Dropped);
        Assert.Equal(1, renderer.Draws);

        AssertTheHorizontalIsRed(image);
        AssertTheVerticalIsGreen(image);
        AssertTheDiagonalFades(image);

        GoldenImage.Verify("lines", image, Tolerance.Edges);
    }

    /// <summary>More vertices than fit are dropped and counted, not thrown for.</summary>
    [Fact]
    public void TooMany() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;

        var renderer = new LineRenderer(
            owned.Device,
            owned.Lines(),
            new RenderOutput([PixelFormat.Rgba8UNorm]),
            capacity: 4
        );

        owned.Owns(renderer.Dispose);
        renderer.Upload(new LineVertex[10]);

        // A debug overlay somebody left on is not a reason to take the process down, and the count is
        // what makes the truncation visible rather than a picture quietly missing its end.
        Assert.Equal(4, renderer.Count);
        Assert.Equal(6, renderer.Dropped);
    }

    /// <summary>An odd vertex count draws whole segments only.</summary>
    [Fact]
    public void HalfALine() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;

        var renderer = new LineRenderer(
            owned.Device,
            owned.Lines(),
            new RenderOutput([PixelFormat.Rgba8UNorm])
        );

        owned.Owns(renderer.Dispose);
        renderer.Upload(new LineVertex[5]);

        // The topology pairs them, so the odd one out would otherwise be joined to whatever the
        // buffer happened to hold next.
        Assert.Equal(4, renderer.Count);
        Assert.Equal(1, renderer.Dropped);
    }

    /// <summary>
    ///     The strongest pixel in a short scan, because which row a one-pixel line lands on is a
    ///     rasterisation rule and not something a test should pin.
    /// </summary>
    /// <remarks>
    ///     ⚠ A line at clip y = 0 lands on a pixel <i>edge</i> in a 128-pixel target, and the diamond
    ///     rule is free to put it on either side. Asserting an exact row is asserting a driver's
    ///     tie-break; scanning three finds the line wherever it went and still fails if it is absent.
    /// </remarks>
    static (int Red, int Green, int Blue) Strongest(in Bitmap image, int x, int y, bool vertical) {
        var best = (Red: 0, Green: 0, Blue: 0);

        for (var offset = -1; offset <= 1; offset++) {
            var sampleX = vertical ? x + offset : x;
            var sampleY = vertical ? y : y + offset;
            var sum = Red(image, sampleX, sampleY) + Green(image, sampleX, sampleY) + Blue(image, sampleX, sampleY);

            if (sum > best.Red + best.Green + best.Blue) {
                best = (Red(image, sampleX, sampleY), Green(image, sampleX, sampleY), Blue(image, sampleX, sampleY));
            }
        }

        return best;
    }

    static void AssertTheHorizontalIsRed(in Bitmap image) {
        // A quarter of the way across, well away from where the other two cross it.
        var pixel = Strongest(image, Side / 4, Side / 2, vertical: false);

        Assert.True(
            pixel.Red > 120 && pixel.Green < 120,
            $"the horizontal line is {pixel}, not red — so the colour attribute is read at the wrong offset"
        );
    }

    static void AssertTheVerticalIsGreen(in Bitmap image) {
        var pixel = Strongest(image, Side / 2, Side / 4, vertical: true);

        Assert.True(
            pixel.Green > 120 && pixel.Red < 120,
            $"the vertical line is {pixel}, not green"
        );
    }

    /// <summary>The diagonal is solid at one end and gone at the other.</summary>
    static void AssertTheDiagonalFades(in Bitmap image) {
        // Clip space has +y up and the image has it down, so the vertex at (-0.8, -0.8) — the solid
        // end — lands at the bottom left. Scanned across, for the reason Strongest gives.
        var solid = Strongest(image, 24, Side - 24, vertical: true).Blue;
        var faded = Strongest(image, Side - 24, 24, vertical: true).Blue;

        Assert.True(
            solid > faded + 40,
            $"the diagonal does not fade along its length ({solid} at one end, {faded} at the other), "
            + "so the colour is being taken per segment rather than per vertex"
        );
    }

    static int Red(in Bitmap image, int x, int y) => image.Pixels[image.Offset(x, y)];

    static int Green(in Bitmap image, int x, int y) => image.Pixels[image.Offset(x, y) + 1];

    static int Blue(in Bitmap image, int x, int y) => image.Pixels[image.Offset(x, y) + 2];

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
