// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     What each quality tier actually looks like.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim no structural test can make.</b>
///         <c>RenderQualityTests</c> asserts that a tier resolves to a set of numbers and
///         <c>StandardFrameTests</c> asserts that those numbers reach the right node properties. Both
///         pass for a frame in which the numbers reach a node that ignores them, a pass that runs and
///         writes nothing, and a composite that blows the level to white — all three of which happened
///         this session and every one of which was found by a human looking at a window.
///     </para>
///     <para>
///         So this renders the same scene four times, once per tier, through the real expansion, and
///         compares each against a committed picture. A frame that changes appearance without anyone
///         intending it fails a build here.
///     </para>
///     <para><b>Determinism.</b> What is pinned and what is allowed to run:</para>
///     <list type="bullet">
///         <item><description>
///             <b>Antialiasing is FXAA, not TAA.</b> TAA converges over frames against a jitter
///             sequence and a history buffer, and a golden that depends on which frame it stopped at is
///             a golden that fails when anything upstream changes the frame count. FXAA is a single
///             pass over the resolved image and settles in one frame. What that costs is stated below.
///         </description></item>
///         <item><description>
///             <b>Exposure is metered, and the meter's adaptation is pinned.</b> The histogram meter
///             eases towards its target at <c>1 − exp(−dt·rate)</c> per frame, so a metered fixture's
///             brightness is otherwise a picture of how many frames it was left running.
///             <see cref="TierScene" /> sets the node's <c>DeltaTime</c> to ten seconds, which makes
///             that fraction one: the meter arrives at its target on the first frame and stays. The
///             <em>rate</em> is untouched, because the rate is what a regression in the adaptation
///             would move. Metered rather than fixed on purpose — the tier's
///             <c>post.localExposure</c> only ever runs with the meter, so a fixed-exposure fixture
///             could not see that knob at all.
///         </description></item>
///         <item><description>
///             <b>The scene is photometric.</b> Twelve thousand lux of sun, a sky in cd/m², a lamp in
///             lumens. Not decoration: the meter and the tone curve are calibrated in real units, so a
///             scene authored in 0–1 colours is a dozen stops under everything downstream of it and
///             the frame comes back flat white. That was this fixture's first picture.
///         </description></item>
///         <item><description>
///             <b>Two frames are rendered and the second is kept.</b> Not for convergence — nothing
///             here converges — but because the first frame of any frame graph is the one where a
///             history plane, a reduced depth pyramid or a reprojected volume has no previous frame to
///             read, and a fixture that only ever renders frame one would pass while every such plane
///             is wrong from frame two onwards. The volumetric fog's temporal reprojection is exactly
///             that: it reads its own last volume.
///         </description></item>
///         <item><description>
///             <b>Nothing moves between the two frames.</b> The camera, the lights and the transforms
///             are written once, so a reprojection that reads last frame's matrices reads the same
///             ones — which is what makes two frames stable rather than a longer settle.
///         </description></item>
///     </list>
///     <para>
///         <b>What the four pictures are supposed to differ by.</b> The tier moves nothing about
///         <em>what</em> the frame is — the seven knobs below are identical across all four — only the
///         fidelity and cost of it. Between Low and Epic the engine table moves, among others: two
///         cascades at 1024 to four at 2048; 75 m of shadow distance to 200; fog off to on;
///         volumetric fog off to on with 128 slices; bloom off to six levels; depth of field off to
///         on; vignette off to on; and FXAA from Performance to Quality. If two tiers' pictures were
///         identical the suite would be asserting that quality does nothing, which is why
///         <see cref="TheFourTiersDoNotAgree" /> exists beside the four goldens.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class StandardFrameTierImageTests {
    /// <summary>How many frames each tier renders before its picture is kept.</summary>
    const int Frames = 2;

    /// <summary>
    ///     The frame every tier expands, with the tier as the only difference between the four runs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>GI and reflections are off, and that is a statement about the harness rather than a
    ///     preference.</b> <see cref="GiMode.Probes" /> emits a clipmap, an irradiance field, a
    ///     surface cache and a screen-probe gather, all of which need scene data this fixture does not
    ///     stage — a distance field of the boxes, a probe grid, cards. What that costs is stated in
    ///     the README: the tiers' GI knobs are not under a picture here, only their shadow, fog and
    ///     post ones.
    /// </remarks>
    static StandardFrameAsset Frame => new() {
        Name = "Frame",
        Shadows = ShadowMode.Cascades,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Fxaa,
        Exposure = ExposureMode.Automatic,
        Particles = false
    };

    static GraphicsCompositorAsset Document => new() { Game = Frame };

    [Theory]
    [InlineData(QualityTier.Low, "tier-low")]
    [InlineData(QualityTier.Medium, "tier-medium")]
    [InlineData(QualityTier.High, "tier-high")]
    [InlineData(QualityTier.Epic, "tier-epic")]
    public void ATierLooksLikeItsReference(QualityTier tier, string name) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        using var scene = Stage(owned, tier);

        // Edges rather than Flat: the frame is shaded, tonemapped and antialiased, so almost every
        // pixel is interpolated — and FXAA's blend decisions sit on a luminance comparison, which is
        // where two conformant drivers may land a pixel differently.
        GoldenImage.Verify(name, scene.Frames(Frames), Tolerance.Shaded);
    }

    /// <summary>
    ///     No two tiers draw the same picture.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The assertion the four goldens cannot make between them.</b> Four references that
    ///         each match themselves are four references that would go on matching if the tier stopped
    ///         reaching the frame entirely — every one of them would simply be re-recorded as the same
    ///         picture on the next <c>--update-golden</c>, and the suite would report four passes for
    ///         a scalability system that does nothing.
    ///     </para>
    ///     <para>
    ///         Stated as a pixel count rather than as inequality: two tiers whose pictures differ in
    ///         nine pixels are two tiers that agree for every practical purpose, and the threshold is
    ///         what makes this fail when a knob is quietly narrowed rather than only when it is
    ///         removed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheFourTiersDoNotAgree() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var pictures = new Dictionary<QualityTier, Bitmap>();

        foreach (var tier in (QualityTier[])[QualityTier.Low, QualityTier.Medium, QualityTier.High, QualityTier.Epic]) {
            using var scene = Stage(owned, tier);

            pictures[tier] = scene.Frames(Frames);
        }

        foreach (var (left, right) in Pairs(pictures.Keys)) {
            var comparison = GoldenImage.Compare(pictures[left], pictures[right], Tolerance.Shaded);
            var required = Least(left, right);

            Assert.True(
                comparison.Fraction > required,
                $"{left} and {right} render the same picture: only {comparison.DifferingPixels} of "
                + $"{comparison.TotalPixels} pixels ({comparison.Fraction:P3}) differ by more than "
                + $"{Tolerance.Shaded.Channel}/255 where {required:P3} is the least this pair may, and "
                + $"the worst channel anywhere is {comparison.WorstChannel}/255. Either the tiers' "
                + "knobs stopped reaching the frame, or the scene stopped containing anything they "
                + "move."
            );
        }
    }

    /// <summary>
    ///     The least two tiers may differ by, as a fraction of the frame.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two per cent everywhere except <b>High against Epic, which is measured at 0.061% — ten
    ///         pixels — and is a finding rather than a threshold</b>. Everything Epic adds over High in
    ///         this frame is either invisible at 128² or gated off by the frame's own knobs: the
    ///         volumetric grid goes from 64 slices to 128 and the shadow through it is already smooth;
    ///         bloom goes from five pyramid levels to six, and level six of a 128-pixel frame is two
    ///         pixels across; depth of field goes from 16 gather samples to 24 of the same radius;
    ///         FXAA goes from Balanced to Quality, which moves the pixels either side of one edge; and
    ///         its remaining moves — reflection steps, the probe tile size, the AO scales — belong to
    ///         the GI and reflection stacks this fixture cannot host.
    ///     </para>
    ///     <para>
    ///         So the pair is held to "differ at all" rather than exempted. Ten pixels is not evidence
    ///         that Epic is worth its cost; zero would be evidence that the tier stopped resolving,
    ///         which is the regression this test is for.
    ///     </para>
    /// </remarks>
    static double Least(QualityTier left, QualityTier right) =>
        (left, right) is (QualityTier.High, QualityTier.Epic) or (QualityTier.Epic, QualityTier.High)
            ? 0d
            : 0.02;

    static IEnumerable<(QualityTier Left, QualityTier Right)> Pairs(IEnumerable<QualityTier> tiers) {
        var list = tiers.ToArray();

        for (var i = 0; i < list.Length; i++) {
            for (var j = i + 1; j < list.Length; j++) {
                yield return (list[i], list[j]);
            }
        }
    }

    /// <summary>
    ///     The scene: three surfaces, a caster, a floor and a lamp, none of them on an axis.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What is in frame, and why each is:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><description>
    ///             a <b>wide floor</b>, rough, which is what a shadow lands on and what fog is read
    ///             against — a tier golden of a wall alone proves very little;
    ///         </description></item>
    ///         <item><description>
    ///             a <b>tall caster</b>, off to the left and behind, throwing a shadow diagonally
    ///             across the near floor — the shadowed edge, which is the one thing a bias or a
    ///             comparison flip moves;
    ///         </description></item>
    ///         <item><description>
    ///             a <b>smooth metal slab</b>, low roughness, off to the right and tilted in depth,
    ///             which is where the environment and the lamp's highlight show and where a roughness
    ///             threshold would;
    ///         </description></item>
    ///         <item><description>
    ///             a <b>bright emissive block</b>, small and near, which is what bloom has to find —
    ///             the tier turns bloom off entirely at Low and gives it six levels at Epic, and
    ///             without something bright in frame those two are the same picture.
    ///         </description></item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>Every one of the four is at a different height as well as a different bearing.</b>
    ///         Varying only x and z would leave the scene mirror-symmetric about the ground plane,
    ///         which is the axis a shadow projection folds around.
    ///     </para>
    /// </remarks>
    static TierScene Stage(Fixture fixture, QualityTier tier) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device), Compiler));

        var scene = TierScene.Open(fixture, effects, Document, tier);

        var casters = scene.Stages.TryGetValue("Shadow", out var shadow) ? shadow.Mask : default;
        var opaque = scene.Stages["Opaque"].Mask;

        // The floor does not cast — it is what is cast *on*, and a ground plane in a cascade is a
        // caster that covers every tile and shadows nothing.
        scene.Box(new(0.4f, -0.25f, -0.6f), new(9f, 0.25f, 9f), Rough, opaque);

        scene.Box(new(-1.85f, 1.05f, -2.35f), new(0.5f, 1.05f, 0.62f), Rough, opaque | casters);
        scene.Box(new(2.15f, 0.34f, -0.35f), new(1.15f, 0.34f, 0.78f), Smooth, opaque | casters);
        scene.Box(new(0.95f, 0.42f, 1.85f), new(0.28f, 0.42f, 0.28f), Bright, opaque | casters);

        scene.Commit(opaque);

        return scene;
    }

    /// <summary>The floor and the caster: matte, and a colour no light in the scene is.</summary>
    static Material Rough => Compile(new(0.42f, 0.46f, 0.38f), metalness: 0f, roughness: 0.85f);

    /// <summary>The slab: metal and smooth, so it shows the environment rather than the sun.</summary>
    static Material Smooth => Compile(new(0.85f, 0.82f, 0.76f), metalness: 1f, roughness: 0.12f);

    /// <summary>The block bloom has to find.</summary>
    /// <remarks>
    ///     Emissive rather than merely light-coloured: the tonemap is what bloom is measured against,
    ///     and a surface that is only brightly lit is at the mercy of the sun's own intensity. An
    ///     emissive value well over one is over the bloom threshold whatever the exposure.
    /// </remarks>
    static Material Bright => Compile(
        new(0.9f, 0.55f, 0.25f),
        metalness: 0f,
        roughness: 0.5f,
        emissive: new(9_000f, 5_200f, 2_100f)
    );

    static Material Compile(Vector3 colour, float metalness, float roughness, Vector3? emissive = null) {
        var features = new List<IMaterialFeature> {
            new MetalRoughnessFeature { BaseColor = colour, Metalness = metalness, Roughness = roughness }
        };

        if (emissive is { } glow) {
            features.Add(new EmissiveFeature { EmissiveColor = glow, Intensity = 1f });
        }

        var compilation = MaterialCompiler.Compile(
            new() { ShaderName = "ForwardPlus", Features = features }
        );

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>The sources one named shader compiles against.</summary>
    /// <remarks>
    ///     Everything for the passes that compose from a material; the library's own tree for the
    ///     rest. See <see cref="Compiling" /> for why the two cannot share one compiler.
    /// </remarks>
    static Vixen.ShaderCompiler.RavenEffectCompiler Compiler(string shaderName) => RavenEffects.Everything();

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the tier goldens may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
