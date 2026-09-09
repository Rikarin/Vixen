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
///     The anamorphic streak, against one bright texel on a black field.
/// </summary>
/// <remarks>
///     <para>
///         <b>A closed form rather than a picture, because the property that matters is a shape and
///         not a look.</b> One bright texel, a horizontal axis, and no taper: the streak must be a run
///         of non-zero pixels along that one row whose length is a function of how many blur passes
///         ran, and every pixel off that row must be <em>exactly</em> what the scene was. The second
///         half is the whole difference between this effect and bloom — an isotropic blur of any
///         radius puts light above and below the texel, so it fails the off-axis assertion at every
///         setting, and no eyeball comparison of two smears would say so.
///     </para>
///     <para>
///         <b>The length is arithmetic and not a measurement copied back in.</b> A pass of
///         <c>S</c> taps each side at a stride of <c>d</c> texels widens a run by <c>S·d</c> at each
///         end, and the host raises the stride to <c>2S + 1</c> per pass — which is exactly the width
///         the previous pass produced, so the intervals tile with no gap and the run stays
///         contiguous. One pass of four taps is 9 pixels; two is 81.
///     </para>
///     <para>
///         ⚠ <b><see cref="LightStreakRenderer.Attenuation" /> is 1 here and that is the fixture
///         being honest.</b> The shipped taper is what makes a streak look like light rather than a
///         bar, but it also makes the far end of the run arbitrarily dim — so a run length measured
///         under it would really be a measurement of where the taper crossed the eight-bit floor,
///         which moves with the intensity and with the format. With no taper the run has an exact
///         end, and the taper is a look rather than a claim.
///     </para>
///     <para>
///         ⚠ <b>The source is <c>Rgba32Float</c> at 100 cd/m² and the target is <c>Rgba8UNorm</c>.</b>
///         The renderer works in photometric units and its threshold is in the source's, so a
///         0–1 tint would be under the default threshold of one and this whole fixture would be a
///         picture of a pass that correctly declined to streak anything.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class LightStreakImageTests {
    const int Side = Fixture.Side;

    /// <summary>Where the one bright texel is. Mid-frame, so no tap of either pass falls outside.</summary>
    const int Centre = Side / 2;

    /// <summary>Taps each side of centre, per pass.</summary>
    const int Samples = 4;

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

    /// <summary>The smear runs along one axis and the frame is untouched off it.</summary>
    /// <remarks>
    ///     ⚠ The off-axis half is the assertion an isotropic blur fails, and it is stated as
    ///     <em>exactly</em> the scene rather than as "dark": a bloom narrow enough to look like a
    ///     streak still writes a few codes above and below, and a bound of "less than something"
    ///     would be a threshold nobody could defend.
    /// </remarks>
    [Theory]
    [InlineData(1, (Samples * 2) + 1)]
    [InlineData(2, ((Samples * ((Samples * 2) + 1)) + Samples) * 2 + 1)]
    public void TheStreakIsARunAlongOneAxisAndNothingOffIt(int blurs, int length) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, blurs, new(1f, 0f));

        // The run: every pixel of it lit, and the two pixels past each end dark. Both halves, because
        // "the middle is lit" is satisfied by a streak of any length including the whole row.
        var first = Centre - (length / 2);
        var last = Centre + (length / 2);

        for (var x = first; x <= last; x++) {
            Assert.True(
                image.Pixels[image.Offset(x, Centre)] > 0,
                $"({x}, {Centre}) is dark, so the run is shorter than the {length} pixels {blurs} "
                + $"pass(es) of {Samples} taps must produce. A stride that stayed at one — a texel "
                + "size taken from the target rather than from the plane the pass reads — gives a "
                + "run of nine however many passes run."
            );
        }

        Assert.Equal(0, image.Pixels[image.Offset(first - 1, Centre)]);
        Assert.Equal(0, image.Pixels[image.Offset(last + 1, Centre)]);

        // And nothing at all off the axis, which is what makes it a streak rather than a bloom.
        for (var y = 0; y < Side; y++) {
            if (y == Centre) {
                continue;
            }

            for (var x = 0; x < Side; x++) {
                Assert.True(
                    image.Pixels[image.Offset(x, y)] == 0,
                    $"({x}, {y}) is lit and it is not on the streak's row, so the blur spread in two "
                    + "dimensions — which is bloom, and bloom is the one thing this effect exists "
                    + "because it cannot do."
                );
            }
        }
    }

    /// <summary>Turn the axis and the run turns with it, which is the instrument for the pair above.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this, both assertions above are satisfied by a pass that streaks nothing at
    ///     all in <c>y</c> for a reason of its own</b> — a shader that hardcoded the horizontal, or a
    ///     host that dropped <c>direction</c> and let the declared default stand. Rotating the axis
    ///     ninety degrees and asserting the same shape down a column is what tells the two apart.
    /// </remarks>
    [Fact]
    public void TheAxisIsTheDocumentsAndNotTheShadersDefault() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, blurs: 1, new(0f, 1f));

        for (var y = Centre - Samples; y <= Centre + Samples; y++) {
            Assert.True(
                image.Pixels[image.Offset(Centre, y)] > 0,
                $"({Centre}, {y}) is dark with the axis turned to vertical, so `direction` did not "
                + "reach the shader and the smear stayed on the axis the .rvn declares."
            );
        }

        for (var x = 0; x < Side; x++) {
            if (x == Centre) {
                continue;
            }

            Assert.Equal(0, image.Pixels[image.Offset(x, Centre)]);
        }
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>The streak chain over a black field with one bright texel in the middle of it.</summary>
    static Bitmap Render(Fixture fixture, int blurs, Vector2 direction) {
        var device = fixture.Device;

        fixture.Graph.Reset();

        var texels = new Vector4[Side * Side];

        // Photometric, and two orders of magnitude over the threshold: the pass measures luminance in
        // the source's units, so a 0–1 highlight would be below the default threshold of one and the
        // whole chain would correctly produce nothing.
        texels[(Centre * Side) + Centre] = new(100f, 100f, 100f, 1f);

        var field = fixture.Sampled("field", Side, MemoryMarshal.AsBytes<Vector4>(texels), PixelFormat.Rgba32Float);
        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

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
                    Path.Combine("PostFx", "LightStreak.rvn")
                )
            )
        );

        using var streak = new LightStreakRenderer {
            Name = "Streak",
            Source = "Field",
            Output = "Display",
            Format = PixelFormat.Rgba8UNorm,

            // ⚠ Full scale, so the blur's grid and the frame's are the same one and the run length
            // below is in the frame's pixels. At the shipped quarter scale the arithmetic still holds
            // in the reduced plane's texels, and the composite's bilinear upsample would then smear
            // the answer across four rows — which is a correct streak and an untestable one.
            Scale = 1f,
            Threshold = 1f,
            BlurPasses = blurs,
            Samples = Samples,
            Direction = direction,
            Attenuation = 1f,
            Intensity = 1f,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Descriptors = allocator
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = new(Side, Side), Game = streak };

        compositor.Imports["Field"] = new(
            field.Texture,
            field.View,
            new(
                PixelFormat.Rgba32Float,
                Side,
                Side,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "field"
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
        Assert.Null(streak.Degraded);
        Assert.Equal(blurs + 2, streak.PassCount);

        foreach (var pass in streak.Passes) {
            Assert.True(pass.PipelineCount > 0, $"{pass.Name} compiled no pipeline, so it drew nothing");
        }

        return fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                commands.Barrier(new([], [new(field.Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
                commands.CopyBufferToTexture(field.Staging, 0, new(field.Texture), new(Side, Side, 1));

                commands.Barrier(
                    new([], [new(field.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
                );
            }
        );
    }
}
