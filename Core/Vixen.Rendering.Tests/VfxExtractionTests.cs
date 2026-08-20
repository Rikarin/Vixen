// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Null;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>Emitters becoming running, drawn particle systems.</summary>
/// <remarks>
///     <para>
///         The bridge that makes a <c>.vxvfx</c> something an author drags onto an entity. What it
///         has to get right is a reconciliation — appear, retire, place — plus the one job no mesh
///         needs: stepping the simulations, because nothing else in the engine does.
///     </para>
///     <para>
///         ⚠ <b>Every failure here is a level that renders perfectly and has no effects in it.</b> An
///         emitter whose effect never resolved, one nothing stepped, one whose render object was never
///         added — none of them throws, and none of them is visible from any counter but this
///         system's own.
///     </para>
/// </remarks>
public sealed class VfxExtractionTests : IDisposable {
    readonly RenderSystem system = new();
    readonly ParticleRenderFeature particles = new();
    readonly MaterialRenderFeature materials = new();
    readonly NullDevice device = new(new());
    readonly GeometryBuffer geometry;
    readonly GeometryResidency residency;
    readonly VfxExtractionSystem extraction;
    readonly RenderStage transparent;

    public VfxExtractionTests() {
        transparent = system.AddStage(new("Transparent"));

        particles.Add(materials);
        system.AddFeature(particles);

        // A real buffer on the null device, on `MeshExtractionTests`' terms: the suballocation and the
        // draw arithmetic a mesh effect ends up with are the ones a frame runs.
        geometry = new(device, SurfaceVertex.SizeInBytes, vertexCapacity: 4096, indexCapacity: 8192);
        residency = new(geometry);

        extraction = new(system, particles, materials) {
            Stages = transparent.Mask,
            Effects = new Source(),
            Material = new("ParticleSprite") { Composition = MaterialCompiler.PassComposition() },
            MeshMaterial = new("ParticleMesh") { Composition = MaterialCompiler.PassComposition() },
            Meshes = new Geometry(),
            Residency = residency
        };
    }

    public void Dispose() {
        extraction.Dispose();
        geometry.Dispose();
        system.Dispose();
        device.Dispose();
    }

    /// <summary>An emitter becomes a render object carrying a running system.</summary>
    [Fact]
    public void AnEmitterBecomesADrawnParticleSystem() {
        using var world = new World();
        var entity = Emitting(world, new(3f, 0f, -4f));

        extraction.Extract(world, 1f / 60f);

        Assert.Equal(1, extraction.Running);
        Assert.Equal(0, extraction.Waiting);
        Assert.True(world.Has<VfxHandle>(entity));

        var id = world.Read<VfxHandle>(entity).Object;

        Assert.True(system.Objects[id].IsAlive);
        Assert.Equal(transparent.Mask, system.Objects[id].Stages);
        Assert.Equal(particles.Index, system.Objects[id].FeatureIndex);

        // Stepped, not merely created. A bridge that added the object and never advanced the
        // simulation is a level of emitters holding zero particles for ever.
        Assert.True(extraction.ParticleCount > 0, "nothing stepped the effect");
    }

    /// <summary>
    ///     The emitter's transform is where its particles are born.
    /// </summary>
    /// <remarks>
    ///     <b>The whole reason one <c>.vxvfx</c> can serve twenty entities.</b> Every opcode that writes
    ///     a position writes a world-space one, so an emitter that did not set <c>Origin</c> would put
    ///     every effect in the level at whatever coordinates its author typed — one pile of particles
    ///     at one point, however many emitters there are.
    /// </remarks>
    [Fact]
    public void ParticlesAreBornWhereTheEmitterIs() {
        using var world = new World();
        var at = new Vector3(12f, 3f, -7f);

        Emitting(world, at);
        extraction.Extract(world, 1f / 60f);

        var effect = Assert.Single(Systems());

        Assert.True(effect.Count > 0);

        for (var index = 0; index < effect.Count; index++) {
            // The graph spawns inside a unit sphere at its own origin, so every particle is within a
            // metre or so of the emitter and nowhere near the world origin.
            Assert.True(
                (effect.Particles.Position[index] - at).Length() < 2f,
                $"a particle was born at {effect.Particles.Position[index]} rather than near {at}"
            );
        }
    }

