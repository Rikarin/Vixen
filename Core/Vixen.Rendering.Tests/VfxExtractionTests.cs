// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
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
    readonly VfxExtractionSystem extraction;
    readonly RenderStage transparent;

    public VfxExtractionTests() {
        transparent = system.AddStage(new("Transparent"));

        particles.Add(materials);
        system.AddFeature(particles);

        extraction = new(system, particles, materials) {
            Stages = transparent.Mask,
            Effects = new Source(),
            Material = new("ParticleSprite") { Composition = MaterialCompiler.PassComposition() }
        };
    }

    public void Dispose() {
        extraction.Dispose();
        system.Dispose();
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

    /// <summary>A source that answers with one graph, or refuses.</summary>
    /// <remarks>
    ///     A burst rather than a rate, because a rate over one sixtieth of a second at any sane rate
    ///     spawns nothing — and every assertion here is about particles that exist.
    /// </remarks>
    sealed class Source : IVfxEffectSource {
        readonly VfxCompiledGraph graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(16)],
            [
                new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, 0f, 1f)),
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.2f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            256,
            VfxRenderer.Billboard
        );

        public bool Ready { get; init; } = true;

        public bool TryGet(AssetReference reference, out VfxCompiledGraph effect) {
            effect = graph;

            return Ready;
        }
    }
}
