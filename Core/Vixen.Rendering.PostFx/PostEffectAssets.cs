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
    ///     ⚠ <b>It fills in for <see cref="Exposure" /> and <see cref="Ev100" /> rather than beating
    ///     them.</b> Naming a view here and leaving both of those alone is how a document says "expose
    ///     this frame the way the camera's aperture, shutter and ISO say to". Naming a view <em>and</em>
    ///     an exposure keeps the authored one, because that is a decision somebody made about this
    ///     level and a lens silently overriding it would move the frame by however many stops the
    ///     aperture happens to be.
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

    /// <summary>The buffer an <c>!AutoExposure</c> left this frame's measured exposure in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The line that made metering reachable from a document at all.</b>
    ///         <c>TonemapRenderer.ExposureBuffer</c> has been there since the meter was written and
    ///         nothing could name it: a frame could run the histogram, reduce it and adapt it, and
    ///         then tonemap with the exposure somebody typed — every counter saying the meter ran.
    ///     </para>
    ///     <para>
    ///         The name is the metering node's own resource, which is its <c>name:</c> followed by
    ///         <c>.Exposure</c> — a node called <c>Meter</c> publishes <c>Meter.Exposure</c>. Naming a
    ///         buffer no node produced is refused by the graph, which is the good failure.
    ///     </para>
    /// </remarks>
    public string ExposureBuffer { get; init; } = string.Empty;
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

    /// <summary>The view whose camera unprojects the depth, or empty for a host that feeds the matrix.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ On <see cref="ScreenProbeGatherAsset.View" />'s terms, and this asset shipping
    ///         without it is why <c>ArenaIllumination.Feed</c> once pushed the inverse by hand: left
    ///         empty with nothing else driving it, the march reconstructs every pixel under an
    ///         identity camera — smooth, plausible and completely wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ Appended, and it has to be — a <c>[DataContract]</c> is serialised in declaration
    ///         order, so a member inserted in the middle re-reads every later one from the wrong
    ///         offset. See <see cref="TonemapAsset.Bloom" /> for the incident that taught this.
    ///     </para>
    /// </remarks>
    public string View { get; init; } = string.Empty;
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

/// <summary>The screen-probe gather: place, trace, resolve, denoise, upsample — one node.</summary>
/// <remarks>
///     <para>
///         [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L3's node, and the last
///         of that document's renderers a file could not name. What a document decides is placement
///         and the numbers — the lattice's tile size, the trace's latency, the denoiser's plane
///         tolerance. What it deliberately does not is the chain itself: the tracer, the resolver,
///         the accumulator and the filter each carry composed sources, a sky and device state that
///         outlive a frame, so they come from <c>CompositorBuilder.ScreenProbeTracer</c> and its
///         neighbours, and a node built with none of them supplied places probes over a frame and
///         traces nothing — the <c>!IrradianceField</c> stance, one probe kind over.
///     </para>
///     <para>
///         ⚠ <b><c>screenTraces</c> is a choice, not a quality slider.</b> The probes' origins are a
///         placement one <c>latency</c> old and the depth the rays march is this frame's — identical
///         for a still scene, sheared by one frame of motion for a moving one. The renderer's own
///         remarks name that shear as the reprojection problem the denoiser owns.
///     </para>
/// </remarks>
[DataContract("ScreenProbeGather")]
public sealed record ScreenProbeGatherAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The depth the probes stand on — and the sky test, in its <c>.r</c>.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The normals the probes are biased along, as the G-buffer stores them.</summary>
    public string Normals { get; init; } = string.Empty;

    /// <summary>The name the upsampled irradiance is published under.</summary>
    public string Output { get; init; } = "PostFx";

    /// <summary>The view whose camera places the probes, or empty for a host that feeds matrices.</summary>
    /// <remarks>
    ///     ⚠ Not optional decoration, exactly as <c>!Ssao</c>'s is not: with nothing driving the
    ///     matrices every probe is reconstructed under an identity camera — a lattice standing on
    ///     surfaces that exist nowhere, smooth and completely wrong.
    /// </remarks>
    public string View { get; init; } = string.Empty;

    /// <summary>How many pixels one probe stands for, along each axis.</summary>
    public int TileSize { get; init; } = 16;

    /// <summary>A multiplier on the upsampled result.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>Whether the trace's first stage marches the frame's own depth buffer.</summary>
    public bool ScreenTraces { get; init; }

    /// <summary>How deep that march's shell is in view units, or zero for the device-depth shell.</summary>
    public float ScreenLinearThickness { get; init; }

    /// <summary>How many frames between a depth copy and placing probes from it.</summary>
    /// <remarks>
    ///     Zero resolves to the device's frames in flight, which is the free answer — the host's own
    ///     loop has waited on that frame. Below it, the caller owns the wait; a test that idles the
    ///     device between frames runs at one.
    /// </remarks>
    public int Latency { get; init; }

    /// <summary>How far off a probe's plane a pixel may stand and still read it, in world units.</summary>
    /// <remarks>Zero keeps the bilinear taps. Above zero it needs the host to have supplied an
    ///     accumulator, because the surface planes the taps test against are the history's.</remarks>
    public float PlaneTolerance { get; init; }
}

