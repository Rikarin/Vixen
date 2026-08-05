// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.PostFx;

/// <summary>How the frame's GPU culling runs, if at all.</summary>
/// <remarks>
///     The three shapes <see cref="GpuCullingAsset" /> can express, plus off — named here as one
///     enum because a quality tier picks a whole shape, and the asset's three booleans admit
///     combinations (readback <em>and</em> indirect draws) that are not shapes at all.
/// </remarks>
public enum CullingMode {
    /// <summary>No culling node: the host's extraction list is drawn as recorded.</summary>
    Off,

    /// <summary>The bits come back to the host — correct everywhere, and an in-frame wait.</summary>
    ReadBack,

    /// <summary>
    ///     No wait: the host records a superset and the indirect draw arguments do the removing.
    /// </summary>
    Indirect,

    /// <summary>
    ///     <see cref="Indirect" /> plus the second dispatch after the depth is reduced — what was
    ///     rejected against last frame's pyramid is asked about again against this frame's, so
    ///     nothing pops at the trailing edge of the camera. Costs a second reduction and a second
    ///     set of draws.
    /// </summary>
    LatePhase
}

/// <summary>Which of FXAA's own trade-off points the pass runs at.</summary>
/// <remarks>
///     A preset over <see cref="FxaaAsset" />'s three thresholds rather than three more numbers in
///     the quality table, because the thresholds only mean anything as a set: a low edge threshold
///     with low subpixel quality finds edges it then declines to soften.
/// </remarks>
public enum FxaaPreset {
    /// <summary>Fewer edges, lighter blend — the integrated-GPU setting.</summary>
    Performance,

    /// <summary>The shader's own defaults.</summary>
    Balanced,

    /// <summary>Every edge the luminance test can find, blended at full strength.</summary>
    Quality
}

/// <summary>Resolution knobs for one tier. Unset fields fall through the waterfall.</summary>
[DataContract("ResolutionQuality")]
public sealed record ResolutionQuality {
    /// <summary>The fraction of the window the scene is rendered at, 1 for native.</summary>
    /// <remarks>
    ///     Applied as the declared scale of every scene-sized resource the expansion emits — the
    ///     generalisation of the AO passes' own <c>Scale</c>. The output resource stays native: the
    ///     upscale happens wherever a pass reads a scaled plane and writes an unscaled one. An
    ///     upscaling filter choice is reserved for when the engine has more than bilinear to offer.
    /// </remarks>
    public float? RenderScale { get; init; }
}

/// <summary>Shadow knobs for one tier. Every entry maps to a shadow node's own property.</summary>
[DataContract("ShadowQuality")]
public sealed record ShadowQuality {
    /// <summary>How many cascades the sun's map fits — <see cref="ShadowMapAsset.CascadeCount" />.</summary>
    public int? CascadeCount { get; init; }

    /// <summary>One cascade's side in texels — <see cref="ShadowMapAsset.Resolution" />.</summary>
    public int? CascadeResolution { get; init; }

    /// <summary>How far shadows are drawn — <see cref="ShadowMapAsset.ShadowDistance" />.</summary>
    public float? ShadowDistance { get; init; }

    /// <summary>The uniform-to-logarithmic split blend — <see cref="ShadowMapAsset.SplitLambda" />.</summary>
    public float? SplitLambda { get; init; }

    /// <summary>The lamps' constant depth bias — <see cref="PunctualShadowAsset.ConstantBias" />.</summary>
    public float? ConstantBias { get; init; }

    /// <summary>The lamps' slope-scaled bias — <see cref="PunctualShadowAsset.SlopeBias" />.</summary>
    public float? SlopeBias { get; init; }

    /// <summary>The lamp atlas's grid side — <see cref="PunctualShadowAsset.TilesPerSide" />.</summary>
    public int? PunctualTilesPerSide { get; init; }

    /// <summary>One lamp tile's side in texels — <see cref="PunctualShadowAsset.Resolution" />.</summary>
    public int? PunctualResolution { get; init; }

    /// <summary>The virtual map's clipmap levels — <see cref="VirtualShadowAsset.Levels" />.</summary>
    public int? VirtualLevels { get; init; }

    /// <summary>Level zero's width in world units — <see cref="VirtualShadowAsset.FirstExtent" />.</summary>
    public float? VirtualFirstExtent { get; init; }

    /// <summary>Pages drawn per frame — <see cref="VirtualShadowAsset.PagesPerFrame" />.</summary>
    public int? VirtualPagesPerFrame { get; init; }
}

/// <summary>Global-illumination knobs for one tier.</summary>
[DataContract("GlobalIlluminationQuality")]
public sealed record GlobalIlluminationQuality {
    /// <summary>Probes re-rendered per frame — <see cref="IrradianceFieldAsset.Budget" />.</summary>
    public int? IrradianceBudget { get; init; }

    /// <summary>Border dilation passes — <see cref="IrradianceFieldAsset.DilationPasses" />.</summary>
    public int? DilationPasses { get; init; }

    /// <summary>The screen-probe tile side — <see cref="ScreenProbeGatherAsset.TileSize" />.</summary>
    public int? ProbeTileSize { get; init; }

    /// <summary>Whether the gather traces the screen first — <see cref="ScreenProbeGatherAsset.ScreenTraces" />.</summary>
    public bool? ScreenTraces { get; init; }

    /// <summary>
    ///     Cone samples per pixel of the distance-field march. ⚠ Carried, not yet consumed: the
    ///     march's sample count is compiled into its shader today, and the knob lands here first so
    ///     presets do not change shape when the node learns to read it.
    /// </summary>
    public int? DfaoSamples { get; init; }

    /// <summary>The distance-field march's resolution scale. ⚠ Carried, not yet consumed — see
    ///     <see cref="DfaoSamples" />.</summary>
    public float? DfaoScale { get; init; }

    /// <summary>Horizon directions per pixel — <see cref="SsaoAsset.Directions" />.</summary>
    public int? SsaoDirections { get; init; }

    /// <summary>March steps per direction — <see cref="SsaoAsset.Steps" />.</summary>
    public int? SsaoSteps { get; init; }

    /// <summary>The AO pass's resolution scale — <see cref="SsaoAsset.Scale" />.</summary>
    public float? SsaoScale { get; init; }

