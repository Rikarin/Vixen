// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Null;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>An entity whose mesh has clusters is drawn by the traversal, not by the vertex buffer.</summary>
/// <remarks>
///     <para>
///         <b>The route the whole virtualized system was missing.</b> Every phase of it was finished from
///         import to shaded pixel, and nothing looked at whether a model had a cluster hierarchy — so
///         <c>MeshRenderable</c> always went to <see cref="MeshRenderFeature" /> and the traversal was
///         reachable from code and not from a scene.
///     </para>
///     <para>
///         ⚠ <b>Why it could have stayed broken indefinitely.</b> A virtualized model also has a fallback
///         mesh, and it draws a correct picture of the same object — so a scene routed entirely the wrong
///         way looks right. What says otherwise is which feature owns the render object, which is what
///         these assert.
///     </para>
/// </remarks>
public sealed class VirtualGeometryExtractionTests : IDisposable {
    readonly NullDevice device = new(new());
    readonly GeometryBuffer buffer;
    readonly GeometryResidency residency;
    readonly RenderSystem system = new();
    readonly MeshRenderFeature meshes = new();
    readonly TransformRenderFeature transforms = new();
    readonly MaterialRenderFeature materials = new();
    readonly VirtualGeometryRenderFeature virtualized = new();
    readonly MeshExtractionSystem extraction;

    static readonly AssetReference Rock = new(new AssetId(new("77777777-7777-7777-7777-777777777777")));

    public VirtualGeometryExtractionTests() {
        buffer = new(device, SurfaceVertex.SizeInBytes, vertexCapacity: 4096, indexCapacity: 8192);
        residency = new(buffer);

        var opaque = system.AddStage(new("Opaque"));

        meshes.Add(transforms);
        meshes.Add(materials);

        system.AddFeature(meshes);
        system.AddFeature(virtualized);

        extraction = new(system, meshes, transforms, materials, residency) { Stages = opaque.Mask };
    }

    /// <summary>A clustered mesh becomes a virtualized object, with the draw the traversal reads.</summary>
    /// <remarks>
    ///     The draw record is asserted and not only the feature index, because a render object pointed at
    ///     the right feature with <c>Mesh = 0</c> from a zeroed array would look extracted and would draw
    ///     whatever mesh happened to be registered first.
    /// </remarks>
    [Fact]
    public void AClusteredMeshIsDrawnByTheTraversal() {
        using var world = new World();

        extraction.Virtualized = virtualized;
        extraction.Clusters = new OneCluster(mesh: 4, radius: 2f);
        extraction.Meshes = new EveryMesh();

        var entity = Placed(world, Matrix4x4.FromTranslation(new(10f, 0f, 0f)));

        extraction.Extract(world);

        Assert.Equal(1, extraction.VirtualizedCount);
        Assert.Equal(1, extraction.ObjectCount);

        var id = world.Read<RenderHandle>(entity).Object;

        Assert.Equal(virtualized.Index, system.Objects[id].FeatureIndex);

        var draw = system.Objects.Data.Data(virtualized.Draws)[id.Index];

        Assert.Equal(4, draw.Mesh);
        Assert.True(draw.IsDrawable);
        Assert.Equal(10f, draw.Position.X, 4);

        // The bound was transformed and the scale derived from what it became, so a scaled instance
        // refines at the right distance rather than at its bind-pose one.
        Assert.Equal(1f, draw.Scale, 4);

        // And the ordinary path holds nothing: no residency claim, no mesh draw.
        Assert.Equal(0, residency.Count);
    }

    /// <summary>A scaled instance carries the factor its error and bound are multiplied by.</summary>
    [Fact]
    public void AScaledInstanceCarriesItsScale() {
        using var world = new World();

        extraction.Virtualized = virtualized;
        extraction.Clusters = new OneCluster(mesh: 0, radius: 2f);

        var entity = Placed(world, Matrix4x4.FromScale(new(3f, 3f, 3f)));

        extraction.Extract(world);

        var id = world.Read<RenderHandle>(entity).Object;

        Assert.Equal(3f, system.Objects.Data.Data(virtualized.Draws)[id.Index].Scale, 4);
    }

    /// <summary>A mesh with no clusters falls straight through to the ordinary path.</summary>
    /// <remarks>
    ///     ⚠ Not a wait. "This mesh has no hierarchy" and "it has one and it is not here" are opposite
    ///     decisions, and folding them together would stall every unclustered mesh in the level for ever.
    /// </remarks>
    [Fact]
    public void AnUnclusteredMeshTakesTheOrdinaryPath() {
        using var world = new World();

        extraction.Virtualized = virtualized;
        extraction.Clusters = new NoClusters();
        extraction.Meshes = new EveryMesh();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        Assert.Equal(0, extraction.VirtualizedCount);
        Assert.Equal(0, extraction.Waiting);
        Assert.Equal(1, extraction.ObjectCount);
        Assert.Equal(meshes.Index, system.Objects[world.Read<RenderHandle>(entity).Object].FeatureIndex);
    }

