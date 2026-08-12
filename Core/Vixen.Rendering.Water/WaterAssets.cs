// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;
using Vixen.Terrain;

namespace Vixen.Rendering.Water;

/// <summary>The water pass, as a document names it.</summary>
/// <remarks>
///     <para>
///         <b>A node in a <c>.vxcompositor</c>, which means a project that has no water does not pay
///         for the copy</b> —
///         [35 § D8](../../docs/plan/35-water.md#d8-the-surface-is-a-pass-between-lighting-and-translucency-and-its-reflections-are-l5s).
///         The <c>!Copy</c> ahead of it is culled with its target when nothing reads the snapshot, so
///         a document carrying both costs nothing in a scene where no surface pass ran.
///     </para>
///     <para>
///         ⚠ <b><see cref="Behind" /> must name a copy and not the scene colour itself.</b> Reading
///         the target a pass is writing is undefined behaviour, and the whole reason § B1 called the
///         copy the blocker rather than the pass. The graph refuses the case where the two names are
///         equal — see <see cref="WaterRenderer" /> — because a document that made that mistake would
///         otherwise render on one driver and not another.
///     </para>
/// </remarks>
[DataContract("Water")]
public sealed record WaterAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The target it writes — the frame's scene colour.</summary>
    public string Output { get; init; } = "SceneColour";

    /// <summary>The copy of the frame so far, which is what is behind the water.</summary>
    public string Behind { get; init; } = "SceneColourCopy";

    /// <summary>The opaque scene's depth.</summary>
    public string SceneDepth { get; init; } = "SceneDepth";

    /// <summary>The surface plane: device depth in <c>r</c>, coverage in <c>g</c>.</summary>
    public string Surface { get; init; } = "WaterSurface";

    /// <summary>The surface's world normal in <c>xyz</c> and its foam in <c>a</c>.</summary>
    public string Normal { get; init; } = "WaterNormal";

    /// <summary>Doc 19 § L5's reflection plane, or empty to compile the variant without it.</summary>
    public string Reflections { get; init; } = string.Empty;

    /// <summary>Which view's camera the two distances are reconstructed against.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>How much light the medium scatters out of a ray per metre, per channel.</summary>
    public Vector3 Scattering { get; init; } = new(0.03f, 0.06f, 0.09f);

    /// <summary>How much it absorbs per metre, per channel.</summary>
    public Vector3 Absorption { get; init; } = new(0.35f, 0.06f, 0.02f);

    /// <summary>Henyey–Greenstein anisotropy. Water is forward-scattering, around 0.7.</summary>
    public float PhaseG { get; init; } = 0.7f;

    /// <summary>§ D8's scale on what is behind the water. One is the physical answer.</summary>
    public Vector3 BehindScale { get; init; } = Vector3.One;

    /// <summary>
    ///     What the sun delivers to the volume, <b>as an illuminance in lux</b>, for a host with no
    ///     lighting feature in it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A fallback rather than the answer, and never a tint.</b> A host that hands the
    ///     compositor an <c>ISunSource</c> — every host with a lighting feature does — gives the water
    ///     the scene's own sun, and this is what a document with none gets. Written as a colour it is
    ///     four decades under the frame it composites into, which is a lake that tonemaps to the same
    ///     black as a pass that never ran; see <c>WaterRenderer.SunColour</c> for what that cost.
    /// </remarks>
    public Vector3 SunColour { get; init; } = new(90000f, 81000f, 63000f);

    /// <summary>Which way the light travels, for a host with no sun to read.</summary>
    /// <remarks>
    ///     ⚠ Left unstated, the water lights with a noon sun whatever the sky in the same document
    ///     says — the forward-scattering peak lands in the wrong place, which reads as a lake lit
    ///     from a different day than its sky.
    /// </remarks>
    public Vector3 SunDirection { get; init; } = new(0f, -1f, 0f);

    /// <summary>
    ///     What arrives from the whole sky, <b>as a radiance in cd/m²</b>, for a frame with no
    ///     environment in it. ⚠ Without it, deep water is black.
    /// </summary>
    /// <remarks>
    ///     ⚠ On <see cref="SunColour" />'s terms, and the same number
    ///     <see cref="UnderwaterAsset.SkyColour" /> defaults to — a sky that changed value at the
    ///     waterline is a lake that changes colour when you put your head under.
    /// </remarks>
    public Vector3 SkyColour { get; init; } = new(1400f, 1680f, 2200f);

    /// <summary>Water against air. ⚠ Not a base colour.</summary>
    public float SurfaceF0 { get; init; } = 0.02f;

    /// <summary>What foam is where the surface plane says there is some.</summary>
    public Vector3 FoamColour { get; init; } = new(0.9f, 0.93f, 0.95f);

    /// <summary>Whether foam is blended over the result at all.</summary>
    public bool Foam { get; init; } = true;

    /// <summary>Whether the pass runs over the tiles that have water rather than over the screen.</summary>
    /// <remarks>
    ///     <para>
    ///         § D8's tile classification — see <see cref="WaterRenderer.Tiled" /> for what it does and
    ///         what it needs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On here and off on the node, and the difference is <see cref="Behind" />.</b> Tiling
    ///         leaves a dry tile's pixels as it found them, which is the same picture only where
    ///         <see cref="Output" /> already holds what <see cref="Behind" /> is a copy of. A document
    ///         guarantees that — the <c>!Copy</c> § B1 requires is what filled it, from this very target
    ///         — and a node somebody wired by hand does not.
    ///     </para>
    /// </remarks>
    public bool Tiled { get; init; } = true;
}

