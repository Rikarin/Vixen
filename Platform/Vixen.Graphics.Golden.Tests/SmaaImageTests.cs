// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     SMAA, on a hard diagonal, measured rather than looked at.
/// </summary>
/// <remarks>
///     <para>
///         <b>The picture this suite would normally commit cannot say whether antialiasing worked.</b>
///         A reference PNG says "the frame is what it was last time", which is a regression test and
///         nothing more — and it says it about a file generated on one driver and compared on
///         another. What "SMAA resolved this edge" means is a number, so this fixture computes one.
///     </para>
///     <para>
///         <b>The number is how straight the edge is.</b> A step edge running at one pixel in four is
///         a staircase: sum a row's brightness and you get how far along the row the boundary is, and
///         for a hard edge that sum is an <em>integer</em> — it jumps by one every fourth row and is
///         flat in between. For a correctly resolved edge the same sum is the boundary's real
///         position, which moves a quarter of a pixel per row. So fit a straight line to the
///         per-row sums and take the root-mean-square residual: quantising a line to integers leaves
///         a residual of about 1/√12 = 0.289, and an antialiased edge leaves the fraction of that
///         which the filter recovered.
///     </para>
///     <para>
///         ⚠ <b>Both orientations, because they are different code.</b> A near-vertical edge is a run
///         of <em>vertical</em> edge texels crossed by horizontal ones, and a near-horizontal edge is
///         the transpose; the weight pass walks each with its own loop, its own crossing lookups and
///         its own two channels of the coverage table. Getting one right and the other's channels
///         swapped is a blend of the correct size in the wrong direction — an edge that is smooth and
///         in the wrong place, which no single-orientation fixture would see.
///     </para>
///     <para>
///         ⚠ <b>The Linux leg is the only one that runs this.</b> See the suite's README: macOS and
///         Windows have no Vulkan driver and skip. The bounds below are deliberately wide of the
///         measurement — they separate "the filter ran" from "it did not" rather than pinning a
///         driver's arithmetic — so that they are not a tolerance one machine happens to meet.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class SmaaImageTests {
    /// <summary>How far the edge moves per row, as one in this many.</summary>
    /// <remarks>
    ///     Four, so the staircase has runs of four texels — inside the weight pass's sixteen-step
    ///     search, and long enough that the crossing edges at a run's ends are unambiguous. A slope
    ///     of one in one is a 45° edge, which is the case the orthogonal path cannot help and the
    ///     diagonal detector this shader does not have exists for.
    /// </remarks>
    const int Run = 4;

    /// <summary>What a hard step edge's residual is, from quantisation alone.</summary>
    /// <remarks>
    ///     A line sampled and rounded to integers has residuals uniform over half a unit either way,
    ///     whose standard deviation is 1/√12. This fixture measures it rather than assuming it, and
    ///     the constant is here to say what the measurement should land on.
    /// </remarks>
    const double Quantised = 0.2886751;

    /// <summary>
    ///     A hard diagonal really is a staircase, which is what the resolved frame is measured against.
    /// </summary>
    /// <remarks>
    ///     The A of the A/B, and it is not a formality: if the source were already smooth the second
    ///     half of this fixture would be measuring nothing. It is also the check on the metric —
    ///     a straightness measure that did not report 0.289 on a hard step is a measure that has a
    ///     bug of its own, and would then report anything at all about the filter.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_hard_diagonal_is_a_staircase(bool transposed) {
        var source = Aliased(transposed);
        var residual = Straightness(source, transposed);

        Assert.Equal(Quantised, residual, 0.03);
    }

    /// <summary>
    ///     <b>And SMAA takes most of the staircase out of it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole chain on a device: the coverage table generated on the host and uploaded by
    ///         the node's own transfer pass, the edge pass, the walk, the lookup and the blend — every
    ///         part of it, against a picture whose correct answer is a straight line.
    ///     </para>
    ///     <para>
    ///         A morphological filter cannot reach zero. It has one sample per pixel and it
    ///         redistributes rather than adds, so the two texels at each end of a run — where the
    ///         crossing edge is and the coverage is smallest — keep some of the step. What it can do
    ///         is take out most of it, and the bound below is set where a filter that ran and a filter
    ///         that did not are on opposite sides with room to spare.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Smaa_resolves_a_hard_diagonal(bool transposed) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var source = Aliased(transposed);
        var resolved = Render(owned, source);

        var before = Straightness(source, transposed);
        var after = Straightness(resolved, transposed);

        Assert.True(
            after < before * 0.6,
            $"the edge was {before:0.0000} pixels from straight and SMAA left it at {after:0.0000}, "
            + "which is not a resolve"
        );

        // And it did it by blending rather than by moving: the two plateaus are untouched, so
        // whatever softening happened happened at the edge. A filter that blurred the whole frame
        // would pass the straightness bound above and fail this one.
        Assert.Equal(255, Grey(resolved, 4, 4));
        Assert.Equal(0, Grey(resolved, Fixture.Side - 5, Fixture.Side - 5));

        // The pair, where CI already uploads this suite's pictures from. Nothing asserts on them —
        // the numbers above are the assertion — but a residual is a hard thing to picture and the
        // two files are not.
        Pair(transposed ? "smaa-diagonal-horizontal" : "smaa-diagonal-vertical", source, resolved);
    }

    /// <summary>Writes the before and the after where a human can look at them.</summary>
    static void Pair(string name, byte[] aliased, in Bitmap resolved) {
        Directory.CreateDirectory(GoldenImage.DiffDirectory);

        PngCodec.Save(
            Path.Combine(GoldenImage.DiffDirectory, $"{name}.aliased.png"),
            new Bitmap(Fixture.Side, Fixture.Side, aliased)
        );

        PngCodec.Save(Path.Combine(GoldenImage.DiffDirectory, $"{name}.resolved.png"), resolved);
    }

    /// <summary>
    ///     <b>An edge a twentieth as bright is resolved exactly as well, which is what "relative"
    ///     means.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ The trap the whole shader's threshold arithmetic exists to avoid. This engine's
    ///         frame is metered in cd/m² and a tonemapped one is not, so an edge detector with an
    ///         absolute threshold finds every boundary in the first and none at all in the second —
    ///         and a pass that finds no edges is pixel-identical to a pass that never ran. Every
    ///         luminance in <c>Smaa.rvn</c> is therefore divided by the brightest in the
    ///         neighbourhood before anything is compared to a threshold.
    ///     </para>
    ///     <para>
    ///         The same staircase at a contrast of 12/255 is the measurement that separates the two.
    ///         It is 0.047 of full scale, well under the reference threshold of 0.1, so an absolute
    ///         detector rejects it outright and leaves the picture exactly as it found it; a relative
    ///         one sees a boundary of full local contrast and resolves it as thoroughly as the bright
    ///         edge above. The residual is normalised by the plateau, so the two are the same number.
    ///     </para>
    ///     <para>
    ///         The other direction — a frame far <em>above</em> one — is the same division and is not
    ///         measured here, because this suite reads its pictures back through an eight-bit target:
    ///         a hundredfold source clips every blended texel to white, and what that measures is the
    ///         readback rather than the filter.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_low_contrast_edge_is_resolved_just_as_well() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        const byte Dim = 12;

        var bright = Aliased(transposed: false);
        var dim = Aliased(transposed: false, plateau: Dim);

        var resolvedBright = Straightness(Render(owned, bright), transposed: false);
        var resolvedDim = Straightness(Render(owned, dim), transposed: false, plateau: Dim);

        // Wide enough for the coarser quantisation a twelve-level plateau gives the measurement, and
        // nowhere near the 0.289 an absolute threshold would leave the dim edge sitting at.
        Assert.True(
            Math.Abs(resolvedBright - resolvedDim) < 0.05,
            $"the bright edge resolved to {resolvedBright:0.0000} and the dim one to {resolvedDim:0.0000}, "
            + "so the threshold is reading an absolute contrast rather than a relative one"
        );

        Assert.True(
            resolvedDim < Quantised * 0.6,
            $"the dim edge was left at {resolvedDim:0.0000}, which is the staircase it started as"
        );
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Runs the chain over one source picture and reads the result back.</summary>
    static Bitmap Render(Fixture fixture, byte[] pixels) {
        var device = fixture.Device;

        // A fixture that renders twice declares into the same graph twice, and a compiled graph has
        // already culled its passes and assigned its memory.
        fixture.Graph.Reset();

        var source = fixture.Owned(
            "Source",
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            PixelFormat.Rgba8UNorm
        );

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);
        var staging = fixture.Buffer<byte>(pixels, BufferUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // Not the whole library: a source set holding the material tree compiles nothing without a
        // composition, and this pass has none. What it needs is what it imports.
        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "Smaa.rvn")
                )
            )
        );

        using var smaa = new SmaaRenderer {
            Name = "Smaa",
            Source = "Source",
            Output = "Display",

            // Eight bits, because the target read back is eight bits and a half-float intermediate
            // would only add a conversion between two of them.
            Format = PixelFormat.Rgba8UNorm,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Descriptors = allocator
        };

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = smaa
        };

        compositor.Imports["Source"] = new(source.Texture, source.View, source.Description);

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();

        var frame = compositor.Build(fixture.Graph, effects, device);

        // ⚠ Asserted rather than assumed. A shader the effect system cannot resolve is a miss, and a
        // node that got no effect draws nothing — which is a picture indistinguishable from a pass
        // nobody scheduled, and would read here as "SMAA did not change the edge".
        Assert.Empty(effects.Misses);
        Assert.Null(smaa.Degraded);

        foreach (var pass in smaa.Passes) {
            Assert.True(pass.PipelineCount > 0, $"{pass.Name} compiled no pipeline, so it drew nothing");
        }

        var picture = fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                commands.Barrier(
                    new([], [new(source.Texture, ResourceState.Undefined, ResourceState.CopyDestination)])
                );

                commands.CopyBufferToTexture(
                    staging,
                    0,
                    new(source.Texture),
                    new(Fixture.Side, Fixture.Side, 1)
                );

                commands.Barrier(
                    new([], [new(source.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
                );
            }
        );

        // The table is uploaded by a pass of the node's own, inside the graph. If it were not, every
        // lookup would read undefined memory and the blend would be a random offset — which reads as
        // an edge that is smooth and in the wrong place.
        Assert.True(smaa.Uploaded, "the coverage table never reached the device");

        return picture;
    }

    // --- The picture and the measurement ------------------------------------

    /// <summary>
    ///     A hard step edge at one pixel in <see cref="Run" />, white on one side and black on the
    ///     other.
    /// </summary>
    /// <remarks>
    ///     Written texel by texel rather than rasterised, because a rasteriser would decide how much
    ///     of the boundary pixel is covered and that decision is the thing under test. Here every
    ///     pixel is exactly one or exactly the other, which is the worst case a resolve can be given.
    /// </remarks>
    static byte[] Aliased(bool transposed, byte plateau = 255) {
        var pixels = new byte[Fixture.Side * Fixture.Side * 4];

        for (var y = 0; y < Fixture.Side; y++) {
            for (var x = 0; x < Fixture.Side; x++) {
                var across = transposed ? y : x;
                var along = transposed ? x : y;

                var white = across < 32 + (along / Run);
                var at = ((y * Fixture.Side) + x) * 4;

                pixels[at] = pixels[at + 1] = pixels[at + 2] = white ? plateau : (byte)0;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>How far the edge is from straight, in pixels, as an RMS residual.</summary>
    /// <remarks>
    ///     <para>
    ///         Each line across the edge is summed: with white on one side and black on the other,
    ///         the sum of the brightnesses <em>is</em> the boundary's position along that line, and a
    ///         partially covered pixel contributes its coverage. A hard edge therefore gives integers
    ///         and a resolved one gives the real position.
    ///     </para>
    ///     <para>
    ///         The residual is against the least-squares line through those positions rather than
    ///         against the line the picture was drawn from, so a filter that shifted the whole edge by
    ///         a constant — or changed its slope — is not punished for it. What is measured is only
    ///         the staircase.
    ///     </para>
    ///     <para>
    ///         ⚠ The first and last <see cref="Run" /> lines are dropped. The edge leaves the picture
    ///         at both ends, and the weight pass's walk clamps at the border, so those lines are
    ///         measuring the frame's edge rather than the filter's.
    ///     </para>
    /// </remarks>
    static double Straightness(byte[] pixels, bool transposed, byte plateau = 255) =>
        Straightness(new Bitmap(Fixture.Side, Fixture.Side, pixels), transposed, plateau);

    static double Straightness(in Bitmap image, bool transposed, byte plateau = 255) {
        var positions = new List<(double Along, double Position)>();

        for (var along = Run; along < Fixture.Side - Run; along++) {
            var sum = 0.0;

            for (var across = 0; across < Fixture.Side; across++) {
                sum += (transposed ? Grey(image, along, across) : Grey(image, across, along))
                    / (double)plateau;
            }

            positions.Add((along, sum));
        }

        var meanAlong = positions.Average(entry => entry.Along);
        var meanPosition = positions.Average(entry => entry.Position);

        var covariance = positions.Sum(e => (e.Along - meanAlong) * (e.Position - meanPosition));
        var variance = positions.Sum(e => (e.Along - meanAlong) * (e.Along - meanAlong));

        var slope = covariance / variance;
        var intercept = meanPosition - (slope * meanAlong);

        var squares = positions.Sum(e => Math.Pow(e.Position - ((slope * e.Along) + intercept), 2));

        return Math.Sqrt(squares / positions.Count);
    }

    static int Grey(in Bitmap image, int x, int y) => image.Pixels[image.Offset(x, y)];

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