    /// <summary>
    ///     A hierarchy that has not arrived is waited for, and never drawn through its fallback instead.
    /// </summary>
    /// <remarks>
    ///     <b>The assertion the whole three-valued answer exists for.</b> The ordinary source here answers
    ///     everything, so an extraction that treated "not yet" as "not mine" would draw the fallback mesh
    ///     immediately — a correct picture of the same object, arrived at by the wrong path, and permanent
    ///     because a settled entity is never re-extracted.
    /// </remarks>
    [Fact]
    public void AHierarchyThatHasNotArrivedIsNotDrawnThroughItsFallback() {
        using var world = new World();

        var clusters = new DeferredCluster();

        extraction.Virtualized = virtualized;
        extraction.Clusters = clusters;
        extraction.Meshes = new EveryMesh();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        Assert.Equal(1, extraction.Waiting);
        Assert.Equal(0, extraction.ObjectCount);
        Assert.Equal(0, residency.Count);

        clusters.Deliver();
        extraction.Extract(world);

        Assert.Equal(1, extraction.VirtualizedCount);
        Assert.Equal(virtualized.Index, system.Objects[world.Read<RenderHandle>(entity).Object].FeatureIndex);
    }

    /// <summary>With no source, a clustered mesh draws through its fallback and nothing is lost.</summary>
    /// <remarks>
    ///     What a project that has not set up the virtualized stack is, and the reason it needs saying:
    ///     nothing looks wrong. <c>VirtualizedCount</c> at zero in a scene of virtualized models is the
    ///     only thing that says the route is not live.
    /// </remarks>
    [Fact]
    public void WithNoSourceAClusteredMeshStillDraws() {
        using var world = new World();

        extraction.Meshes = new EveryMesh();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        Assert.Equal(0, extraction.VirtualizedCount);
        Assert.Equal(1, extraction.ObjectCount);
        Assert.Equal(meshes.Index, system.Objects[world.Read<RenderHandle>(entity).Object].FeatureIndex);
    }

    /// <summary>The material a virtualized entity wears reaches the resolve's dispatch list.</summary>
    /// <remarks>
    ///     ⚠ At index zero, and only the first — a cluster carries the material index its meshlet was
    ///     built with and nothing maps that index back to an asset, so a scene's virtualized geometry
    ///     resolves with one material. See <c>MeshExtractionSystem.Resolved</c>.
    /// </remarks>
    [Fact]
    public void TheMaterialOfAVirtualizedObjectReachesTheResolve() {
        using var world = new World();

        var painted = new Material("ForwardPlus");

        extraction.Virtualized = virtualized;
        extraction.Clusters = new OneCluster(mesh: 0, radius: 1f);
        extraction.Material = painted;

        Placed(world, Matrix4x4.Identity);
        extraction.Extract(world);

        Assert.Same(painted, Assert.Single(extraction.ResolveMaterials).Material);
        Assert.Equal(0, extraction.ResolveMaterials[0].Index);
    }

    static Entity Placed(World world, Matrix4x4 matrix) {
        var entity = world.Create();

        world.Add(entity, new WorldTransform { Value = matrix });
        world.Add(entity, MeshRenderables.Default(Rock));

        return entity;
    }

    /// <summary>A source with one registered hierarchy, for every reference asked.</summary>
    sealed class OneCluster(int mesh, float radius) : IVirtualGeometrySource {
        public ClusterState TryGet(AssetReference reference, out int index, out BoundingSphere bounds) {
            index = mesh;
            bounds = new(Vector3.Zero, radius);

            return ClusterState.Ready;
        }
    }

    /// <summary>A source that says no mesh has clusters.</summary>
    sealed class NoClusters : IVirtualGeometrySource {
        public ClusterState TryGet(AssetReference reference, out int index, out BoundingSphere bounds) {
            index = -1;
            bounds = default;

            return ClusterState.None;
        }
    }

    /// <summary>A source that says "mine, not yet" until it is told to answer.</summary>
    sealed class DeferredCluster : IVirtualGeometrySource {
        bool ready;

        public void Deliver() => ready = true;

        public ClusterState TryGet(AssetReference reference, out int index, out BoundingSphere bounds) {
            index = ready ? 0 : -1;
            bounds = new(Vector3.Zero, 1f);

            return ready ? ClusterState.Ready : ClusterState.Waiting;
        }
    }

    /// <summary>A mesh source that answers every reference, so the fallback path is always available.</summary>
    sealed class EveryMesh : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = new() {
                Name = "fallback",
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
                Indices = [0, 1, 2]
            };

            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        extraction.Clear();
        buffer.Dispose();
        system.Dispose();
        device.Dispose();
    }
}