    /// <summary>Two emitters of one effect are two simulations, seeded apart.</summary>
    /// <remarks>
    ///     Two systems of one graph with one seed produce identical particles, which at a distance
    ///     reads as a repeated texture rather than as two fires. The seed is derived from the entity so
    ///     that the ordinary case needs nothing said and stays reproducible across runs.
    /// </remarks>
    [Fact]
    public void TwoEmittersOfOneEffectAreSeededApart() {
        using var world = new World();

        Emitting(world, Vector3.Zero);
        Emitting(world, new(20f, 0f, 0f));

        extraction.Extract(world, 1f / 60f);

        Assert.Equal(2, extraction.Running);

        var effects = Systems();

        Assert.Equal(2, effects.Count);
        Assert.NotEqual(effects[0].Seed, effects[1].Seed);
        Assert.All(effects, effect => Assert.NotEqual(0u, effect.Seed));
    }

    /// <summary>An emitter whose seed is written down keeps it.</summary>
    [Fact]
    public void AWrittenSeedWins() {
        using var world = new World();
        var entity = Emitting(world, Vector3.Zero);

        world.Set(entity, world.Read<VfxEmitter>(entity) with { Seed = 4242u });
        extraction.Extract(world, 1f / 60f);

        Assert.Equal(4242u, Assert.Single(Systems()).Seed);
    }

    /// <summary>Removing the component takes the render object and the simulation with it.</summary>
    /// <remarks>
    ///     A <c>ParticleBuffer</c> holds pooled native arrays for its whole capacity whether or not
    ///     anything is alive in it, so an emitter that was retired without being disposed is a leak
    ///     rather than a stale object.
    /// </remarks>
    [Fact]
    public void RemovingTheComponentRetiresEverything() {
        using var world = new World();
        var entity = Emitting(world, Vector3.Zero);

        extraction.Extract(world, 1f / 60f);

        var id = world.Read<VfxHandle>(entity).Object;

        world.Remove<VfxEmitter>(entity);
        extraction.Extract(world, 1f / 60f);

        Assert.Equal(0, extraction.Running);
        Assert.False(world.Has<VfxHandle>(entity));
        Assert.False(system.Objects[id].IsAlive);
    }

    /// <summary>A stopped emitter keeps its live particles and spawns no more.</summary>
    /// <remarks>
    ///     Stopping an effect and killing it are different things — a fire that is put out should let
    ///     its last embers finish — which is what makes <c>Playing</c> a field rather than a reason to
    ///     remove the component.
    /// </remarks>
    [Fact]
    public void AStoppedEmitterKeepsWhatIsAlreadyAlive() {
        using var world = new World();
        var entity = Emitting(world, Vector3.Zero);

        extraction.Extract(world, 1f / 60f);

        var effect = Assert.Single(Systems());
        var alive = effect.Count;

        Assert.True(alive > 0);

        world.Set(entity, world.Read<VfxEmitter>(entity) with { Playing = false });
        extraction.Extract(world, 1f / 60f);

        Assert.False(effect.Emitting);
        Assert.Equal(1, extraction.Running);

        // Nothing new, and nothing lost: the graph's lifetime is long enough that nothing died in a
        // sixtieth of a second.
        Assert.Equal(alive, effect.Count);
    }

    /// <summary>An emitter whose effect has not arrived is waited for rather than dropped.</summary>
    /// <remarks>
    ///     The asynchronous story in one assertion: no handle means it matches the appeared query again
    ///     next frame and is asked about again, which is why nothing here needs a queue.
    /// </remarks>
    [Fact]
    public void AnEffectThatHasNotArrivedIsAskedAboutAgain() {
        using var world = new World();

        extraction.Effects = new Source { Ready = false };

        var entity = Emitting(world, Vector3.Zero);

        extraction.Extract(world, 1f / 60f);

        Assert.Equal(0, extraction.Running);
        Assert.Equal(1, extraction.Waiting);
        Assert.False(world.Has<VfxHandle>(entity));

        extraction.Effects = new Source();
        extraction.Extract(world, 1f / 60f);

        Assert.Equal(1, extraction.Running);
        Assert.Equal(0, extraction.Waiting);
    }

