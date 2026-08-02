// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.PostFx;

/// <summary>The dual-filter bloom chain.</summary>
/// <remarks>
///     A node rather than a list of passes, because the chain's shape follows from its depth and the
///     frame's size — nine passes and nine textures out of one line, and a document that spelled them
///     out would have to be rewritten to change the resolution.
/// </remarks>
[DataContract("Bloom")]
public sealed record BloomAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The shader to run, in its permuted modes.</summary>
    public string Shader { get; init; } = "Bloom";

    /// <summary>The texture the chain reads.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Bloom";

    /// <summary>How many levels the pyramid has, the first at half resolution.</summary>
    public int Levels { get; init; } = 5;

    /// <summary>The format every level has.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>Luminance above which a pixel contributes.</summary>
    public float Threshold { get; init; } = 1f;

    /// <summary>How soft that threshold is.</summary>
    public float Knee { get; init; } = 0.5f;

    /// <summary>The upsample tent's radius in texels.</summary>
    public float FilterRadius { get; init; } = 1f;

    /// <summary>How much of each level is added on the way up.</summary>
    public float Intensity { get; init; } = 1f;
}

/// <summary>The background, drawn as the environment the scene is lit by.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>output</c> names a target the document already declared</b>, normally the frame's
///         own HDR colour, and the pass that draws the scene into it afterwards must
///         <c>load: Load</c> rather than clear. That is what puts the sky behind the level; a node
///         that declared a target of its own would need a composite to get it into the frame.
///     </para>
///     <para>
///         <c>view</c> is not optional decoration. Without it the cube is sampled along rays built
///         from a camera at the origin looking down −Z — a plausible picture of the wrong direction.
///     </para>
/// </remarks>
[DataContract("Sky")]
public sealed record SkyAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The colour target it fills — an existing one, so the scene draws over it.</summary>
    public string Output { get; init; } = "SceneColour";

    /// <summary>The view whose rays the cube is sampled along.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>Whether to sample the blurred end of the prefiltered chain: haze rather than sun.</summary>
    public bool Soften { get; init; }

    /// <summary>A multiplier on the sampled luminance. One means the sky you see lights the scene.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>The format of the target, for a document that declared none under that name.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;
}

