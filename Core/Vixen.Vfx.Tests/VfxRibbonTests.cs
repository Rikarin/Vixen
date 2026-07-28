// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     The mesh and ribbon renderers — doc 06 § VFX pipeline's other two.
/// </summary>
/// <remarks>
///     <para>
///         A mesh renderer is a transform per particle rather than geometry, which makes it the
///         smaller of the two: nothing about it needs particles to know about each other.
///     </para>
///     <para>
///         A ribbon is the one renderer that does. Which strip a particle belongs to is a custom
///         attribute — the first real consumer of them — and where it sits within one is its age,
///         which the runtime already keeps. That is why the ribbon waited for custom attributes and
///         not for anything in the storage layer.
///     </para>
/// </remarks>
public class VfxRibbonTests {
    static readonly VfxCamera Camera = VfxCamera.Looking(new(0f, 0f, -10f), Vector3.UnitZ, Vector3.UnitY);

    // --- Mesh instances ----------------------------------------------------

    /// <summary>
    ///     An instance is a world matrix and a colour, with the particle's position in the fourth
    ///     column and its size as a uniform scale.
    /// </summary>
    [Fact]
    public void An_instance_carries_the_particle_where_a_shader_expects_it() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetPosition, new Vector4(3f, 4f, 5f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(2f, 2f, 0f, 0f)),
                new(VfxOpcode.SetColour, new Vector4(0.25f, 0.5f, 0.75f, 1f))
            ],
            [],
            8,
            VfxRenderer.Instanced()
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        var builder = new VfxGeometryBuilder();
        var instances = new ParticleInstance[4];

        Assert.Equal(1, builder.BuildInstances(system, Camera, instances));

        // Translation in the w lanes, which is the packing that makes a transformed position one dot
        // product per axis.
        Assert.Equal(3f, instances[0].Row0.W);
        Assert.Equal(4f, instances[0].Row1.W);
        Assert.Equal(5f, instances[0].Row2.W);

        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), instances[0].Colour);

        // Uniform scale of two: every basis column is that long, and they stay orthogonal.
        var right = new Vector3(instances[0].Row0.X, instances[0].Row1.X, instances[0].Row2.X);
        var up = new Vector3(instances[0].Row0.Y, instances[0].Row1.Y, instances[0].Row2.Y);
        var forward = new Vector3(instances[0].Row0.Z, instances[0].Row1.Z, instances[0].Row2.Z);

        Assert.Equal(2f, right.Length(), 4);
        Assert.Equal(2f, up.Length(), 4);
        Assert.Equal(2f, forward.Length(), 4);
        Assert.Equal(0f, Vector3.Dot(right, up), 4);
        Assert.Equal(0f, Vector3.Dot(up, forward), 4);
    }

    /// <summary>
    ///     A velocity-aligned mesh points its local +Y along the velocity — the same axis a streak
    ///     stretches along.
    /// </summary>
    /// <remarks>
    ///     One convention across both renderers is worth more than each being locally reasonable: a
    ///     model authored for a streak is a model that works for an instanced spark.
    /// </remarks>
    [Fact]
    public void A_velocity_aligned_mesh_points_its_own_up_along_the_velocity() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetVelocity, new Vector4(0f, 0f, 7f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One)
            ],
            [],
            8,
            VfxRenderer.Instanced(VfxBillboardAlignment.Velocity)
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        var instances = new ParticleInstance[1];
        new VfxGeometryBuilder().BuildInstances(system, Camera, instances);

        var up = new Vector3(instances[0].Row0.Y, instances[0].Row1.Y, instances[0].Row2.Y);

        Assert.Equal(Vector3.UnitZ, Vector3.Normalize(up));
    }

    // --- Ribbons -----------------------------------------------------------

    /// <summary>A trail: particles sharing a strip, at increasing ages.</summary>
    static VfxSystem Trail(int strips, int perStrip) {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(strips * perStrip)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f)),
                new(VfxOpcode.SetCustom, Vector4.Zero)
            ],
            [],
            256,
            VfxRenderer.Ribbon(0),
            [new("strand", VfxAttributeType.Float)]
        );

        var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        // Placed by hand: what is being tested is the strip-building, and a spawner that produced the
        // shape would be testing the spawner.
        var positions = system.Particles.Position;
        var ages = system.Particles.Age;
        var strands = system.Particles.Custom(0);

        for (var strip = 0; strip < strips; strip++) {
            for (var along = 0; along < perStrip; along++) {
                var index = (strip * perStrip) + along;

                positions[index] = new(along, strip * 10f, 0f);
                strands[index] = strip;

                // Newest last: the ribbon runs from the oldest particle, so the ages descend along it.
                ages[index] = perStrip - along;
            }
        }

        return system;
    }

    [Fact]
    public void A_ribbon_makes_two_triangles_between_each_pair() {
        using var system = Trail(1, 5);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[64];
        var indices = new uint[256];

        Assert.Equal(5, builder.BuildRibbons(system, Camera, vertices, indices, out var indexCount));

        // Five particles, four lengths of ribbon, two triangles each.
        Assert.Equal(4 * VfxGeometryBuilder.IndicesPerRibbonSegment, indexCount);
    }

    /// <summary>Two strips are two ribbons, and nothing joins them.</summary>
    /// <remarks>
    ///     The failure this catches is the one that looks like a bug in the effect rather than in the
    ///     renderer: a triangle spanning the gap between two trails, which is a bright sheet across
    ///     the scene wherever two emitters happen to be far apart.
    /// </remarks>
    [Fact]
    public void Two_strips_are_never_joined() {
        using var system = Trail(2, 4);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[64];
        var indices = new uint[256];

        builder.BuildRibbons(system, Camera, vertices, indices, out var indexCount);

        // Two ribbons of four: three lengths each, not seven.
        Assert.Equal(6 * VfxGeometryBuilder.IndicesPerRibbonSegment, indexCount);

        // And no triangle reaches from one strip's vertices to the other's. The strips are ten metres
        // apart in y, so a spanning triangle is one whose corners differ by that.
        for (var index = 0; index < indexCount; index += 3) {
            var a = vertices[indices[index]].Position.Y;
            var b = vertices[indices[index + 1]].Position.Y;
            var c = vertices[indices[index + 2]].Position.Y;

            Assert.True(MathF.Abs(a - b) < 5f && MathF.Abs(b - c) < 5f);
        }
    }

    /// <summary>A ribbon runs from its oldest particle to its newest, whatever order they are stored in.</summary>
    [Fact]
    public void A_ribbon_runs_oldest_first() {
        using var system = Trail(1, 4);

        // Shuffled in the buffer, so the order the strip comes out in is the sort's doing and not the
        // storage's.
        var positions = system.Particles.Position;
        var ages = system.Particles.Age;

        (positions[0], positions[3]) = (positions[3], positions[0]);
        (ages[0], ages[3]) = (ages[3], ages[0]);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[32];
        var indices = new uint[128];

        builder.BuildRibbons(system, Camera, vertices, indices, out _);

        // x increases along the trail and age decreases, so the strip comes out in increasing x.
        for (var particle = 0; particle + 1 < 4; particle++) {
            var here = vertices[particle * VfxGeometryBuilder.VerticesPerRibbonParticle].Position.X;
            var next = vertices[(particle + 1) * VfxGeometryBuilder.VerticesPerRibbonParticle].Position.X;

            Assert.True(next > here, $"Particle {particle + 1} is at {next}, behind {here}.");
        }
    }

    /// <summary>The texture runs from one end of a ribbon to the other.</summary>
    [Fact]
    public void The_texture_stretches_along_the_whole_ribbon() {
        using var system = Trail(1, 5);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[32];
        var indices = new uint[128];

        builder.BuildRibbons(system, Camera, vertices, indices, out _);

        Assert.Equal(0f, vertices[0].Texture.X);
        Assert.Equal(1f, vertices[4 * VfxGeometryBuilder.VerticesPerRibbonParticle].Texture.X);

        // And across it, which is what gives the strip a width in the texture rather than a line.
        Assert.Equal(0f, vertices[0].Texture.Y);
        Assert.Equal(1f, vertices[1].Texture.Y);
    }

    /// <summary>
    ///     A ribbon of one particle draws no triangles — a strip needs two points to have a direction.
    /// </summary>
    [Fact]
    public void A_ribbon_of_one_draws_nothing() {
        using var system = Trail(4, 1);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[32];
        var indices = new uint[128];

        Assert.Equal(4, builder.BuildRibbons(system, Camera, vertices, indices, out var indexCount));
        Assert.Equal(0, indexCount);
    }

    /// <summary>The strip is square to the view, so a ribbon is never seen edge-on as a line.</summary>
    [Fact]
    public void A_ribbon_faces_the_camera_along_its_length() {
        using var system = Trail(1, 3);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[32];
        var indices = new uint[128];

        builder.BuildRibbons(system, Camera, vertices, indices, out _);

        for (var particle = 0; particle < 3; particle++) {
            var left = vertices[particle * VfxGeometryBuilder.VerticesPerRibbonParticle].Position;
            var right = vertices[(particle * VfxGeometryBuilder.VerticesPerRibbonParticle) + 1].Position;
            var across = right - left;

            // The trail runs along x and the camera is down -z, so the width is along y.
            Assert.Equal(1f, across.Length(), 4);
            Assert.Equal(0f, Vector3.Dot(Vector3.Normalize(across), Vector3.UnitX), 4);
        }
    }

    /// <summary>A ribbon renderer naming a slot the graph does not declare is refused.</summary>
    [Fact]
    public void A_ribbon_with_no_strip_attribute_is_refused() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(2)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One)
            ],
            [],
            8,
            VfxRenderer.Ribbon(0)
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        var builder = new VfxGeometryBuilder();

        Assert.Throws<InvalidOperationException>(
            () => builder.BuildRibbons(system, Camera, new ParticleVertex[8], new uint[32], out _)
        );
    }

    /// <summary>Drawing a ribbon is what makes the graph keep the ages it is ordered by.</summary>
    [Fact]
    public void A_ribbon_renderer_declares_that_it_reads_age() {
        Assert.True((VfxRenderer.Ribbon(0).Reads & VfxAttribute.Age) != 0);
        Assert.True((VfxRenderer.Billboard.Reads & VfxAttribute.Age) == 0);
    }
}
