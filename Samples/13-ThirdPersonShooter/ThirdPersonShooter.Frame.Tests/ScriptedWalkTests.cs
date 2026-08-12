// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Players;
using Xunit;

namespace Vixen.Samples.ThirdPersonShooter.Tests;

/// <summary>The scripted walk: what it parses, and — the load-bearing one — what it counts time by.</summary>
/// <remarks>
///     <para>
///         <b>A harness that reintroduces a wall clock is worse than no harness</b>: it produces two
///         pictures of two different moments and calls the difference a rendering defect. So the
///         tests here were checked against a sabotaged copy — one given a
///         <c>Stopwatch.StartNew()</c>, exactly the second clock that was found driving
///         <c>TerrainSceneSource</c>'s grass wind — and the result is worth writing down, because
///         the obvious guard is the one that does not work.
///     </para>
///     <para>
///         ⚠ <b><see cref="Two_runs_of_one_script_walk_identically" /> passed against the stopwatch.</b>
///         So did <see cref="A_yaw_rate_turns_by_its_rate_over_the_legs_time" />. Both drive a few
///         dozen samples in a few microseconds of wall time, so a stopwatch reads about zero
///         throughout and every sample lands in the first leg — the two runs agree with each other
///         perfectly and both are wrong. "Two runs match" is a necessary property and a nearly
///         useless test: a machine fast enough to make the bug invisible is what a unit test is.
///     </para>
///     <para>
///         <b>What does catch it is asking for time that did not come through the parameter.</b>
///         <see cref="Wall_time_passing_does_not_advance_the_script" /> lets real milliseconds go by
///         and then hands in a delta of zero; <see cref="Elapsed_is_the_sum_of_the_deltas_and_not_a_wall_clock" />
///         demands that sixty sixtieths are a second. Both fail against the sabotage, and the second
///         one is the reason the accumulator is a <c>double</c>.
///     </para>
/// </remarks>
public sealed class ScriptedWalkTests {
    const float Step = 1f / 60f;

    [Fact]
    public void A_bare_duration_walks_forward() {
        var walk = ScriptedWalk.Parse("14");

        Assert.Equal(14f, walk.Duration);
        Assert.Equal(new ScriptedWalk.Leg(14f), Assert.Single(walk.Legs));
    }