/// <summary>The pass a frame ends with, and the grade that goes with it.</summary>
/// <remarks>
///     A node rather than a <c>!FullScreen</c> with the shader named by hand, which is what every host
///     wrote before this: five parameters and three bindings spelled out per project, and no way for a
///     document to say "grade with this table".
/// </remarks>
[DataContract("Tonemap")]
public sealed record TonemapAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The linear HDR colour it maps.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>A 3D colour lookup table, or empty for none.</summary>
    public string Lut { get; init; } = "";

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Tonemapped";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8UNormSrgb;

    /// <summary>Which curve maps the range: 0 Reinhard, 1 ACES, 2 AgX, 3 filmic, 4 none.</summary>
    /// <remarks>
    ///     ⚠ 3 was documented as Uncharted and implemented as a clamp. It is Hable's curve now and the
    ///     clamp is 4 — so a document that said 3 gets the curve it asked for, and one that meant the
    ///     clamp has to say so.
    /// </remarks>
    public int Operator { get; init; } = 1;

    /// <summary>Whether the result is encoded to sRGB here rather than by the target's format.</summary>
    public bool EncodeSrgb { get; init; }

    /// <summary>What the scene's radiance is multiplied by before the curve.</summary>
    /// <remarks>Ignored when <see cref="Ev100" /> is set, which is the unit an author wants.</remarks>
    public float Exposure { get; init; } = 1f;

    /// <summary>
    ///     The exposure value at ISO 100, or zero to use <see cref="Exposure" /> as a bare multiplier.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The number a frame lit in physical units is actually tuned by.</b> A scene whose
    ///         sun is 16000 lux and whose lamps are 900 lumens produces luminances in the thousands —
    ///         multiplying those by anything an author would think to type gives white. An EV names
    ///         the luminance that comes out as middle grey, which is what a light meter reads and what
    ///         a photographer changes: 15 is bright sun, 12 overcast, 5 a lit interior, and each step
    ///         of one is a stop.
    ///     </para>
    ///     <para>
    ///         ⚠ Zero means "not set" rather than EV 0, which is moonlight. A frame that meant
    ///         moonlight says 0.001 and gets the same answer to four decimal places; a frame that says
    ///         nothing gets the multiplier it always had, so every document written before this
    ///         existed is unchanged. See <see cref="Photometry.ExposureFromEv100" />.
    ///     </para>
    /// </remarks>
    public float Ev100 { get; init; }

    /// <summary>The radiance that maps to white.</summary>
    public float WhitePoint { get; init; } = 4f;

    /// <summary>Contrast, around middle grey.</summary>
    public float Contrast { get; init; } = 1f;

    /// <summary>Saturation, 0 for greyscale.</summary>
    public float Saturation { get; init; } = 1f;

    /// <summary>The colour temperature the scene is lit at, in kelvin, or zero for none.</summary>
    /// <remarks>
    ///     ⚠ Kelvin, and it used to be a −1..1 warm/cool shift. A document carrying the old value gets
    ///     a temperature of a fraction of a kelvin, which <see cref="Photometry.WhiteBalance" /> reads
    ///     as "leave it alone" — so an old frame loses a subtle tint rather than turning orange.
    /// </remarks>
    public float Temperature { get; init; }

    /// <summary>A <c>!Bloom</c> node's pyramid to add before the curve, or empty for no glow.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is what composites a bloom, and a document has nowhere else to put one.</b>
    ///         <c>!Bloom</c> publishes the pyramid rather than the scene with a glow added, so a frame
    ///         that tonemapped that output instead of the scene threw the scene away — a black window
    ///         with every counter reporting a frame that drew. Named here, the two cannot be wired the
    ///         wrong way round.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Appended, and it has to be.</b> A <c>[DataContract]</c> is serialised in
    ///         declaration order, so a member inserted in the middle is every later member read from
    ///         the wrong offset by anything holding a compiled copy — here that is a frame whose
    ///         <c>Output</c> came back as a LUT path and whose first pass then declared a target named
    ///         null. Appending is the change the format supports; reordering is not.
    ///     </para>
    /// </remarks>
    public string Bloom { get; init; } = "";

    /// <summary>How much of that pyramid reaches the image.</summary>
    public float BloomIntensity { get; init; } = 0.2f;

    /// <summary>Green to magenta, −1 to 1 — the axis a colour temperature does not have.</summary>
    public float Tint { get; init; }

    /// <summary>Multiplied into the scene before anything else grades it.</summary>
    public Vector3 ColorFilter { get; init; } = Vector3.One;

    /// <summary>Hue rotation in radians.</summary>
    public float HueShift { get; init; }

    /// <summary>Where split toning crosses over, −1 towards shadows and 1 towards highlights.</summary>
    public float SplitBalance { get; init; }

    /// <summary>Hable's shoulder strength. Read when <see cref="Operator" /> is 3.</summary>
    public float FilmicShoulderStrength { get; init; } = 0.15f;

    /// <summary>Hable's linear strength.</summary>
    public float FilmicLinearStrength { get; init; } = 0.5f;

    /// <summary>Hable's linear angle.</summary>
    public float FilmicLinearAngle { get; init; } = 0.1f;

    /// <summary>Hable's toe strength.</summary>
    public float FilmicToeStrength { get; init; } = 0.2f;

    /// <summary>Hable's toe numerator.</summary>
    public float FilmicToeNumerator { get; init; } = 0.02f;

    /// <summary>Hable's toe denominator.</summary>
    public float FilmicToeDenominator { get; init; } = 0.3f;

    /// <summary>What colour the bloom's spill is.</summary>
    public Vector3 BloomTint { get; init; } = Vector3.One;

    /// <summary>A lens-dirt texture that brightens the bloom, or empty for a clean lens.</summary>
    public string BloomDirt { get; init; } = "";

    /// <summary>How much that dirt brightens it.</summary>
    public float BloomDirtIntensity { get; init; }

    /// <summary>The view whose lens sets the exposure, or empty for an authored one.</summary>
    /// <remarks>
    ///     ⚠ It wins over <see cref="Exposure" /> and <see cref="Ev100" /> when that view's camera
    ///     carries a valid <c>PhysicalCamera</c>, because an aperture that sets the defocus and an
    ///     exposure value typed beside it are two answers to one question. A camera with no lens
    ///     changes nothing, which is every camera until one is added.
    /// </remarks>
    public string View { get; init; } = "";

    /// <summary>Whether the four-range colour decision list runs.</summary>
    /// <remarks>
    ///     ⚠ Off, the whole grade folds out of the compiled variant. A document that authors ranges
    ///     and forgets this line gets the picture it had before, which is the failure the flag makes
    ///     visible rather than the one it causes.
    /// </remarks>
    public bool UseColorGrading { get; init; }

    /// <summary>The grade, when <see cref="UseColorGrading" /> is on.</summary>
    public ColorGrading Grading { get; init; } = ColorGrading.Neutral;
}