    /// <summary>
    ///     The surface-cache atlas side in texels. ⚠ Carried, not yet consumed:
    ///     <see cref="SurfaceCacheAsset" /> sizes its atlas internally today.
    /// </summary>
    public int? SurfaceCacheSize { get; init; }
}

/// <summary>Reflection knobs for one tier.</summary>
[DataContract("ReflectionQuality")]
public sealed record ReflectionQuality {
    /// <summary>Screen-trace steps per ray — <see cref="ReflectionsAsset.ScreenSteps" />.</summary>
    public int? ScreenSteps { get; init; }

    /// <summary>Roughness above which nothing is traced — <see cref="ReflectionsAsset.RoughnessThreshold" />.</summary>
    public float? RoughnessThreshold { get; init; }

    /// <summary>
    ///     The trace target's resolution scale. ⚠ Carried, not yet consumed: the trace runs at the
    ///     plane's own size today.
    /// </summary>
    public float? TraceScale { get; init; }
}

/// <summary>
///     Post-chain fidelity for one tier — how hard each pass works, and, on the cheap tiers,
///     whether it runs at all.
/// </summary>
/// <remarks>
///     The booleans are the one stated exception to "the tier never decides what the frame does": a
///     pass that runs at any cost is not a Low tier, so the cheapest tiers drop whole post passes.
///     Which passes <em>exist</em> for the tier to drop stays the frame's knobs —
///     <see cref="StandardFrameAsset.Antialiasing" /> owns TAA and FXAA, not this.
/// </remarks>
[DataContract("PostFidelityQuality")]
public sealed record PostFidelityQuality {
    /// <summary>Whether TAA clips its history by variance — <see cref="TemporalAntialiasingAsset.VarianceClipping" />.</summary>
    public bool? TaaVarianceClipping { get; init; }

    /// <summary>Whether the fog pass runs.</summary>
    public bool? Fog { get; init; }

    /// <summary>Whether the froxel volume is filled and composited — <see cref="VolumetricFogAsset" />.</summary>
    /// <remarks>
    ///     ⚠ Off does not mean no fog. <see cref="Fog" /> still applies the analytic falloff, which is
    ///     what every tier has always had; this decides whether the first
    ///     <see cref="VolumetricFar" /> metres of it are marched instead. A tier with
    ///     <see cref="Fog" /> false and this true gets nothing at all, because the composite lives in
    ///     the pass <see cref="Fog" /> switches off.
    /// </remarks>
    public bool? VolumetricFog { get; init; }

    /// <summary>Whether the froxels are shadowed against the sun's cascades.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The knob that decides whether the volume draws beams or a glow</b>, and by some way
    ///         the most expensive thing in the feature: a 3×3 filter per froxel, doubled across a
    ///         cascade seam, against a grid of 160 × 90 × <see cref="VolumetricSlices" />. The
    ///         injection and the march are each one cheap pass over the same grid; this is the rest of
    ///         the bill.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off is not "no fog", and it is not even "no volume".</b> The volume is still
    ///         filled and still composited — the height gradient and the forward scattering peak both
    ///         survive. What goes is the one thing an analytic falloff could never do: a shaft, which
    ///         is the <em>absence</em> of light behind a caster. A tier that switches this off is
    ///         choosing to pay for marched fog and not for the reason to march it, which is a
    ///         defensible trade only where the slice count is already low.
    ///     </para>
    ///     <para>
    ///         ⚠ True cannot conjure an atlas. A frame with no shadow node publishes no cascades, and
    ///         the pass detects that and goes unshadowed regardless — availability is the floor and
    ///         this is a ceiling under it. Which is why the knob is only worth having now that
    ///         something answers to it.
    ///     </para>
    /// </remarks>
    public bool? VolumetricShadows { get; init; }

    /// <summary>How many depth slices the froxel grid has — <see cref="VolumetricFogAsset.Slices" />.</summary>
    /// <remarks>
    ///     <para>
    ///         The knob that decides whether a shadow edge crossing the volume reads as an edge or as a
    ///         staircase, and the one that costs the most: the march is serial in this.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The only grid axis a tier moves, and the arithmetic says it should stay that
    ///         way.</b> A froxel at view depth <c>d</c> is <c>d · 2·tan(fovX/2) / 160</c> wide and
    ///         <c>d · ((far/near)^(1/slices) − 1)</c> deep. At 91° horizontal that is 0.013 d against
    ///         0.079 d at 64 slices, 0.042 d at 128, and 0.153 d at 32 — so the depth axis is between
    ///         three and twelve times the coarser one at every tier this ships, and it is where a
    ///         staircase comes from. Tiering the screen axes instead would sharpen what is already
    ///         fine, and now costs four volumes rather than three because the scattering one is a pair.
    ///     </para>
    /// </remarks>
    public int? VolumetricSlices { get; init; }

    /// <summary>How far the volume reaches, in metres — <see cref="VolumetricFogAsset.Far" />.</summary>
    /// <remarks>
    ///     ⚠ Not a draw distance, and raising it does not extend the fog — the analytic falloff
    ///     already covers everything past it. What it changes is how the slices are <em>spent</em>: a
    ///     grid stretched to a kilometre puts almost all of its resolution where nothing is visible
    ///     and leaves the near slices too thick to hold a beam's edge.
    /// </remarks>
    public float? VolumetricFar { get; init; }

    /// <summary>Whether the bloom pyramid is built.</summary>
    public bool? Bloom { get; init; }

    /// <summary>Pyramid depth — <see cref="BloomAsset.Levels" />.</summary>
    public int? BloomLevels { get; init; }

    /// <summary>The upsample filter radius — <see cref="BloomAsset.FilterRadius" />.</summary>
    public float? BloomFilterRadius { get; init; }

    /// <summary>Whether depth of field runs.</summary>
    public bool? DepthOfField { get; init; }

    /// <summary>Gather samples per pixel — <see cref="DepthOfFieldAsset.Samples" />.</summary>
    public int? DofSamples { get; init; }

    /// <summary>The widest blur in pixels — <see cref="DepthOfFieldAsset.MaximumRadius" />.</summary>
    public float? DofMaximumRadius { get; init; }

    /// <summary>Whether motion blur runs — always additionally gated on TAA's velocity pass.</summary>
    public bool? MotionBlur { get; init; }

