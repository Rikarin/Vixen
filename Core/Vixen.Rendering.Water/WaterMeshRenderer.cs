// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Vixen.Terrain;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>
///     Draws every zone's surface into the two planes the water pass reads.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D4](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source),
///         and the node that makes a wet pixel possible at all.</b> Without it nothing writes
///         <c>WaterSurface</c> or <c>WaterNormal</c>, so § D8's <c>!Water</c> reads a cleared mask,
///         finds no coverage anywhere and passes the frame through unchanged — a water stack that is
///         wired, tested and invisible.
///     </para>
///     <para>
///         <b>It runs before <c>!Copy</c> and <c>!Water</c>, and after the opaque pass.</b> The order
///         is what the two planes are for: the surface has to be rasterised against the scene's own
///         depth so a lake behind a hill does not draw over it, and the composite has to have both
///         planes before it integrates anything.
///     </para>
///     <para>
///         ⚠ <b>One <see cref="WaterSurfacePass" /> per zone, not one per frame.</b> A zone owns a
///         window, a field, an info texture and a patch buffer; sharing a pass between two would mean
///         re-uploading both windows every time either moved. Zones that disappear from the fold have
///         their resources dropped at the next build, which is what keeps a level that streams a zone
///         out from leaking a megabyte of staging per visit.
///     </para>
/// </remarks>
public sealed class WaterMeshRenderer : SceneRenderer, IDisposable {
    readonly Dictionary<Entity, Zone> drawn = [];
    readonly List<Entity> stale = [];
    readonly GerstnerWave[] sea = new GerstnerWave[WaterSurfacePass.MaxWaves];

    WaterMeshShaders near;
    WaterMeshShaders far;
    bool disposed;

    /// <summary>The surface plane it writes: device depth in <c>r</c>, coverage in <c>g</c>.</summary>
    public required string Surface { get; init; }

    /// <summary>The normal plane: the surface's world normal in <c>xyz</c> and its foam in <c>a</c>.</summary>
    public required string Normal { get; init; }

    /// <summary>The opaque scene's depth, which the surface is tested against and never writes.</summary>
    public required string SceneDepth { get; init; }

    /// <summary>The view the patches are selected for.</summary>
    /// <remarks>
    ///     ⚠ <b>Without one nothing is drawn, rather than everything being drawn from the origin.</b>
    ///     A selection is a function of where the camera is; run against a default view it chooses the
    ///     finest level around the world origin and the far skirt everywhere else, which is a full
    ///     patch budget spent on water nobody is looking at.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>Where the zones and their fields come from.</summary>
    public WaterZoneSystem? Zones { get; set; }

    /// <summary>The simulation's water time, in seconds.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a frame time, and the host has to say so</b> —
    ///     [§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test). The
    ///     buoyancy solver reads the fixed step's water time; a vertex stage reading a smoothed frame
    ///     time is the drift that makes a boat hover, and it is invisible until the frame rate changes.
    /// </remarks>
    public float WaterTime { get; set; }

    /// <summary>How many quads the shared grid patch spans.</summary>
    /// <remarks>The terrain's, because the lattice is the terrain's — see <see cref="WaterSurfacePass" />.</remarks>
    public int GridQuads { get; set; } = PatchSelector.DefaultGridQuads;

    /// <summary>Where each level takes over, and where each one morphs.</summary>
    public TerrainLodRanges Ranges { get; set; } = TerrainLodRanges.Default;

    /// <summary>How far past a window the far skirt reaches, in metres.</summary>
    public float FarDistance { get; set; } = 8000f;

    /// <summary>How wide the band is over which the waves fade at a window's edge, in metres.</summary>
    public float EdgeFade { get; set; } = 32f;

    /// <summary>The shoreline, foam and rest-height numbers the shader's block carries.</summary>
    public WaterMeshSettings Settings { get; set; } = WaterMeshSettings.Default;