/// <summary>The screen-space pass that marches the distance field for occlusion and sun shadow.</summary>
/// <remarks>
///     <para>
///         [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L1's consumer.
///         <c>GlobalDistanceField</c> composites the clipmap and this reads it — and ⚠ <b>the two have
///         to name the same shader</b>, because that name is the compose-slot prefix one writes its
///         bindings under and the other reads them from. They are separate strings on purpose: a frame
///         may march a field a different node composited, or none at all.
///     </para>
///     <para>
///         <see cref="Source" /> left alone answers "nothing is near" — fully open, fully lit — which
///         is the honest default for a project with no clipmap, and is why this node is safe to leave
///         in a document that a given build has no field for.
///     </para>
/// </remarks>
[DataContract("DistanceFieldAo")]
public sealed record DistanceFieldAoAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The depth it reconstructs world positions from.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The normals it orients the occlusion integral by.</summary>
    public string Normals { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "AmbientOcclusion";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>The shader behind the field slot — what the march actually reads.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Whether to trace a sun shadow alongside the occlusion.</summary>
    /// <remarks>
    ///     A permutation rather than a branch, so off means the march is not compiled and the pass
    ///     carries none of the sun's uniforms either.
    /// </remarks>
    public bool SunShadow { get; init; } = true;
}

/// <summary>The screen-space pass that reads the irradiance field into the ambient term.</summary>
/// <remarks>
///     [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L2's consumer, paired with
///     <c>IrradianceField</c> exactly as <see cref="DistanceFieldAoAsset" /> is paired with
///     <c>GlobalDistanceField</c>. Its default <c>Source</c> answers no indirect light <i>and</i> an
///     unshadowed sun — two different right answers rather than one convenient zero, because
///     answering zero for the second would put every surface in the world into shadow.
/// </remarks>
[DataContract("IndirectDiffuse")]
public sealed record IndirectDiffuseAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The depth it reconstructs world positions from.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The normals it asks the probe about.</summary>
    public string Normals { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "IndirectDiffuse";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>The shader behind the field slot — what the lookup actually reads.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>A multiplier on the result.</summary>
    /// <remarks>
    ///     Physically one. It exists because artists ask for it, and because a value that is not one
    ///     is then visible in a capture rather than folded into the field, where it would quietly
    ///     corrupt what the next bounce reads.
    /// </remarks>
    public float Intensity { get; init; } = 1f;
}