    /// <summary>A moving emitter moves its bound, or it is culled where it no longer is.</summary>
    [Fact]
    public void TheBoundFollowsTheEmitter() {
        using var world = new World();
        var entity = Emitting(world, Vector3.Zero);

        extraction.Extract(world, 1f / 60f);

        var id = world.Read<VfxHandle>(entity).Object;
        var started = system.Objects[id].Bounds.Center;

        world.Set(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(new(50f, 0f, 0f)) });
        extraction.Extract(world, 1f / 60f);

        Assert.NotEqual(started, system.Objects[id].Bounds.Center);
        Assert.Equal(50f, system.Objects[id].Bounds.Center.X, 3);
    }

    // --- Lights -------------------------------------------------------------

    /// <summary>
    ///     An emitter whose effect is authored as a light lights the scene through the ordinary list.
    /// </summary>
    /// <remarks>
    ///     <b>The call <c>ParticleLights.Collect</c> did not have.</b> The <c>Vfx/Output/Light</c> node
    ///     has shipped in the editor's library the whole time and nothing collected what it produced,
    ///     so the effect drew billboards and lit nothing — a failure that presents as a lighting bug in
    ///     the author's own scene. A particle light is one more entry in the same list a lamp is.
    /// </remarks>
    [Fact]
    public void AnEmitterAuthoredAsALightFillsTheLightList() {
        using var world = new World();
        List<RenderLight> lights = [];

        particles.Lights = lights;
        extraction.Effects = new Source { Lighting = true };

        Emitting(world, new(4f, 1f, -2f));
        extraction.Extract(world, 1f / 60f);

        Assert.NotEmpty(lights);
        Assert.Equal(lights.Count, particles.CollectedLights);
        Assert.All(lights, light => Assert.Equal(LightKind.Point, light.Kind));

        // Where the emitter is, not where the graph's author typed. The same origin the quads use.
        Assert.All(lights, light => Assert.True((light.Position - new Vector3(4f, 1f, -2f)).Length() < 2f));
    }

    /// <summary>The list is refilled each step rather than accumulated.</summary>
    /// <remarks>
    ///     <c>LightExtractionSystem</c> clears it and this appends to it, so a bridge that did not
    ///     re-collect from scratch would grow the scene's light list without bound — and the frame
    ///     would get slower every second with nothing to blame.
    /// </remarks>
    [Fact]
    public void TheParticleLightsAreRebuiltEveryStep() {
        using var world = new World();
        List<RenderLight> lights = [];

        particles.Lights = lights;
        extraction.Effects = new Source { Lighting = true };

        Emitting(world, Vector3.Zero);
        extraction.Extract(world, 1f / 60f);

        var first = lights.Count;

        Assert.True(first > 0);

        // What a frame does between the two: the scene's own lights are re-extracted over the top.
        lights.Clear();
        extraction.Extract(world, 1f / 60f);

        Assert.Equal(first, lights.Count);
    }

    /// <summary>
    ///     The bridge runs after <see cref="LightExtractionSystem" />, whichever order they were added.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing but the declared order keeps this right.</b> The two systems share a phase and
    ///     conflict on no component — one reads <c>Light</c> and the other <c>VfxEmitter</c> — so the
    ///     data-dependency pass has nothing to say about them, and the one that clears the list would
    ///     wipe the sparks every frame if it happened to run second. Registered here in the wrong
    ///     order on purpose.
    /// </remarks>
    [Fact]
    public void TheBridgeRunsAfterTheSceneLightsAreExtracted() {
        using var lighting = new ForwardLightingRenderFeature();
        var lights = new LightExtractionSystem(lighting);

        var graph = SystemGraph.Build([extraction, lights]);
        var order = graph.InPhase(SystemPhase.PreRender).Select(node => node.System).ToList();

        Assert.Equal([lights, extraction], order);
    }

    // --- Meshes -------------------------------------------------------------

    /// <summary>
    ///     An emitter whose effect is authored as a mesh renderer is given the mesh it instances.
    /// </summary>
    /// <remarks>
    ///     <b>The call <c>ParticleRenderFeature.SetMesh</c> did not have.</b> A mesh effect expanded its
    ///     instances every frame and uploaded them, and the draw then skipped it — a <c>MeshDraw</c>
    ///     nobody set is not drawable — so the whole renderer was a simulation that cost its particles
    ///     and appeared nowhere. The same shape as the light output before <c>CollectLights</c>.
    /// </remarks>
    [Fact]
    public void AnEmitterAuthoredAsAMeshIsGivenItsGeometry() {
        using var world = new World();

        extraction.Effects = new Source { Instanced = true };

        var entity = Emitting(world, Vector3.Zero, Geometry.Cube);

        extraction.Extract(world, 1f / 60f);

        Assert.Equal(1, extraction.Running);
        Assert.Equal(0, extraction.Meshless);
        Assert.Equal(0, extraction.Dropped);

        var id = world.Read<VfxHandle>(entity).Object;
        var draw = system.Objects.Data.Data(particles.Meshes)[id.Index];

        Assert.True(draw.IsDrawable, "the mesh effect was extracted with no geometry to instance");

        // The layout the *pair* of buffers is described by, not the surface one. A mesh particle binds
        // the mesh's vertices and this feature's instance stream, and entry zero describes one buffer.
        Assert.Equal(extraction.MeshVertexLayout, draw.VertexLayout);

        // Left alone by SetMesh, because the particles say how many there are.
        Assert.Equal(0, draw.InstanceCount);
    }

    /// <summary>A billboard effect is given no mesh, whatever its emitter names.</summary>
    /// <remarks>
    ///     The mesh is read only where the graph's renderer asks for one, so an author who set a mesh
    ///     and then changed the output node does not keep a claim on geometry nothing draws.
    /// </remarks>
    [Fact]
    public void ABillboardEffectTakesNoGeometry() {
        using var world = new World();
        var entity = Emitting(world, Vector3.Zero, Geometry.Cube);

        extraction.Extract(world, 1f / 60f);

        var id = world.Read<VfxHandle>(entity).Object;

        Assert.False(system.Objects.Data.Data(particles.Meshes)[id.Index].IsDrawable);
        Assert.Equal(0, residency.Claims);
    }

    /// <summary>A mesh effect whose emitter named no mesh still simulates, and says so.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted rather than refused.</b> An emitter dropped onto an entity before anybody has
    ///     chosen a mesh is a real state, and a frame in which it draws nothing looks exactly like a
    ///     host that never wired <c>Meshes</c> — which is somebody else's mistake entirely.
    /// </remarks>
    [Fact]
    public void AMeshEffectWithNoMeshIsCountedRatherThanRefused() {
        using var world = new World();

        extraction.Effects = new Source { Instanced = true };
        Emitting(world, Vector3.Zero, AssetReference.Null);

        extraction.Extract(world, 1f / 60f);

        Assert.Equal(1, extraction.Running);
        Assert.Equal(1, extraction.Meshless);
        Assert.Equal(0, extraction.Waiting);
    }

    /// <summary>An emitter whose mesh has not loaded is asked about again rather than drawn empty.</summary>
    /// <remarks>
    ///     A settled emitter is never re-extracted, so extracting one now and finding its geometry
    ///     later is a mesh effect that draws nothing for the rest of the level.
    /// </remarks>
    [Fact]
    public void AMeshStillLoadingLeavesTheEmitterUnextracted() {
        using var world = new World();

        extraction.Effects = new Source { Instanced = true };
        extraction.Meshes = new Geometry { Ready = false };

        var entity = Emitting(world, Vector3.Zero, Geometry.Cube);

        extraction.Extract(world, 1f / 60f);

        Assert.Equal(0, extraction.Running);
        Assert.Equal(1, extraction.Waiting);
        Assert.False(world.Has<VfxHandle>(entity));

        // And picked up when it lands, without anything having to remember it was owed.
        extraction.Meshes = new Geometry();
        extraction.Extract(world, 1f / 60f);

        Assert.Equal(1, extraction.Running);
        Assert.Equal(0, extraction.Waiting);
    }

    /// <summary>Retiring a mesh emitter gives its geometry back.</summary>
    /// <remarks>
    ///     ⚠ <b>A claim leaked here is a slice of the geometry buffer no level unload gets back</b> —
    ///     the same leak <c>MeshExtractionSystem.Forget</c> exists for, arrived at from the other side.
    /// </remarks>
    [Fact]
    public void RetiringAMeshEmitterReleasesItsClaim() {
        using var world = new World();

        extraction.Effects = new Source { Instanced = true };

        var entity = Emitting(world, Vector3.Zero, Geometry.Cube);

        extraction.Extract(world, 1f / 60f);
        Assert.Equal(1, residency.Claims);

        world.Remove<VfxEmitter>(entity);
        extraction.Extract(world, 1f / 60f);

        Assert.Equal(0, residency.Claims);
        Assert.Equal(0, residency.Count);
    }

    /// <summary>Two emitters of one mesh are one upload and two claims.</summary>
    /// <remarks>
    ///     The same residency the scene's meshes use, deliberately — so a rock drawn as scenery and the
    ///     same rock drawn as debris share their bytes rather than being resident twice.
    /// </remarks>
    [Fact]
    public void TwoMeshEmittersShareOneUpload() {
        using var world = new World();

        extraction.Effects = new Source { Instanced = true };

        Emitting(world, Vector3.Zero, Geometry.Cube);
        Emitting(world, new(10f, 0f, 0f), Geometry.Cube);

        extraction.Extract(world, 1f / 60f);

        Assert.Equal(2, extraction.Running);
        Assert.Equal(1, residency.Count);
        Assert.Equal(2, residency.Claims);
    }

    // --- The fixture --------------------------------------------------------

    /// <summary>Every running system, in no particular order.</summary>
    List<VfxSystem> Systems() {
        var found = new List<VfxSystem>();

        foreach (var effect in particles.Systems) {
            if (effect is not null) {
                found.Add(effect);
            }
        }

        return found;
    }

    /// <summary>An entity emitting the fixture's effect at a point.</summary>
    static Entity Emitting(World world, in Vector3 at) {
        var entity = world.Create();

        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });
        world.Add(entity, VfxEmitters.Default(new(new AssetId(Guid.NewGuid()))));

        return entity;
    }

    /// <summary>The same, naming a mesh for the effects that instance one.</summary>
    static Entity Emitting(World world, in Vector3 at, AssetReference mesh) {
        var entity = world.Create();
        var emitter = VfxEmitters.Default(new(new AssetId(Guid.NewGuid())));

        emitter.Mesh = mesh;

        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });
        world.Add(entity, emitter);

        return entity;
    }

    /// <summary>A mesh source that answers with a cube, or refuses.</summary>
    sealed class Geometry : IMeshSource {
        /// <summary>The one reference this source knows about.</summary>
        public static AssetReference Cube { get; } =
            new(new AssetId(new("22222222-2222-2222-2222-222222222222")));

        public bool Ready { get; init; } = true;

        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = MeshPrimitives.Create(PrimitiveKind.Cube);

            return Ready && !reference.IsNull;
        }
    }

    /// <summary>A source that answers with one graph, or refuses.</summary>
    /// <remarks>
    ///     A burst rather than a rate, because a rate over one sixtieth of a second at any sane rate
    ///     spawns nothing — and every assertion here is about particles that exist.
    /// </remarks>
    sealed class Source : IVfxEffectSource {
        public bool Ready { get; init; } = true;

        /// <summary>Whether the graph's output node is a light rather than a billboard.</summary>
        public bool Lighting { get; init; }

        /// <summary>Whether it is a mesh renderer rather than a billboard.</summary>
        public bool Instanced { get; init; }

        public bool TryGet(AssetReference reference, out VfxCompiledGraph effect) {
            effect = VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(16)],
                [
                    new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, 0f, 1f)),
                    new(VfxOpcode.SetSize, new Vector4(0.1f, 0.2f, 0f, 0f)),
                    new(VfxOpcode.SetColour, Vector4.One),
                    new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
                ],
                [],
                256,
                Lighting ? VfxRenderer.Light(2f, 5f)
                : Instanced ? VfxRenderer.Instanced()
                : VfxRenderer.Billboard
            );

            return Ready;
        }
    }
}
