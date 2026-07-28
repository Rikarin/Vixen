// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Net.Time;
using Vixen.Physics;
using Vixen.Physics.Bodies;
using Xunit;

namespace Vixen.Net.Physics.Tests;

/// <summary>Lag compensation: the shot that missed, and the claim that was not believed.</summary>
/// <remarks>
///     The two tests that matter are the first two. Everything else here is about the world being
///     put back exactly as it was found, which is the property that fails silently — a world left in
///     the past keeps simulating and replicating and looks entirely normal, with everybody standing
///     where they were a fifth of a second ago.
/// </remarks>
public sealed class LagCompensationTests {
    static readonly TickRate Rate = TickRate.Default;

    /// <summary>A shot that misses live and hits when the world is put back where the shooter saw it.</summary>
    /// <remarks>
    ///     <para>
    ///         The whole feature in one test, and it is written as the difference between two
    ///         identical queries rather than as an assertion about one of them. A target moving at
    ///         6 m/s is 20 cm further on after a tick and 1.2 m after six — comfortably past its own
    ///         width — so a ray aimed where it was six ticks ago misses it now and hits it then.
    ///     </para>
    ///     <para>
    ///         The ray is the same ray in both halves. If the compensated one had to be aimed
    ///         differently the test would be proving something about arithmetic rather than about
    ///         the rewind.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AShotThatMissesNowHitsWhereTheShooterSawIt() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        var target = world.CreateBody(
            BodyDescription.Kinematic(world.Shapes.Box(new Vector3(0.5f, 1f, 0.5f)), new(0f, 0f, 0f))
        );

        compensator.Track(target);

        // Six ticks of walking sideways, captured as it goes. Nothing here steps the simulation —
        // a kinematic body is moved by whatever owns it, and what is under test is the history.
        var aimedAt = Vector3.Zero;

        for (var tick = 1u; tick <= 7; tick++) {
            var position = new Vector3((tick - 1) * 0.2f, 0f, 0f);
            world.SetTransform(target, position, Quaternion.Identity);
            compensator.Capture(new(tick));

            if (tick == 1) {
                aimedAt = position;
            }
        }

        // Live: the target has walked 1.2 m and the shot goes through where it used to be.
        var from = new Vector3(aimedAt.X, 0f, -10f);
        var along = Vector3.UnitZ;

        Assert.False(
            world.Raycast(from, along, 20f, out _),
            "The target should have moved out of the way by now."
        );

        // Compensated: the same ray, against the world as it was on tick 1.
        using (var rewind = compensator.Rewind(new(1))) {
            Assert.Equal(new Tick(1), rewind.At);
            Assert.Equal(1, rewind.BodyCount);

            Assert.True(
                world.Raycast(from, along, 20f, out var hit),
                "The shot should hit where the shooter saw the target."
            );

            Assert.Equal(target, hit.Body);
        }

        // And the world is back where it was, so the next tick simulates from the present.
        world.GetTransform(target, out var after, out _);
        Assert.Equal(1.2f, after.X, 3);
    }

    /// <summary>A claim further back than the player's latency justifies is clamped, not honoured.</summary>
    /// <remarks>
    ///     <para>
    ///         The anti-cheat surface. A client picks the tick in its hit claim, so a client can pick
    ///         any tick — and "I was looking at the world half a second ago" from somebody on a 20 ms
    ///         connection is a claim to have been shown something they were not shown. What bounds it
    ///         is the round trip the <i>server</i> measured.
    ///     </para>
    ///     <para>
    ///         Clamped rather than refused, which is the kinder half of the same rule: a player whose
    ///         connection genuinely is that bad gets their shot resolved against the oldest world
    ///         they could honestly have seen, rather than having it thrown away.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AClaimBeyondThePlayersLatency_IsClampedToWhatTheyCouldHaveSeen() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        for (var tick = 1u; tick <= 20; tick++) {
            compensator.Capture(new(tick));
        }

        // 66 ms round trip is two ticks at 30 Hz, plus two of interpolation slack: four ticks back.
        var quick = TimeSpan.FromMilliseconds(66);

        Assert.Equal(new Tick(16), compensator.ClampFor(new(16), quick));
        Assert.Equal(new Tick(16), compensator.ClampFor(new(4), quick));
        Assert.Equal(1, compensator.ClampedCount);

        // A slower connection genuinely gets a longer window, and is not clamped for using it.
        var slow = TimeSpan.FromMilliseconds(200);

        Assert.Equal(new Tick(12), compensator.ClampFor(new(12), slow));
        Assert.Equal(1, compensator.ClampedCount);

        // Nothing rewinds into the future, however far ahead the client's clock is running — and a
        // client's clock legitimately does run ahead, so this is not counted as a lie.
        Assert.Equal(new Tick(20), compensator.ClampFor(new(25), quick));
        Assert.Equal(1, compensator.ClampedCount);
    }

    /// <summary>No latency at all still rewinds by the interpolation delay.</summary>
    /// <remarks>
    ///     The bound that is easy to leave out, and leaving it out makes compensation systematically
    ///     one buffer too shallow: a client does not render the newest snapshot it holds, it renders
    ///     behind it. Even a player on a loopback saw the world a couple of ticks ago.
    /// </remarks>
    [Fact]
    public void EvenAPlayerWithNoLatencyIsAllowedTheInterpolationDelay() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        for (var tick = 1u; tick <= 10; tick++) {
            compensator.Capture(new(tick));
        }

        Assert.Equal(new Tick(8), compensator.ClampFor(new(8), TimeSpan.Zero));
        Assert.Equal(new Tick(8), compensator.ClampFor(new(2), TimeSpan.Zero));
    }

    /// <summary>The rewind window has a ceiling nobody's latency can raise.</summary>
    [Fact]
    public void TheWindowHasACeilingThatLatencyCannotRaise() {
        using var world = new PhysicsWorld();

        var compensator = new LagCompensator(
            world,
            Rate,
            new() { MaxRewind = TimeSpan.FromMilliseconds(100), HistoryTicks = 32 }
        );

        for (var tick = 1u; tick <= 40; tick++) {
            compensator.Capture(new(tick));
        }

        // Three ticks at 30 Hz, whatever a two-second round trip would otherwise buy.
        Assert.Equal(new Tick(37), compensator.ClampFor(new(5), TimeSpan.FromSeconds(2)));
    }

    /// <summary>A rewind that throws still puts the world back.</summary>
    /// <remarks>
    ///     The failure this whole design is shaped around. A rewound query is a few lines and any of
    ///     them can throw; a world left in the past does not report anything, it just quietly plays
    ///     the rest of the match a fifth of a second ago.
    /// </remarks>
    [Fact]
    public void AQueryThatThrowsStillLeavesTheWorldWhereItWas() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        var body = world.CreateBody(BodyDescription.Kinematic(world.Shapes.Sphere(0.5f), new(0f, 0f, 0f)));
        compensator.Track(body);

        for (var tick = 1u; tick <= 5; tick++) {
            world.SetTransform(body, new(tick, 0f, 0f), Quaternion.Identity);
            compensator.Capture(new(tick));
        }

        world.GetTransform(body, out var before, out var facing);

        // An Action rather than a lambda inline, so overload resolution cannot pick the async
        // overload of Throws and assert on a task nobody awaited.
        var query = new Action(
            () => {
                using var rewind = compensator.Rewind(new(1));

                Assert.NotEqual(before, Position(world, body));

                throw new InvalidOperationException("The query went wrong.");
            }
        );

        Assert.Throws<InvalidOperationException>(query);

        Assert.False(compensator.IsRewound);
        world.GetTransform(body, out var after, out var stillFacing);
        Assert.Equal(before, after);
        Assert.Equal(facing, stillFacing);
    }

    /// <summary>Capturing during a rewind is refused rather than recording the past as the present.</summary>
    /// <remarks>
    ///     A mistake that feeds itself: the history fills with its own contents and the compensation
    ///     drifts further back every tick. Far better as an exception at the call site than as a
    ///     hit-registration bug nobody can reproduce.
    /// </remarks>
    [Fact]
    public void CapturingDuringARewind_IsRefused() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        var body = world.CreateBody(BodyDescription.Kinematic(world.Shapes.Sphere(0.5f), new(0f, 0f, 0f)));
        compensator.Track(body);
        compensator.Capture(new(1));

        using var rewind = compensator.Rewind(new(1));

        Assert.Throws<InvalidOperationException>(() => compensator.Capture(new(2)));
        Assert.Throws<InvalidOperationException>(() => compensator.Rewind(new(1)));
    }

    /// <summary>Interpolation puts a body between two captures rather than on the nearer one.</summary>
    /// <remarks>
    ///     Worth its own test because it is the difference between compensation that mostly works
    ///     and compensation players call broken: at 30 Hz a body moving 6 m/s covers 20 cm between
    ///     captures, so snapping puts it up to 10 cm out — most of the width of a head.
    /// </remarks>
    [Fact]
    public void ARewindLandsBetweenCapturesWhenAskedTo() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        var body = world.CreateBody(BodyDescription.Kinematic(world.Shapes.Sphere(0.5f), new(0f, 0f, 0f)));
        compensator.Track(body);

        for (var tick = 1u; tick <= 4; tick++) {
            world.SetTransform(body, new((tick - 1) * 2f, 0f, 0f), Quaternion.Identity);
            compensator.Capture(new(tick));
        }

        using (var rewind = compensator.Rewind(new(1), 0.5f)) {
            Assert.Equal(1f, Position(world, body).X, 3);
        }

        using (var rewind = compensator.Rewind(new(1), 0.25f)) {
            Assert.Equal(0.5f, Position(world, body).X, 3);
        }

        // And with interpolation off it lands on a capture, which is what a replay wants.
        var snapping = new LagCompensator(world, Rate, new() { Interpolate = false });
        snapping.Track(body);

        for (var tick = 1u; tick <= 4; tick++) {
            world.SetTransform(body, new((tick - 1) * 2f, 0f, 0f), Quaternion.Identity);
            snapping.Capture(new(tick));
        }

        using (var rewind = snapping.Rewind(new(1), 0.5f)) {
            Assert.Equal(0f, Position(world, body).X, 3);
        }
    }

    /// <summary>Untracked bodies do not move, which is what makes the walls stay where they are.</summary>
    [Fact]
    public void AnUntrackedBodyIsNotRewound() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        var wall = world.CreateBody(BodyDescription.Static(world.Shapes.Box(new Vector3(5f, 5f, 0.5f)), new(0f, 0f, 5f)));
        var player = world.CreateBody(BodyDescription.Kinematic(world.Shapes.Sphere(0.5f), new(0f, 0f, 0f)));

        compensator.Track(player);

        for (var tick = 1u; tick <= 4; tick++) {
            world.SetTransform(player, new(tick, 0f, 0f), Quaternion.Identity);
            compensator.Capture(new(tick));
        }

        using var rewind = compensator.Rewind(new(1));

        Assert.Equal(1, rewind.BodyCount);
        Assert.Equal(new Vector3(0f, 0f, 5f), Position(world, wall));
    }

    /// <summary>A body destroyed under the compensator is dropped rather than queried.</summary>
    /// <remarks>
    ///     Nothing tells this type that a handle was destroyed — a player disconnecting is a
    ///     despawn somewhere else entirely — so it finds out by asking, on the tick it was going to
    ///     ask anyway.
    /// </remarks>
    [Fact]
    public void ABodyThatIsDestroyed_IsForgottenOnTheNextCapture() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        var body = world.CreateBody(BodyDescription.Kinematic(world.Shapes.Sphere(0.5f), new(0f, 0f, 0f)));
        compensator.Track(body);
        compensator.Capture(new(1));

        Assert.Equal(1, compensator.TrackedCount);

        world.DestroyBody(body);
        compensator.Capture(new(2));

        Assert.Equal(0, compensator.TrackedCount);
        Assert.False(compensator.IsTracked(body));

        // And a rewind over nothing is a rewind that changes nothing, rather than a throw.
        using var rewind = compensator.Rewind(new(1));
        Assert.Equal(0, rewind.BodyCount);
    }

    /// <summary>History older than the ring is gone, and a rewind to it lands on the oldest held.</summary>
    [Fact]
    public void ARewindPastTheHistory_LandsOnTheOldestPoseHeld() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate, new() { HistoryTicks = 4 });

        var body = world.CreateBody(BodyDescription.Kinematic(world.Shapes.Sphere(0.5f), new(0f, 0f, 0f)));
        compensator.Track(body);

        for (var tick = 1u; tick <= 10; tick++) {
            world.SetTransform(body, new(tick, 0f, 0f), Quaternion.Identity);
            compensator.Capture(new(tick));
        }

        Assert.True(compensator.TryGetHistory(body, out var history));
        Assert.Equal(4, history!.Count);
        Assert.Equal(new Tick(7), history.Oldest.At);

        using var rewind = compensator.Rewind(new(1));
        Assert.Equal(7f, Position(world, body).X, 3);
    }

    /// <summary>Capturing a tick allocates nothing, which is the one path that scales with players.</summary>
    /// <remarks>
    ///     <para>
    ///         A rewind happens once per shot; a capture happens once per tick for every tracked
    ///         body, so it is the half of this that a hundred players multiply. Zero steady-state
    ///         allocation in the frame loop is a stated non-negotiable
    ///         ([00](../../docs/plan/00-vision-and-principles.md) § Non-negotiables).
    ///     </para>
    ///     <para>
    ///         <b>The measurement is bracketed by a collection count, and that is not decoration.</b>
    ///         <c>GC.GetAllocatedBytesForCurrentThread</c> settles up the unused remainder of the
    ///         thread's allocation context whenever a collection happens, so a measured region
    ///         containing a Gen0 reads up to eight kilobytes high having allocated nothing at all. A
    ///         test that asserts zero without checking for that is a test that fails a couple of runs
    ///         in five for a reason that is not in the code under test — there is one of those
    ///         elsewhere in this repository, and it is being fixed separately.
    ///     </para>
    /// </remarks>
    [Fact]
    public void CapturingATickAllocatesNothing() {
        using var world = new PhysicsWorld();
        var compensator = new LagCompensator(world, Rate);

        for (var i = 0; i < 16; i++) {
            compensator.Track(
                world.CreateBody(BodyDescription.Kinematic(world.Shapes.Sphere(0.5f), new(i, 0f, 0f)))
            );
        }

        // Warm-up: the histories allocate their rings on the first capture that reaches them, and
        // the dictionaries grow. Neither is a per-tick cost, and both land on whichever tick is first.
        var tick = 1u;

        for (; tick <= 64; tick++) {
            compensator.Capture(new(tick));
        }

        long allocated;
        var attempts = 0;

        do {
            var collections = GC.CollectionCount(0);
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 240; i++) {
                compensator.Capture(new(tick++));
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // A collection landed inside the measurement, so the number is the counter's artefact
            // rather than this code's. Measure again rather than assert on it.
            if (GC.CollectionCount(0) == collections) {
                break;
            }

            allocated = -1;
        } while (++attempts < 8);

        Assert.True(
            allocated == 0,
            allocated < 0
                ? "Every attempt contained a garbage collection, so nothing could be measured."
                : $"Capturing 240 ticks over 16 bodies allocated {allocated} bytes."
        );
    }

    static Vector3 Position(PhysicsWorld world, BodyHandle body) {
        world.GetTransform(body, out var position, out _);

        return position;
    }
}
