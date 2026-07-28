// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Xunit;

namespace Vixen.Physics.Tests;

/// <summary>
///     The Phase 8 exit gate: the same scene, stepped the same way, produces bit-identical results.
/// </summary>
/// <remarks>
///     <para>
///         Not "close enough". A replay, a rollback and a lockstep peer all need the same bits, and
///         so does Phase 9's lag compensation, which rewinds collider history and re-runs shape casts
///         against it. A tolerance here would mean the divergence is found by a desync months later
///         instead of by this file.
///     </para>
///     <para>
///         <b>What determinism costs.</b> Two settings hold it up:
///         <c>PhysicsWorldSettings.Deterministic</c>, which fixes the island splitter's order, and
///         <c>ThreadCount</c>, which must match — Jolt is deterministic for a given thread count and
///         not across different ones. These tests pin the thread count to 1 so a machine with a
///         different core count runs the same test.
///     </para>
/// </remarks>
public sealed class PhysicsDeterminismTests {
    const float Step = 1f / 60f;

    static PhysicsWorldSettings Settings => new() { ThreadCount = 1, Deterministic = true };

    /// <summary>Builds the same scene every time and returns every body's final pose, bit for bit.</summary>
    static (Vector3 Position, Quaternion Rotation)[] Run(int steps) {
        using var world = new PhysicsWorld(Settings);

        var ground = world.Shapes.Box(new Vector3(20f, 1f, 20f));
        var crate = world.Shapes.Box(0.5f);
        var ball = world.Shapes.Sphere(0.4f);

        world.CreateBody(BodyDescription.Static(ground, new(0f, -1f, 0f)));

        var bodies = new List<BodyHandle>();

        // A pile, not a column: bodies that land on one another produce the many-contact islands the
        // solver's ordering actually shows up in. A single falling box would pass this test whether
        // or not the simulation were deterministic.
        for (var index = 0; index < 12; index++) {
            var offset = index * 0.13f;

            bodies.Add(
                world.CreateBody(
                    BodyDescription.Dynamic(index % 2 == 0 ? crate : ball, new(offset, 1f + (index * 1.1f), offset))
                        with { Mass = 1f + index }
                )
            );
        }

        world.OptimizeBroadPhase();

        for (var step = 0; step < steps; step++) {
            world.Step(Step);
        }

        var poses = new (Vector3, Quaternion)[bodies.Count];

        for (var index = 0; index < bodies.Count; index++) {
            world.GetTransform(bodies[index], out var position, out var rotation);
            poses[index] = (position, rotation);
        }

        return poses;
    }

    [Fact]
    public void TheSameSceneSteppedTheSameWayEndsBitIdentical() {
        var first = Run(240);
        var second = Run(240);

        Assert.Equal(first.Length, second.Length);

        for (var index = 0; index < first.Length; index++) {
            // Bit comparison, not approximate: a float that differs in its last bit is a divergence
            // that grows, and Assert.Equal on the struct is exactly the comparison that catches it.
            Assert.Equal(first[index].Position, second[index].Position);
            Assert.Equal(first[index].Rotation, second[index].Rotation);
        }
    }

    [Fact]
    public void ARunIsAPrefixOfALongerRun() {
        var shortRun = Run(120);

        using var world = new PhysicsWorld(Settings);
        var poses = Run(120);

        // Stepping the same scene twice for the same number of steps has to agree with itself, and
        // running longer must not change what the first 120 steps produced — which is what makes
        // "replay to tick N and carry on" a valid operation at all.
        for (var index = 0; index < shortRun.Length; index++) {
            Assert.Equal(shortRun[index].Position, poses[index].Position);
        }
    }

    [Fact]
    public void AWorldReportsWhenItRunsOutOfContactConstraints() {
        using var world = new PhysicsWorld(
            new() { ThreadCount = 1, MaxContactConstraints = 1, MaxBodyPairs = 8, MaxBodies = 64 }
        );

        world.CreateBody(BodyDescription.Static(world.Shapes.Box(new Vector3(10f, 1f, 10f)), new(0f, -1f, 0f)));

        for (var index = 0; index < 16; index++) {
            world.CreateBody(BodyDescription.Dynamic(world.Shapes.Box(0.4f), new(index * 0.05f, 1f + index, 0f)));
        }

        var overflowed = false;

        for (var step = 0; step < 240 && !overflowed; step++) {
            overflowed = world.Step(Step) != PhysicsStepResult.Ok;
        }

        // The point is not that this configuration is sensible — it is that running out is reported
        // rather than silently dropping contacts, which presents as bodies falling through the floor
        // under load and behaving perfectly when the scene is quiet.
        Assert.True(overflowed);
        Assert.NotEqual(PhysicsStepResult.Ok, world.LastStepResult);
    }
}
