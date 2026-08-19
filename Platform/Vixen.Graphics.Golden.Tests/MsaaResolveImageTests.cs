// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics.RenderGraph;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A multisampled attachment, resolved — the first frame in this repository to have any samples
///     in it at all.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything under this was already built and nothing above it could reach.</b> A
///         texture's <c>SampleCount</c> has always reached <c>vkCreateImage</c>, a pipeline's has
///         always reached <c>RasterizationSamples</c>, and <c>ColourAttachment.ResolveView</c> with
///         <see cref="StoreAction.Resolve" /> is honoured by the Vulkan and the WebGPU backend both.
///         What never existed was a way for a pass to <em>name</em> the pair, so no frame anywhere —
///         fixture, sample or engine — had ever run a multisampled pipeline, and the whole lower half
///         was untested by construction.
///     </para>
///     <para>
///         <b>The assertion is coverage, not a reference image, and that is deliberate.</b> Vulkan
///         does not fix the standard sample locations' effect to the bit and two conformant drivers
///         weight a diagonal's edge differently, so a committed PNG of an antialiased edge is a
///         cross-driver tolerance argument with nothing behind it. What every conformant driver
///         <em>must</em> produce is intermediate values: a pixel a diagonal crosses is part covered,
///         and the resolve averages covered samples with uncovered ones. So the fixture counts how
///         many pixels came back neither the clear colour nor the triangle's, which a single-sampled
///         draw cannot produce even one of.
///     </para>
///     <para>
///         ⚠ <b>The A/B is in the fixture rather than in a diff directory.</b>
///         <see cref="TheSameEdgeIsHardWithoutMsaaAndSoftWithIt" /> draws the identical triangle both
///         ways in one test and compares the two counts, so the claim "MSAA did something" is checked
///         against the same geometry on the same driver in the same run — which is the comparison a
///         picture beside a picture is only a proxy for.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class MsaaResolveImageTests {
    /// <summary>How many samples the fixtures ask for. Four is the tier every desktop driver has.</summary>
    const int Samples = 4;

    /// <summary>The clear, and the triangle's colour. Far apart so an average of the two is obvious.</summary>
    static readonly (byte R, byte G, byte B) Background = (8, 8, 13);

    static readonly (byte R, byte G, byte B) Foreground = (255, 32, 16);

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>
    ///     <b>The A/B.</b> One diagonal edge, drawn twice on one device: once single-sampled, once at
    ///     4× and resolved. The second has a partly covered border along the diagonal and the first
    ///     cannot have one.
    /// </summary>
    /// <remarks>
    ///     <b>Measured on MoltenVK at 128×128: 128 partly covered pixels resolved against 0 aliased.</b>
    ///     One per row, which is what a corner-to-corner 45° edge has to give and is the number that
    ///     makes the fixture self-checking — a count near 128 is the geometry, and a count near 0 or
    ///     near 16 384 is something else entirely. The bar is set at 32 rather than at 128 because a
    ///     driver whose sample pattern is arranged differently may leave a row or two fully covered,
    ///     and nothing that fails to resolve can produce even one.
    /// </remarks>
    [Fact]
    public void TheSameEdgeIsHardWithoutMsaaAndSoftWithIt() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        int aliased;

        using (var single = fixture!) {
            aliased = Intermediates(Diagonal(single, samples: 1));
        }

        if (!TryOpen(out var second)) {
            return;
        }

        int resolved;

        using (var multi = second!) {
            if (!multi.Device.Features.SupportsSampleCount(Samples)) {
                Assert.Skip($"the device does not support {Samples}× rasterisation");
                return;
            }

            resolved = Intermediates(Diagonal(multi, Samples));
        }

        Assert.Equal(0, aliased);

        Assert.True(
            resolved > 32,
            $"the resolved edge has {resolved} partly covered pixels against the aliased edge's "
            + $"{aliased}. A resolve that never happened gives exactly the same picture as the "
            + "single-sampled draw."
        );
    }

    /// <summary>
    ///     And the picture itself, so a driver that resolves to the wrong place is caught as well as
    ///     one that does not resolve at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The reference exists to pin the <em>geometry</em>: which side of the diagonal is filled,
    ///         that the resolve lands in the target's own texels rather than offset by one, and that
    ///         the interior is the flat triangle colour rather than an average of four samples of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="Tolerance.Edges" /> would be the wrong bound here and it is the obvious one
    ///         to reach for. It allows 0.2% of pixels — 32 of these 16 384 — to differ by any amount at
    ///         all, and the entire signal this fixture is about is a band of roughly 128 pixels along
    ///         one diagonal. A driver whose sample pattern weights that band differently moves every
    ///         one of them, so the fraction is raised to cover the whole band while the channel bound
    ///         stays tight enough that the flat regions on either side cannot drift.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheResolvedEdgeLandsWhereTheGeometryIs() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        if (!owned.Device.Features.SupportsSampleCount(Samples)) {
            Assert.Skip($"the device does not support {Samples}× rasterisation");
            return;
        }

        GoldenImage.Verify("msaa-resolve-4x", Diagonal(owned, Samples), new(16, 0.02));
    }

    /// <summary>
    ///     The flat regions either side of the edge are exactly what they would be at one sample.
    /// </summary>
    /// <remarks>
    ///     The guard against the resolve being read as a blur. A resolve averages the samples <em>of
    ///     one pixel</em>; a fully covered pixel's four samples are all the triangle and a fully
    ///     uncovered one's are all the clear, so neither moves. A fixture that only counted
    ///     intermediates would pass just as happily against a pass that blurred the whole image.
    /// </remarks>
    [Fact]
    public void AFullyCoveredPixelIsUntouchedByTheResolve() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        if (!owned.Device.Features.SupportsSampleCount(Samples)) {
            Assert.Skip($"the device does not support {Samples}× rasterisation");
            return;
        }

        var image = Diagonal(owned, Samples);

        // Deep inside the triangle, and far outside it — see Diagonal for where the geometry is.
        AssertPixel(image, 20, 108, Foreground, "well inside the triangle");
        AssertPixel(image, 108, 20, Background, "well outside the triangle");
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One triangle with a long diagonal hypotenuse, drawn at the given sample count.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A right triangle filling the lower-left half of the frame, so the hypotenuse runs corner
    ///         to corner: the longest edge a square frame has, and one at 45° — the angle a sample grid
    ///         resolves most visibly and the angle a nearest-neighbour copy masquerading as a resolve
    ///         cannot fake.
    ///     </para>
    ///     <para>
    ///         At one sample the multisampled texture and the resolve are not declared at all and the
    ///         draw goes straight into the imported target, which is what makes the A/B honest: the two
    ///         legs differ in the sample count and the resolve, and in nothing else.
    ///     </para>
    /// </remarks>
    static Bitmap Diagonal(Fixture owned, int samples) {
        var colour = owned.ColourTarget($"resolve-{samples}x");

        var pipeline = owned.Pipeline(
            owned.Shader("packed.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled,
            [new(12, [new(0, VertexFormat.Float32X2, 0), new(1, VertexFormat.UNorm8X4, 8)])],
            sampleCount: samples
        );

        var packed = (uint)(0xFF000000 | ((uint)Foreground.B << 16) | ((uint)Foreground.G << 8) | Foreground.R);

        // Bottom-left, bottom-right, top-left in clip space — clip y = +1 is the TOP, so this fills
        // the lower-left half of the picture and the hypotenuse runs from the top-left corner to the
        // bottom-right one.
        var vertices = owned.Buffer<byte>(
            Packed([(-1f, -1f, packed), (1f, -1f, packed), (-1f, 1f, packed)]),
            BufferUsage.Vertex
        );

        var clear = new Core.Mathematics.Color4(
            Background.R / 255f,
            Background.G / 255f,
            Background.B / 255f,
            1f
        );

        if (samples == 1) {
            owned.Graph.AddPass("draw", pass => {
                pass.ColourAttachment(colour, LoadAction.Clear, clear);
                pass.SideEffect();

                pass.Execute(context => {
                    context.CommandList.BindPipeline(pipeline);
                    context.CommandList.BindVertexBuffer(0, vertices);
                    context.CommandList.Draw(3);
                });
            });

            return owned.Render(colour);
        }

        // ⚠ ColourTarget usage and nothing else. A multisampled image is not sampleable through an
        // ordinary sampler and does not get a Sampled flag; what the rest of a frame reads is the
        // resolve. It is also transient — the samples exist for this pass and the resolve is what
        // survives, which is exactly what lets the graph alias and drop this.
        var multisampled = owned.Graph.CreateTexture(
            new TextureDescription(
                PixelFormat.Rgba8UNorm,
                Fixture.Side,
                Fixture.Side,
                TextureUsage.ColourTarget,
                SampleCount: samples,
                Name: "samples"
            )
        );

        owned.Graph.AddPass("draw", pass => {
            pass.ColourAttachment(multisampled, LoadAction.Clear, clear, resolve: colour);
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindPipeline(pipeline);
                context.CommandList.BindVertexBuffer(0, vertices);
                context.CommandList.Draw(3);
            });
        });

        return owned.Render(colour);
    }

    /// <summary>
    ///     How many pixels are neither the clear colour nor the triangle's — the partly covered ones.
    /// </summary>
    /// <remarks>
    ///     A generous margin around each of the two, because <c>Rgba8UNorm</c> rounds and a driver may
    ///     land a fully covered pixel one level off. What it must not do is land a pixel <em>between</em>
    ///     the two without partial coverage to explain it, and the two colours here are 247 levels
    ///     apart in red so the margin has nothing to do with the answer.
    /// </remarks>
    static int Intermediates(in Bitmap image) {
        var count = 0;

        for (var index = 0; index < image.Width * image.Height; index++) {
            var red = image.Pixels[index * 4];

            if (red > Background.R + 4 && red < Foreground.R - 4) {
                count++;
            }
        }

        return count;
    }

    static void AssertPixel(in Bitmap image, int x, int y, (byte R, byte G, byte B) expected, string where) {
        var offset = ((y * image.Width) + x) * 4;
        var actual = (image.Pixels[offset], image.Pixels[offset + 1], image.Pixels[offset + 2]);

        Assert.True(
            Math.Abs(actual.Item1 - expected.R) <= 2
            && Math.Abs(actual.Item2 - expected.G) <= 2
            && Math.Abs(actual.Item3 - expected.B) <= 2,
            $"({x}, {y}) is {where} and came back {actual} rather than {expected}. A fully covered "
            + "pixel's samples are all the same, so the resolve has nothing to average."
        );
    }

    /// <summary>Vertices for <c>packed.vert</c>: two floats and a packed ABGR colour.</summary>
    static byte[] Packed(ReadOnlySpan<(float X, float Y, uint Colour)> vertices) {
        var bytes = new byte[vertices.Length * 12];

        for (var index = 0; index < vertices.Length; index++) {
            var (x, y, colour) = vertices[index];
            var offset = index * 12;
            BitConverter.TryWriteBytes(bytes.AsSpan(offset), x);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4), y);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset + 8), colour);
        }

        return bytes;
    }
}
