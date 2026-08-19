// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Transforms;
using Vixen.Physics.Ecs;
using Vixen.Water;
using Vixen.Water.Physics;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Tests;

/// <summary>The join that was owed: a host's flag reaching <c>BuoyancyDebugDraw.Draw</c>.</summary>
/// <remarks>
///     <para>
///         <b><c>BuoyancyDebugDrawTests</c> proves the lines are right; this proves anything ever asks
///         for them.</b> The verb was registered in <c>Vixen.Rendering.Water</c>, the drawing was
///         written and tested here, and nothing joined the two — no host copied
///         <c>WaterDebug.ShowBuoyancy</c> into <c>Enabled</c> and no host called <c>Draw</c>. A
///         toggle that sets a bool nobody reads is indistinguishable, from the outside, from a toggle
///         that works over a scene with nothing floating in it.
///     </para>
///     <para>
///         ⚠ <b>The flag is a <see cref="Func{TResult}" /> here for the reason the assembly split
///         exists.</b> <c>WaterDebug</c> is in the renderer's water assembly and this one must not
///         reference it — § D1. So what is asserted is that whatever the host points
///         <c>Show</c> at reaches the geometry, which is the whole of the mechanism a game wires.
///     </para>
/// </remarks>
public sealed class BuoyancyDebugSystemTests {
    /// <summary>The flag on puts geometry in the accumulator.</summary>
    [Fact]
    public void The_flag_reaches_the_draw() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var draw = new DebugDraw();
        var debug = Wire(scene, draw, show: true);

        Raft(entities);
        debug.Step(entities);

        Assert.True(draw.Count > 0, "`water.showBuoyancy` was on and nothing was drawn");
        Assert.Equal(1, debug.Frames);
        Assert.True(debug.Draw.Enabled, "the flag never reached the draw's own switch");
    }

    /// <summary>
    ///     ⚠ The flag off draws nothing — and turns a draw that was on back off.
    /// </summary>
    /// <remarks>
    ///     <b>The negative control, and the second half of it is the one that would rot.</b> A copy
    ///     written as <c>if (flag()) Draw.Enabled = true;</c> passes the positive test and never
    ///     switches off again, so the verb becomes a latch: on for the rest of the session, whatever
    ///     the console says. The assertion is on the <em>second</em> step rather than the first for
    ///     exactly that reason.
    /// </remarks>
    [Fact]
    public void The_flag_off_switches_the_draw_back_off() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var draw = new DebugDraw();
        var on = true;
        var debug = Wire(scene, draw, () => on);

        Raft(entities);
        debug.Step(entities);

        Assert.True(draw.Count > 0, "`water.showBuoyancy` was on and nothing was drawn");

        on = false;
        draw.Clear();
        debug.Step(entities);

        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.Texts.Length);
        Assert.Equal(1, debug.Frames);
        Assert.False(debug.Draw.Enabled, "the verb latched on");
    }

    /// <summary>
    ///     ⚠ It is read every step, not captured when the system was constructed.
    /// </summary>
    /// <remarks>
    ///     A console verb is typed while the game is running, and a system that had read the flag at
    ///     registration would draw for ever or never — with nothing on screen saying which of the two
    ///     it was doing.
    /// </remarks>
    [Fact]
    public void The_flag_is_read_every_step() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var draw = new DebugDraw();
        var on = false;
        var debug = Wire(scene, draw, () => on);

        Raft(entities);
        debug.Step(entities);

        Assert.Equal(0, draw.Count);

        on = true;
        debug.Step(entities);

        Assert.True(draw.Count > 0, "the flag was cached rather than read");
    }

    /// <summary>
    ///     ⚠ With no flag wired the draw's own switch is honoured, which is what a headless build has.
    /// </summary>
    /// <remarks>
    ///     A dedicated server links this assembly and not the renderer's, so there is no
    ///     <c>WaterDebug</c> to point at — and overwriting <c>Enabled</c> with a default of false
    ///     would make the draw unreachable from such a host at all.
    /// </remarks>
    [Fact]
    public void With_no_flag_the_draws_own_switch_is_honoured() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var draw = new DebugDraw();
        var debug = new BuoyancyDebugSystem(new(scene, new Still()), draw);

        Assert.Null(debug.Show);

        Raft(entities);
        debug.Step(entities);

        Assert.Equal(0, draw.Count);

        debug.Draw.Enabled = true;
        debug.Step(entities);

        Assert.True(draw.Count > 0, "the draw's own switch was overwritten by an absent flag");
    }

    /// <summary>
    ///     ⚠ <c>PreRender</c>, and both neighbours decide it.
    /// </summary>
    /// <remarks>
    ///     <c>TransformSystem</c> writes the <c>WorldTransform</c> the pontoons are placed from in
    ///     <c>LateUpdate</c>, so anything earlier draws the spheres where the body was; the
    ///     accumulator is drained during <c>Render</c>, so anything later draws into a frame already
    ///     recorded. Neither failure says anything, which is why the attribute is asserted rather
    ///     than trusted — <c>BuoyancySystemTests.ItIsDeclaredBetweenTheSyncAndTheStep</c>'s reason
    ///     exactly, one system along.
    /// </remarks>
    [Fact]
    public void It_is_declared_in_prerender() {
        var group = Attribute.GetCustomAttribute(typeof(BuoyancyDebugSystem), typeof(UpdateInGroupAttribute));

        Assert.Equal(SystemPhase.PreRender, Assert.IsType<UpdateInGroupAttribute>(group).Phase);
    }

    static BuoyancyDebugSystem Wire(PhysicsScene scene, DebugDraw draw, bool show) => Wire(scene, draw, () => show);

    static BuoyancyDebugSystem Wire(PhysicsScene scene, DebugDraw draw, Func<bool> show) =>
        new(new BuoyancySystem(scene, new Still()), draw) { Show = show };

    /// <summary>A raft the last step floated, which is what the draw reads.</summary>
    static void Raft(EcsWorld entities) {
        var entity = entities.Create(LocalTransform.At(Vector3.Zero));

        entities.Add(entity, BuoyancyBody.Raft(halfLength: 2f, halfWidth: 1f, radius: 0.5f));
        entities.Add(entity, new BuoyancyState { Wet = 4, Total = 4, Submerged = 0.4f, SurfaceHeight = 0.2f });
        entities.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
    }

    /// <summary>Still water at zero, everywhere — the solver is not what is under test here.</summary>
    sealed class Still : IWaterSurface {
        public float WaterTime => 0f;

        public WaterQuery? QueryAt(Vector2 position) => null;
    }
}
