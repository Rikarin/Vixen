// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics.RenderGraph;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A multisampled <em>depth</em> attachment, resolved by a named rule — and the proof that the
///     rule is the one the engine's reversed-Z asks for.
/// </summary>
/// <remarks>
///     <para>
///         <b>Resolved depth cannot be looked at, so this fixture makes the GPU look at it instead.</b>
///         A depth buffer is not a picture: reading one back and asserting on floats would test the
///         readback path as much as the resolve, and `Fixture.Render` reads colour. So the resolved
///         depth becomes the depth attachment of a <em>second</em> pass, which draws one full-screen
///         quad at a probe depth between the two the first pass wrote. Every pixel of the returned
///         image is then a one-bit answer to "did the probe beat what the resolve kept", and the
///         partly covered pixels along the diagonal are where the two candidate rules disagree.
///     </para>
///     <para>
///         ⚠ <b>The claim is a difference between two runs, not an absolute count.</b> Vulkan does not
///         fix the standard sample locations' effect to the bit, so how many pixels a 45° edge leaves
///         partly covered is a driver's business. What no conformant driver may do is return the same
///         picture for <see cref="DepthResolveMode.Min" /> and <see cref="DepthResolveMode.Max" /> —
///         those two disagree on exactly the partly covered pixels, and a resolve that silently did
///         nothing, or averaged, or ignored the mode would return one picture for both. That is the
///         failure this exists to catch, and it is invisible to every assertion that looks at one run.
///     </para>
///     <para>
///         ⚠ <b>Which way the difference runs is the reversed-Z assertion.</b> Near is depth 1 here,
///         so <c>Max</c> keeps the near triangle along the edge and <c>Min</c> keeps the far clear.
///         The probe sits between them, so it fails against <c>Max</c>'s edge and passes against
///         <c>Min</c>'s — <c>Min</c> must therefore light <em>more</em> pixels. Asserting the
///         direction rather than the magnitude is what makes this test fail if the mode mapping is
///         swapped, which is the one mistake here that renders perfectly.
///     </para>
///     <para>
///         <b>Measured on MoltenVK at 128×128: <c>Min</c> lights 8256 pixels and <c>Max</c> 8128, a
///         difference of exactly 128.</b> That is one pixel per row, which is what a corner-to-corner
///         45° edge has to give and is the same count the sibling <c>MsaaResolveImageTests</c> arrives
///         at from the other direction — it counts the partly covered pixels in a resolved colour
///         image and finds 128 of them. The two fixtures measure the same band by different means,
///         which is what makes the number worth writing down. Both counts straddle half the image
///         (8192) because the diagonal halves it and the edge band flips entirely.
///     </para>
///     <para>
///         The bars below are set well away from 128 for the reason that fixture gives: a driver
///         arranging its samples differently may leave a row or two fully covered. What no driver may
///         do is return zero difference.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class DepthResolveImageTests {
    /// <summary>How many samples the fixture asks for.</summary>
    const int Samples = 4;

    /// <summary>Far, in reversed-Z terms — what the multisampled depth buffer is cleared to.</summary>
    const float Far = 0.2f;

    /// <summary>Near. The triangle the first pass draws, and the larger value under reversed-Z.</summary>
    const float Near = 0.8f;

    /// <summary>
    ///     The probe the second pass draws, strictly between <see cref="Far" /> and
    ///     <see cref="Near" /> so that which of the two the resolve kept decides whether it survives.
    /// </summary>
    const float ProbeDepth = 0.5f;

    static readonly (byte R, byte G, byte B) Background = (8, 8, 13);

    static readonly (byte R, byte G, byte B) Probed = (255, 32, 16);

    static readonly ushort[] QuadIndices = [0, 1, 2, 2, 1, 3];

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
    ///     <b>The A/B.</b> One multisampled depth buffer, resolved twice by the two rules that
    ///     disagree, probed identically both times.
    /// </summary>
    [Fact]
    public void MinAndMaxResolveTheSameDepthBufferDifferently() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        int minimum;
        int maximum;

        using (var owned = fixture!) {
            minimum = Survivors(Run(owned, DepthResolveMode.Min));
        }

        if (!TryOpen(out fixture)) {
            return;
        }

        using (var owned = fixture!) {
            maximum = Survivors(Run(owned, DepthResolveMode.Max));
        }

        // ⚠ The direction, which is the reversed-Z claim. Min keeps the far clear along the edge and
        // the probe beats it; Max keeps the near triangle and the probe loses to it. Swap the two
        // arms of the backend's mode mapping and this inequality is the only thing that notices.
        Assert.True(
            minimum > maximum,
            $"Min resolved {minimum} surviving pixels and Max resolved {maximum}. Under reversed-Z "
            + "Min keeps the farther sample, which the probe beats, so it must light more pixels than "
            + "Max — equal counts mean the mode never reached the driver."
        );

        // The band that flipped is the partly covered edge. A corner-to-corner diagonal at 128×128
        // gives about one such pixel per row; the bar is well under that so a driver whose samples
        // sit differently still passes, and nothing that ignores the mode can clear it at all.
        var flipped = minimum - maximum;

        Assert.True(
            flipped is > 16 and < 4 * Fixture.Side,
            $"{flipped} pixels changed between the two rules. A 45° edge across {Fixture.Side} rows "
            + "should flip on the order of one pixel per row — a count near zero is a resolve that "
            + "ignored the mode, and a huge one is not an edge."
        );

        // Both runs must still be recognisably half a picture. Without this the test would pass on a
        // frame that drew nothing under Max and a sliver under Min.
        var half = Fixture.Side * Fixture.Side / 2;

        Assert.InRange(maximum, half / 2, half * 3 / 2);
        Assert.InRange(minimum, half / 2, half * 3 / 2);
    }

    /// <summary>
    ///     ⚠ <b>The control that makes the A/B mean something.</b> The same two rules over a
    ///     <em>single-sampled</em> depth buffer must agree exactly — there is only one sample, so
    ///     every rule keeps it.
    /// </summary>
    /// <remarks>
    ///     Without this, a difference above could be the second pass reacting to something other than
    ///     the resolve. It also pins the geometry: the two fixtures differ in the sample count and the
    ///     resolve and in nothing else, so the counts here are what the multisampled legs are a
    ///     perturbation of.
    /// </remarks>
    [Fact]
    public void WithoutMultisamplingTheRuleCannotMatter() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        int minimum;
        int maximum;

        using (var owned = fixture!) {
            minimum = Survivors(Run(owned, DepthResolveMode.Min, samples: 1));
        }

        if (!TryOpen(out fixture)) {
            return;
        }

        using (var owned = fixture!) {
            maximum = Survivors(Run(owned, DepthResolveMode.Max, samples: 1));
        }

        Assert.Equal(minimum, maximum);
    }

    /// <summary>
    ///     Draws a near triangle into a multisampled depth buffer, resolves it by
    ///     <paramref name="mode" />, then probes the resolve with a full-screen quad at
    ///     <see cref="ProbeDepth" /> and returns what survived.
    /// </summary>
    /// <param name="owned">The fixture.</param>
    /// <param name="mode">The rule the resolve keeps.</param>
    /// <param name="samples">
    ///     How many samples the first pass has. At one the depth buffer is not multisampled and is
    ///     not resolved at all — the second pass probes it directly, which is what makes the control
    ///     above an honest comparison.
    /// </param>
    static Bitmap Run(Fixture owned, DepthResolveMode mode, int samples = Samples) {
        var colour = owned.ColourTarget($"probe-{mode}-{samples}x");
        var resolved = owned.DepthTarget($"resolved-{mode}-{samples}x");

        // ⚠ A vec3 vertex, so that depth is data. The vec2 layouts the other fixtures use write z = 0
        // for every vertex, which would leave both legs probing the clear value.
        VertexBufferLayout[] layout = [
            new(
                sizeof(float) * 7,
                [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, sizeof(float) * 3)]
            )
        ];

        var writer = owned.Pipeline(
            owned.Shader("depth.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Default,
            layout,
            sampleCount: samples
        );

        // Bottom-left, bottom-right, top-left in clip space — clip y = +1 is the TOP, so this covers
        // the lower-left half and its hypotenuse runs corner to corner at 45°. That diagonal is where
        // pixels are partly covered, and partly covered pixels are the only ones the two rules
        // disagree about.
        var triangle = owned.Buffer<float>(
            [
                -1f, -1f, Near, 1f, 1f, 1f, 1f,
                1f, -1f, Near, 1f, 1f, 1f, 1f,
                -1f, 1f, Near, 1f, 1f, 1f, 1f
            ],
            BufferUsage.Vertex
        );

        var depth = samples == 1
            ? resolved
            : owned.Graph.CreateTexture(
                new TextureDescription(
                    PixelFormat.Depth32Float,
                    Fixture.Side,
                    Fixture.Side,
                    TextureUsage.DepthStencilTarget,
                    SampleCount: samples,
                    Name: $"depth-samples-{mode}"
                )
            );

        // ⚠ A multisampled colour attachment beside it, because every attachment of one pass has the
        // same sample count. Nothing reads it and nothing resolves it — the samples this fixture is
        // about are the depth buffer's.
        var scratch = owned.Graph.CreateTexture(
            new TextureDescription(
                PixelFormat.Rgba8UNorm,
                Fixture.Side,
                Fixture.Side,
                TextureUsage.ColourTarget,
                SampleCount: samples,
                Name: $"scratch-{mode}-{samples}x"
            )
        );

        owned.Graph.AddPass("write depth", pass => {
            pass.ColourAttachment(scratch, LoadAction.Clear, default);

            // Zero is far under reversed-Z; this clears to 0.2, which the probe at 0.5 beats.
            if (samples == 1) {
                pass.DepthAttachment(depth, LoadAction.Clear, Far);
            } else {
                pass.DepthAttachment(depth, LoadAction.Clear, Far, resolve: resolved, resolveMode: mode);
            }

            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindPipeline(writer);
                context.CommandList.BindVertexBuffer(0, triangle);
                context.CommandList.Draw(3);
            });
        });

        var prober = owned.Pipeline(
            owned.Shader("depth.vert.spv", ShaderStage.Vertex),
            owned.Shader("mesh.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,

            // Test but do not write: the resolved buffer is read-only in this pass, which is what
            // lets it be loaded rather than cleared.
            DepthStencilState.TestOnly,
            layout
        );

        var clear = new Core.Mathematics.Color4(
            Background.R / 255f,
            Background.G / 255f,
            Background.B / 255f,
            1f
        );

        var quad = owned.Buffer<float>(Screen(), BufferUsage.Vertex);
        var indices = owned.Buffer<ushort>(QuadIndices, BufferUsage.Index);

        owned.Graph.AddPass("probe depth", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, clear);
            pass.DepthAttachment(resolved, LoadAction.Load, readOnly: true);
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.BindPipeline(prober);
                context.CommandList.BindVertexBuffer(0, quad);
                context.CommandList.BindIndexBuffer(indices, IndexFormat.UInt16);
                context.CommandList.DrawIndexed(QuadIndices.Length);
            });
        });

        return owned.Render(colour);
    }

    /// <summary>A full-screen quad at one depth, in the vec3 layout.</summary>
    static float[] Screen() => [
        -1f, -1f, ProbeDepth, Probed.R / 255f, Probed.G / 255f, Probed.B / 255f, 1f,
        1f, -1f, ProbeDepth, Probed.R / 255f, Probed.G / 255f, Probed.B / 255f, 1f,
        -1f, 1f, ProbeDepth, Probed.R / 255f, Probed.G / 255f, Probed.B / 255f, 1f,
        1f, 1f, ProbeDepth, Probed.R / 255f, Probed.G / 255f, Probed.B / 255f, 1f
    ];

    /// <summary>How many pixels the probe won — that is, how many are its colour rather than the clear.</summary>
    /// <remarks>
    ///     A generous margin, because <c>Rgba8UNorm</c> rounds. The two colours are 247 levels apart
    ///     in red, so the margin has nothing to do with the answer.
    /// </remarks>
    static int Survivors(in Bitmap image) {
        var count = 0;

        for (var index = 0; index < image.Width * image.Height; index++) {
            if (image.Pixels[index * 4] > Probed.R - 8) {
                count++;
            }
        }

        return count;
    }
}