    /// <summary>Samples along the velocity — <see cref="MotionBlurAsset.Samples" />.</summary>
    public int? MotionBlurSamples { get; init; }

    /// <summary>Whether local exposure runs — only ever with the meter.</summary>
    public bool? LocalExposure { get; init; }

    /// <summary>Bilateral taps — <see cref="LocalExposureAsset.Taps" />.</summary>
    public int? LocalExposureTaps { get; init; }

    /// <summary>Whether the lens flare runs.</summary>
    public bool? LensFlare { get; init; }

    /// <summary>Whether the vignette runs.</summary>
    public bool? Vignette { get; init; }

    /// <summary>Which threshold set FXAA runs at — <see cref="FxaaPreset" />.</summary>
    public FxaaPreset? Fxaa { get; init; }
}

/// <summary>Light-list capacities for one tier.</summary>
/// <remarks>
///     ⚠ Both entries are carried, not yet consumed: the capacities live on
///     <c>ForwardLightingRenderFeature</c>, which the host constructs before any document is read.
///     They land in the preset first so a project's tiers do not change shape when the host learns
///     to read them; clustered on/off and cookie resolution join them when those knobs exist.
/// </remarks>
[DataContract("LightQuality")]
public sealed record LightQuality {
    /// <summary>How many punctual lights the frame packs.</summary>
    public int? MaxLights { get; init; }

    /// <summary>How many of them one object may read.</summary>
    public int? MaxLightsPerObject { get; init; }
}

/// <summary>Geometry and culling knobs for one tier.</summary>
[DataContract("GeometryQuality")]
public sealed record GeometryQuality {
    /// <summary>Which culling shape the frame runs — <see cref="CullingMode" />.</summary>
    public CullingMode? Culling { get; init; }

    /// <summary>
    ///     Whether virtualized geometry draws. ⚠ Carried, not yet consumed: whether a project
    ///     <em>has</em> virtualized geometry is the host's, and the expansion does not emit the
    ///     traversal node yet.
    /// </summary>
    public bool? VirtualGeometry { get; init; }

    /// <summary>
    ///     Levels added to every LOD pick, positive for coarser. ⚠ Carried, not yet consumed: its
    ///     first consumer now exists — <c>TerrainComponent.LodBias</c> declares itself "for a
    ///     scalability setting" — but nothing extracts terrain into a frame yet, so no path hands
    ///     the resolved value over.
    /// </summary>
    public float? LodBias { get; init; }
}

/// <summary>Vegetation and terrain-surface budgets for one tier.</summary>
/// <remarks>
///     <para>
///         Every entry lands on a parameter that exists: the density scales are the scatter kernels'
///         own multipliers (<c>GrassScatter</c>'s <c>densityScale</c>, and the same fold over
///         <c>FoliageType.Density</c>), the cull scales multiply each type's authored cull distances,
///         the cell counts are <c>GrassResidency</c>'s and the foliage streamer's capacities, the near
///         range is <c>TerrainLodRanges.NearRange</c>, and the streaming pool is
///         <c>TerrainStreamer</c>'s byte budget.
///     </para>
///     <para>
///         ⚠ <b>They reach those renderers by hand-off, not by reference — and the hand-off is
///         <c>AppGraphics</c>'.</b> <c>Vixen.Rendering.Terrain</c> cannot see this assembly, because
///         the dependency runs the other way, so the terrain factory declares its own plain-numbered
///         copy (<c>TerrainVegetationQuality</c>) and the host folds a resolved tier into
///         <c>TerrainFactory.Vegetation</c> before it builds the frame. A knob added here is not
///         wired until that copy grows the same field <em>and</em> the host's fold assigns it: both
///         halves, or the number is carried and dropped.
///     </para>
/// </remarks>
[DataContract("VegetationQuality")]
public sealed record VegetationQuality {
    /// <summary>A multiplier on every grass type's blades-per-square-metre.</summary>
    public float? GrassDensityScale { get; init; }

    /// <summary>A multiplier on every grass type's authored cull distances.</summary>
    public float? GrassCullDistanceScale { get; init; }

    /// <summary>How many grass cells stay resident around the camera.</summary>
    public int? GrassResidentCells { get; init; }

    /// <summary>A multiplier on every foliage type's instances-per-square-metre.</summary>
    public float? FoliageDensityScale { get; init; }

    /// <summary>A multiplier on every foliage type's authored cull distances.</summary>
    public float? FoliageCullDistanceScale { get; init; }

    /// <summary>How many foliage cells the streamer keeps resident.</summary>
    public int? FoliageCellBudget { get; init; }

    /// <summary>Metres of full-detail terrain before the first LOD step.</summary>
    public float? TerrainLodNearRange { get; init; }

    /// <summary>Megabytes of staged terrain tile chains a frame may hold.</summary>
    /// <remarks>
    ///     ⚠ <b>Bytes here and cells for the foliage, and the asymmetry is the content's rather than
    ///     an oversight.</b> A terrain tile's mip chain is a fixed number of <c>ushort</c> samples, so
    ///     a byte budget converts to a slot count the moment the tile size is known; a foliage cell
    ///     holds however many instances somebody painted into it, which is why
    ///     <c>FoliageCellPages.PageSize</c> is one and <see cref="FoliageCellBudget" /> counts cells.
    /// </remarks>
    public int? TerrainStreamingMegabytes { get; init; }
}

/// <summary>Texture and effect budgets for one tier.</summary>
/// <remarks>
///     <para>
///         The streaming pool and the mip bias are consumed, and not by the compositor: they belong
///         to a system it does not construct, so <c>AppGraphics</c> resolves the tier and hands the
///         two numbers to <c>WorldRenderer.Textures</c> before it mounts content. A pool is sized
///         when the texture source is built and never afterwards, which is what makes a budget a
///         budget — see <c>TextureStreamer</c>.
///     </para>
///     <para>
///         ⚠ <see cref="ParticleBudgetScale" /> is still carried and not consumed; it belongs to the
///         VFX runtime. Foliage, grass and terrain live in their own group,
///         <see cref="VegetationQuality" />.
///     </para>
/// </remarks>
[DataContract("TextureQuality")]
public sealed record TextureQuality {
    /// <summary>The streaming pool's size in megabytes.</summary>
    public int? StreamingPoolMegabytes { get; init; }