/// <summary>Builds the effect set's node kinds from a compositor document.</summary>
/// <remarks>
///     <para>
///         What a host registers on a <see cref="CompositorBuilder" /> so a document can name
///         <c>!Bloom</c> or <c>!Tonemap</c>. The builder cannot know these types — this project is
///         downstream of it, and a switch case here would be a cycle — so the knowledge travels the
///         only direction it can.
///     </para>
///     <para>
///         One factory for the whole set rather than one per effect, because what it does is a switch
///         over asset types and a list of single-case factories would be the same switch spread over
///         seven files.
///     </para>
/// </remarks>
public sealed class PostEffectFactory : ISceneRendererFactory {
    /// <inheritdoc />
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        return declared switch {
            BloomAsset bloom => Bloom(bloom, builder),
            SkyAsset sky => Sky(sky, builder),
            TonemapAsset tonemap => Tonemap(tonemap, builder),
            DistanceFieldAoAsset occlusion => Occlusion(occlusion, builder),
            IndirectDiffuseAsset indirect => Indirect(indirect, builder),
            FxaaAsset fxaa => Fxaa(fxaa, builder),
            TemporalAntialiasingAsset taa => Taa(taa, builder),
            SharpenAsset sharpen => Sharpen(sharpen, builder),
            VignetteAsset lens => Lens(lens, builder),
            FogAsset fog => Fog(fog, builder),
            OutlineAsset outline => Outline(outline, builder),
            SsaoAsset ssao => Ssao(ssao, builder),
            AutoExposureAsset exposure => Exposure(exposure, builder),
            _ => null
        };
    }

    static BloomRenderer Bloom(BloomAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            ShaderName = declared.Shader,
            Source = declared.Source,
            Output = declared.Output,
            Levels = declared.Levels,
            Format = declared.Format,
            Threshold = declared.Threshold,
            Knee = declared.Knee,
            FilterRadius = declared.FilterRadius,
            Intensity = declared.Intensity,
            Modules = builder.Modules,
            Device = builder.Device,
            Descriptors = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static DistanceFieldAoRenderer Occlusion(DistanceFieldAoAsset declared, CompositorBuilder builder) {
        var node = new DistanceFieldAoRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Normals = declared.Normals,
            Output = declared.Output,
            Format = declared.Format,
            SunShadow = declared.SunShadow,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

        // Empty leaves the renderer's own default — the shader that answers "nothing is near" — rather
        // than naming a slot nothing fills. See the asset.
        if (declared.Source is { Length: > 0 } source) {
            node.Source = source;
        }

        return node;
    }

    static IndirectDiffuseRenderer Indirect(IndirectDiffuseAsset declared, CompositorBuilder builder) {
        var node = new IndirectDiffuseRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Normals = declared.Normals,
            Output = declared.Output,
            Format = declared.Format,
            Intensity = declared.Intensity,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

        if (declared.Source is { Length: > 0 } source) {
            node.Source = source;
        }

        return node;
    }

    /// <summary>The sky node, with the frame's camera and the frame's set 0 in it.</summary>
    /// <remarks>
    ///     ⚠ <c>SceneConstants</c> is the line that makes this node work at all: the environment cube
    ///     is per-frame state, so a pass that did not bind set 0 would have nothing to sample.
    /// </remarks>
    static SkyRenderer Sky(SkyAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Output = declared.Output,
            Format = declared.Format,
            Soften = declared.Soften,
            Intensity = declared.Intensity,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static TonemapRenderer Tonemap(TonemapAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Lut = declared.Lut,
            Bloom = declared.Bloom,
            BloomIntensity = declared.BloomIntensity,
            Output = declared.Output,
            Format = declared.Format,
            Operator = declared.Operator,
            EncodeSrgb = declared.EncodeSrgb,

            // The exposure value wins where a document names one, because it is the unit an author
            // reaches for and a multiplier is what it resolves to.
            Exposure = declared.Ev100 != 0f ? Photometry.ExposureFromEv100(declared.Ev100) : declared.Exposure,
            WhitePoint = declared.WhitePoint,
            Contrast = declared.Contrast,
            Saturation = declared.Saturation,
            Temperature = declared.Temperature,
            Tint = declared.Tint,
            ColorFilter = declared.ColorFilter,
            HueShift = declared.HueShift,
            SplitBalance = declared.SplitBalance,
            FilmicShoulderStrength = declared.FilmicShoulderStrength,
            FilmicLinearStrength = declared.FilmicLinearStrength,
            FilmicLinearAngle = declared.FilmicLinearAngle,
            FilmicToeStrength = declared.FilmicToeStrength,
            FilmicToeNumerator = declared.FilmicToeNumerator,
            FilmicToeDenominator = declared.FilmicToeDenominator,
            BloomTint = declared.BloomTint,
            BloomDirt = declared.BloomDirt,
            BloomDirtIntensity = declared.BloomDirtIntensity,
            Grading = declared.UseColorGrading ? declared.Grading : null,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static FxaaRenderer Fxaa(FxaaAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Output = declared.Output,
            Format = declared.Format,
            Subpixel = declared.Subpixel,
            EdgeThreshold = declared.EdgeThreshold,
            EdgeThresholdMinimum = declared.EdgeThresholdMinimum,
            SubpixelQuality = declared.SubpixelQuality,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static TemporalAntialiasingRenderer Taa(TemporalAntialiasingAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            MotionVectors = declared.MotionVectors,
            Depth = declared.Depth,
            Output = declared.Output,
            Format = declared.Format,
            VarianceClipping = declared.VarianceClipping,
            Feedback = declared.Feedback,
            VarianceGamma = declared.VarianceGamma,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static SharpenRenderer Sharpen(SharpenAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Output = declared.Output,
            Format = declared.Format,
            PerChannel = declared.PerChannel,
            Sharpness = declared.Sharpness,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    /// <summary>The lens pass — vignette, aberration and grain, which are one shader.</summary>
    /// <remarks>
    ///     <c>FrameIndex</c> is deliberately not a document's business: it has to advance every frame
    ///     or the grain is a static pattern welded to the screen, and a document has no frame counter.
    ///     A host advances it on the node the builder returns.
    /// </remarks>
    static VignetteRenderer Lens(VignetteAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Output = declared.Output,
            Format = declared.Format,
            UseVignette = declared.UseVignette,
            UseChromaticAberration = declared.UseChromaticAberration,
            UseGrain = declared.UseGrain,
            LuminanceWeightedGrain = declared.LuminanceWeightedGrain,
            VignetteIntensity = declared.VignetteIntensity,
            VignetteSmoothness = declared.VignetteSmoothness,
            AberrationStrength = declared.AberrationStrength,
            GrainIntensity = declared.GrainIntensity,
            GrainScale = declared.GrainScale,
            VignetteColour = declared.VignetteColour,
            VignetteCentre = declared.VignetteCentre,
            VignetteRoundness = declared.VignetteRoundness,
            UseLensDistortion = declared.UseLensDistortion,
            DistortionIntensity = declared.DistortionIntensity,
            DistortionScale = declared.DistortionScale,
            DistortionCentre = declared.DistortionCentre,
            DistortionZoom = declared.DistortionZoom,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static FogRenderer Fog(FogAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Depth = declared.Depth,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Output = declared.Output,
            Format = declared.Format,
            Mode = declared.Mode,
            HeightFalloff = declared.HeightFalloff,
            SunScattering = declared.SunScattering,
            Colour = declared.Colour,
            Density = declared.Density,
            Start = declared.Start,
            End = declared.End,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static OutlineRenderer Outline(OutlineAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Depth = declared.Depth,
            Normals = declared.Normals,
            SelectionMask = declared.SelectionMask,
            Output = declared.Output,
            Format = declared.Format,
            UseNormals = declared.UseNormals,
            SelectionOnly = declared.SelectionOnly,
            Colour = declared.Colour,
            Thickness = declared.Thickness,
            NearPlane = declared.NearPlane,
            FarPlane = declared.FarPlane,
            DepthThreshold = declared.DepthThreshold,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static AmbientOcclusionRenderer Ssao(SsaoAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Normals = declared.Normals,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Output = declared.Output,
            Format = declared.Format,
            Directions = declared.Directions,
            Steps = declared.Steps,
            BentNormal = declared.BentNormal,
            Radius = declared.Radius,
            Intensity = declared.Intensity,
            Falloff = declared.Falloff,
            Scale = declared.Scale,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static AutoExposureRenderer Exposure(AutoExposureAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            DeltaTime = declared.DeltaTime,
            BrightenRate = declared.BrightenRate,
            DarkenRate = declared.DarkenRate,
            MiddleGrey = declared.MiddleGrey,
            MinimumExposure = declared.MinimumExposure,
            MaximumExposure = declared.MaximumExposure,
            StartSize = declared.StartSize,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers,

            // Its own cache, which is what CompositorBuilder's `!Compute` node already does: a
            // compute pipeline is keyed by module and layout, and a node that owns its modules owns
            // the cache for them.
            Pipelines = builder.Device is null ? null : new ComputePipelineCache(builder.Device)
        };
}
