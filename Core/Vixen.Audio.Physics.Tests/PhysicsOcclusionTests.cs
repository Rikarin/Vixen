// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Vixen.Physics;
using Vixen.Physics.Bodies;
using Xunit;

namespace Vixen.Audio.Physics.Tests;

/// <summary>The bridge, against a real Jolt world with real geometry in it.</summary>
public sealed class PhysicsOcclusionTests {
    /// <summary>A wall spanning the way between two points, or a gap in one.</summary>
    static void Wall(PhysicsWorld world, Vector3 at, Vector3 halfExtents) =>
        world.CreateBody(BodyDescription.Static(world.Shapes.Box(halfExtents), at));

    [Fact]
    public void NothingInTheWayIsNotOccluded() {
        using var world = new PhysicsWorld();
        var provider = new PhysicsOcclusionProvider(world);

        Assert.Equal(0f, provider.Occlusion(new Vector3(0f, 0f, -10f), Vector3.Zero));
    }

    [Fact]
    public void AWallInTheWayIsFullyOccluded() {
        using var world = new PhysicsWorld();

        // Wide and tall enough that every ray in the fan hits it.
        Wall(world, new Vector3(0f, 0f, -5f), new Vector3(20f, 20f, 0.5f));

        var provider = new PhysicsOcclusionProvider(world);

        Assert.Equal(1f, provider.Occlusion(new Vector3(0f, 0f, -10f), Vector3.Zero));
    }

    /// <summary>
    ///     The reason there is a fan of rays rather than one. A source at the edge of an opening is
    ///     neither blocked nor clear, and a single centre cast would have to call it one of them.
    /// </summary>
    [Fact]
    public void APartlyBlockedPathIsPartlyOccluded() {
        using var world = new PhysicsWorld();

        // A wall covering everything from x = 0.2 rightwards, so the line of sight itself is clear
        // and only the part of the fan that leans right is blocked. The fan is cast around the
        // source at z = −10 and the wall is halfway, so an offset of s at the source is s/2 here:
        // of the five rays, the one pushed +1.5 right crosses at x = 0.75 and is stopped, and the
        // other four cross at x ≤ 0 and are not.
        Wall(world, new Vector3(10.2f, 0f, -5f), new Vector3(10f, 10f, 0.5f));

        var provider = new PhysicsOcclusionProvider(world) { Spread = 1.5f };
        var occlusion = provider.Occlusion(new Vector3(0f, 0f, -10f), Vector3.Zero);

        Assert.Equal(0.2f, occlusion, 1e-4f);
    }