/// <summary>The water surface mesh, as a document names it.</summary>
/// <remarks>
///     <para>
///         <b>[35 § D4](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source),
///         and the node without which <c>!Water</c> has nothing to composite.</b> It draws every
///         zone's quadtree into the two planes — a coverage mask with the surface's device depth, and
///         the surface's normal with its foam — and writes no colour of its own.
///     </para>
///     <para>
///         ⚠ <b>It goes after the opaque pass and before <c>!Copy</c>.</b> The surface is rasterised
///         against the scene's own depth so a lake behind a hill does not draw over it, and the copy
///         has to be taken after everything the water will be composited <em>over</em>.
///     </para>
/// </remarks>
[DataContract("WaterSurface")]
public sealed record WaterSurfaceAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The surface plane it writes: device depth in <c>r</c>, coverage in <c>g</c>.</summary>
    public string Surface { get; init; } = "WaterSurface";

    /// <summary>The normal plane: the world normal in <c>xyz</c> and the foam in <c>a</c>.</summary>
    public string Normal { get; init; } = "WaterNormal";

    /// <summary>The opaque scene's depth, tested against and never written.</summary>
    public string SceneDepth { get; init; } = "SceneDepth";

    /// <summary>Which view the patches are selected for.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>How many quads the shared grid patch spans.</summary>
    public int GridQuads { get; init; } = PatchSelector.DefaultGridQuads;

    /// <summary>How far level 0 reaches, in metres.</summary>
    public float NearRange { get; init; } = 64f;

    /// <summary>How many levels of detail the descent may use.</summary>
    public int LevelCount { get; init; } = 5;

    /// <summary>How far past a window the far skirt reaches, in metres.</summary>
    public float FarDistance { get; init; } = 8000f;

    /// <summary>How wide the band is over which the waves fade at a window's edge, in metres.</summary>
    public float EdgeFade { get; init; } = 32f;

    /// <summary>Where the open surface sits, for the far skirt, in world units.</summary>
    public float RestHeight { get; init; }

    /// <summary>How deep the water has to be before it is fully opaque water, in metres.</summary>
    public float ShoreDepth { get; init; } = 0.25f;

    /// <summary>How shallow it has to be before it foams, in metres.</summary>
    public float FoamDepth { get; init; } = 0.4f;

    /// <summary>How far above the rest height a crest has to rise before it foams, in metres.</summary>
    public float FoamCrest { get; init; } = 0.5f;
}

