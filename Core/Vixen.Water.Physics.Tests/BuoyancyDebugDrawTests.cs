// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Transforms;
using Vixen.Physics.Ecs;
using Vixen.Water;
using Vixen.Water.Physics;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Tests;

/// <summary>
///     `water.showBuoyancy` — [docs/plan/35 § Part 2 § Debugging]'s sixth verb.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The only one of the six that is not in <c>Vixen.Rendering.Water</c>.</b> The pontoons
///         and the forces are this assembly's and the renderer must not reference it — § D1 puts the
///         physics join in its own assembly precisely so that nothing linking Jolt is linked by a
///         renderer. So the flag lives with the console verb and the drawing lives with the data,
///         and a host copies one into the other.
///     </para>
///     <para>
///         What is asserted is the thing that is silent when it is wrong: a verb that is on and draws
///         nothing is indistinguishable from a verb nobody typed.
///     </para>
/// </remarks>
public sealed class BuoyancyDebugDrawTests {
    /// <summary>Switched off, it draws nothing at all.</summary>
    /// <remarks>
    ///     The negative control the rest need. A draw that emitted geometry unconditionally would
    ///     pass every other assertion here, and would cost a scene with fifty crates in it four
    ///     hundred spheres a frame that nobody asked for.
    /// </remarks>
    [Fact]
    public void Switched_off_it_draws_nothing() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var draw = new DebugDraw();

        Raft(entities, wet: 4);

        new BuoyancyDebugDraw().Draw(draw, entities, new BuoyancySystem(scene, new Still()));

        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.Texts.Length);
    }

    /// <summary>Switched on, every pontoon is a sphere and the state is a label.</summary>
    /// <remarks>
    ///     ⚠ <b>"2/4 wet" is the whole diagnosis</b>, which is why the label is asserted separately
    ///     from the spheres. A boat that sits too low, launches out of the lake or drifts sideways is
    ///     a bug with no visible cause; the count is the difference between "buoyancy is broken" and
    ///     "two of four pontoons are dry".
    /// </remarks>
    [Fact]
    public void Switched_on_it_draws_a_sphere_per_pontoon_and_the_count() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var draw = new DebugDraw();

        Raft(entities, wet: 2);

        new BuoyancyDebugDraw { Enabled = true }.Draw(draw, entities, new BuoyancySystem(scene, new Still()));

        Assert.True(draw.Count > 0, "the overlay drew no pontoons");

        var label = Assert.Single(draw.Texts.ToArray());

        Assert.Contains("2/4", label.Text, StringComparison.Ordinal);
    }

    /// <summary>A body with no pontoons is skipped rather than drawn as a point.</summary>
    /// <remarks>
    ///     It is what an entity part-way through being authored looks like, and a sphere of radius
    ///     zero at its origin would read as a pontoon that is somehow there and infinitely small.
    /// </remarks>
    [Fact]
    public void A_body_with_no_pontoons_draws_nothing() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var draw = new DebugDraw();
        var entity = entities.Create(LocalTransform.At(Vector3.Zero));

        entities.Add(entity, BuoyancyBody.Default);
        entities.Add(entity, new BuoyancyState { Wet = 0, Total = 0 });
        entities.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });

        new BuoyancyDebugDraw { Enabled = true }.Draw(draw, entities, new BuoyancySystem(scene, new Still()));

        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.Texts.Length);
    }

    static void Raft(EcsWorld entities, int wet) {
        var entity = entities.Create(LocalTransform.At(Vector3.Zero));

        entities.Add(entity, BuoyancyBody.Raft(halfLength: 2f, halfWidth: 1f, radius: 0.5f));
        entities.Add(entity, new BuoyancyState { Wet = wet, Total = 4, Submerged = 0.4f, SurfaceHeight = 0.2f });
        entities.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
    }

    /// <summary>Still water at zero, everywhere — the solver is not what is under test here.</summary>
    sealed class Still : IWaterSurface {
        public float WaterTime => 0f;

        public WaterQuery? QueryAt(Vector2 position) => null;
    }
}