    [Fact]
    public void Legs_are_ordered_and_each_field_is_optional() {
        var walk = ScriptedWalk.Parse("2:0:0:90; 6:1:0.5:-20:5");

        Assert.Equal(8f, walk.Duration);
        Assert.Equal(new ScriptedWalk.Leg(2f, 0f, 0f, 90f), walk.Legs[0]);
        Assert.Equal(new ScriptedWalk.Leg(6f, 1f, 0.5f, -20f, 5f), walk.Legs[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("4:1:0:0:0:0")]
    [InlineData("4:sideways")]
    public void A_malformed_script_throws_rather_than_standing_still(string spec) =>
        Assert.ThrowsAny<Exception>(() => ScriptedWalk.Parse(spec));

    /// <summary>The clock is the deltas handed in and nothing else.</summary>
    /// <remarks>
    ///     ⚠ Sixty samples of a sixtieth is one second <em>of simulated time</em>, whatever the wall
    ///     clock did while the test ran. A stopwatch in <c>Sample</c> would make this assertion a
    ///     function of how fast xunit scheduled it.
    /// </remarks>
    [Fact]
    public void Elapsed_is_the_sum_of_the_deltas_and_not_a_wall_clock() {
        var walk = ScriptedWalk.Parse("1");
        var rotation = ControlRotation.Default;
        var intent = default(MoveIntent);

        for (var frame = 0; frame < 60; frame++) {
            walk.Sample(ref rotation, ref intent, Step);
        }

        Assert.Equal(1f, walk.Elapsed, 4);
        Assert.True(walk.Finished);
    }

    /// <summary>Real time going by moves nothing, because real time is not an input here.</summary>
    /// <remarks>
    ///     ⚠ <b>A delta of zero and a sleep is the whole test</b>, and it is not a flaky one: a
    ///     correct implementation is a pure function of the deltas, so fifty milliseconds or fifty
    ///     seconds of wall time both leave <see cref="ScriptedWalk.Elapsed" /> at zero. An
    ///     implementation with a clock in it advances by however long the sleep took, which is past
    ///     the first leg's ten milliseconds by a factor of five even on a machine that oversleeps.
    /// </remarks>
    [Fact]
    public void Wall_time_passing_does_not_advance_the_script() {
        var walk = ScriptedWalk.Parse("0.01:1:0:0;100:0:1:0");
        var rotation = ControlRotation.Default;
        var intent = default(MoveIntent);

        Thread.Sleep(50);
        walk.Sample(ref rotation, ref intent, 0f);

        Assert.Equal(0d, walk.Elapsed);
        Assert.Equal(new Vector2(0f, 1f), intent.Move);
    }

    [Fact]
    public void Two_runs_of_one_script_walk_identically() {
        var (firstAim, firstMove) = Drive(ScriptedWalk.Parse("0.5:1:0:45;0.5:0:1:-45:10"), 60);
        var (secondAim, secondMove) = Drive(ScriptedWalk.Parse("0.5:1:0:45;0.5:0:1:-45:10"), 60);

        Assert.Equal(firstAim, secondAim);
        Assert.Equal(firstMove, secondMove);
    }

    /// <summary>A turn is a rate, so the total is the rate times the simulated time.</summary>
    [Fact]
    public void A_yaw_rate_turns_by_its_rate_over_the_legs_time() {
        var walk = ScriptedWalk.Parse("2:0:0:45");
        var rotation = ControlRotation.Default;
        var intent = default(MoveIntent);

        for (var frame = 0; frame < 120; frame++) {
            walk.Sample(ref rotation, ref intent, Step);
        }

        Assert.Equal(90f, MathUtil.RadiansToDegrees(rotation.Yaw), 2);
    }

    /// <summary>Past the end the player stands still and keeps looking where it was looking.</summary>
    [Fact]
    public void The_script_running_out_clears_the_intent_and_keeps_the_aim() {
        var walk = ScriptedWalk.Parse("0.1:1:0:90");
        var rotation = ControlRotation.Default;
        var intent = default(MoveIntent);

        for (var frame = 0; frame < 60; frame++) {
            walk.Sample(ref rotation, ref intent, Step);
        }

        var aim = rotation.Yaw;

        walk.Sample(ref rotation, ref intent, Step);

        Assert.Equal(Vector2.Zero, intent.Move);
        Assert.Equal(aim, rotation.Yaw);
        Assert.Equal(aim, intent.Yaw);
    }

    /// <summary>Forward is <c>MoveIntent.Move.Y</c>, which is what walks the player where they look.</summary>
    /// <remarks>
    ///     Asserted through <c>WorldDirection</c> rather than on the field, because the field's sign
    ///     convention is the one <c>ActionPlayerInput</c> has a fourteen-line comment about: a screen
    ///     vector's Y is up-negative and an intent's Y is forward-positive, and a harness that copied
    ///     the wrong one walks backwards out of the wrong gate while every counter agrees it moved.
    /// </remarks>
    [Fact]
    public void Forward_is_forward_in_the_world() {
        var walk = ScriptedWalk.Parse("1");
        var rotation = ControlRotation.Default;
        var intent = default(MoveIntent);

        walk.Sample(ref rotation, ref intent, Step);

        var direction = intent.WorldDirection();

        Assert.Equal(0f, direction.X, 5);
        Assert.Equal(-1f, direction.Z, 5);
    }

    [Fact]
    public void A_script_writes_back_out_as_one_it_parses() {
        var walk = ScriptedWalk.Parse("2:0:0:90;6");

        Assert.Equal(walk.Legs, ScriptedWalk.Parse(walk.ToString()).Legs);
    }

    static (float Aim, Vector2 Move) Drive(ScriptedWalk walk, int frames) {
        var rotation = ControlRotation.Default;
        var intent = default(MoveIntent);
        var move = Vector2.Zero;

        for (var frame = 0; frame < frames; frame++) {
            walk.Sample(ref rotation, ref intent, Step);
            move += intent.Move;
        }

        return (rotation.Yaw, move);
    }
}