    /// <summary>
    ///     The setting most likely to make this sound wrong if it is left alone: a handrail on a
    ///     layer nobody meant to block sound should not muffle a conversation.
    /// </summary>
    [Fact]
    public void OnlyTheLayersThatWereAskedForBlockSound() {
        using var world = new PhysicsWorld();
        var scenery = new PhysicsLayer(1);

        world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(new Vector3(20f, 20f, 0.5f)), new Vector3(0f, 0f, -5f))
                with { Layer = scenery }
        );

        var provider = new PhysicsOcclusionProvider(world);

        // Everything blocks by default, so the wall is found.
        Assert.Equal(1f, provider.Occlusion(new Vector3(0f, 0f, -10f), Vector3.Zero));

        // Told to look at everything except that layer, it is not.
        provider.Layers = PhysicsLayerMask.All.Without(scenery);
        Assert.Equal(0f, provider.Occlusion(new Vector3(0f, 0f, -10f), Vector3.Zero));
    }

    /// <summary>
    ///     An emitter inside its own collider — a vehicle's engine — is reported as blocked by the
    ///     thing making the sound, and nothing in the provider can know otherwise: it is handed two
    ///     points and no bodies. The layer mask is the fix, and this pins both halves of that so
    ///     nobody later mistakes the first for a bug in the raycast.
    /// </summary>
    [Fact]
    public void AnEmitterInsideItsOwnColliderNeedsALayerAndNotMagic() {
        using var world = new PhysicsWorld();
        var source = new Vector3(0f, 0f, -10f);
        // Layer 1, because the default table declares two.
        var vehicles = new PhysicsLayer(1);

        world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(new Vector3(1f, 1f, 1f)), source) with { Layer = vehicles }
        );

        var provider = new PhysicsOcclusionProvider(world);

        // Looking at everything, the vehicle occludes its own engine.
        Assert.Equal(1f, provider.Occlusion(source, Vector3.Zero));

        // Looking only at what a level designer said blocks sound, it does not.
        provider.Layers = PhysicsLayerMask.All.Without(vehicles);
        Assert.Equal(0f, provider.Occlusion(source, Vector3.Zero));
    }

    [Fact]
    public void ASoundOnTopOfTheListenerIsNotOccluded() {
        using var world = new PhysicsWorld();
        Wall(world, new Vector3(0f, 0f, -5f), new Vector3(20f, 20f, 0.5f));

        var provider = new PhysicsOcclusionProvider(world);

        Assert.Equal(0f, provider.Occlusion(Vector3.Zero, Vector3.Zero));
    }

    /// <summary>Straight up is the direction that catches a naive basis, so it is worth its own test.</summary>
    [Fact]
    public void TheFanIsWellFormedInEveryDirectionIncludingStraightUp() {
        using var world = new PhysicsWorld();
        var provider = new PhysicsOcclusionProvider(world);

        foreach (var direction in new[] {
            new Vector3(0f, 10f, 0f),
            new Vector3(0f, -10f, 0f),
            new Vector3(10f, 0f, 0f),
            new Vector3(0f, 0f, 10f),
            new Vector3(3f, 4f, 5f)
        }) {
            var occlusion = provider.Occlusion(direction, Vector3.Zero);

            // Nothing in the world, so nothing is blocked — and no NaN out of a degenerate basis,
            // which is what an equality against zero actually catches.
            Assert.Equal(0f, occlusion);
        }
    }

    [Fact]
    public void OneRayIsAllowedAndIsBinary() {
        using var world = new PhysicsWorld();
        Wall(world, new Vector3(0f, 0f, -5f), new Vector3(20f, 20f, 0.5f));

        var provider = new PhysicsOcclusionProvider(world) { Rays = 1 };

        Assert.Equal(1, provider.Rays);
        Assert.Equal(1f, provider.Occlusion(new Vector3(0f, 0f, -10f), Vector3.Zero));
        Assert.Equal(1, provider.Casts);
    }

    [Fact]
    public void TheRayCountIsClampedToWhatThePatternHas() {
        using var world = new PhysicsWorld();
        var provider = new PhysicsOcclusionProvider(world) { Rays = 99 };

        Assert.Equal(PhysicsOcclusionProvider.MaxRays, provider.Rays);

        provider.Rays = 0;
        Assert.Equal(1, provider.Rays);
    }

    /// <summary>The cost claim, so a budget can be reasoned about.</summary>
    [Fact]
    public void TheCostIsRaysPerQueryAndNothingElse() {
        using var world = new PhysicsWorld();
        var provider = new PhysicsOcclusionProvider(world);

        provider.Occlusion(new Vector3(0f, 0f, -10f), Vector3.Zero);
        Assert.Equal(PhysicsOcclusionProvider.MaxRays, provider.Casts);

        provider.Occlusion(new Vector3(0f, 0f, -20f), Vector3.Zero);
        Assert.Equal(PhysicsOcclusionProvider.MaxRays * 2, provider.Casts);
    }

    /// <summary>Through the interface, which is all <c>Vixen.Audio</c> ever sees of this.</summary>
    [Fact]
    public void ItIsAnOcclusionProviderTheMixerCanTake() {
        using var world = new PhysicsWorld();
        var provider = new PhysicsOcclusionProvider(world);

        Assert.Equal(0f, AsTheMixerWould(provider, new Vector3(0f, 0f, -10f)));

        static float AsTheMixerWould(IAudioOcclusionProvider seam, Vector3 source) =>
            seam.Occlusion(source, Vector3.Zero);
    }

    [Fact]
    public void AWorldIsRequired() =>
        Assert.Throws<ArgumentNullException>(() => new PhysicsOcclusionProvider(null!));
}