/// <summary>The waterline composite, as a document names it.</summary>
/// <remarks>
///     <para>
///         <b>[35 § D9](../../docs/plan/35-water.md#d9-underwater-is-a-post-process-volume-and-the-waterline-is-named-as-the-hard-part)'s
///         second half.</b> The volume half is a <c>PostProcessVolume</c> with
///         <see cref="UnderwaterShape" /> and grades the whole frame; this is the curve, which a fold
///         cannot express because a fold produces one weight.
///     </para>
///     <para>
///         ⚠ <b>It goes <em>after</em> <c>!Water</c>, and after a second <c>!Copy</c>.</b> What it
///         grades is the finished frame including the water surface, so the copy it reads has to be
///         taken after the surface was composited — a document that reuses the copy <c>!Water</c> read
///         would grade the frame as it was before the water was in it, which at the waterline is a
///         band of unlit lake.
///     </para>
///     <para>
///         A project that never puts a camera in the water can leave the node out entirely; a project
///         that names it pays a fullscreen pass that returns on its first branch whenever the camera
///         is dry.
///     </para>
/// </remarks>
[DataContract("Underwater")]
public sealed record UnderwaterAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The target it writes — the frame's scene colour.</summary>
    public string Output { get; init; } = "SceneColour";

    /// <summary>The copy of the finished frame, which is what it grades.</summary>
    public string Behind { get; init; } = "SceneColourCopy";

    /// <summary>The opaque scene's depth.</summary>
    public string SceneDepth { get; init; } = "SceneDepth";

    /// <summary>The surface plane, read for where a ray leaves the water.</summary>
    public string Surface { get; init; } = "WaterSurface";

    /// <summary>Which view's camera the waterline is solved against.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>How wide the waterline's fade is, in metres.</summary>
    public float WaterlineFeather { get; init; } = 0.04f;

    /// <summary>How much light the medium scatters out of a ray per metre, per channel.</summary>
    /// <remarks>⚠ The same triple <c>!Water</c> carries, or the lake changes colour when you go under.</remarks>
    public Vector3 Scattering { get; init; } = new(0.03f, 0.06f, 0.09f);

    /// <summary>How much it absorbs per metre, per channel.</summary>
    public Vector3 Absorption { get; init; } = new(0.35f, 0.06f, 0.02f);

    /// <summary>Henyey–Greenstein anisotropy, carried so the medium is one description.</summary>
    public float PhaseG { get; init; } = 0.7f;

    /// <summary>
    ///     What arrives from the whole sky, <b>as a radiance in cd/m²</b>, for a frame with no
    ///     environment in it. ⚠ Without it, below the surface is black.
    /// </summary>
    /// <remarks>
    ///     ⚠ The same number <see cref="WaterAsset.SkyColour" /> defaults to, for the reason
    ///     <see cref="Scattering" />'s remarks give from the other side.
    /// </remarks>
    public Vector3 SkyColour { get; init; } = new(1400f, 1680f, 2200f);

    /// <summary>§ D8's scale on what shows through.</summary>
    public Vector3 BehindScale { get; init; } = Vector3.One;

    /// <summary>Whether what is seen is refracted at all.</summary>
    public bool Distortion { get; init; } = true;

    /// <summary>How far the wobble displaces what is seen, in UV.</summary>
    public float DistortionAmount { get; init; } = 0.004f;

    /// <summary>How many wobbles across the screen.</summary>
    public float DistortionScale { get; init; } = 12f;

    /// <summary>How fast they travel.</summary>
    public float DistortionSpeed { get; init; } = 0.6f;

    /// <summary>Whether the moving caustic bands are added.</summary>
    public bool Caustics { get; init; } = true;

    /// <summary>How bright they are.</summary>
    public float CausticAmount { get; init; } = 0.12f;

    /// <summary>How large a caustic cell is.</summary>
    public float CausticScale { get; init; } = 1.5f;

    /// <summary>How fast the pattern drifts.</summary>
    public float CausticSpeed { get; init; } = 0.35f;

    /// <summary>How deep they fade out over, in metres.</summary>
    public float CausticDepth { get; init; } = 12f;
}

