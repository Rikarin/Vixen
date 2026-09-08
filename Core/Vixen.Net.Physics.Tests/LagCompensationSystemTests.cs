// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Net.Time;
using Vixen.Physics.Ecs;
using Xunit;

namespace Vixen.Net.Physics.Tests;

/// <summary>The ring being filled by something other than a test that wanted it filled.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>LagCompensator.Capture</c> was never called in any process</b>
///         (<see href="https://github.com/Rikarin/Vixen/issues/515" />). Everything under it was built
///         and correct and had only ever been driven by hand from
///         <c>LagCompensationTests</c> — which is the suite that owns the arithmetic and keeps owning
///         it. What is new here is the joining: a tag on an entity, a system on the loop, and a ring
///         that follows the world instead of the call sites.
///     </para>
///     <para>
///         These drive a real <see cref="PhysicsScene" /> rather than a bare <c>PhysicsWorld</c>,
///         because the thing being tested is the bridge between an ECS world and a body handle — a
///         fake that handed out handles would be a test of the fake, and the handle is exactly what a
///         game does not have and the reason the tag exists.
///     </para>
/// </remarks>
public sealed class LagCompensationSystemTests : IDisposable {
    static readonly TickRate Rate = TickRate.Default;

    readonly World world = new("lag-compensation-system");
    readonly PhysicsScene scene;
    readonly TickManager clock = new(Rate);

    public LagCompensationSystemTests() => scene = new(world);

    public void Dispose() {
        scene.Dispose();
        world.Dispose();
    }

    /// <summary>A tagged body is tracked and captured, and nothing had to name its handle.</summary>
    [Fact]
    public void ATaggedBodyIsTrackedAndCaptured() {
        var compensator = new LagCompensator(scene.World, Rate);
        var system = new LagCompensationSystem(compensator, clock);

        var target = Body(new(0f, 0f, 0f));
        world.Add<LagCompensated>(target);
        Settle();

        for (var tick = 1u; tick <= 4; tick++) {
            Move(target, new(tick * 0.2f, 0f, 0f));
            system.Capture(world, new(tick));
        }

        Assert.Equal(1, compensator.TrackedCount);
        Assert.Equal(1, system.AdoptedCount);
        Assert.Equal(4, system.CapturedTicks);
        Assert.True(compensator.HasCaptured);
        Assert.Equal(new Tick(4), compensator.NewestTick);

        Assert.True(compensator.TryGetHistory(world.Read<PhysicsBody>(target).Handle, out var history));
        Assert.Equal(4, history!.Count);

        // And the ring holds the past rather than four copies of the present, which is the whole
        // point of it and the thing a capture in the wrong place would get wrong.
        Assert.Equal(0.2f, history.Oldest.Position.X, 3);
        Assert.Equal(0.8f, history.Newest.Position.X, 3);
    }

    /// <summary>An untagged body is not in the ring, however many there are of it.</summary>
    /// <remarks>
    ///     The instrument check for the test above. A system that tracked everything with a body
    ///     would pass that one and would also fill the ring with the level, which is what the tag
    ///     exists to avoid.
    /// </remarks>
    [Fact]
    public void AnUntaggedBodyIsNotInTheRing() {
        var compensator = new LagCompensator(scene.World, Rate);
        var system = new LagCompensationSystem(compensator, clock);

        var tagged = Body(new(0f, 0f, 0f));
        world.Add<LagCompensated>(tagged);

        Body(new(4f, 0f, 0f));
        Body(new(8f, 0f, 0f));
        Settle();

        system.Capture(world, new(1));

        Assert.Equal(1, compensator.TrackedCount);
        Assert.Equal(1, system.AdoptedCount);
    }

