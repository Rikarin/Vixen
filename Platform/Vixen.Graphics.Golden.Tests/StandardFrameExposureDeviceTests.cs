// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Both exposure modes record a frame the validation layer accepts.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of the Standard Frame no picture covers.</b>
///         <see cref="StandardFrameTierImageTests" /> meters on purpose — <c>post.localExposure</c>
///         only ever runs with the meter — so the four goldens render
///         <see cref="ExposureMode.Automatic" /> and the other mode, which is
///         <see cref="StandardFrameAsset" />'s <em>default</em> and therefore what every frame that
///         does not say otherwise gets, was never once recorded on a device.
///     </para>
///     <para>
///         ⚠ <b>A permutation folds code, not bindings.</b> Reflection reports a shader's bindings, not
///         a variant's, so <c>Tonemap.rvn</c>'s <c>exposureBuffer</c> is in the layout whether or not
///         <c>UseExposureBuffer</c> is on — and <see cref="EffectSetWriter" /> fills a set whole or not
///         at all. A fixed-exposure frame that left the slot alone therefore drew with a descriptor
///         nothing had written, every frame, for as long as the frame ran.
///     </para>
///     <para>
///         Asserted as "no validation errors" rather than against a reference picture, because that is
///         what the defect was: the frames were plausible. <see cref="TierScene.Frames" /> throws on
///         anything the layer says, before the submit as well as after it.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class StandardFrameExposureDeviceTests {
    /// <summary>Two frames, for <see cref="StandardFrameTierImageTests" />'s reason.</summary>
    const int Frames = 2;

    [Theory]
    [InlineData(ExposureMode.Fixed)]
    [InlineData(ExposureMode.Automatic)]
    public void AFrameInEitherExposureModeRecordsCleanly(ExposureMode exposure) {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        using var scene = Stage(owned, exposure);

        // The picture is not the assertion — the throw inside this call is. A fixed-exposure frame
        // with the binding unfilled reports "the descriptor [...] is being used in draw but has never
        // been updated" once per draw that reads set 2, on every frame.
        var picture = scene.Frames(Frames);

        Assert.Equal(Fixture.Side, picture.Width);
    }

    /// <summary>
    ///     The two modes do not draw the same picture.
    /// </summary>
    /// <remarks>
    ///     What keeps the test above honest. A tonemap that stopped reading the meter's buffer at all
    ///     — the shape a "fix" that simply dropped the permutation would have — records just as
    ///     cleanly, and this is the assertion it would fail. The scene is photometric and lit at 12 000
    ///     lux, so the meter lands nowhere near the authored EV the fixed path resolves.
    /// </remarks>
    [Fact]
    public void MeteringChangesTheFrame() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        using var fixedScene = Stage(owned, ExposureMode.Fixed);
        var authored = fixedScene.Frames(Frames);

        using var meteredScene = Stage(owned, ExposureMode.Automatic);
        var metered = meteredScene.Frames(Frames);

        var comparison = GoldenImage.Compare(authored, metered, Tolerance.Shaded);

        Assert.True(
            comparison.Fraction > 0.02,
            $"A metered frame and a fixed one render the same picture: only "
            + $"{comparison.DifferingPixels} of {comparison.TotalPixels} pixels differ. Either the "
            + "meter stopped reaching the tonemap, or the tonemap stopped reading it."
        );
    }

    /// <summary>The frame both cases expand, with the exposure mode as the only difference.</summary>
    /// <remarks>
    ///     High rather than the platform's pick, because High is the first tier whose
    ///     <c>post.localExposure</c> is on — which is the second node in this subsystem that resolves
    ///     an exposure, and the one whose pivot has to agree with the tonemap's.
    /// </remarks>
    static GraphicsCompositorAsset Document(ExposureMode exposure) =>
        new() {
            Game = new StandardFrameAsset {
                Name = "Frame",
                Shadows = ShadowMode.Cascades,
                Gi = GiMode.Off,
                Reflections = ReflectionsMode.Off,
                Antialiasing = AntialiasingMode.Fxaa,
                Exposure = exposure,
                Particles = false
            }
        };

    static TierScene Stage(Fixture fixture, ExposureMode exposure) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device), _ => RavenEffects.Everything()));

        var scene = TierScene.Open(fixture, effects, Document(exposure), QualityTier.High);

        var casters = scene.Stages.TryGetValue("Shadow", out var shadow) ? shadow.Mask : default;
        var opaque = scene.Stages["Opaque"].Mask;

        scene.Box(new(0.4f, -0.25f, -0.6f), new(9f, 0.25f, 9f), Rough, opaque);
        scene.Box(new(-1.85f, 1.05f, -2.35f), new(0.5f, 1.05f, 0.62f), Rough, opaque | casters);
        scene.Box(new(0.95f, 0.42f, 1.85f), new(0.28f, 0.42f, 0.28f), Bright, opaque | casters);

        scene.Commit(opaque);

        return scene;
    }

    static Material Rough => Compile(new(0.42f, 0.46f, 0.38f), roughness: 0.85f);

    /// <summary>The block a meter has to be dragged by, and bloom has to find.</summary>
    static Material Bright => Compile(
        new(0.9f, 0.55f, 0.25f),
        roughness: 0.5f,
        emissive: new(9_000f, 5_200f, 2_100f)
    );

    static Material Compile(Vector3 colour, float roughness, Vector3? emissive = null) {
        var features = new List<IMaterialFeature> {
            new MetalRoughnessFeature { BaseColor = colour, Metalness = 0f, Roughness = roughness }
        };

        if (emissive is { } glow) {
            features.Add(new EmissiveFeature { EmissiveColor = glow, Intensity = 1f });
        }

        var compilation = MaterialCompiler.Compile(new() { ShaderName = "ForwardPlus", Features = features });

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the exposure frames may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
