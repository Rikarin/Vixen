// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Engine.Frames;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Vixen.Shaders;

namespace Vixen.Engine.Renderer;

/// <summary>
///     Everything between a world and a drawn frame, assembled.
/// </summary>
/// <remarks>
///     <para>
///         <b>The join an application was missing.</b> The scene half was finished — components,
///         extraction systems, a residency cache — and so was the frame half, and nothing put them
///         together: a game had to construct four features, a geometry buffer, a residency, two
///         extraction systems and a host, in an order, and get every reference between them right. The
///         samples opened a device and issued draws instead, which is why none of them is a game.
///     </para>
///     <para>
///         <b>What it is not is a policy.</b> It creates the standard features and the standard
///         extraction, and it decides nothing about the frame: the compositor comes from a document,
///         the stages come with it, and the content comes from wherever the application mounted it. A
///         project that wants a different set adds to <see cref="Host" /> and skips
///         <see cref="Register" />.
///     </para>
///     <para>
///         <b>The order it enforces is the one worth enforcing.</b> Extraction runs in
///         <c>SystemPhase.PreRender</c> after the transforms are written, so an object is culled against
///         where it is this frame rather than where it was last one; the compositor then collects the
///         views and runs the render system's own phases. Both of those are decisions somebody has
///         already made correctly, and this is where a second host stops being able to make them again
///         differently.
///     </para>
/// </remarks>
public sealed class WorldRenderer : IDisposable {
    bool disposed;

    /// <summary>Builds the standard renderer for a world.</summary>
    /// <param name="device">The device everything lives on.</param>
    /// <param name="effects">Where variants are compiled.</param>
    /// <param name="vertexCapacity">How many vertices the shared geometry buffer holds.</param>
    /// <param name="indexCapacity">How many indices it holds.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     The two capacities are the scene's geometry budget and are the caller's because they are a
    ///     project's decision — <see cref="MeshExtractionSystem.Dropped" /> is what says a level asked
    ///     for more than was reserved, which otherwise looks like meshes that stop appearing past a
    ///     certain number.
    /// </remarks>
    public WorldRenderer(
        IGraphicsDevice device,
        EffectSystem effects,
        int vertexCapacity = 1 << 20,
        int indexCapacity = 1 << 21
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(effects);

        Host = new(device, effects);

        // SurfaceVertex's own size rather than a number: it is what SurfaceGeometry.Packed produces and
        // what GeometryResidency puts in the buffer, so a stride written down here would be a second
        // opinion about a layout that already has one.
        Geometry = new(device, SurfaceVertex.SizeInBytes, vertexCapacity, indexCapacity, name: "Scene");
        Residency = new(Geometry);

        Materials = new() { Effects = effects };
        Transforms = new() { Device = device };
        Lighting = new() { Device = device };

        Meshes = new() {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        Meshes.Add(Materials);
        Meshes.Add(Transforms);
        Meshes.Add(Lighting);

        Host.System.AddFeature(Meshes);
    }

    /// <summary>The frame: a compositor, a graph and the render system.</summary>
    public SceneRenderHost Host { get; }

    /// <summary>The shared vertex and index memory every scene mesh is suballocated from.</summary>
    public GeometryBuffer Geometry { get; }

    /// <summary>What decides which meshes are in it, and shares one slice between every user.</summary>
    public GeometryResidency Residency { get; }

    /// <summary>The feature that draws them.</summary>
    public MeshRenderFeature Meshes { get; }

    /// <summary>The materials they are drawn with.</summary>
    public MaterialRenderFeature Materials { get; }

    /// <summary>Their world matrices.</summary>
    public TransformRenderFeature Transforms { get; }

    /// <summary>The lights that reach them.</summary>
    public ForwardLightingRenderFeature Lighting { get; }

    /// <summary>Where the geometry a mesh reference names comes from.</summary>
    /// <remarks>
    ///     Null until <see cref="Mount" /> or a caller sets one. A world whose entities carry mesh
    ///     references and whose renderer has no source draws none of them and counts them in
    ///     <see cref="MeshExtractionSystem.Waiting" />.
    /// </remarks>
    public IMeshSource? Source { get; set; }

    /// <summary>The extraction the last <see cref="Register" /> added, or null.</summary>
    public MeshExtractionSystem? Extraction { get; private set; }

    /// <summary>Points the renderer at a content manager, so mesh references resolve.</summary>
    /// <param name="assets">Where the meshes come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public void Mount(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);
        ObjectDisposedException.ThrowIf(disposed, this);

        Source = new AssetMeshSource(assets);

        if (Extraction is { } extraction) {
            extraction.Meshes = Source;
        }
    }

    /// <summary>
    ///     Adds the extraction systems to a loop, so the world's entities reach the render system.
    /// </summary>
    /// <param name="loop">The loop that runs them.</param>
    /// <param name="stages">Which stages the extracted objects are drawn in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    /// <remarks>
    ///     The stage mask is the caller's because a stage's index is assigned by the render system when
    ///     the compositor's document declares it — so this is called after
    ///     <see cref="SceneRenderHost.Load" />, and a mask of none draws nothing at all.
    /// </remarks>
    public void Register(EngineLoop loop, RenderStageMask stages) {
        ArgumentNullException.ThrowIfNull(loop);
        ObjectDisposedException.ThrowIf(disposed, this);

        Extraction = new(Host.System, Meshes, Transforms, Materials, Residency) {
            Stages = stages,
            Meshes = Source
        };

        loop.Add(Extraction);
        loop.Add(new LightExtractionSystem(Lighting));
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Host.Dispose();
        Geometry.Dispose();
    }
}