    /// <summary>Where shader modules come from. Set before the first frame that builds.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>Where samplers come from, shared so a frame does not exhaust the device's supply.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>The device its pipelines, buffers and textures are created on.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How many zones the last frame drew.</summary>
    public int ZonesDrawn { get; private set; }

    /// <summary>How many patches they selected between them.</summary>
    public int PatchesDrawn { get; private set; }

    /// <summary>And how many they had to drop. ⚠ Non-zero is a hole in the water.</summary>
    public int DroppedPatches { get; private set; }

    /// <summary>How many frames have declared the pass.</summary>
    public int BuildCount { get; private set; }

    /// <summary>What one zone needs on the device.</summary>
    sealed record Zone(
        WaterInfoTexture Info,
        WaterFieldPyramid Reduced,
        WaterSurfaceMesh Mesh,
        WaterSurfacePass Pass
    ) : IDisposable {
        public void Dispose() {
            Pass.Dispose();
            Info.Dispose();
        }
    }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);

        ZonesDrawn = 0;
        PatchesDrawn = 0;
        DroppedPatches = 0;

        if (Device is null || Samplers is null || Zones is null || View is not { } view) {
            // A document naming this node in a host that has not wired the renderer up should cost a
            // frame with no water, not an exception — the terms !ScreenProbeGather and !Water are
            // both built on.
            return;
        }

        if (!Resolve(frame)) {
            return;
        }

        var surface = frame.Texture(ToString(), Surface);
        var normal = frame.Texture(ToString(), Normal);
        var depth = frame.Texture(ToString(), SceneDepth);

        var formats = new[] { frame.FormatOf(ToString(), Surface), frame.FormatOf(ToString(), Normal) };
        var depthFormat = frame.FormatOf(ToString(), SceneDepth);

        Reap();

        var ready = new List<(Zone Zone, WaterZoneState State, WaterZoneComponent Component)>();

        foreach (var (entity, component) in Zones.Zones) {
            if (!Zones.States.TryGetValue(entity, out var state) || state.Field is null) {
                continue;
            }

            if (!drawn.TryGetValue(entity, out var zone)) {
                zone = Create(state, formats, depthFormat);
                drawn[entity] = zone;
            }

            ready.Add((zone, state, component));
        }

        if (ready.Count == 0) {
            BuildCount++;

            return;
        }

        var frustum = new BoundingFrustum(view.ViewProjection);
        var meshView = new WaterMeshView(view.Position, frustum, view.ViewProjection);

        // The uploads outside any render pass: buffer writes and the info texture's copy are both
        // what a render pass refuses. SideEffect because the graph cannot see these edges — a zone's
        // info texture is the node's own resource, not the graph's.
        frame.Graph.AddPass(
            $"{this}.Upload",
            pass => {
                pass.SideEffect();

                pass.Execute(
                    context => {
                        foreach (var (zone, state, component) in ready) {
                            Stage(zone, state, component, context.CommandList, meshView);
                        }
                    }
                );
            }
        );

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // ⚠ Cleared and not loaded, which is the frame's whole contract with § D8's pass: a
                // coverage of zero is "no water here", so a plane carrying last frame's mask would
                // integrate a lake the camera has walked away from.
                pass.ColourAttachment(surface, LoadAction.Clear, default);
                pass.ColourAttachment(normal, LoadAction.Clear, default);

                // Loaded, and never written — see WaterSurfacePass.DepthState. The surface joins a
                // frame that already has its opaque depth, and § D8 unprojects that same depth to
                // find what is behind the water.
                pass.DepthAttachment(depth, LoadAction.Load, 0f);

                pass.Execute(
                    context => {
                        foreach (var (zone, _, _) in ready) {
                            zone.Pass.Record(context.CommandList);

                            ZonesDrawn++;
                            PatchesDrawn += zone.Pass.NearPatches + zone.Pass.FarPatches;
                            DroppedPatches += zone.Pass.DroppedPatches;
                        }
                    }
                );
            }
        );

        BuildCount++;
    }

    /// <summary>Brings one zone's device resources into line with its field, and stages the draw.</summary>
    void Stage(
        Zone zone,
        WaterZoneState state,
        in WaterZoneComponent component,
        ICommandList commands,
        in WaterMeshView view
    ) {
        if (state.Field is not { } field) {
            return;
        }

        if (zone.Info.Stage(state)) {
            zone.Info.Record(commands);
        }

        // ⚠ Reduced every frame rather than only when the field changed, and that is the honest
        // shape rather than the cheap one: the reduction is what the descent prunes on, and a stale
        // pyramid selects patches for a shoreline that has moved. It is a pass over a texture the
        // field already walked, and the field is what costs — see WaterZoneState.RasterCount.
        zone.Reduced.Build(field);

        var spectrum = component.Waves;
        var count = spectrum.Validate() is null ? spectrum.Generate(sea) : 0;
        var amplitude = WaterWaveSpectrum.MaximumAmplitude(sea.AsSpan(0, count));

        zone.Mesh.FarDistance = FarDistance;
        zone.Mesh.EdgeFade = EdgeFade;
        zone.Mesh.Update(zone.Reduced, amplitude);

        var attenuation = component.AttenuationDepth > 0f
            ? component.AttenuationDepth
            : WaterAttenuation.Default.Depth;

        zone.Pass.Upload(
            zone.Mesh,
            in view,
            sea.AsSpan(0, count),
            zone.Info.View,
            Samplers!.LinearClamp,
            Settings with { WaterTime = WaterTime, AttenuationDepth = attenuation }
        );
    }

    Zone Create(WaterZoneState state, ReadOnlySpan<PixelFormat> formats, PixelFormat depthFormat) {
        var window = state.Window;

        return new(
            new(Device!, state.Zone),
            new(window.Resolution),
            new WaterSurfaceMesh(window, Ranges, GridQuads) { FarDistance = FarDistance, EdgeFade = EdgeFade },
            new(Device!, (near, far), formats, depthFormat, GridQuads)
        );
    }

    /// <summary>Drops the device resources of zones the fold no longer has.</summary>
    void Reap() {
        stale.Clear();

        foreach (var entity in drawn.Keys) {
            if (!Zones!.States.ContainsKey(entity)) {
                stale.Add(entity);
            }
        }

        foreach (var entity in stale) {
            drawn[entity].Dispose();
            drawn.Remove(entity);
        }
    }

    /// <summary>Resolves the two variants, once, through the frame's effect system.</summary>
    /// <remarks>
    ///     Both together rather than each on first need, because the answer gates one decision: a
    ///     frame whose window draws while its skirt cannot yet is an ocean that ends in a straight
    ///     line at the window's edge, which reads as a worse bug than both waiting for one compile.
    /// </remarks>
    bool Resolve(CompositorFrame frame) {
        if (near.IsValid && far.IsValid) {
            return true;
        }

        if (Modules is null) {
            return false;
        }

        var window = EffectKey.Of(
            WaterMeshKeys.ShaderName,
            [
                KeyValuePair.Create("GridQuads", GridQuads.ToString()),
                KeyValuePair.Create("FarMesh", "false")
            ]
        );

        var skirt = EffectKey.Of(
            WaterMeshKeys.ShaderName,
            [
                KeyValuePair.Create("GridQuads", GridQuads.ToString()),
                KeyValuePair.Create("FarMesh", "true")
            ]
        );

        if (frame.Effects.Resolve(window) is not { } windowEffect
            || frame.Effects.Resolve(skirt) is not { } skirtEffect) {
            return false;
        }

        near = new(
            Modules.ModuleOf(windowEffect, ShaderStage.Vertex),
            Modules.ModuleOf(windowEffect, ShaderStage.Fragment)
        );

        far = new(
            Modules.ModuleOf(skirtEffect, ShaderStage.Vertex),
            Modules.ModuleOf(skirtEffect, ShaderStage.Fragment)
        );

        return near.IsValid && far.IsValid;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var zone in drawn.Values) {
            zone.Dispose();
        }

        drawn.Clear();
    }
}
