// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
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

    /// <summary>The same frame with the ambient split on, which is the shape sample 13 ships.</summary>
    static StandardFrameAsset SplitFrame => Frame with {
        Name = "Split",
        Gi = GiMode.Ambient,
        Reflections = ReflectionsMode.Screen
    };

    static GraphicsCompositorAsset Document => new() { Game = Frame };

    static GraphicsCompositorAsset SplitDocument => new() { Game = SplitFrame };

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
    ///     The split frame: GI on, reflections on, and every plane the combine reads present.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The four tier goldens above cannot reach this path and never could.</b>
    ///         <see cref="Frame" /> stages <c>Gi = Off, Reflections = Off</c> and
    ///         <c>StandardFrame.Emit</c> splits on <c>frame.Gi != GiMode.Off || mirrors</c>, so all
    ///         four references are pictures of the single-target frame. Nothing above them covers
    ///         <c>ForwardPlus.SplitOutputs</c>, the albedo, normal and <c>f0</c> planes, the rebuild
    ///         in <c>!AmbientCombine</c>, or the reflection blend — which is the shape sample 13
    ///         ships and the one most of this area's recent work went into.
    ///     </para>
    ///     <para>
    ///         <b>One picture at one tier rather than a fifth row of the theory, argued on cost.</b>
    ///         The theory renders four scenes and <see cref="TheFourTiersDoNotAgree" /> renders four
    ///         more; this is a ninth, about a twelfth added to the slowest fixture in the suite. A
    ///         fifth row would be four more scenes — half again — to buy tier variation this fixture
    ///         has already measured itself unable to see: the remarks on <see cref="Least" /> name
    ///         the reflection steps, the probe tile size and the AO scales as exactly the knobs that
    ///         move nothing at 128², and High against Epic differs by ten pixels for that reason.
    ///         What a second tier would add here is a second picture of the same arithmetic.
    ///     </para>
    ///     <para>
    ///         <b>High, and not for the shipping-default reason.</b> Its <c>gi.ssaoScale</c> is 0.5,
    ///         so the occlusion planes arrive at half the frame's resolution and the combine's
    ///         bilateral upsample is a path this picture actually takes — at Epic's scale of 1 the
    ///         upsample degenerates to its own texel at weight one and is not under test at all.
    ///     </para>
    ///     <para>
    ///         <b><see cref="GiMode.Ambient" /> and not <see cref="GiMode.Probes" />.</b> Probes emit
    ///         an irradiance field and a surface cache over scene data this fixture does not stage —
    ///         cards, a probe grid. Ambient emits the clipmap and the occlusion pair over the same
    ///         split, which is all the split itself needs, and is what turns <c>useSpecular</c> and
    ///         <c>useReflections</c> on together in <c>AmbientCombineRenderer.Configure</c>: with
    ///         both the shading pass stops writing its own specular ambient and this pass adds the
    ///         traced answer weighted by <c>f0</c>. Neither half is reachable with GI off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The scene already holds what the two arithmetic decisions need.</b>
    ///         <see cref="Smooth" /> is metalness 1 at roughness 0.12 — under the tier's
    ///         <c>roughnessThreshold</c> of 0.5, so it is screen-traced, and its <c>f0</c> is its
    ///         base colour rather than a dielectric 0.04 — and <see cref="Rough" /> is metalness 0
    ///         at roughness 0.85, whose <c>f0</c> is 0.04 and which takes the wide path. One picture
    ///         holds both ends of the plane the fourth target exists to carry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its first reference was a picture of a frame with no diffuse ambient in it, and
    ///         this is what caught that.</b> The split pass withholds the diffuse half for
    ///         <c>!AmbientCombine</c> to rebuild from an irradiance plane, and <c>!StandardFrame</c>
    ///         names one only at <c>gi: probes</c> — so <c>gi: ambient</c> here withheld the term and
    ///         put nothing back. Against <c>tier-high</c> that was 86.0% of the frame and a mean of
    ///         8.473: the shadowed floor and the caster losing the cool sky, and the *sky itself*
    ///         rising 12.6/11.4/10.2 because the meter lifts a frame short a lighting term.
    ///         <c>AmbientCombine</c> falls back to the scene's own environment coefficients now, and
    ///         the same comparison is 10.9% and a mean of 2.708 — the occlusion pair and the traced
    ///         reflection, which is all a correct split adds.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ASplitFrameLooksLikeItsReference() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        using var scene = Stage(owned, QualityTier.High, SplitDocument);

        // The claim the picture cannot make for itself: that this frame is the split one. A
        // regression that quietly stopped splitting would re-record as a perfectly good picture.
        Assert.True(
            scene.Renderer.Host.Builder.Nodes.ContainsKey("Combine"),
            "The split frame expanded without an !AmbientCombine node, so whatever this renders is "
            + "not the path the reference is a picture of."
        );

        Assert.True(
            scene.Renderer.Host.Builder.Nodes.ContainsKey("Mirrors"),
            "The split frame expanded without a !Reflections node, so the reflection blend below is "
            + "weighting a plane nothing wrote."
        );

        var picture = scene.Frames(Frames);

        // ⚠ The claim the two node assertions above cannot make, and the one this fixture was
        // actually caught by. Both nodes existed, the graph ran, the frame came back clean — and it
        // was bit-identical to `tier-high` across every one of its 16 384 pixels, because
        // `SplitOutputs` was off and a cleared normals plane makes the combine treat the whole frame
        // as sky and hand the direct target straight back. Every structural assertion available
        // passed. So the difference is asserted against a committed picture of the *unsplit* frame at
        // the same tier, which costs one file load and no second rendering: whatever else moves this
        // reference, it can never again be re-recorded as the frame beside it.
        //
        // ⚠ **The mean and not the pixel count, and the bound came down from a quarter of the
        // frame.** Both are consequences of the split gaining its diffuse ambient back. The old
        // numbers were taken against a split frame that had lost the term entirely, which moved
        // 86.0% of the pixels and 8.473 of average channel — a quarter of the frame was a
        // comfortable floor under a defect. A split frame that rebuilds its ambient correctly is
        // *supposed* to resemble the unsplit one: what is left is the occlusion pair and the traced
        // reflection, measured at 10.9% and a mean of 2.708. The count is the wrong instrument at
        // that size — the AO's own deltas are 7 to 12 of 255, straddling `Tolerance.Shaded`'s
        // threshold of 12, so a driver rounding differently moves the count and not the frame. The
        // mean does not sit on that cliff: 1.0 is under half of what this measures and far above
        // both zero and the 0.35 the tolerance allows two renderings of the *same* picture.
        var unsplit = PngCodec.Load(Path.Combine(GoldenImage.ReferenceDirectory, "tier-high.png"));
        var against = GoldenImage.Compare(unsplit, picture, Tolerance.Shaded);

        Assert.True(
            against.MeanChannel > 1.0,
            $"The split frame and the unsplit tier-high reference differ by an average channel of "
            + $"{against.MeanChannel:F3}/255 over {against.DifferingPixels} of {against.TotalPixels} "
            + $"pixels ({against.Fraction:P3}), where 1.000 is the least a rebuilt frame may — this "
            + "one is measured at 2.708. The likeliest cause is that ForwardPlus compiled without "
            + "SplitOutputs — CompositorBuilder infers it from the Main pass's four colourTargets, "
            + "so a pass that lost a target or a permutation that stopped reaching the material "
            + "feature both land here. The shading pass then writes location 0 alone, the albedo, "
            + "normal and f0 planes stay at the clear, and !AmbientCombine reads a zero-length "
            + "normal as sky and returns the direct target untouched."
        );

        GoldenImage.Verify("frame-split", picture, Tolerance.Shaded);
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
    /// <summary>
    ///     A frame rendered below native is still a picture of the same scene.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The claim every structural test of <c>RenderScale</c> misses.</b>
    ///         <c>RenderQualityTests</c> asserts that a tier's scale reaches the six scene planes'
    ///         <c>Scale</c> in the expanded <em>document</em>, and it passed throughout the whole
    ///         period in which no consumer of those planes measured in their grid — because a
    ///         document is not a frame. What was wrong lived in the nodes that read the planes, and
    ///         nothing there is visible to a test that never renders.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both bounds, because the two failures look nothing alike.</b> Under it, the scale
    ///         reached nothing and the tier is a knob wired to no frame — <c>Tolerance.Shaded</c>
    ///         allows 0.35 of mean channel between two renderings of the <em>same</em> picture, so
    ///         anything at or below that is two renderings of the native frame. Over it, the scale
    ///         reached the frame and broke it: a march indexing a rectangle its depth plane does not
    ///         occupy, or a neighbourhood collapsed onto one texel, moves a picture far further than
    ///         resolving it more coarsely does. The ceiling is this fixture's own worst recorded
    ///         silent defect for scale — the split frame that had lost its diffuse ambient, measured
    ///         at 8.473 over 86% of the frame.
    ///     </para>
    ///     <para>
    ///         Measured at <b>mean 1.361</b>, 3.28% of pixels, worst channel 74 — half resolution
    ///         upscaled back, which is what the number should be. Stated as the mean rather than as a
    ///         pixel count for <see cref="ASplitFrameLooksLikeItsReference" />'s reason: at 128² the
    ///         count sits on <c>Tolerance.Shaded</c>'s 12/255 cliff and a driver rounding differently
    ///         moves it without moving the frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Half, not three quarters.</b> 128² is the fixture's whole budget; at 0.75 the
    ///         scene plane is 96² and several of the differences this is watching for round away.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ceiling is a sanity bound and not a guard, and the difference was measured.</b>
    ///         The floor fires: asking the preset for a scale of 1 makes the two pictures
    ///         bit-identical and the first assertion reports 0.000. The ceiling could not be made to
    ///         fire — reverting <c>ReflectionRenderer</c> to size its march by the window while the
    ///         depth plane is half that leaves this test <em>green</em>. So the screen-space
    ///         reflection path's own render-scale correctness is <b>not</b> under a picture here, and
    ///         nothing else in the suite covers it either; at 128² the traced term is too small a
    ///         part of the frame, and a <c>Load</c> off the end of a smaller plane returns zero
    ///         rather than a wrong colour, so the failure subtracts reflection instead of adding
    ///         nonsense. Read the second assertion as "the frame did not fall apart", not as
    ///         "every consumer measured in the right grid".
    ///     </para>
    ///     <para>
    ///         No reference image, deliberately. What is asserted is the relationship between two
    ///         pictures rendered in the same run on the same device, which is exactly the claim a
    ///         committed PNG cannot make and would go on matching after the scale stopped reaching
    ///         the frame — the failure this fixture has already been caught by twice.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AFrameRenderedBelowNativeIsTheSameScene() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var half = new RenderQualityAsset {
            High = new() { Resolution = new() { RenderScale = 0.5f } }
        };

        Bitmap native;
        Bitmap scaled;

        using (var scene = Stage(owned, QualityTier.High, SplitDocument)) {
            native = scene.Frames(Frames);
        }

        using (var scene = Stage(owned, QualityTier.High, new() { Game = SplitFrame with { Preset = half } })) {
            scaled = scene.Frames(Frames);
        }

        var comparison = GoldenImage.Compare(native, scaled, Tolerance.Shaded);

        Assert.True(
            comparison.MeanChannel > 0.35,
            $"A half-scale frame and a native one differ by a mean channel of "
            + $"{comparison.MeanChannel:F3}/255, where 0.35 is what Tolerance.Shaded allows two "
            + "renderings of the same picture — so the render scale reached nothing. The tier's "
            + "value lands on the six scene planes' Scale in StandardFrame.Emit; if those are still "
            + "the frame's size, the expansion stopped reading resolution.renderScale."
        );

        Assert.True(
            comparison.MeanChannel < 8.0,
            $"A half-scale frame and a native one differ by a mean channel of "
            + $"{comparison.MeanChannel:F3}/255 over {comparison.Fraction:P3} of the frame, where "
            + "8.000 is this fixture's worst recorded silent defect and 1.361 is what half "
            + "resolution upscaled back measures. The scale reached the frame and something read "
            + "the scaled planes in the window's grid — the suspects are the ones that ask a size "
            + "rather than the plane: a texel uniform, a march's screenViewport, the depth "
            + "pyramid's extent, or a probe lattice."
        );
    }

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
    static TierScene Stage(Fixture fixture, QualityTier tier) => Stage(fixture, tier, Document);

    /// <remarks>
    ///     ⚠ <b>Nothing here turns the split on, and something used to have to.</b> A <c>gi:</c> above
    ///     <see cref="GiMode.Off" /> declares the split targets and the combine that reads them;
    ///     whether <c>ForwardPlus</c> <em>writes</em> them is a permutation, and
    ///     <c>CompositorBuilder</c> now infers it from the expanded Main pass's four
    ///     <c>colourTargets</c> — so opening a split document is the whole of it, exactly as opening
    ///     an unsplit one is.
    ///     <para>
    ///         Worth stating rather than merely deleting, because of what the missing half looked
    ///         like: not an unsplit frame but a <em>silently</em> unsplit one. The single-target
    ///         variant writes location 0 and leaves the albedo, normal and <c>f0</c> planes at the
    ///         clear; <c>AmbientCombine</c>'s sky test is the normal plane's length, so a cleared
    ///         plane makes every pixel in the frame read as sky and the pass returns the direct
    ///         target untouched. This fixture's first rendering was <b>bit-identical</b> to
    ///         <c>tier-high</c> across all 16 384 pixels for exactly that reason — a golden that
    ///         would have been recorded, passed forever, and asserted nothing about the split at all.
    ///         The comparison against <c>tier-high</c> in <see cref="ASplitFrameLooksLikeItsReference" />
    ///         is what stands guard over that now, and it stands over the inference too.
    ///     </para>
    /// </remarks>
    static TierScene Stage(Fixture fixture, QualityTier tier, GraphicsCompositorAsset document) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device), Compiler));

        var scene = TierScene.Open(fixture, effects, document, tier);
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
