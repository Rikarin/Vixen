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

    // ------------------------------------------------------------------ materials

    /// <summary>An entity is drawn with the material it names, not with the host's.</summary>
    /// <remarks>
    ///     <b>What "per-entity materials" means, and the assertion that would have been vacuous a day
    ///     ago.</b> Every drawable in a scene was assigned <see cref="MeshExtractionSystem.Material" />
    ///     — one material for everything — because a reference could not be turned into a material. The
    ///     two materials here are distinguished by their index in the feature, which is where a frame
    ///     reads "which material" from.
    /// </remarks>
    [Fact]
    public void AnEntityIsDrawnWithTheMaterialItNames() {
        using var world = new World();

        var painted = new Material("ForwardPlus");
        var reference = new AssetReference(new AssetId(new("44444444-4444-4444-4444-444444444444")));

        extraction.Material = new Material("ForwardPlus");
        extraction.Materials = new OneMaterial(reference, painted);

        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        world.Get<PrimitiveShape>(entity).Material = reference;

        extraction.Extract(world);

        var id = world.Read<RenderHandle>(entity).Object;
        var index = system.Objects.Data.Data(materials.MaterialIndex)[id.Index];

        Assert.Same(painted, materials.Materials[index]);
        Assert.NotSame(extraction.Material, materials.Materials[index]);
    }

    /// <summary>An entity that names no material is drawn with the host's.</summary>
    /// <remarks>
    ///     ⚠ Not a stopgap. A block-out mesh dropped into a level before anybody has made a material for
    ///     it has to draw in something neutral, and a null reference is how an author says so — which is
    ///     why <c>MeshRenderable.Material</c>'s own remarks call null a usable value.
    /// </remarks>
    [Fact]
    public void AnEntityThatNamesNoMaterialTakesTheDefault() {
        using var world = new World();

        extraction.Material = new Material("ForwardPlus");
        extraction.Materials = new OneMaterial(default, new Material("Unused"));

        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);

        var id = world.Read<RenderHandle>(entity).Object;
        var index = system.Objects.Data.Data(materials.MaterialIndex)[id.Index];

        Assert.Same(extraction.Material, materials.Materials[index]);
        Assert.Equal(0, extraction.Waiting);
    }

    /// <summary>
    ///     A material that has not arrived is waited for, and painted on the frame it does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The half that would rot silently. Drawing the entity now and repainting it later is not
    ///         available — a settled entity is never re-extracted — so an extraction that took the
    ///         default while a material loaded would give every object in a level the host's material
    ///         permanently, and on a fast disk it would not even be reproducible.
    ///     </para>
    ///     <para>
    ///         ⚠ Distinct from a material that <em>cannot</em> be supplied at all: with no source the
    ///         same entity draws immediately in the default, because a host with no content mounted
    ///         should show geometry rather than nothing. See
    ///         <see cref="AnEntityWithNoMaterialSourceIsStillDrawn" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AMaterialThatHasNotArrivedIsWaitedForAndThenPainted() {
        using var world = new World();

        var painted = new Material("ForwardPlus");
        var reference = new AssetReference(new AssetId(new("55555555-5555-5555-5555-555555555555")));
        var source = new DeferredMaterial(painted);

        extraction.Material = new Material("ForwardPlus");
        extraction.Materials = source;

        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        world.Get<PrimitiveShape>(entity).Material = reference;

        extraction.Extract(world);

        Assert.Equal(1, extraction.Waiting);
        Assert.Equal(0, extraction.ObjectCount);

        source.Deliver();
        extraction.Extract(world);

        Assert.Equal(0, extraction.Waiting);

        var id = world.Read<RenderHandle>(entity).Object;

        Assert.Same(painted, materials.Materials[system.Objects.Data.Data(materials.MaterialIndex)[id.Index]]);
    }

    /// <summary>With no source at all, a named material is the default rather than a wait.</summary>
    /// <inheritdoc cref="AMaterialThatHasNotArrivedIsWaitedForAndThenPainted" path="/remarks/para[2]" />
    [Fact]
    public void AnEntityWithNoMaterialSourceIsStillDrawn() {
        using var world = new World();

        extraction.Material = new Material("ForwardPlus");

        var entity = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        world.Get<PrimitiveShape>(entity).Material =
            new(new AssetId(new("66666666-6666-6666-6666-666666666666")));

        extraction.Extract(world);

        Assert.Equal(0, extraction.Waiting);
        Assert.Equal(1, extraction.ObjectCount);
    }

    /// <summary>A source that answers one reference and nothing else.</summary>
    sealed class OneMaterial(AssetReference named, Material material) : IMaterialSource {
        public bool TryGet(AssetReference reference, out Material found) {
            found = material;
            return reference == named;
        }
    }

    /// <summary>A source that answers "not yet" until it is told to answer.</summary>
    sealed class DeferredMaterial(Material material) : IMaterialSource {
        bool ready;

        public void Deliver() => ready = true;

        public bool TryGet(AssetReference reference, out Material found) {
            found = ready ? material : null!;
            return ready;
        }
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

    // ------------------------------------------------------ the static caster split

    /// <summary>
    ///     An entity claiming to be static is stamped with the other mask, and its neighbours are not.
    /// </summary>
    /// <remarks>
    ///     The whole mechanism behind <c>ShadowMapRenderer.StaticCasterStage</c>: that node caches one
    ///     stage and redraws another, and the only thing it needs from the world is the level's casters
    ///     in one and the movers in the other. A stage is exactly that split, so no filtering machinery
    ///     was needed — only a way for an entity to say which side it is on.
    /// </remarks>
    [Fact]
    public void AStaticShadowCasterIsStampedWithTheStaticMask() {
        var still = system.AddStage(new("ShadowStatic"));

        extraction.StaticStages = still.Mask;

        using var world = new World();
        var level = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);
        var mover = Shaped(world, PrimitiveKind.Cube, Vector3.UnitX);

        world.Add<StaticShadowCaster>(level);
        extraction.Extract(world);

        Assert.Equal(still.Mask, system.Objects[world.Read<RenderHandle>(level).Object].Stages);
        Assert.Equal(opaque.Mask, system.Objects[world.Read<RenderHandle>(mover).Object].Stages);
    }

    /// <summary>
    ///     With no static mask the claim is ignored, rather than obeyed with a mask of none.
    /// </summary>
    /// <remarks>
    ///     ⚠ The difference between the two is a level that casts no shadows at all. A project that has
    ///     not opted into a cached sun draws one caster stage with everything in it, and a scene of
    ///     somebody else's that happens to carry the tag must not silently lose its geometry from it.
    /// </remarks>
    [Fact]
    public void WithNoStaticMaskTheClaimIsIgnored() {
        using var world = new World();
        var level = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        world.Add<StaticShadowCaster>(level);
        extraction.Extract(world);

        Assert.Equal(opaque.Mask, system.Objects[world.Read<RenderHandle>(level).Object].Stages);
    }

    // ------------------------------------------------------ the shadow-casting flag

    /// <summary>Puts a mesh entity in the world, casting or not.</summary>
    static Entity Meshed(World world, bool casts, Vector3 position) {
        var entity = world.Create();
        var reference = new AssetReference(new AssetId(Guid.NewGuid()));

        world.Add(entity, MeshRenderables.Default(reference) with { CastsShadows = casts });
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(position) });

        return entity;
    }

    /// <summary>The flag decides whether the object is in the caster stage at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that was impossible until <c>CasterStages</c> existed.</b> A render object
    ///     carries a stage mask and nothing else a shadow pass consults, so an entity saying it casts no
    ///     shadow could only ever be honoured by leaving those bits out — and nothing told extraction
    ///     which bits those were. The flag round-tripped through the inspector and a saved scene the
    ///     whole time, and the object cast a shadow regardless.
    /// </remarks>
    [Fact]
    public void ADrawableThatCastsNoShadowIsLeftOutOfTheCasterStage() {
        var shadow = system.AddStage(new("Shadow"));

        extraction.Meshes = new OneMesh();
        extraction.Stages = opaque.Mask | shadow.Mask;
        extraction.CasterStages = shadow.Mask;

        using var world = new World();
        var casting = Meshed(world, casts: true, Vector3.Zero);
        var quiet = Meshed(world, casts: false, Vector3.UnitX);

        extraction.Extract(world);

        Assert.Equal(opaque.Mask | shadow.Mask, system.Objects[world.Read<RenderHandle>(casting).Object].Stages);
        Assert.Equal(opaque.Mask, system.Objects[world.Read<RenderHandle>(quiet).Object].Stages);
    }

    /// <summary>A static caster that casts no shadow is in neither caster stage.</summary>
    /// <remarks>
    ///     ⚠ <b>The interaction, and the one that is easy to get wrong in the direction that hides
    ///     itself.</b> The two questions are independent — the tag chooses <em>which</em> caster stages,
    ///     the flag chooses whether any — so the flag has to be applied to whichever set the tag picked.
    ///     Testing it against the movers' mask alone would leave this wall in the *cached* atlas, where
    ///     its shadow then survives every frame that does not bump <c>StaticVersion</c>: a shadow with
    ///     no object, permanently, from a checkbox that says it should not be there.
    /// </remarks>
    [Fact]
    public void AStaticCasterThatCastsNoShadowIsInNeitherCasterStage() {
        var shadow = system.AddStage(new("Shadow"));
        var still = system.AddStage(new("ShadowStatic"));

        extraction.Meshes = new OneMesh();
        extraction.Stages = opaque.Mask | shadow.Mask;
        extraction.StaticStages = opaque.Mask | still.Mask;
        extraction.CasterStages = shadow.Mask | still.Mask;

        using var world = new World();
        var wall = Meshed(world, casts: false, Vector3.Zero);
        var pillar = Meshed(world, casts: true, Vector3.UnitX);

        world.Add<StaticShadowCaster>(wall);
        world.Add<StaticShadowCaster>(pillar);
        extraction.Extract(world);

        Assert.Equal(opaque.Mask, system.Objects[world.Read<RenderHandle>(wall).Object].Stages);
        Assert.Equal(opaque.Mask | still.Mask, system.Objects[world.Read<RenderHandle>(pillar).Object].Stages);
    }

    /// <summary>With no caster mask the flag is ignored, rather than obeyed with a mask of none.</summary>
    /// <remarks>
    ///     ⚠ <b>The difference between the two is every shadow in the scene.</b> A zeroed
    ///     <c>MeshRenderable</c> has the flag clear and a scene file that omits the field deserialises to
    ///     exactly that, so a host that has not named its caster stages must go on drawing what it drew
    ///     yesterday — the opt-in is what keeps the fix from being a silent regression everywhere.
    /// </remarks>
    [Fact]
    public void WithNoCasterMaskTheFlagIsIgnored() {
        var shadow = system.AddStage(new("Shadow"));

        extraction.Meshes = new OneMesh();
        extraction.Stages = opaque.Mask | shadow.Mask;

        using var world = new World();
        var quiet = Meshed(world, casts: false, Vector3.Zero);

        extraction.Extract(world);

        Assert.Equal(opaque.Mask | shadow.Mask, system.Objects[world.Read<RenderHandle>(quiet).Object].Stages);
    }

    /// <summary>A primitive has no flag and is drawn into the caster stages regardless.</summary>
    /// <remarks>
    ///     <c>PrimitiveShape</c> carries no such field, and giving it one would change a component's
    ///     layout — which every saved scene is a copy of. Asserted rather than left implied, because
    ///     "the flag is honoured" and "primitives are exempt" are two claims and only one of them is
    ///     obvious from the code.
    /// </remarks>
    [Fact]
    public void APrimitiveIsAlwaysACaster() {
        var shadow = system.AddStage(new("Shadow"));

        extraction.Stages = opaque.Mask | shadow.Mask;
        extraction.CasterStages = shadow.Mask;

        using var world = new World();
        var cube = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);

        Assert.Equal(opaque.Mask | shadow.Mask, system.Objects[world.Read<RenderHandle>(cube).Object].Stages);
    }

    // ------------------------------------------------------ re-stamping a settled entity

    /// <summary>A flag toggled after extraction takes effect once the entity is unsettled.</summary>
    /// <remarks>
    ///     <b>Both halves of the contract, in one test, because each is meaningless without the
    ///     other.</b> The mask is stamped when the object is created and a settled entity is never
    ///     re-extracted, so the first assertion is the documented limitation rather than a bug; the
    ///     second is the verb that answers it. Asserting only the second would let the flag quietly
    ///     become live and nobody would notice — and live is the wrong answer here, because a static
    ///     caster's shadow is already in a cache that a re-stamp does not redraw.
    /// </remarks>
    [Fact]
    public void AToggledFlagReachesTheFrameOnlyAfterResettle() {
        var shadow = system.AddStage(new("Shadow"));

        extraction.Meshes = new OneMesh();
        extraction.Stages = opaque.Mask | shadow.Mask;
        extraction.CasterStages = shadow.Mask;

        using var world = new World();
        var entity = Meshed(world, casts: true, Vector3.Zero);

        extraction.Extract(world);
        Assert.Equal(opaque.Mask | shadow.Mask, system.Objects[world.Read<RenderHandle>(entity).Object].Stages);

        world.Get<MeshRenderable>(entity).CastsShadows = false;
        extraction.Extract(world);

        // Still settled, so still stamped with what it was created with.
        Assert.Equal(opaque.Mask | shadow.Mask, system.Objects[world.Read<RenderHandle>(entity).Object].Stages);

        extraction.Resettle(world);

        Assert.False(world.Has<RenderHandle>(entity));
        Assert.Equal(0, system.Objects.LiveCount);

        extraction.Extract(world);

        Assert.Equal(opaque.Mask, system.Objects[world.Read<RenderHandle>(entity).Object].Stages);
    }

    /// <summary>Resettling releases the claim it held rather than leaking one per entity.</summary>
    /// <remarks>
    ///     ⚠ <b>The reason this is a verb on the system and not <c>world.Remove&lt;RenderHandle&gt;</c>
    ///     at the call site.</b> Removing the component alone unsettles the entity and strands both the
    ///     render object and the residency claim — a leak per entity per call, and a stale handle into a
    ///     slot something else takes next.
    /// </remarks>
    [Fact]
    public void ResettleReleasesWhatItUnsettles() {
        using var world = new World();
        var cube = Shaped(world, PrimitiveKind.Cube, Vector3.Zero);

        extraction.Extract(world);

        Assert.Equal(1, residency.ClaimsOn(GeometryKey.Of(PrimitiveKind.Cube)));
        Assert.Equal(1, system.Objects.LiveCount);

        extraction.Resettle(world);

        Assert.Equal(0, residency.ClaimsOn(GeometryKey.Of(PrimitiveKind.Cube)));
        Assert.Equal(0, system.Objects.LiveCount);

        // And it is genuinely re-extractable, not merely emptied.
        extraction.Extract(world);

        Assert.Equal(1, system.Objects.LiveCount);
        Assert.Equal(1, residency.ClaimsOn(GeometryKey.Of(PrimitiveKind.Cube)));
        Assert.True(world.Has<RenderHandle>(cube));
    }
}