/// <summary>Builds this assembly's node kinds for a document that names them.</summary>
/// <remarks>
///     Registered on <c>CompositorBuilder.Factories</c>, which is asked <em>after</em> the built-ins —
///     so a factory cannot quietly replace a node kind the document's schema already defines.
/// </remarks>
public sealed class WaterRendererFactory : ISceneRendererFactory {
    /// <inheritdoc />
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        return declared switch {
            WaterAsset water => Water(water, builder),
            WaterSurfaceAsset mesh => Mesh(mesh, builder),
            UnderwaterAsset under => Underwater(under, builder),
            _ => null
        };
    }

    /// <summary>Where the zones the surface node draws come from.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A factory-level property rather than something a document names, because a system
    ///         is not a document's to reference.</b> A <c>.vxcompositor</c> describes a frame; which
    ///         world is running is the host's business, and <c>AppGraphics</c> is what has both in its
    ///         hands. Left unset, a <c>!WaterSurface</c> node builds and draws nothing — a frame with
    ///         no water rather than an exception, on <c>!ScreenProbeGather</c>'s terms.
    ///     </para>
    /// </remarks>
    public WaterZoneSystem? Zones { get; set; }

    UnderwaterRenderer Underwater(UnderwaterAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Output = declared.Output,
            Behind = declared.Behind,
            SceneDepth = declared.SceneDepth,
            Surface = declared.Surface,
            View = declared.View is { Length: > 0 } name ? builder.Views.GetValueOrDefault(name) : null,

            // ⚠ The same system the surface node draws from, so the waterline is solved against the
            // field a boat floats on and the water is drawn from — § D2 applied to a third consumer.
            Zones = Zones,
            WaterlineFeather = declared.WaterlineFeather,
            Scattering = declared.Scattering,
            Absorption = declared.Absorption,
            PhaseG = declared.PhaseG,
            SkyColour = declared.SkyColour,

            // The same environment !Water reads, so the sky is one number either side of the
            // waterline — see WaterRenderer.Frame.
            Frame = builder.SceneConstants,
            BehindScale = declared.BehindScale,
            Distortion = declared.Distortion,
            DistortionAmount = declared.DistortionAmount,
            DistortionScale = declared.DistortionScale,
            DistortionSpeed = declared.DistortionSpeed,
            Caustics = declared.Caustics,
            CausticAmount = declared.CausticAmount,
            CausticScale = declared.CausticScale,
            CausticSpeed = declared.CausticSpeed,
            CausticDepth = declared.CausticDepth,
            Modules = builder.Modules,
            Samplers = builder.Samplers,
            Allocator = builder.Descriptors,
            Device = builder.Device
        };

    WaterMeshRenderer Mesh(WaterSurfaceAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Surface = declared.Surface,
            Normal = declared.Normal,
            SceneDepth = declared.SceneDepth,
            View = declared.View is { Length: > 0 } name ? builder.Views.GetValueOrDefault(name) : null,
            Zones = Zones,
            GridQuads = declared.GridQuads,
            Ranges = TerrainLodRanges.Default with {
                NearRange = declared.NearRange,
                LevelCount = declared.LevelCount
            },
            FarDistance = declared.FarDistance,
            EdgeFade = declared.EdgeFade,
            Settings = WaterMeshSettings.Default with {
                RestHeight = declared.RestHeight,
                ShoreDepth = declared.ShoreDepth,
                FoamDepth = declared.FoamDepth,
                FoamCrest = declared.FoamCrest
            },
            Modules = builder.Modules,
            Samplers = builder.Samplers,
            Device = builder.Device
        };

    static WaterRenderer Water(WaterAsset declared, CompositorBuilder builder) =>
        new() {
            Tiled = declared.Tiled,

            // Its own, like every other compute node a document places — see CompositorBuilder's
            // !Compute. A cache is keyed by effect and costs a dictionary; sharing one would be a
            // lifetime shared between nodes that are disposed separately.
            Pipelines = builder.Device is { } device ? new(device) : null,
            Name = declared.Name,
            Enabled = declared.Enabled,
            Output = declared.Output,
            Behind = declared.Behind,
            SceneDepth = declared.SceneDepth,
            Surface = declared.Surface,
            Normal = declared.Normal,
            Reflections = declared.Reflections,
            View = declared.View is { Length: > 0 } name ? builder.Views.GetValueOrDefault(name) : null,
            Scattering = declared.Scattering,
            Absorption = declared.Absorption,
            PhaseG = declared.PhaseG,
            BehindScale = declared.BehindScale,
            SunColour = declared.SunColour,
            SunDirection = declared.SunDirection,
            SkyColour = declared.SkyColour,

            // ⚠ The frame's own sun and sky, which is what makes a !Water node in a plain document
            // correct — the three above are photometric quantities of the scene and a document can
            // only write a tint. Task #119 fixed this by having one sample call LightFrom every
            // frame; every other host omitted it, and the fix was in the tree while the lake stayed
            // black. !Fog and !VolumetricFog take the same two from the same builder.
            Sun = builder.Sun,
            Frame = builder.SceneConstants,
            SurfaceF0 = declared.SurfaceF0,
            FoamColour = declared.FoamColour,
            Foam = declared.Foam,
            Modules = builder.Modules,
            Samplers = builder.Samplers,
            Allocator = builder.Descriptors,
            Device = builder.Device
        };
}