/// <summary>Traced reflections over the frame — doc 19 § L5 as a node a document names.</summary>
/// <remarks>
///     <para>
///         [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L5's node, on
///         <see cref="ScreenProbeGatherAsset" />'s terms exactly. A document names the frame's
///         resources and the march's numbers; the host supplies what a file cannot — the effect
///         system the kernel resolves through (<c>CompositorBuilder.Effects</c>), the nearest chain
///         the screen march skips by (<c>TracePyramid</c>), and the composed sources on the node's
///         own <c>Trace</c>, which are questions about the scene. Built with none of them, the node
///         does nothing.
///     </para>
///     <para>
///         <b>The normals resource is a contract: world normal in xyz, roughness in alpha.</b> A
///         G-buffer that packs differently writes an adapter pass, not a different node.
///     </para>
/// </remarks>
[DataContract("Reflections")]
public sealed record ReflectionsAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The depth positions are reconstructed from — and what the screen march reads.</summary>
    public string Depth { get; init; } = "Depth";

    /// <summary>The frame resource holding world normals in xyz and roughness in alpha.</summary>
    public string Normals { get; init; } = "Normals";

    /// <summary>The frame's colour, which is what a screen hit reflects — or empty for none.</summary>
    /// <remarks>
    ///     Empty hands every sharp ray straight to the field march. Which colour to name — last
    ///     frame's lit buffer, this frame's opaque pass — is a scheduling decision the document makes
    ///     by where it places this node relative to what it names.
    /// </remarks>
    public string Colour { get; init; } = string.Empty;

    /// <summary>The name the answer is published under — radiance in rgb, validity in alpha.</summary>
    public string Target { get; init; } = "Reflections";

    /// <summary>The view whose camera drew the frame, or empty for a host that feeds matrices.</summary>
    /// <remarks>⚠ On <see cref="ScreenProbeGatherAsset.View" />'s terms: left empty with nothing
    ///     else driving the matrices, every ray reflects a frame drawn by an identity camera.</remarks>
    public string View { get; init; } = string.Empty;

    /// <summary>The roughness at and above which the field answers instead of the trace.</summary>
    public float RoughnessThreshold { get; init; } = 0.5f;

    /// <summary>How far below the threshold the cross-fade starts. Zero is the hard switch.</summary>
    public float RoughnessBlend { get; init; }

    /// <summary>How far a mirror ray looks before deciding it escaped.</summary>
    public float MaxDistance { get; init; } = 100f;

    /// <summary>How far off its surface a mirror ray starts.</summary>
    public float Bias { get; init; } = 0.01f;

    /// <summary>How many equal steps a screen ray takes.</summary>
    public int ScreenSteps { get; init; } = 32;

    /// <summary>How deep behind a surface a sample still counts as inside it, in device depth.</summary>
    public float ScreenThickness { get; init; } = 0.02f;

    /// <summary>How deep the march's shell is in view units, or zero for the device-depth shell.</summary>
    public float ScreenLinearThickness { get; init; }
}

