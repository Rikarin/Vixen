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

/// <summary>Drawable entities becoming render objects, and the geometry they share.</summary>
/// <remarks>
///     Over a real <see cref="GeometryBuffer" /> on the null device, so the suballocation, the staging and
///     the draw arithmetic are the ones a frame runs rather than a stub of them.
/// </remarks>
public sealed class MeshExtractionTests : IDisposable {
    readonly NullDevice device = new(new());
    readonly GeometryBuffer buffer;
    readonly GeometryResidency residency;
    readonly RenderSystem system = new();
    readonly MeshRenderFeature meshes = new();
    readonly TransformRenderFeature transforms = new();
    readonly MaterialRenderFeature materials = new();
    readonly MeshExtractionSystem extraction;
    readonly RenderStage opaque;

    public MeshExtractionTests() {
        buffer = new(device, SurfaceVertex.SizeInBytes, vertexCapacity: 4096, indexCapacity: 8192);
        residency = new(buffer);

        opaque = system.AddStage(new("Opaque"));

        meshes.Add(transforms);
        meshes.Add(materials);
        system.AddFeature(meshes);

        extraction = new(system, meshes, transforms, materials, residency) { Stages = opaque.Mask };
    }

    public void Dispose() {
        extraction.Clear();
        buffer.Dispose();
        system.Dispose();
        device.Dispose();
    }

    // ------------------------------------------------------------------ residency

    [Fact]
    public void OneMeshDrawnByTwoEntitiesIsUploadedOnce() {
        var builds = 0;
        var key = GeometryKey.Of(PrimitiveKind.Cube);

        MeshData Build() {
            builds++;
            return MeshPrimitives.Create(PrimitiveKind.Cube);
        }

        Assert.True(residency.Acquire(key, Build, out var first, out _));
        Assert.True(residency.Acquire(key, Build, out var second, out _));

        Assert.Equal(1, builds);
        Assert.Equal(first, second);
        Assert.Equal(1, residency.Count);
        Assert.Equal(2, residency.ClaimsOn(key));
    }

    /// <remarks>
    ///     ⚠ Freeing on the first release would drop the crate the other thirty-nine entities are still
    ///     drawing.
    /// </remarks>
    [Fact]
    public void AMeshIsFreedOnlyWhenTheLastEntityGivesItUp() {
        var key = GeometryKey.Of(PrimitiveKind.Sphere);

        residency.Acquire(key, () => MeshPrimitives.Create(PrimitiveKind.Sphere), out _, out _);
        residency.Acquire(key, () => MeshPrimitives.Create(PrimitiveKind.Sphere), out _, out _);

        Assert.False(residency.Release(key));
        Assert.Equal(1, residency.Count);

        Assert.True(residency.Release(key));
        Assert.Equal(0, residency.Count);
        Assert.False(residency.TryGet(key, out _));
    }

    [Fact]
    public void ReleasingSomethingNeverHeldIsHarmless() =>
        Assert.False(residency.Release(GeometryKey.Of(PrimitiveKind.Torus)));

    [Fact]
    public void APrimitiveAndAnAssetAreDifferentKeys() {
        var asset = new AssetReference(new AssetId(new("11111111-1111-1111-1111-111111111111")));

        Assert.NotEqual(GeometryKey.Of(asset), GeometryKey.Of(PrimitiveKind.Cube));
        Assert.Equal(GeometryKey.Of(PrimitiveKind.Cube), GeometryKey.Of(PrimitiveKind.Cube));
    }

    /// <remarks>
    ///     Refused rather than growing the buffer, because growing means recreating one the GPU may still
    ///     be reading from.
    /// </remarks>
    [Fact]
    public void AMeshTooLargeForTheBufferIsRefused() {
        using var small = new GeometryBuffer(device, SurfaceVertex.SizeInBytes, 8, 8);
        var tiny = new GeometryResidency(small);

        Assert.False(
            tiny.Acquire(
                GeometryKey.Of(PrimitiveKind.Sphere),
                () => MeshPrimitives.Create(PrimitiveKind.Sphere),
                out _,
                out _
            )
        );

        Assert.Equal(0, tiny.Count);
    }

    // ------------------------------------------------------------------ extraction

    [Fact]
    public void APrimitiveEntityBecomesADrawableRenderObject() {
        using var world = new World();
        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);

        Assert.Equal(1, extraction.ObjectCount);
        Assert.Equal(0, extraction.Dropped);
        Assert.True(world.Has<RenderHandle>(entity));

        var id = world.Read<RenderHandle>(entity).Object;
        Assert.True(id.IsValid);

