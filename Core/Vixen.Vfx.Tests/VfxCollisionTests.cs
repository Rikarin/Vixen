// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     Colliders: a plane and a sphere, with a bounce and a friction — doc 06 § VFX pipeline.
/// </summary>
/// <remarks>
///     Every test here drives <see cref="VfxSimulation" /> directly rather than through a
///     <see cref="VfxSystem" />, because what is under test is one operation's arithmetic and a
///     system would wrap it in spawning, ageing and reaping. The GPU transcription of the same
///     arithmetic is checked where the rest of the emitted source is.
/// </remarks>
public class VfxCollisionTests {
    /// <summary>One particle, placed and moving, with nothing but the operations under test.</summary>
    static ParticleBuffer One(Vector3 position, Vector3 velocity) {
        var buffer = new ParticleBuffer(
            VfxAttribute.Position | VfxAttribute.Velocity | VfxAttribute.Age | VfxAttribute.Lifetime,
            1
        );

        buffer.Spawn(1, out _);
        buffer.Position[0] = position;
        buffer.Velocity[0] = velocity;
        buffer.Lifetime[0] = 1000f;

        return buffer;
    }

    /// <summary>The ground: the plane y = 0, with its normal pointing up.</summary>
    static VfxOperation Ground(float bounce, float friction) =>
        new(VfxOpcode.CollidePlane, new Vector4(0f, 1f, 0f, 0f)) { B = new(bounce, friction, 0f, 0f) };

    [Fact]
    public void A_particle_that_went_through_the_floor_is_put_back_on_it() {
        using var buffer = One(new(0f, -0.5f, 0f), new(0f, -10f, 0f));

        VfxSimulation.Update(buffer, [Ground(0f, 0f)], 1f / 60f);

        Assert.Equal(0f, buffer.Position[0].Y);
    }

    [Fact]
    public void A_particle_above_the_floor_is_left_alone() {
        using var buffer = One(new(0f, 2f, 0f), new(0f, -10f, 0f));

        VfxSimulation.Update(buffer, [Ground(1f, 1f)], 1f / 60f);

        Assert.Equal(new Vector3(0f, 2f, 0f), buffer.Position[0]);
        Assert.Equal(new Vector3(0f, -10f, 0f), buffer.Velocity[0]);
    }

    [Fact]
    public void A_perfect_bounce_returns_the_approach_speed() {
        using var buffer = One(new(0f, -0.5f, 0f), new(0f, -10f, 0f));

        VfxSimulation.Update(buffer, [Ground(1f, 0f)], 1f / 60f);

        Assert.Equal(10f, buffer.Velocity[0].Y);
    }

    [Fact]
    public void No_bounce_stops_the_approach_dead() {
        using var buffer = One(new(0f, -0.5f, 0f), new(0f, -10f, 0f));

        VfxSimulation.Update(buffer, [Ground(0f, 0f)], 1f / 60f);

        Assert.Equal(0f, buffer.Velocity[0].Y);
    }

    /// <summary>Bounce is the normal component and friction is the tangential one, separately.</summary>
    /// <remarks>
    ///     The distinction is the point of splitting the velocity at all: reflecting the whole vector
    ///     and scaling it would make a particle dropped straight down and one skimming along the
    ///     floor lose the same fraction of their speed, which is neither of the two words.
    /// </remarks>
    [Fact]
    public void Friction_scrubs_the_slide_and_leaves_the_bounce_alone() {
        using var buffer = One(new(0f, -0.1f, 0f), new(8f, -10f, 0f));

        VfxSimulation.Update(buffer, [Ground(0.5f, 0.25f)], 1f / 60f);

        Assert.Equal(6f, buffer.Velocity[0].X);
        Assert.Equal(5f, buffer.Velocity[0].Y);
    }

    /// <summary>
    ///     A particle already leaving is not bounced again, which is what stops a collider buzzing.
    /// </summary>
    /// <remarks>
    ///     The case arises constantly: a particle pushed onto the surface last step is exactly on it
    ///     this step, and a collider that reflected everything it touched would flip its velocity
    ///     every frame and hold it there vibrating.
    /// </remarks>
    [Fact]
    public void A_particle_already_leaving_keeps_going() {
        using var buffer = One(new(0f, -0.001f, 0f), new(0f, 4f, 0f));

        VfxSimulation.Update(buffer, [Ground(1f, 0f)], 1f / 60f);

        Assert.Equal(4f, buffer.Velocity[0].Y);
    }

    [Fact]
    public void A_particle_inside_a_sphere_is_pushed_out_to_its_surface() {
        using var buffer = One(new(0.5f, 0f, 0f), new(-4f, 0f, 0f));

        VfxSimulation.Update(
            buffer,
            [new(VfxOpcode.CollideSphere, new Vector4(0f, 0f, 0f, 2f)) { B = new(1f, 0f, 0f, 0f) }],
            1f / 60f
        );

        Assert.Equal(new Vector3(2f, 0f, 0f), buffer.Position[0]);
        Assert.Equal(4f, buffer.Velocity[0].X);
    }

    [Fact]
    public void A_particle_outside_a_sphere_is_left_alone() {
        using var buffer = One(new(5f, 0f, 0f), new(-4f, 0f, 0f));

        VfxSimulation.Update(
            buffer,
            [new(VfxOpcode.CollideSphere, new Vector4(0f, 0f, 0f, 2f)) { B = new(1f, 0f, 0f, 0f) }],
            1f / 60f
        );

        Assert.Equal(new Vector3(5f, 0f, 0f), buffer.Position[0]);
    }

    /// <summary>
    ///     A particle exactly at the centre has no direction to be pushed out along, and normalizing
    ///     its zero offset is how a collider fills a system with NaNs.
    /// </summary>
    [Fact]
    public void A_particle_at_the_very_centre_of_a_sphere_does_not_become_a_nan() {
        using var buffer = One(Vector3.Zero, Vector3.Zero);

        VfxSimulation.Update(
            buffer,
            [new(VfxOpcode.CollideSphere, new Vector4(0f, 0f, 0f, 2f)) { B = new(1f, 0f, 0f, 0f) }],
            1f / 60f
        );

        var position = buffer.Position[0];

        Assert.False(float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z));
        Assert.Equal(2f, position.Length(), 5);
    }

    /// <summary>A collider declares both attributes, so a graph with one allocates both.</summary>
    /// <remarks>
    ///     The velocity initializer is not decoration: a collider reads velocity, and
    ///     <see cref="VfxCompiledGraph.Compile" /> refuses a graph whose updaters read an attribute
    ///     no initializer ever wrote. That rule is older than this opcode and applies to
    ///     <see cref="VfxOpcode.Integrate" /> the same way.
    /// </remarks>
    [Fact]
    public void A_collider_makes_a_graph_keep_position_and_velocity() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [
                new(VfxOpcode.SetPosition, new Vector4(0f, 4f, 0f, 0f)),
                new(VfxOpcode.SetVelocity, Vector4.Zero)
            ],
            [Ground(0.5f, 0.1f)],
            4
        );

        Assert.True((graph.Attributes & VfxAttribute.Position) != 0);
        Assert.True((graph.Attributes & VfxAttribute.Velocity) != 0);
    }
}