/// <summary>Puts a split frame back together — the consumer end of the ambient split.</summary>
/// <remarks>
///     <para>
///         The node a split document ends its lighting with. <c>ForwardPlus.SplitOutputs</c> (and the
///         resolve, under the same key) writes direct light to one target and holds diffuse ambient
///         back; this multiplies an irradiance plane by the albedo plane, occlusion into that ambient
///         and sun visibility into the direct term, and blends reflections over by validity —
///         <c>direct × sun + albedo × irradiance × occlusion</c>, one full-screen pass. A document
///         names the planes; the first three are the split pass's own targets, and everything after
///         them is optional with a stated stand-in: absent occlusion reads as one, absent irradiance
///         and reflections as nothing.
///     </para>
///     <para>
///         <b>Sun visibility multiplies DIRECT and occlusion multiplies AMBIENT</b> —
///         <c>!DistanceFieldAo</c>'s own channel rule, kept here. Where a shadow map already shadows
///         the sun, the document sets that node's <c>sunShadow: false</c>; the double-application
///         guard is that knob, not arithmetic in this pass.
///     </para>
/// </remarks>
[DataContract("AmbientCombine")]
public sealed record AmbientCombineAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The split pass's first target: direct light, emissive and specular ambient.</summary>
    public string Direct { get; init; } = "SceneHdr";

    /// <summary>Its second: diffuse albedo in rgb, the material's own occlusion in a.</summary>
    public string Albedo { get; init; } = "SceneAlbedo";

    /// <summary>Its third: world normal in xyz, roughness in a. Zero length marks a skyward pixel.</summary>
    public string Normals { get; init; } = "SceneNormals";

    /// <summary>
    ///     Screen irradiance over π — <c>!IndirectDiffuse</c>'s output, or <c>!ScreenProbeGather</c>'s;
    ///     both publish the same kind of plane. Empty contributes no ambient at all.
    /// </summary>
    public string Irradiance { get; init; } = string.Empty;

    /// <summary><c>!DistanceFieldAo</c>'s plane: occlusion in r, sun visibility in g. Empty reads both as one.</summary>
    public string Occlusion { get; init; } = string.Empty;

    /// <summary><c>!Ssao</c>'s plane: occlusion in r, contact scale over the field's room scale. Empty reads one.</summary>
    public string ContactOcclusion { get; init; } = string.Empty;

    /// <summary><c>!Reflections</c>' plane: radiance in rgb, validity in a. Empty blends none in.</summary>
    public string Reflections { get; init; } = string.Empty;

    /// <summary>The full-resolution depth, for the bilateral AO upsample. Empty keeps the linear read.</summary>
    /// <remarks>
    ///     The AO planes usually arrive at half resolution, and this node is where they meet the
    ///     full-resolution frame — naming the depth here is what keeps their occlusion from
    ///     smearing across the depth edge at a corner. Wants <see cref="View" /> beside it.
    /// </remarks>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The view whose camera drives the upsample's plane test.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>How far off a pixel's plane a reduced texel may stand and still count, in metres.</summary>
    public float PlaneTolerance { get; init; } = 0.05f;

    /// <summary>The name the combined radiance is published under.</summary>
    public string Output { get; init; } = "PostFx";

    /// <summary>A multiplier on the rebuilt ambient alone.</summary>
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
///     <para>
///         Also the door for <c>!StandardFrame</c>: the preset expands into this set's node kinds, so
///         the assembly that owns the kinds owns the expansion, and registering this factory is the
///         whole installation — the builder hands the document through
///         <see cref="ICompositorAssetTransformer" /> before it reads a node of it.
///     </para>
/// </remarks>
public sealed class PostEffectFactory : ISceneRendererFactory, ICompositorAssetTransformer {
    /// <summary>The project's quality preset — its <c>RenderQuality.vxpreset</c>, loaded.</summary>
    /// <remarks>
    ///     The middle layer of doc 39's waterfall: over <see cref="RenderQuality.EngineDefaults" />,
    ///     under a document's own <see cref="StandardFrameAsset.Preset" />. The factory takes the
    ///     asset rather than an address because the transform it feeds must stay pure — the host
    ///     that constructs the factory is the one with an <c>AssetManager</c> in its hands, and it
    ///     loads the preset on its own schedule. Null is the ordinary case: engine defaults.
    /// </remarks>
    public RenderQualityAsset? Preset { get; set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Besides the expansion, this deposits the frame's inline <c>look:</c> on
    ///     <see cref="CompositorBuilder.Look" /> — the look is runtime layering, never baked into the
    ///     emitted nodes, and the builder is the one object both the transform and the host hold.
    ///     Written whole every build, null included, so a reload that dropped the look drops it.
    /// </remarks>
    public GraphicsCompositorAsset Transform(GraphicsCompositorAsset document, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(builder);

        var expanded = StandardFrame.Expand(document, builder.Quality, Preset);
        builder.Look = StandardFrame.LookOf(document);

        return expanded;
    }

    /// <summary>
    ///     What tier a document resolves to, before anything expands or builds it.
    /// </summary>
    /// <param name="document">The authored document.</param>
    /// <param name="fallback">
    ///     The platform's pick — <c>GraphicsOptions.Quality</c>, which a document that names its own
    ///     <c>quality:</c> out-votes and a document that names none takes unchanged.
    /// </param>
    /// <param name="project">The project's <c>.vxpreset</c>, or null for engine defaults alone.</param>
    /// <returns>The folded numbers, for a document with a frame node and for one without.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The seam a tier's non-post consumers reach the document through.</b>
    ///         <see cref="Transform(GraphicsCompositorAsset, CompositorBuilder)" /> replaces the
    ///         frame node during the build, so by the time
    ///         anything holds the built document the node — and its <c>quality:</c> and inline
    ///         <c>preset:</c> — is gone. The vegetation budgets are read while the frame builds and
    ///         the texture pool is sized around it, and both are wired by a host that has the
    ///         document in its hand one line earlier; asking here is what lets them honour the same
    ///         vote the post chain does. Without it a project shipping a frame that names Epic over a
    ///         host defaulting to High got Epic post effects and High ground, silently.
    ///     </para>
    ///     <para>
    ///         Static and pure for <see cref="Transform(GraphicsCompositorAsset, out
    ///         IReadOnlyDictionary{object, string})" />'s reason: no state of the factory's enters
    ///         it, and the caller passes the same two layers the transform would have read — so a
    ///         host and the expansion cannot resolve one document to two tiers.
    ///     </para>
    /// </remarks>
    public static ResolvedQuality QualityOf(
        GraphicsCompositorAsset document,
        QualityTier fallback,
        RenderQualityAsset? project = null
    ) {
        ArgumentNullException.ThrowIfNull(document);

        return StandardFrame.QualityOf(document, fallback, project);
    }

    /// <summary>
    ///     The expansion with a sentence about each thing it emitted — the explode tooling's door.
    /// </summary>
    /// <param name="document">The authored document.</param>
    /// <param name="notes">
    ///     Emitted instance to comment, for the expansion's resources, stages and top-level nodes:
    ///     why each exists, and what its neighbours rely on. Keyed by reference, because the values
    ///     are records and two emitted nodes that agree member-for-member are still two nodes.
    /// </param>
    /// <returns>The document to build — the input itself when there was nothing to expand.</returns>
    /// <remarks>
    ///     What <c>vixen frame explode</c> calls: docs/plan/39's escape hatch writes the expanded
    ///     document as text, and doc 39 promises that text carries explanations rather than eleven
    ///     hundred bare lines. The notes are those explanations, keyed so a writer walking the
    ///     expanded asset can put each one above the YAML it explains. A document with no
    ///     <c>!StandardFrame</c> comes back unchanged with no notes, which is how a caller tells
    ///     "already exploded" from "exploded just now". Static because the expansion is: no state
    ///     of the factory's enters it, and a caller with a document does not need an instance.
    /// </remarks>
    public static GraphicsCompositorAsset Transform(
        GraphicsCompositorAsset document,
        out IReadOnlyDictionary<object, string> notes
    ) {
        ArgumentNullException.ThrowIfNull(document);

        var collected = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);

        // The document's own tier and inline preset still apply; what explode cannot see is the
        // host's platform pick and project preset, because a file on disk has neither. The tier
        // default matches CompositorBuilder.Quality's, so an exploded document and a built one
        // disagree only where a host was configured away from the defaults — which the exploded
        // header states.
        var expanded = StandardFrame.Expand(document, notes: collected);

        notes = collected;
        return expanded;
    }

