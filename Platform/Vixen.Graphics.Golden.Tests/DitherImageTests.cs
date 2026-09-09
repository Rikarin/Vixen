// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Output dithering, against a ramp that crosses exactly one code.
/// </summary>
/// <remarks>
///     <para>
///         <b>What a dither does is trade amplitude for density, and that is what these assert.</b> A
///         gradient that changes by less than one code across tens of pixels is rounded to a flat run
///         and then steps — and a straight edge in a smooth field is the one thing the eye is best at
///         finding. Noise of about the size of a step makes each pixel round up with a probability
///         equal to how far through the step it really was, so a run a quarter of the way up comes
///         back a quarter covered rather than flat.
///     </para>
///     <para>
///         <b>The fixture is one code wide on purpose.</b> A ramp from <c>100/255</c> to
///         <c>101/255</c> across the frame quantises, undithered, to exactly two values with one hard
///         edge down the middle: the left quarter is <em>flat</em>, and everything below is a
///         statement about that flat. A wider ramp would have several edges and the assertions would
///         be about where they fell.
///     </para>
///     <para>
///         ⚠ <b>"The image changed" is satisfied by any noise, including one added in the wrong
///         space.</b> So the two halves are asserted separately: the density has to track the ramp —
///         the left quarter brightens by about an eighth of a code and the right quarter dims by
///         about an eighth — and the whole frame's mean has to be <em>unmoved</em>, because a dither
///         that shifts the mean is a bias. Grain, added in the same pass one line earlier, moves the
///         first and would also move the second at any amplitude that showed up in the first.
///     </para>
///     <para>
///         ⚠ <b>The mean assertion is what makes the triangular PDF load-bearing rather than
///         decorative.</b> A single uniform sample would satisfy it too — a uniform dither is
///         unbiased in the mean — and what a triangular one additionally buys is that the error's
///         <em>variance</em> stops depending on the signal, which is noise that visibly swells and
///         fades across a gradient rather than a constant texture. That second moment is not asserted
///         here, and saying so is better than implying it is.
///     </para>
///     <para>
///         <b>Both sabotages, on device at the commit that added these.</b> Replacing the noise with a
///         plain uniform offset of half a code — a bias rather than a dither — turns
///         <see cref="TheFrameMeanDoesNotMove" /> red at <c>101.000</c> against <c>100.500</c>, and
///         turns the density half red as well, because a constant offset moves a flat run to the next
///         code without breaking it. Dropping the noise to zero turns the density half red alone and
///         leaves the mean where it was, which is the two halves separating exactly as intended.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class DitherImageTests {
    const int Side = Fixture.Side;

    /// <summary>The code the ramp starts on, well away from either rail.</summary>
    const int Base = 100;

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

    /// <summary>Undithered, a sub-code ramp is a flat run and then a step. That is the banding.</summary>
    /// <remarks>
    ///     The instrument before the measurement: if this did not hold there would be nothing for a
    ///     dither to fix and every assertion below would be about the fixture instead.
    /// </remarks>
    [Fact]
    public void WithoutDitherASubCodeRampIsAFlatRun() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var plain = Render(owned, dither: false);

        Assert.Equal(1, Distinct(plain, 0, Side / 4));
        Assert.Equal(1, Distinct(plain, Side * 3 / 4, Side));
    }

    /// <summary>With it, the flat run is broken and the density tracks the ramp.</summary>
    /// <remarks>
    ///     ⚠ Two assertions and not one, and the second is the one that says it is a dither. Breaking
    ///     a flat run is what <em>any</em> noise does; carrying the value in the proportion of pixels
    ///     that rounded up is what only a dither does. An eighth of a code is what the ramp's own
    ///     average over each quarter comes to, so the number is the fixture's rather than a
    ///     measurement copied back in.
    /// </remarks>
    [Fact]
    public void DitherBreaksTheFlatRunAndCarriesTheValueInTheDensity() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var plain = Render(owned, dither: false);
        var dithered = Render(owned, dither: true);

        Assert.True(
            Distinct(dithered, 0, Side / 4) >= 2,
            "the flat quarter came back flat with the dither on, so no noise reached the encode."
        );

        var leftPlain = Mean(plain, 0, Side / 4);
        var leftDithered = Mean(dithered, 0, Side / 4);
        var rightPlain = Mean(plain, Side * 3 / 4, Side);
        var rightDithered = Mean(dithered, Side * 3 / 4, Side);

        Assert.InRange(leftDithered - leftPlain, 0.05, 0.25);
        Assert.InRange(rightPlain - rightDithered, 0.05, 0.25);
    }

    /// <summary>And the frame's mean does not move, because a dither that shifts it is a bias.</summary>
    [Fact]
    public void TheFrameMeanDoesNotMove() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var plain = Mean(Render(owned, dither: false), 0, Side);
        var dithered = Mean(Render(owned, dither: true), 0, Side);

        Assert.True(
            Math.Abs(plain - dithered) < 0.05,
            $"the undithered frame means {plain:F3} and the dithered one {dithered:F3}: the noise is "
            + "not centred, so it is a brightness change wearing a dither's clothes."
        );
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The lens pass over a horizontal ramp spanning exactly one output code, with every other
    ///     effect in it turned off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The source is <c>Rgba32Float</c>, because a ramp staged in eight bits is already the
    ///         staircase the pass is meant to break up — the fixture would be handing the shader the
    ///         defect and asserting that it saw a gradient.
    ///     </para>
    ///     <para>
    ///         ⚠ The target is <c>Rgba8UNorm</c> and not the asset's default sRGB. A transfer curve
    ///         between the shader's number and the stored code would make "one code" a different size
    ///         at every brightness, and every assertion here is in codes.
    ///     </para>
    ///     <para>
    ///         Vignette, aberration, grain and distortion are all off: each of them would move the
    ///         picture, and two of them by more than the code this measures.
    ///     </para>
    /// </remarks>
    static Bitmap Render(
        Fixture fixture,
        bool dither,
        PixelFormat format = PixelFormat.Rgba8UNorm,
        float? flat = null
    ) {
        var device = fixture.Device;

        fixture.Graph.Reset();

        var texels = new Vector4[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                // Exactly one code across the frame, starting on one. Undithered this rounds to Base
                // for the left half and Base + 1 for the right, with nothing in between to round to.
                //
                // ⚠ Or a flat field, for the sRGB fixture, whose question is the noise's *amplitude*
                // rather than what the noise does to a gradient. A ramp would put a second source of
                // spread in the same numbers.
                var value = flat ?? (Base + ((x + 0.5f) / Side)) / 255f;

                texels[(y * Side) + x] = new(value, value, value, 1f);
            }
        }

        var ramp = fixture.Sampled("ramp", Side, MemoryMarshal.AsBytes<Vector4>(texels), PixelFormat.Rgba32Float);
        var display = fixture.Owned(
            "display",
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            format
        );

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "Vignette.rvn")
                )
            )
        );

        using var lens = new VignetteRenderer {
            Name = "Lens",
            Source = "Ramp",
            Output = "Display",
            Format = format,
            UseVignette = false,
            UseChromaticAberration = false,
            UseGrain = false,
            UseLensDistortion = false,
            UseDither = dither,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = new(Side, Side), Game = lens };

        compositor.Imports["Ramp"] = new(
            ramp.Texture,
            ramp.View,
            new(
                PixelFormat.Rgba32Float,
                Side,
                Side,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "ramp"
            ),
            ResourceState.ShaderRead
        );

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();

        var frame = compositor.Build(fixture.Graph, effects, device);

        Assert.Empty(effects.Misses);
        Assert.True(lens.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        return fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                commands.Barrier(new([], [new(ramp.Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
                commands.CopyBufferToTexture(ramp.Staging, 0, new(ramp.Texture), new(Side, Side, 1));

                commands.Barrier(
                    new([], [new(ramp.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
                );
            }
        );
    }

    /// <summary>A linear value dark enough that one stored sRGB code is a very small linear step.</summary>
    /// <remarks>
    ///     ⚠ <b>0.005 and not 0.01, because the separation this fixture rests on is the slope.</b>
    ///     One stored code here is about <c>0.0004</c> of linear, so a dither sized as a flat
    ///     <c>1/255</c> of linear is about nine and a half codes each side — a spread nothing could
    ///     mistake for a dither. It is also comfortably clear of black: the undithered encode lands
    ///     near code 16, so neither rail clips the noise and the range below measures the noise
    ///     rather than the clamp.
    /// </remarks>
    const float Shadow = 0.005f;

    /// <summary>A linear value where one stored sRGB code is a large linear step.</summary>
    const float Highlight = 0.9f;

    /// <summary>On an sRGB target the noise is one stored code at both ends of the range.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half <c>DitherImageTests</c> deliberately said nothing about until #1181,
    ///         and the half every shipped frame is in.</b> Every fixture above stages
    ///         <c>Rgba8UNorm</c> so that "one code" is one number; <c>VignetteAsset.Format</c>
    ///         defaults to <c>Rgba8UNormSrgb</c> and <c>StandardFrame</c>'s lens node takes that
    ///         default, so the target a frame actually dithers into has a transfer curve on it. A
    ///         stored code is then a <i>linear</i> step whose size changes across the range by a
    ///         factor of forty-five, and a fixed <c>1/255</c> of the shader's own value is far too
    ///         much noise in the shadows — which is exactly where banding is worst — and too little
    ///         in the highlights.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The range and not the count, and not the mean.</b> A mean is unmoved by a dither
    ///         of any size at all — that is what makes it a dither — so it cannot see this. A count
    ///         of distinct codes saturates: a spread of nine codes and a spread of nineteen both
    ///         report "many". The peak-to-peak range is the amplitude, which is the quantity that is
    ///         wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two ends, because one of them alone is satisfiable by a dither that is simply
    ///         off.</b> A shader that added nothing passes the shadow ceiling perfectly. The
    ///         highlight floor is what refuses that, and it is also the end an over-correction would
    ///         fail: the linear-sized noise is about half a code up there, so the frame that is too
    ///         noisy in the shadows is too quiet in the highlights in the same picture.
    ///     </para>
    ///     <para>
    ///         Arithmetic, so the numbers are the fixture's rather than a measurement copied back
    ///         in. At <see cref="Shadow" /> the encode's slope is <c>dE/dL ≈ 9.67</c>, so a linear
    ///         step of <c>1/255</c> is <c>9.67 × 255 / 255 ≈ 9.7</c> codes each side and one stored
    ///         code is one. At <see cref="Highlight" /> the slope is <c>≈ 0.467</c>, so the same
    ///         linear step is under half a code.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OnAnSrgbTargetTheDitherIsOneStoredCodeAtBothEndsOfTheRange() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        // The instrument: an undithered flat field is one code, so anything the dithered frames
        // spread over is the noise and not the fixture.
        Assert.Equal(1, Distinct(Render(owned, dither: false, PixelFormat.Rgba8UNormSrgb, Shadow), 0, Side));

        var shadow = Range(Render(owned, dither: true, PixelFormat.Rgba8UNormSrgb, Shadow), 0, Side);
        var highlight = Range(Render(owned, dither: true, PixelFormat.Rgba8UNormSrgb, Highlight), 0, Side);

        Assert.True(
            shadow <= 5,
            $"the dither spans {shadow} stored codes at a linear {Shadow}, where about two is one code "
            + "either side. A step sized in linear units is about nine and a half codes each side "
            + "there — see `Vignette.rvn`'s `DitherStep`, and `VignetteRenderer.Configure`, which is "
            + "what chooses the variant from the attachment format."
        );

        Assert.True(
            highlight >= 2,
            $"the dither spans {highlight} stored codes at a linear {Highlight}, so it cannot carry a "
            + "value across the step beside it and the banding survives at reduced contrast. Under a "
            + "step sized in linear units this end is where the noise is too small."
        );
    }

    /// <summary>The peak-to-peak spread of the red channel over a column band, in codes.</summary>
    static int Range(in Bitmap image, int from, int to) {
        var low = 255;
        var high = 0;

        for (var y = 0; y < image.Height; y++) {
            for (var x = from; x < to; x++) {
                var value = image.Pixels[image.Offset(x, y)];

                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }
        }

        return high - low;
    }

    /// <summary>How many distinct red values a column band holds.</summary>
    static int Distinct(in Bitmap image, int from, int to) {
        var seen = new HashSet<byte>();

        for (var y = 0; y < image.Height; y++) {
            for (var x = from; x < to; x++) {
                seen.Add(image.Pixels[image.Offset(x, y)]);
            }
        }

        return seen.Count;
    }

    /// <summary>The mean red value over a column band, in codes.</summary>
    static double Mean(in Bitmap image, int from, int to) {
        var total = 0L;

        for (var y = 0; y < image.Height; y++) {
            for (var x = from; x < to; x++) {
                total += image.Pixels[image.Offset(x, y)];
            }
        }

        return (double)total / (image.Height * (to - from));
    }
}
