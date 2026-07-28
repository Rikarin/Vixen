// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Audio.Spatial;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>A zone is a thing in a level, which means it is an entity.</summary>
public sealed class ReverbZoneSystemTests : IDisposable {
    readonly World world = new("ReverbZones");
    readonly AudioEngine engine;
    readonly AudioSystem system;

    public ReverbZoneSystemTests() {
        (engine, _) = AudioTestData.Engine();
        system = new AudioSystem(engine);
    }

    public void Dispose() {
        engine.Dispose();
        world.Dispose();
    }

    static AudioReverbZone Cave(float radius = 10f, int priority = 0, float strength = 1f) => new() {
        Parameter = "cave",
        Shape = AudioZoneShape.Sphere,
        Extent = new Vector3(radius, radius, radius),
        Strength = strength,
        Priority = priority
    };

    Entity Place(AudioReverbZone zone, Vector3 at) {
        var entity = world.Create();
        world.Add(entity, AudioReverbZoneRef.Of(zone));
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });
        return entity;
    }

    void Listen(Vector3 at) {
        var entity = world.Create();
        world.Add(entity, AudioListenerComponent.Default);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });
    }

    /// <summary>
    ///     The entity's transform decides where the zone is, not the asset's own position — the same
    ///     rule <c>AudioSpatial</c> follows, so a room moves because the room moved.
    /// </summary>
    [Fact]
    public void TheTransformDecidesWhereTheZoneIs() {
        // A description whose own Position is a long way from where it is placed.
        var zone = Cave() with { Position = new Vector3(1_000f, 0f, 0f) };

        Place(zone, new Vector3(50f, 0f, 0f));
        Listen(new Vector3(50f, 0f, 0f));

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(1, system.ZoneCount);
        Assert.Equal(1f, engine.ReverbZones.StrengthOf("cave"));
    }

    [Fact]
    public void WalkingOutOfItReleasesIt() {
        Place(Cave(), Vector3.Zero);
        var ear = world.Create();
        world.Add(ear, AudioListenerComponent.Default);
        world.Add(ear, new WorldTransform { Value = Matrix4x4.FromTranslation(Vector3.Zero) });

        system.Synchronize(world, 1f / 60f);
        Assert.Equal(1f, engine.ReverbZones.StrengthOf("cave"));

        world.Set(ear, new WorldTransform { Value = Matrix4x4.FromTranslation(new Vector3(500f, 0f, 0f)) });
        system.Synchronize(world, 1f / 60f);

        Assert.Equal(0f, engine.ReverbZones.StrengthOf("cave"));
    }

    /// <summary>
    ///     The reason the set is rebuilt rather than maintained: nobody has to remember to tear a
    ///     zone down, and a destroyed entity cannot leave its parameter stuck on.
    /// </summary>
    [Fact]
    public void DestroyingTheEntityStopsTheRoom() {
        var entity = Place(Cave(), Vector3.Zero);
        Listen(Vector3.Zero);

        system.Synchronize(world, 1f / 60f);
        Assert.Equal(1f, engine.ReverbZones.StrengthOf("cave"));

        world.Destroy(entity);
        system.Synchronize(world, 1f / 60f);

        Assert.Equal(0, system.ZoneCount);
        Assert.Equal(0f, engine.ReverbZones.StrengthOf("cave"));
    }

    /// <summary>For a door that seals, without moving the room somewhere the listener cannot reach.</summary>
    [Fact]
    public void SwitchingItOffStopsIt() {
        var entity = Place(Cave(), Vector3.Zero);
        Listen(Vector3.Zero);

        system.Synchronize(world, 1f / 60f);
        Assert.Equal(1f, engine.ReverbZones.StrengthOf("cave"));

        world.Set(entity, new AudioReverbZoneRef { Zone = Cave(), Enabled = false });
        system.Synchronize(world, 1f / 60f);

        Assert.Equal(0, system.ZoneCount);
        Assert.Equal(0f, engine.ReverbZones.StrengthOf("cave"));
    }

    /// <summary>One description, many rooms — which is what makes changing all of them one edit.</summary>
    [Fact]
    public void OneDescriptionCanBeManyRooms() {
        var cathedral = Cave(radius: 8f);

        Place(cathedral, new Vector3(-100f, 0f, 0f));
        Place(cathedral, new Vector3(100f, 0f, 0f));
        Listen(new Vector3(100f, 0f, 0f));

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(2, system.ZoneCount);
        Assert.Equal(1f, engine.ReverbZones.StrengthOf("cave"));
    }

    [Fact]
    public void TheMoreSpecificPlacedZoneStillWins() {
        Place(Cave(radius: 100f, priority: 0, strength: 1f), Vector3.Zero);
        Place(Cave(radius: 5f, priority: 10, strength: 0.2f), Vector3.Zero);
        Listen(Vector3.Zero);

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(0.2f, engine.ReverbZones.StrengthOf("cave"), 1e-4f);
    }

    /// <summary>Zones added from code and zones placed in the world are both zones.</summary>
    [Fact]
    public void APlacedZoneAndACodeOneCoexist() {
        engine.ReverbZones.Add(new AudioReverbZone {
            Parameter = "underwater",
            Position = Vector3.Zero,
            Extent = new Vector3(20f, 20f, 20f)
        });

        Place(Cave(), Vector3.Zero);
        Listen(Vector3.Zero);

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(1f, engine.ReverbZones.StrengthOf("cave"));
        Assert.Equal(1f, engine.ReverbZones.StrengthOf("underwater"));
    }

    [Fact]
    public void AnEntityWithNoZoneIsNotAZone() {
        var entity = world.Create();
        world.Add(entity, new AudioReverbZoneRef { Zone = null, Enabled = true });
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        Listen(Vector3.Zero);

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(0, system.ZoneCount);
    }
}