    /// <summary>Taking the tag off releases the body, which the compensator on its own cannot see.</summary>
    /// <remarks>
    ///     ⚠ <c>LagCompensator.Capture</c> drops a body the physics world no longer knows — the
    ///     destroyed case, already right. It cannot see an entity that is alive and has had the tag
    ///     removed: a spectator, a corpse, a vehicle nobody is in. Left to the compensator, that body
    ///     is captured for the rest of the match.
    /// </remarks>
    [Fact]
    public void RemovingTheTagReleasesTheBody() {
        var compensator = new LagCompensator(scene.World, Rate);
        var system = new LagCompensationSystem(compensator, clock);

        var target = Body(new(0f, 0f, 0f));
        world.Add<LagCompensated>(target);
        Settle();

        system.Capture(world, new(1));
        Assert.Equal(1, compensator.TrackedCount);

        world.Remove<LagCompensated>(target);
        system.Capture(world, new(2));

        Assert.Equal(0, compensator.TrackedCount);
        Assert.Equal(1, system.ReleasedCount);
        Assert.False(compensator.IsTracked(world.Read<PhysicsBody>(target).Handle));
    }

    /// <summary>Tracked once, however many ticks run.</summary>
    /// <remarks>
    ///     <c>Track</c> on an already-tracked body does nothing, so a system that called it every tick
    ///     would be correct and would report a hundred adoptions an hour for one player — which is a
    ///     counter that cannot be read.
    /// </remarks>
    [Fact]
    public void ABodyIsAdoptedOnceRatherThanEveryTick() {
        var compensator = new LagCompensator(scene.World, Rate);
        var system = new LagCompensationSystem(compensator, clock);

        var target = Body(new(0f, 0f, 0f));
        world.Add<LagCompensated>(target);
        Settle();

        for (var tick = 1u; tick <= 5; tick++) {
            system.Capture(world, new(tick));
        }

        Assert.Equal(1, system.AdoptedCount);
        Assert.Equal(0, system.ReleasedCount);
    }

    /// <summary>A capture during a rewind throws rather than recording the past as the present.</summary>
    /// <remarks>
    ///     Not caught and counted, deliberately. The mistake is self-reinforcing — the ring fills with
    ///     its own past — and it is a bug in the game's shot code, which is where the stack trace
    ///     should point.
    /// </remarks>
    [Fact]
    public void ACaptureInsideARewindRefuses() {
        var compensator = new LagCompensator(scene.World, Rate);
        var system = new LagCompensationSystem(compensator, clock);

        var target = Body(new(0f, 0f, 0f));
        world.Add<LagCompensated>(target);
        Settle();

        system.Capture(world, new(1));
        Move(target, new(1f, 0f, 0f));
        system.Capture(world, new(2));

        using var rewind = compensator.Rewind(new(1));

        Assert.Throws<InvalidOperationException>(() => system.Capture(world, new(3)));
    }

    /// <summary>Neither seam is optional.</summary>
    [Fact]
    public void TheCompensatorAndTheClockAreRequired() {
        var compensator = new LagCompensator(scene.World, Rate);

        Assert.Throws<ArgumentNullException>(() => new LagCompensationSystem(null!, clock));
        Assert.Throws<ArgumentNullException>(() => new LagCompensationSystem(compensator, null!));
    }

    Entity Body(in Vector3 at) {
        var entity = world.Create(LocalTransform.At(at));

        world.Add(entity, Collider.Of(scene.Shapes.Box(new Vector3(0.5f, 1f, 0.5f))));
        world.Add(entity, RigidBody.Kinematic());

        return entity;
    }

    /// <summary>Builds the bodies the components asked for, which is what gives them a handle.</summary>
    void Settle() => scene.Synchronize(1f / 30f);

    /// <summary>Moves the body itself, the way whatever owns a kinematic body does.</summary>
    /// <remarks>
    ///     ⚠ Writing <c>LocalTransform</c> and calling <c>Synchronize</c> is <i>not</i> the same
    ///     thing: a kinematic body is steered toward its target by the solver and does not arrive
    ///     until a step runs, so the pose the compensator reads would be the old one and the ring
    ///     would fill with four copies of the origin. Nothing here steps the simulation — what is
    ///     under test is the history, and <c>LagCompensationTests</c> moves bodies the same way for
    ///     the same reason.
    /// </remarks>
    void Move(Entity entity, in Vector3 to) =>
        scene.World.SetTransform(world.Read<PhysicsBody>(entity).Handle, to, Quaternion.Identity);
}