    /// <summary>Levels added to every sampled mip, positive for blurrier.</summary>
    public float? MipBias { get; init; }

    /// <summary>A multiplier on every emitter's particle budget.</summary>
    public float? ParticleBudgetScale { get; init; }
}

/// <summary>What one tier says — each group optional, each field within a group optional.</summary>
/// <remarks>
///     Doc 32's opinion model, group-shaped: a tier names only what it overrides, and an unset
///     field falls through the waterfall to the layer below. ⚠ An unset field is not a zero —
///     <c>bloom: false</c> turns bloom off at this layer, which is a real thing for a tier to say,
///     while an absent <c>bloom:</c> keeps whatever the layer below decided. Nullable is that
///     distinction, exactly as it is on <c>PostProcessSettings</c>.
/// </remarks>
[DataContract("QualityTierOverrides")]
public sealed record QualityTierOverrides {
    /// <summary>Render-scale overrides, or null for no opinion.</summary>
    public ResolutionQuality? Resolution { get; init; }

    /// <summary>Shadow overrides, or null for no opinion.</summary>
    public ShadowQuality? Shadows { get; init; }

    /// <summary>Global-illumination overrides, or null for no opinion.</summary>
    public GlobalIlluminationQuality? GlobalIllumination { get; init; }

    /// <summary>Reflection overrides, or null for no opinion.</summary>
    public ReflectionQuality? Reflections { get; init; }

    /// <summary>Post-chain fidelity overrides, or null for no opinion.</summary>
    public PostFidelityQuality? PostFidelity { get; init; }

    /// <summary>Light-capacity overrides, or null for no opinion.</summary>
    public LightQuality? Lights { get; init; }

    /// <summary>Geometry and culling overrides, or null for no opinion.</summary>
    public GeometryQuality? Geometry { get; init; }

    /// <summary>Vegetation and terrain-surface overrides, or null for no opinion.</summary>
    public VegetationQuality? Vegetation { get; init; }

    /// <summary>Texture and effect budget overrides, or null for no opinion.</summary>
    public TextureQuality? Textures { get; init; }
}

/// <summary>
///     A project's <c>RenderQuality.vxpreset</c>: per-tier overrides of the engine's quality table.
/// </summary>
/// <remarks>
///     <para>
///         Doc 39's scalability layer, as an asset rather than an ini: everything quality-shaped
///         lives here, every entry maps to a named engine knob, and the boundary with the look
///         profile is one rule — look changes the intent, quality changes only the fidelity and
///         cost of the same intent. Bloom threshold is look; bloom pyramid levels are quality.
///     </para>
///     <para>
///         The waterfall is <see cref="RenderQuality.Resolve" />'s: the engine's own table (this
///         same type, at <see cref="RenderQuality.EngineDefaults" />), then the project's asset,
///         then a per-document overlay on <see cref="StandardFrameAsset.Preset" /> — per parameter,
///         so a tier that raises one number inherits every sibling from below. Tiers do not inherit
///         from <em>each other</em>: Low says what Low is, not what it changed since Medium.
///     </para>
/// </remarks>
[DataContract("RenderQuality")]
public sealed record RenderQualityAsset {
    /// <summary>What this preset says about the Low tier, or null for nothing.</summary>
    public QualityTierOverrides? Low { get; init; }

    /// <summary>What it says about Medium.</summary>
    public QualityTierOverrides? Medium { get; init; }

    /// <summary>What it says about High.</summary>
    public QualityTierOverrides? High { get; init; }

    /// <summary>What it says about Epic.</summary>
    public QualityTierOverrides? Epic { get; init; }
}

/// <summary>One tier's effective numbers, every field decided — what the expansion consumes.</summary>
/// <remarks>
///     The flat product of <see cref="RenderQuality.Resolve" /> and only ever made there, which is
///     what keeps the zero-value trap out: no field here can be an accidental default, because the
///     engine table underneath the waterfall is complete and the fold refuses a hole rather than
///     reading one as zero. Fields the expansion does not consume yet (the ⚠-carried entries on the
///     group records) are resolved all the same, so their consumers read this and never the layers.
/// </remarks>
public sealed record ResolvedQuality {
    /// <summary>See <see cref="ResolutionQuality.RenderScale" />.</summary>
    public required float RenderScale { get; init; }

    /// <summary>See <see cref="ShadowQuality.CascadeCount" />.</summary>
    public required int CascadeCount { get; init; }

    /// <summary>See <see cref="ShadowQuality.CascadeResolution" />.</summary>
    public required int CascadeResolution { get; init; }

    /// <summary>See <see cref="ShadowQuality.ShadowDistance" />.</summary>
    public required float ShadowDistance { get; init; }

    /// <summary>See <see cref="ShadowQuality.SplitLambda" />.</summary>
    public required float SplitLambda { get; init; }

    /// <summary>See <see cref="ShadowQuality.ConstantBias" />.</summary>
    public required float ConstantBias { get; init; }

    /// <summary>See <see cref="ShadowQuality.SlopeBias" />.</summary>
    public required float SlopeBias { get; init; }

    /// <summary>See <see cref="ShadowQuality.PunctualTilesPerSide" />.</summary>
    public required int PunctualTilesPerSide { get; init; }

    /// <summary>See <see cref="ShadowQuality.PunctualResolution" />.</summary>
    public required int PunctualResolution { get; init; }

    /// <summary>See <see cref="ShadowQuality.VirtualLevels" />.</summary>
    public required int VirtualLevels { get; init; }

    /// <summary>See <see cref="ShadowQuality.VirtualFirstExtent" />.</summary>
    public required float VirtualFirstExtent { get; init; }