    /// <inheritdoc />
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        return declared switch {
            BloomAsset bloom => Bloom(bloom, builder),
            SkyAsset sky => Sky(sky, builder),
            TonemapAsset tonemap => Tonemap(tonemap, builder),
            DistanceFieldAoAsset occlusion => Occlusion(occlusion, builder),
            IndirectDiffuseAsset indirect => Indirect(indirect, builder),
            ScreenProbeGatherAsset gather => Probes(gather, builder),
            ReflectionsAsset reflections => Reflections(reflections, builder),
            AmbientCombineAsset combine => Combine(combine, builder),
            FxaaAsset fxaa => Fxaa(fxaa, builder),
            TemporalAntialiasingAsset taa => Taa(taa, builder),
            SharpenAsset sharpen => Sharpen(sharpen, builder),
            VignetteAsset lens => Lens(lens, builder),
            FogAsset fog => Fog(fog, builder),
            VolumetricFogAsset volume => Volumetric(volume, builder),
            OutlineAsset outline => Outline(outline, builder),
            SsaoAsset ssao => Ssao(ssao, builder),
            AutoExposureAsset exposure => Exposure(exposure, builder),
            DepthOfFieldAsset defocus => Defocus(defocus, builder),
            MotionBlurAsset blur => Blur(blur, builder),
            LocalExposureAsset local => Local(local, builder),
            LensFlareAsset flare => Flare(flare, builder),
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
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,

            // ⚠ Set 0, on the terms CompositorBuilder's own !RenderPass case states — and this
            // case not having the line is what a host once worked around per frame: the pass
            // composes the clipmap, whose five volumes and sampler are declared in set 0, and a
            // full-screen pass has no mesh feature and no children, so nothing else ever binds it.
            // Without this every draw is refused for a set nobody wrote.
            SceneConstants = builder.SceneConstants,
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

    /// <summary>The probe gather, with the host's trace chain handed onto it.</summary>
    /// <remarks>
    ///     The four fills come straight off the builder's slots — each independently null, each
    ///     null a stage the node simply does not run. The pyramid is <em>taken</em> rather than
    ///     read, and only when the document asked for screen traces: taking it deepens the shared
    ///     chain's descriptor ring, and a node that will never build the chain should not cost a
    ///     ring slot.
    /// </remarks>
    static ScreenProbeGatherRenderer Probes(ScreenProbeGatherAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Normals = declared.Normals,
            Output = declared.Output,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            TileSize = declared.TileSize,
            Intensity = declared.Intensity,
            ScreenTraces = declared.ScreenTraces,
            ScreenLinearThickness = declared.ScreenLinearThickness,
            Latency = declared.Latency,
            PlaneTolerance = declared.PlaneTolerance,
            Tracer = builder.ScreenProbeTracer,
            Resolver = builder.ScreenProbeResolver,
            Accumulator = builder.ScreenProbeAccumulator,
            SpatialFilter = builder.ScreenProbeFilter,
            Pyramid = declared.ScreenTraces ? builder.TakeTracePyramid() : null,
            Device = builder.Device,
            Samplers = builder.Samplers,
            Allocator = builder.Descriptors
        };

    /// <summary>The reflections node, resolving its kernel through the host's effect system.</summary>
    /// <remarks>
    ///     The pipeline cache is the node's own, which is what <see cref="Exposure" /> below already
    ///     argues: a compute pipeline is keyed by module and layout, and a node that owns its modules
    ///     owns the cache for them. The effect system is the builder's, because two effect systems
    ///     are two variant caches disagreeing about one shader.
    /// </remarks>
    static ReflectionRenderer Reflections(ReflectionsAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Normals = declared.Normals,
            Colour = declared.Colour is { Length: > 0 } colour ? colour : null,
            Target = declared.Target,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            RoughnessThreshold = declared.RoughnessThreshold,
            RoughnessBlend = declared.RoughnessBlend,
            MaxDistance = declared.MaxDistance,
            Bias = declared.Bias,
            ScreenSteps = declared.ScreenSteps,
            ScreenThickness = declared.ScreenThickness,
            ScreenLinearThickness = declared.ScreenLinearThickness,
            Pyramid = builder.TakeTracePyramid(),
            Effects = builder.Effects,
            Allocator = builder.Descriptors,
            Device = builder.Device,
            Pipelines = builder.Device is null ? null : new ComputePipelineCache(builder.Device)
        };

    /// <summary>The combine, with each empty optional staying null rather than a resource of no name.</summary>
    static AmbientCombineRenderer Combine(AmbientCombineAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Direct = declared.Direct,
            Albedo = declared.Albedo,
            Normals = declared.Normals,
            Irradiance = declared.Irradiance is { Length: > 0 } irradiance ? irradiance : null,
            Occlusion = declared.Occlusion is { Length: > 0 } occlusion ? occlusion : null,
            ContactOcclusion = declared.ContactOcclusion is { Length: > 0 } contact ? contact : null,
            Reflections = declared.Reflections is { Length: > 0 } mirrors ? mirrors : null,
            Depth = declared.Depth is { Length: > 0 } depth ? depth : null,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            PlaneTolerance = declared.PlaneTolerance,
            Output = declared.Output,
            Intensity = declared.Intensity,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

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

            // ⚠ And the lens is the fallback, not the winner. Every camera is a physical camera now,
            // so "there is a lens" no longer means "the author asked for a physical exposure" — it is
            // true of every frame. A document that names an exposure has made a decision about this
            // level, and a lens overriding it would move every authored frame by however many stops
            // its aperture happens to be. See TonemapRenderer.LensExposure.
            LensExposure = declared is { Ev100: 0f, Exposure: 1f },
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
            ExposureBuffer = declared.ExposureBuffer,
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
            Height = declared.Height,
            HeightFalloffRate = declared.HeightFalloffRate,
            SunDirection = declared.SunDirection,
            SunColour = declared.SunColour,
            SunAnisotropy = declared.SunAnisotropy,
            Volume = declared.Volume,
            VolumeNear = declared.VolumeNear,
            VolumeFar = declared.VolumeFar,
            VolumeSlices = declared.VolumeSlices,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static VolumetricFogRenderer Volumetric(VolumetricFogAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Media = declared.Media,
            Scattered = declared.Scattered,
            Volume = declared.Output,
            Resolution = new(declared.Width, declared.Height, declared.Slices),
            Near = declared.Near,
            Far = declared.Far,
            Density = declared.Density,
            ScatteringAlbedo = declared.ScatteringAlbedo,
            HeightFalloff = declared.HeightFalloff,
            Height = declared.FogHeight,
            HeightFalloffRate = declared.HeightFalloffRate,
            SunDirection = declared.SunDirection,
            SunColour = declared.SunColour,
            PhaseG = declared.PhaseG,
            AmbientColour = declared.AmbientColour,

            // ⚠ The same source the shadow nodes fit their cascades along. A medium marched against
            // one sun's cascades and lit by another's direction is a valley whose beams miss the
            // shafts they belong to — and the illuminance that comes with it is what keeps the
            // in-scatter in the units the rest of the frame is lit in.
            Sun = builder.Sun,
            ShadowAtlas = declared.ShadowAtlas,
            ScenePass = declared.ScenePass,
            Shadows = declared.Shadows,

            // ⚠ What the shadowing needs, and the only route to it: the cascades are not graph
            // resources and do not travel on an edge. Without this the fog is unshadowed however
            // many shadow nodes the document has.
            Frame = builder.SceneConstants,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers,
            Device = builder.Device,
            Pipelines = builder.Device is null ? null : new ComputePipelineCache(builder.Device)
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
            Bias = declared.Bias,
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
            UseHistogram = declared.UseHistogram,
            MinimumLogLuminance = declared.MinimumLogLuminance,
            MaximumLogLuminance = declared.MaximumLogLuminance,
            LowPercentile = declared.LowPercentile,
            HighPercentile = declared.HighPercentile,
            MeteringPower = declared.MeteringPower,
            StartSize = declared.StartSize,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers,

            // Its own cache, which is what CompositorBuilder's `!Compute` node already does: a
            // compute pipeline is keyed by module and layout, and a node that owns its modules owns
            // the cache for them.
            Pipelines = builder.Device is null ? null : new ComputePipelineCache(builder.Device)
        };

    static LocalExposureRenderer Local(LocalExposureAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            ExposureBuffer = declared.ExposureBuffer,
            Output = declared.Output,
            Format = declared.Format,
            Ev100 = declared.Ev100,
            Taps = declared.Taps,
            BlurRadius = declared.BlurRadius,
            EdgeRange = declared.EdgeRange,
            HighlightContrast = declared.HighlightContrast,
            ShadowContrast = declared.ShadowContrast,
            MaximumStops = declared.MaximumStops,
            Modules = builder.Modules,
            Device = builder.Device,
            Descriptors = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static LensFlareRenderer Flare(LensFlareAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Output = declared.Output,
            Format = declared.Format,
            Threshold = declared.Threshold,
            Ghosts = declared.Ghosts,
            GhostSpacing = declared.GhostSpacing,
            GhostIntensity = declared.GhostIntensity,
            UseHalo = declared.UseHalo,
            HaloRadius = declared.HaloRadius,
            HaloThickness = declared.HaloThickness,
            HaloIntensity = declared.HaloIntensity,
            ChromaticOffset = declared.ChromaticOffset,
            UseStarburst = declared.UseStarburst,
            StarburstBlades = declared.StarburstBlades,
            StarburstIntensity = declared.StarburstIntensity,
            StarburstAngle = declared.StarburstAngle,
            Tint = declared.Tint,
            Modules = builder.Modules,
            Device = builder.Device,
            Descriptors = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static MotionBlurRenderer Blur(MotionBlurAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            MotionVectors = declared.MotionVectors,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Output = declared.Output,
            Format = declared.Format,
            Samples = declared.Samples,
            UseNeighbourMax = declared.UseNeighbourMax,
            MaximumRadius = declared.MaximumRadius,
            MinimumRadius = declared.MinimumRadius,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static DepthOfFieldRenderer Defocus(DepthOfFieldAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Depth = declared.Depth,
            View = declared.View is { Length: > 0 } view ? builder.Views.GetValueOrDefault(view) : null,
            Output = declared.Output,
            Format = declared.Format,
            Samples = declared.Samples,
            UseBladeShape = declared.UseBladeShape,
            MaximumRadius = declared.MaximumRadius,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };
}