        var draw = system.Objects.Data.Data(meshes.Draws)[id.Index];
        Assert.True(draw.IsDrawable);
        Assert.True(draw.IsIndexed);
        Assert.Equal(1, draw.InstanceCount);
        Assert.Equal(opaque.Mask, system.Objects[id].Stages);
        Assert.Equal(meshes.Index, system.Objects[id].FeatureIndex);
    }

    [Fact]
    public void TwoEntitiesWithTheSameShapeShareOneSlice() {
        using var world = new World();
        Shaped(world, PrimitiveKind.Cube, Vector3.Zero);
        Shaped(world, PrimitiveKind.Cube, Vector3.UnitX);

        extraction.Extract(world);

        Assert.Equal(2, extraction.ObjectCount);
        Assert.Equal(1, residency.Count);
        Assert.Equal(2, residency.ClaimsOn(GeometryKey.Of(PrimitiveKind.Cube)));
        Assert.Equal(2, system.Objects.LiveCount);
    }

    [Fact]
    public void ExtractingTwiceDoesNotExtractTheSameEntityAgain() {
        using var world = new World();
        Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);
        extraction.Extract(world);

        Assert.Equal(1, extraction.ObjectCount);
        Assert.Equal(1, system.Objects.LiveCount);
        Assert.Equal(1, residency.ClaimsOn(GeometryKey.Of(PrimitiveKind.Cube)));
    }

    /// <remarks>
    ///     The transform feature's array is what the shader reads the world matrix from, so a moved entity
    ///     that did not reach it is one drawn where it used to be.
    /// </remarks>
    [Fact]
    public void TheWorldMatrixReachesTheTransformFeature() {
        using var world = new World();
        var entity = Shaped(world, PrimitiveKind.Cube, new Vector3(3f, 0f, 0f));

        extraction.Extract(world);

        var id = world.Read<RenderHandle>(entity).Object;
        Assert.Equal(3f, system.Objects.Data.Data(transforms.World)[id.Index].Translation.X, 5);

        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromTranslation(new Vector3(-7f, 0f, 0f));
        extraction.Extract(world);

        Assert.Equal(-7f, system.Objects.Data.Data(transforms.World)[id.Index].Translation.X, 5);
    }

    [Fact]
    public void MovingAnEntityMovesWhatTheCullingLoopTests() {
        using var world = new World();
        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);
        var before = system.Objects[world.Read<RenderHandle>(entity).Object].Bounds.Center;

        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromTranslation(new Vector3(0f, 20f, 0f));
        extraction.Extract(world);

        var after = system.Objects[world.Read<RenderHandle>(entity).Object].Bounds.Center;

        Assert.Equal(Vector3.Zero, before);
        Assert.Equal(20f, after.Y, 5);
    }

    /// <remarks>
    ///     A scaled entity's bounds have to grow with it, or a scaled-up wall is culled while still on
    ///     screen.
    /// </remarks>
    [Fact]
    public void ScalingAnEntityGrowsItsBounds() {
        using var world = new World();
        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);
        var before = system.Objects[world.Read<RenderHandle>(entity).Object].Bounds.Radius;

        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromUniformScale(4f);
        extraction.Extract(world);

        var after = system.Objects[world.Read<RenderHandle>(entity).Object].Bounds.Radius;

        Assert.Equal(before * 4f, after, 4);
    }

    /// <remarks>
    ///     ⚠ The largest axis, not the average: under-estimating one axis culls an object that is still on
    ///     screen, and a disappearing wall is worse than one drawn a frame too long.
    /// </remarks>
    [Fact]
    public void ANonUniformScaleTakesTheLargestAxis() {
        var local = new BoundingSphere(Vector3.Zero, 1f);
        var matrix = Matrix4x4.FromScale(new Vector3(1f, 5f, 2f));

        Assert.Equal(5f, MeshExtractionSystem.Transformed(local, matrix).Radius, 4);
    }

    [Fact]
    public void RemovingTheComponentRetiresTheObjectAndTheClaim() {
        using var world = new World();
        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);
        world.Remove<PrimitiveShape>(entity);
        extraction.Extract(world);

        Assert.Equal(0, extraction.ObjectCount);
        Assert.False(world.Has<RenderHandle>(entity));
        Assert.Equal(0, residency.Count);
        Assert.Equal(0, system.Objects.LiveCount);
    }

    /// <remarks>
    ///     ⚠ <b>A destroyed entity cannot be found by a query</b>, so the object and the claim it held
    ///     outlive it unless somebody says. Without this a scene unload leaks a slice per entity.
    /// </remarks>
    [Fact]
    public void ForgettingADestroyedEntityReleasesWhatItHeld() {
        using var world = new World();
        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);
        world.Destroy(entity);

        Assert.True(extraction.Forget(entity));
        Assert.Equal(0, residency.Count);
        Assert.Equal(0, extraction.ObjectCount);

        Assert.False(extraction.Forget(entity));
    }

    [Fact]
    public void ClearingReleasesEverything() {
        using var world = new World();
        Shaped(world, PrimitiveKind.Cube, Vector3.Zero);
        Shaped(world, PrimitiveKind.Sphere, Vector3.UnitX);

        extraction.Extract(world);
        Assert.Equal(2, residency.Count);

        extraction.Clear();

        Assert.Equal(0, residency.Count);
        Assert.Equal(0, extraction.ObjectCount);
    }

    /// <remarks>
    ///     An entity with no world transform has not been through <c>TransformSystem</c> yet, and drawing
    ///     it at the origin puts it in the middle of the level for a frame.
    /// </remarks>
    [Fact]
    public void AnEntityWithNoWorldTransformIsNotExtracted() {
        using var world = new World();
        var entity = world.Create();
        PrimitiveShapes.Attach(world, entity, PrimitiveKind.Cube);

        extraction.Extract(world);

        Assert.Equal(0, extraction.ObjectCount);
    }

    /// <remarks>
    ///     <para>
    ///         A reference with nowhere to load from is <em>waited</em> for rather than drawn as
    ///         something wrong — which is the same answer a mesh that has simply not arrived yet gets,
    ///         and deliberately so: from here they are the same state, and the entity keeps no render
    ///         handle either way so the next reconciliation asks again.
    ///     </para>
    ///     <para>
    ///         Distinct from <c>Dropped</c>, which is geometry that arrived and did not fit. One is a
    ///         frame away from being drawn and the other never will be, and a level that stops appearing
    ///         past a certain size looks exactly like a level whose content is missing until the two
    ///         numbers are separate.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMeshReferenceWithNoSourceIsWaitedFor() {
        using var world = new World();
        var entity = world.Create();

        MeshRenderables.Attach(
            world,
            entity,
            MeshRenderables.Default(new AssetReference(new AssetId(new("22222222-2222-2222-2222-222222222222"))))
        );

        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });

        extraction.Extract(world);

        Assert.Equal(1, extraction.Waiting);
        Assert.Equal(0, extraction.Dropped);
        Assert.Equal(0, extraction.ObjectCount);
    }

    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An entity carrying both draws its mesh, and the shape is not a fallback.</b> Two
    ///         answers to "what does this draw" is a question with no good one, so the mesh wins and the
    ///         queries are written so that only one of them ever matches — the same rule
    ///         <c>AudioSystem</c> applies to an event beside a clip.
    ///     </para>
    ///     <para>
    ///         So an entity whose mesh has not arrived draws <i>nothing</i> rather than falling back to
    ///         the cube. That is deliberate: an entity that changed shape while its mesh loaded would be
    ///         a level that looks different depending on how fast the disk is.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnEntityCarryingBothTakesItsMeshAndNotItsShape() {
        using var world = new World();
        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);
        var reference = new AssetReference(new AssetId(new("33333333-3333-3333-3333-333333333333")));

        MeshRenderables.Attach(world, entity, MeshRenderables.Default(reference));

        // Nowhere to load from: the mesh is waited for and the cube it also carries is not drawn instead.
        extraction.Extract(world);

        Assert.Equal(0, system.Objects.LiveCount);
        Assert.Equal(1, extraction.Waiting);
        Assert.Equal(0, residency.ClaimsOn(GeometryKey.Of(PrimitiveKind.Cube)));

        // And when it does arrive it is the mesh that is drawn, still not the cube — which is the half
        // of the claim that could not be made while no reference ever resolved.
        extraction.Meshes = new OneMesh();
        extraction.Extract(world);

        Assert.Equal(1, system.Objects.LiveCount);
        Assert.Equal(1, residency.ClaimsOn(GeometryKey.Of(reference)));
        Assert.Equal(0, residency.ClaimsOn(GeometryKey.Of(PrimitiveKind.Cube)));
    }

    /// <summary>A source that answers every reference with the same triangle.</summary>
    sealed class OneMesh : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = new() {
                Name = "triangle",
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
                Indices = [0, 1, 2]
            };

            return true;
        }
    }

    static Entity Shaped(World world, PrimitiveKind kind, Vector3 position) {
        var entity = world.Create();

        PrimitiveShapes.Attach(world, entity, kind);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(position) });

        return entity;
    }
}