    /// <summary>See <see cref="ShadowQuality.VirtualPagesPerFrame" />.</summary>
    public required int VirtualPagesPerFrame { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.IrradianceBudget" />.</summary>
    public required int IrradianceBudget { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.DilationPasses" />.</summary>
    public required int DilationPasses { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.ProbeTileSize" />.</summary>
    public required int ProbeTileSize { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.ScreenTraces" />.</summary>
    public required bool ScreenTraces { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.DfaoSamples" />.</summary>
    public required int DfaoSamples { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.DfaoScale" />.</summary>
    public required float DfaoScale { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.SsaoDirections" />.</summary>
    public required int SsaoDirections { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.SsaoSteps" />.</summary>
    public required int SsaoSteps { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.SsaoScale" />.</summary>
    public required float SsaoScale { get; init; }

    /// <summary>See <see cref="GlobalIlluminationQuality.SurfaceCacheSize" />.</summary>
    public required int SurfaceCacheSize { get; init; }

    /// <summary>See <see cref="ReflectionQuality.ScreenSteps" />.</summary>
    public required int ReflectionSteps { get; init; }

    /// <summary>See <see cref="ReflectionQuality.RoughnessThreshold" />.</summary>
    public required float RoughnessThreshold { get; init; }

    /// <summary>See <see cref="ReflectionQuality.TraceScale" />.</summary>
    public required float TraceScale { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.TaaVarianceClipping" />.</summary>
    public required bool TaaVarianceClipping { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.Fog" />.</summary>
    public required bool Fog { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.VolumetricFog" />.</summary>
    public required bool VolumetricFog { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.VolumetricShadows" />.</summary>
    public required bool VolumetricShadows { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.VolumetricSlices" />.</summary>
    public required int VolumetricSlices { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.VolumetricFar" />.</summary>
    public required float VolumetricFar { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.Bloom" />.</summary>
    public required bool Bloom { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.BloomLevels" />.</summary>
    public required int BloomLevels { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.BloomFilterRadius" />.</summary>
    public required float BloomFilterRadius { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.DepthOfField" />.</summary>
    public required bool DepthOfField { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.DofSamples" />.</summary>
    public required int DofSamples { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.DofMaximumRadius" />.</summary>
    public required float DofMaximumRadius { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.MotionBlur" />.</summary>
    public required bool MotionBlur { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.MotionBlurSamples" />.</summary>
    public required int MotionBlurSamples { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.LocalExposure" />.</summary>
    public required bool LocalExposure { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.LocalExposureTaps" />.</summary>
    public required int LocalExposureTaps { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.LensFlare" />.</summary>
    public required bool LensFlare { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.Vignette" />.</summary>
    public required bool Vignette { get; init; }

    /// <summary>See <see cref="PostFidelityQuality.Fxaa" />.</summary>
    public required FxaaPreset Fxaa { get; init; }

    /// <summary>See <see cref="LightQuality.MaxLights" />.</summary>
    public required int MaxLights { get; init; }

    /// <summary>See <see cref="LightQuality.MaxLightsPerObject" />.</summary>
    public required int MaxLightsPerObject { get; init; }

    /// <summary>See <see cref="GeometryQuality.Culling" />.</summary>
    public required CullingMode Culling { get; init; }

    /// <summary>See <see cref="GeometryQuality.VirtualGeometry" />.</summary>
    public required bool VirtualGeometry { get; init; }

    /// <summary>See <see cref="GeometryQuality.LodBias" />.</summary>
    public required float LodBias { get; init; }

    /// <summary>See <see cref="VegetationQuality.GrassDensityScale" />.</summary>
    public required float GrassDensityScale { get; init; }

    /// <summary>See <see cref="VegetationQuality.GrassCullDistanceScale" />.</summary>
    public required float GrassCullDistanceScale { get; init; }

    /// <summary>See <see cref="VegetationQuality.GrassResidentCells" />.</summary>
    public required int GrassResidentCells { get; init; }

    /// <summary>See <see cref="VegetationQuality.FoliageDensityScale" />.</summary>
    public required float FoliageDensityScale { get; init; }

    /// <summary>See <see cref="VegetationQuality.FoliageCullDistanceScale" />.</summary>
    public required float FoliageCullDistanceScale { get; init; }

    /// <summary>See <see cref="VegetationQuality.FoliageCellBudget" />.</summary>
    public required int FoliageCellBudget { get; init; }

    /// <summary>See <see cref="VegetationQuality.TerrainLodNearRange" />.</summary>
    public required float TerrainLodNearRange { get; init; }

    /// <summary>See <see cref="VegetationQuality.TerrainStreamingMegabytes" />.</summary>
    public required int TerrainStreamingMegabytes { get; init; }

    /// <summary>See <see cref="TextureQuality.StreamingPoolMegabytes" />.</summary>
    public required int StreamingPoolMegabytes { get; init; }

    /// <summary>See <see cref="TextureQuality.MipBias" />.</summary>
    public required float MipBias { get; init; }

    /// <summary>See <see cref="TextureQuality.ParticleBudgetScale" />.</summary>
    public required float ParticleBudgetScale { get; init; }
}

/// <summary>The quality waterfall: engine tier defaults, folded under a project's overrides.</summary>
/// <remarks>
///     <para>
///         Doc 39's layers as one pure function. <see cref="EngineDefaults" /> is the table the
///         Standard Frame used to hardcode, expressed as the same asset type a project authors —
///         which is what makes the waterfall one mechanism instead of a table and a patch format.
///         The engine layer is <em>complete</em>: every group of every tier, every field set. The
///         layers above it say only what they change.
///     </para>
///     <para>
///         What is deliberately absent is any loading: the function takes assets, not addresses. A
///         project preset reaches the expansion through <see cref="PostEffectFactory.Preset" /> —
///         the host loads the <c>.vxpreset</c> and hands it over — or inline through
///         <see cref="StandardFrameAsset.Preset" />; resolving an address from inside the document
///         transform would put content IO inside a build that must stay pure.
///     </para>
/// </remarks>
public static class RenderQuality {
    /// <summary>The engine's own tier table — the waterfall's complete bottom layer.</summary>
    /// <remarks>
    ///     The numbers docs/plan/39 assigns per tier, moved here verbatim from the Standard Frame's
    ///     phase-1 hardcoded table. Fields the expansion consumes vary per tier; the ⚠-carried
    ///     fields hold each node's own shipped default until their consumers exist, so resolving
    ///     them answers what the engine would do rather than zero.
    /// </remarks>
    public static RenderQualityAsset EngineDefaults { get; } = new() {
        Low = new() {
            Resolution = new() { RenderScale = 1f },
            Shadows = new() {
                CascadeCount = 2,
                CascadeResolution = 1024,
                ShadowDistance = 75f,
                SplitLambda = 0.75f,
                ConstantBias = 0.0015f,
                SlopeBias = 0.004f,
                PunctualTilesPerSide = 4,
                PunctualResolution = 256,
                VirtualLevels = 8,
                VirtualFirstExtent = 10f,
                VirtualPagesPerFrame = 8
            },
            GlobalIllumination = new() {
                IrradianceBudget = 2,
                DilationPasses = 1,
                ProbeTileSize = 32,
                ScreenTraces = false,
                DfaoSamples = 8,
                DfaoScale = 0.5f,
                SsaoDirections = 4,
                SsaoSteps = 4,
                SsaoScale = 0.5f,
                SurfaceCacheSize = 1024
            },
            Reflections = new() { ScreenSteps = 16, RoughnessThreshold = 0.5f, TraceScale = 0.5f },
            PostFidelity = new() {
                TaaVarianceClipping = false,
                Fog = false,
                VolumetricFog = false,
                VolumetricShadows = false,
                VolumetricSlices = 32,
                VolumetricFar = 48f,
                Bloom = false,
                BloomLevels = 3,
                BloomFilterRadius = 1f,
                DepthOfField = false,
                DofSamples = 8,
                DofMaximumRadius = 24f,
                MotionBlur = false,
                MotionBlurSamples = 4,
                LocalExposure = false,
                LocalExposureTaps = 4,
                LensFlare = false,
                Vignette = false,
                Fxaa = FxaaPreset.Performance
            },
            Lights = new() { MaxLights = 64, MaxLightsPerObject = 4 },
            Geometry = new() { Culling = CullingMode.Off, VirtualGeometry = false, LodBias = 1f },
            Vegetation = new() {
                GrassDensityScale = 0.5f,
                GrassCullDistanceScale = 0.6f,
                GrassResidentCells = 128,
                FoliageDensityScale = 0.6f,
                FoliageCullDistanceScale = 0.7f,
                FoliageCellBudget = 128,
                TerrainLodNearRange = 48f,
                TerrainStreamingMegabytes = 32
            },
            Textures = new() { StreamingPoolMegabytes = 1024, MipBias = 0.5f, ParticleBudgetScale = 0.5f }
        },
        Medium = new() {
            Resolution = new() { RenderScale = 1f },
            Shadows = new() {
                CascadeCount = 4,
                CascadeResolution = 1024,
                ShadowDistance = 120f,
                SplitLambda = 0.75f,
                ConstantBias = 0.0015f,
                SlopeBias = 0.004f,
                PunctualTilesPerSide = 6,
                PunctualResolution = 256,
                VirtualLevels = 8,
                VirtualFirstExtent = 10f,
                VirtualPagesPerFrame = 12
            },
            GlobalIllumination = new() {
                IrradianceBudget = 4,
                DilationPasses = 1,
                ProbeTileSize = 16,
                ScreenTraces = false,
                DfaoSamples = 12,
                DfaoScale = 0.5f,
                SsaoDirections = 6,
                SsaoSteps = 4,
                SsaoScale = 0.5f,
                SurfaceCacheSize = 2048
            },
            Reflections = new() { ScreenSteps = 24, RoughnessThreshold = 0.5f, TraceScale = 0.5f },
            PostFidelity = new() {
                TaaVarianceClipping = true,
                Fog = true,
                VolumetricFog = false,
                VolumetricShadows = false,
                VolumetricSlices = 32,
                VolumetricFar = 48f,
                Bloom = true,
                BloomLevels = 4,
                BloomFilterRadius = 1f,
                DepthOfField = false,
                DofSamples = 12,
                DofMaximumRadius = 24f,
                MotionBlur = false,
                MotionBlurSamples = 6,
                LocalExposure = false,
                LocalExposureTaps = 6,
                LensFlare = false,
                Vignette = false,
                Fxaa = FxaaPreset.Balanced
            },
            Lights = new() { MaxLights = 128, MaxLightsPerObject = 8 },
            Geometry = new() { Culling = CullingMode.Indirect, VirtualGeometry = true, LodBias = 0f },
            Vegetation = new() {
                GrassDensityScale = 0.75f,
                GrassCullDistanceScale = 0.8f,
                GrassResidentCells = 192,
                FoliageDensityScale = 0.8f,
                FoliageCullDistanceScale = 0.85f,
                FoliageCellBudget = 192,
                TerrainLodNearRange = 64f,
                TerrainStreamingMegabytes = 48
            },
            Textures = new() { StreamingPoolMegabytes = 2048, MipBias = 0f, ParticleBudgetScale = 0.75f }
        },
        High = new() {
            Resolution = new() { RenderScale = 1f },
            Shadows = new() {
                CascadeCount = 4,
                CascadeResolution = 2048,
                ShadowDistance = 150f,
                SplitLambda = 0.75f,
                ConstantBias = 0.0015f,
                SlopeBias = 0.004f,
                PunctualTilesPerSide = 8,
                PunctualResolution = 256,
                VirtualLevels = 8,
                VirtualFirstExtent = 10f,
                VirtualPagesPerFrame = 16
            },
            GlobalIllumination = new() {
                IrradianceBudget = 8,
                DilationPasses = 1,
                ProbeTileSize = 16,
                ScreenTraces = true,
                DfaoSamples = 16,
                DfaoScale = 0.5f,
                SsaoDirections = 8,
                SsaoSteps = 6,
                SsaoScale = 0.5f,
                SurfaceCacheSize = 4096
            },
            Reflections = new() { ScreenSteps = 32, RoughnessThreshold = 0.5f, TraceScale = 1f },
            PostFidelity = new() {
                TaaVarianceClipping = true,
                Fog = true,
                VolumetricFog = true,
                VolumetricShadows = true,
                VolumetricSlices = 64,
                VolumetricFar = 64f,
                Bloom = true,
                BloomLevels = 5,
                BloomFilterRadius = 1f,
                DepthOfField = true,
                DofSamples = 16,
                DofMaximumRadius = 24f,
                MotionBlur = true,
                MotionBlurSamples = 8,
                LocalExposure = true,
                LocalExposureTaps = 6,
                LensFlare = true,
                Vignette = true,
                Fxaa = FxaaPreset.Balanced
            },
            Lights = new() { MaxLights = 256, MaxLightsPerObject = 8 },
            Geometry = new() { Culling = CullingMode.Indirect, VirtualGeometry = true, LodBias = 0f },
            // High is the vegetation libraries' own shipped defaults: GrassResidency's 256 cells,
            // FoliageType/GrassType densities at par, TerrainLodRanges.Default's 64 m near range and
            // TerrainStreamer's own 64 MiB pool.
            Vegetation = new() {
                GrassDensityScale = 1f,
                GrassCullDistanceScale = 1f,
                GrassResidentCells = 256,
                FoliageDensityScale = 1f,
                FoliageCullDistanceScale = 1f,
                FoliageCellBudget = 256,
                TerrainLodNearRange = 64f,
                TerrainStreamingMegabytes = 64
            },
            Textures = new() { StreamingPoolMegabytes = 3072, MipBias = 0f, ParticleBudgetScale = 1f }
        },
        Epic = new() {
            Resolution = new() { RenderScale = 1f },
            Shadows = new() {
                CascadeCount = 4,
                CascadeResolution = 2048,
                ShadowDistance = 200f,
                SplitLambda = 0.75f,
                ConstantBias = 0.0015f,
                SlopeBias = 0.004f,
                PunctualTilesPerSide = 8,
                PunctualResolution = 512,
                VirtualLevels = 8,
                VirtualFirstExtent = 10f,
                VirtualPagesPerFrame = 32
            },
            GlobalIllumination = new() {
                IrradianceBudget = 16,
                DilationPasses = 1,
                ProbeTileSize = 8,
                ScreenTraces = true,
                DfaoSamples = 24,
                DfaoScale = 1f,
                SsaoDirections = 12,
                SsaoSteps = 8,
                SsaoScale = 1f,
                SurfaceCacheSize = 8192
            },
            Reflections = new() { ScreenSteps = 64, RoughnessThreshold = 0.5f, TraceScale = 1f },
            PostFidelity = new() {
                TaaVarianceClipping = true,
                Fog = true,
                VolumetricFog = true,
                VolumetricShadows = true,
                VolumetricSlices = 128,
                VolumetricFar = 96f,
                Bloom = true,
                BloomLevels = 6,
                BloomFilterRadius = 1f,
                DepthOfField = true,
                DofSamples = 24,
                DofMaximumRadius = 24f,
                MotionBlur = true,
                MotionBlurSamples = 12,
                LocalExposure = true,
                LocalExposureTaps = 12,
                LensFlare = true,
                Vignette = true,
                Fxaa = FxaaPreset.Quality
            },
            Lights = new() { MaxLights = 512, MaxLightsPerObject = 8 },
            Geometry = new() { Culling = CullingMode.Indirect, VirtualGeometry = true, LodBias = 0f },
            Vegetation = new() {
                GrassDensityScale = 1f,
                GrassCullDistanceScale = 1.25f,
                GrassResidentCells = 384,
                FoliageDensityScale = 1f,
                FoliageCullDistanceScale = 1.25f,
                FoliageCellBudget = 384,
                TerrainLodNearRange = 96f,
                TerrainStreamingMegabytes = 128
            },
            Textures = new() { StreamingPoolMegabytes = 4096, MipBias = 0f, ParticleBudgetScale = 1f }
        }
    };

    /// <summary>Folds the waterfall for one tier into the flat numbers the expansion consumes.</summary>
    /// <param name="tier">Which tier's column is read, at every layer.</param>
    /// <param name="project">The project's <c>.vxpreset</c>, or null for engine defaults alone.</param>
    /// <param name="overlay">
    ///     A per-document overlay — <see cref="StandardFrameAsset.Preset" /> — and the top vote.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     A field null at every layer, which can only mean <see cref="EngineDefaults" /> lost an
    ///     entry — refused by name rather than read as zero, because a zero here is a shadow
    ///     distance of nothing and an atlas of no tiles, silently.
    /// </exception>
    public static ResolvedQuality Resolve(
        QualityTier tier,
        RenderQualityAsset? project = null,
        RenderQualityAsset? overlay = null
    ) {
        var engine = TierOf(EngineDefaults, tier);
        var mid = project is null ? null : TierOf(project, tier);
        var top = overlay is null ? null : TierOf(overlay, tier);

        // One local per field would be quieter, but the shape below is the contract stated once:
        // top layer, then project, then engine, per parameter — never per group, so a tier that
        // raises one number inherits every sibling from the layer below it.
        T Pick<TGroup, T>(Func<QualityTierOverrides, TGroup?> group, Func<TGroup, T?> field, string name)
            where TGroup : class
            where T : struct {
            return Of(top) ?? Of(mid) ?? Of(engine)
                ?? throw new InvalidOperationException(
                    $"The engine quality table has no '{name}' for tier {tier}. The bottom layer "
                    + "of the waterfall must be complete — a hole here would resolve as zero, and "
                    + "a zero quality knob is never what anyone wrote."
                );

            T? Of(QualityTierOverrides? layer) => layer is null
                ? null
                : group(layer) is { } opinions
                    ? field(opinions)
                    : null;
        }

        return new() {
            RenderScale = Pick(t => t.Resolution, g => g.RenderScale, "resolution.renderScale"),
            CascadeCount = Pick(t => t.Shadows, g => g.CascadeCount, "shadows.cascadeCount"),
            CascadeResolution = Pick(t => t.Shadows, g => g.CascadeResolution, "shadows.cascadeResolution"),
            ShadowDistance = Pick(t => t.Shadows, g => g.ShadowDistance, "shadows.shadowDistance"),
            SplitLambda = Pick(t => t.Shadows, g => g.SplitLambda, "shadows.splitLambda"),
            ConstantBias = Pick(t => t.Shadows, g => g.ConstantBias, "shadows.constantBias"),
            SlopeBias = Pick(t => t.Shadows, g => g.SlopeBias, "shadows.slopeBias"),
            PunctualTilesPerSide = Pick(t => t.Shadows, g => g.PunctualTilesPerSide, "shadows.punctualTilesPerSide"),
            PunctualResolution = Pick(t => t.Shadows, g => g.PunctualResolution, "shadows.punctualResolution"),
            VirtualLevels = Pick(t => t.Shadows, g => g.VirtualLevels, "shadows.virtualLevels"),
            VirtualFirstExtent = Pick(t => t.Shadows, g => g.VirtualFirstExtent, "shadows.virtualFirstExtent"),
            VirtualPagesPerFrame = Pick(t => t.Shadows, g => g.VirtualPagesPerFrame, "shadows.virtualPagesPerFrame"),
            IrradianceBudget = Pick(t => t.GlobalIllumination, g => g.IrradianceBudget, "gi.irradianceBudget"),
            DilationPasses = Pick(t => t.GlobalIllumination, g => g.DilationPasses, "gi.dilationPasses"),
            ProbeTileSize = Pick(t => t.GlobalIllumination, g => g.ProbeTileSize, "gi.probeTileSize"),
            ScreenTraces = Pick(t => t.GlobalIllumination, g => g.ScreenTraces, "gi.screenTraces"),
            DfaoSamples = Pick(t => t.GlobalIllumination, g => g.DfaoSamples, "gi.dfaoSamples"),
            DfaoScale = Pick(t => t.GlobalIllumination, g => g.DfaoScale, "gi.dfaoScale"),
            SsaoDirections = Pick(t => t.GlobalIllumination, g => g.SsaoDirections, "gi.ssaoDirections"),
            SsaoSteps = Pick(t => t.GlobalIllumination, g => g.SsaoSteps, "gi.ssaoSteps"),
            SsaoScale = Pick(t => t.GlobalIllumination, g => g.SsaoScale, "gi.ssaoScale"),
            SurfaceCacheSize = Pick(t => t.GlobalIllumination, g => g.SurfaceCacheSize, "gi.surfaceCacheSize"),
            ReflectionSteps = Pick(t => t.Reflections, g => g.ScreenSteps, "reflections.screenSteps"),
            RoughnessThreshold = Pick(t => t.Reflections, g => g.RoughnessThreshold, "reflections.roughnessThreshold"),
            TraceScale = Pick(t => t.Reflections, g => g.TraceScale, "reflections.traceScale"),
            TaaVarianceClipping = Pick(t => t.PostFidelity, g => g.TaaVarianceClipping, "post.taaVarianceClipping"),
            Fog = Pick(t => t.PostFidelity, g => g.Fog, "post.fog"),
            VolumetricFog = Pick(t => t.PostFidelity, g => g.VolumetricFog, "post.volumetricFog"),
            VolumetricShadows = Pick(t => t.PostFidelity, g => g.VolumetricShadows, "post.volumetricShadows"),
            VolumetricSlices = Pick(t => t.PostFidelity, g => g.VolumetricSlices, "post.volumetricSlices"),
            VolumetricFar = Pick(t => t.PostFidelity, g => g.VolumetricFar, "post.volumetricFar"),
            Bloom = Pick(t => t.PostFidelity, g => g.Bloom, "post.bloom"),
            BloomLevels = Pick(t => t.PostFidelity, g => g.BloomLevels, "post.bloomLevels"),
            BloomFilterRadius = Pick(t => t.PostFidelity, g => g.BloomFilterRadius, "post.bloomFilterRadius"),
            DepthOfField = Pick(t => t.PostFidelity, g => g.DepthOfField, "post.depthOfField"),
            DofSamples = Pick(t => t.PostFidelity, g => g.DofSamples, "post.dofSamples"),
            DofMaximumRadius = Pick(t => t.PostFidelity, g => g.DofMaximumRadius, "post.dofMaximumRadius"),
            MotionBlur = Pick(t => t.PostFidelity, g => g.MotionBlur, "post.motionBlur"),
            MotionBlurSamples = Pick(t => t.PostFidelity, g => g.MotionBlurSamples, "post.motionBlurSamples"),
            LocalExposure = Pick(t => t.PostFidelity, g => g.LocalExposure, "post.localExposure"),
            LocalExposureTaps = Pick(t => t.PostFidelity, g => g.LocalExposureTaps, "post.localExposureTaps"),
            LensFlare = Pick(t => t.PostFidelity, g => g.LensFlare, "post.lensFlare"),
            Vignette = Pick(t => t.PostFidelity, g => g.Vignette, "post.vignette"),
            Fxaa = Pick(t => t.PostFidelity, g => g.Fxaa, "post.fxaa"),
            MaxLights = Pick(t => t.Lights, g => g.MaxLights, "lights.maxLights"),
            MaxLightsPerObject = Pick(t => t.Lights, g => g.MaxLightsPerObject, "lights.maxLightsPerObject"),
            Culling = Pick(t => t.Geometry, g => g.Culling, "geometry.culling"),
            VirtualGeometry = Pick(t => t.Geometry, g => g.VirtualGeometry, "geometry.virtualGeometry"),
            LodBias = Pick(t => t.Geometry, g => g.LodBias, "geometry.lodBias"),
            GrassDensityScale = Pick(t => t.Vegetation, g => g.GrassDensityScale, "vegetation.grassDensityScale"),
            GrassCullDistanceScale = Pick(t => t.Vegetation, g => g.GrassCullDistanceScale, "vegetation.grassCullDistanceScale"),
            GrassResidentCells = Pick(t => t.Vegetation, g => g.GrassResidentCells, "vegetation.grassResidentCells"),
            FoliageDensityScale = Pick(t => t.Vegetation, g => g.FoliageDensityScale, "vegetation.foliageDensityScale"),
            FoliageCullDistanceScale = Pick(t => t.Vegetation, g => g.FoliageCullDistanceScale, "vegetation.foliageCullDistanceScale"),
            FoliageCellBudget = Pick(t => t.Vegetation, g => g.FoliageCellBudget, "vegetation.foliageCellBudget"),
            TerrainLodNearRange = Pick(t => t.Vegetation, g => g.TerrainLodNearRange, "vegetation.terrainLodNearRange"),
            TerrainStreamingMegabytes = Pick(t => t.Vegetation, g => g.TerrainStreamingMegabytes, "vegetation.terrainStreamingMegabytes"),
            StreamingPoolMegabytes = Pick(t => t.Textures, g => g.StreamingPoolMegabytes, "textures.streamingPoolMegabytes"),
            MipBias = Pick(t => t.Textures, g => g.MipBias, "textures.mipBias"),
            ParticleBudgetScale = Pick(t => t.Textures, g => g.ParticleBudgetScale, "textures.particleBudgetScale")
        };
    }

    static QualityTierOverrides? TierOf(RenderQualityAsset preset, QualityTier tier) => tier switch {
        QualityTier.Low => preset.Low,
        QualityTier.Medium => preset.Medium,
        QualityTier.Epic => preset.Epic,
        _ => preset.High
    };
}
