// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Testing;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Vfx.Tests;

/// <summary>
///     Turning particles into quads: the last step that is arithmetic, and so the last one that can be
///     checked against a number rather than against a screenshot.
/// </summary>
public sealed class VfxGeometryTests {
    /// <summary>A camera ten metres back down -Z, looking at the origin, right-handed and level.</summary>
    static VfxCamera Camera => new(new(0f, 0f, -10f), Vector3.UnitX, Vector3.UnitY);

    /// <summary>One particle at the origin, a metre across, white, still.</summary>
    static VfxCompiledGraph OneParticle(VfxRenderer renderer, Vector4? velocity = null) {
        var initializers = new List<VfxOperation> {
            new(VfxOpcode.SetPosition, Vector4.Zero),
            new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
            new(VfxOpcode.SetColour, Vector4.One),
            new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
        };

        if (velocity is { } speed) {
            initializers.Add(new(VfxOpcode.SetVelocity, speed));
        }

        return VfxCompiledGraph.Compile([VfxSpawner.Burst(1)], [.. initializers], [], 64, renderer);
    }

    static VfxSystem Started(VfxCompiledGraph graph) {
        var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        return system;
    }

    [Fact]
    public void ACameraFacingQuadLiesInThePlaneTheCameraSees() {
        using var system = Started(OneParticle(VfxRenderer.Billboard));

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        Assert.Equal(1, builder.Build(system, Camera, vertices));

        // A metre across, centred on the origin, square to a camera whose right is +X and up is +Y.
        Assert.Equal(new Vector3(-0.5f, -0.5f, 0f), vertices[0].Position);
        Assert.Equal(new Vector3(0.5f, -0.5f, 0f), vertices[1].Position);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0f), vertices[2].Position);
        Assert.Equal(new Vector3(-0.5f, 0.5f, 0f), vertices[3].Position);
    }

    [Fact]
    public void TheCornersCarryTheTextureAndTheColour() {
        using var system = Started(OneParticle(VfxRenderer.Billboard));

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        builder.Build(system, Camera, vertices);

        Assert.Equal(new Vector2(0f, 0f), vertices[0].Texture);
        Assert.Equal(new Vector2(1f, 0f), vertices[1].Texture);
        Assert.Equal(new Vector2(1f, 1f), vertices[2].Texture);
        Assert.Equal(new Vector2(0f, 1f), vertices[3].Texture);

        foreach (var vertex in vertices) {
            Assert.Equal(Vector4.One, vertex.Colour);
        }
    }

    [Fact]
    public void TheQuadTurnsWithTheCamera() {
        using var system = Started(OneParticle(VfxRenderer.Billboard));

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        // Looking down the X axis instead: right is now -Z, so the quad lies in the YZ plane.
        builder.Build(system, new VfxCamera(new(10f, 0f, 0f), -Vector3.UnitZ, Vector3.UnitY), vertices);

        foreach (var vertex in vertices) {
            Assert.Equal(0f, vertex.Position.X, 0.0001f);
        }
    }

    [Fact]
    public void AVelocityAlignedQuadStretchesAlongItsVelocity() {
        // Ten metres a second up, stretched a tenth of a metre per metre per second: half a metre of
        // size plus half of ten times a tenth, so the quad reaches a metre above the centre.
        using var system = Started(OneParticle(VfxRenderer.Streak(0.1f), new Vector4(0f, 10f, 0f, 0f)));

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        builder.Build(system, Camera, vertices);

        var top = MathF.Max(vertices[2].Position.Y, vertices[3].Position.Y);
        var side = MathF.Max(MathF.Abs(vertices[1].Position.X), MathF.Abs(vertices[0].Position.X));

        Assert.Equal(1f, top, 0.01f);
        Assert.Equal(0.5f, side, 0.01f);
    }

    [Fact]
    public void AStreakSeenEndOnDoesNotCollapse() {
        // Moving straight at the camera, so the axis to turn about points at it and the cross product
        // that would give the quad its width vanishes. It has to fall back to something, or a spark
        // coming towards the viewer becomes a line of zero width.
        using var system = Started(OneParticle(VfxRenderer.Streak(), new Vector4(0f, 0f, -10f, 0f)));

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        builder.Build(system, Camera, vertices);

        var width = Vector3.Distance(vertices[0].Position, vertices[1].Position);

        Assert.True(width > 0.9f, $"The quad is {width} across, which is a streak that has vanished.");
    }

    [Fact]
    public void AFixedAxisQuadKeepsItsAxis() {
        var renderer = new VfxRenderer(Alignment: VfxBillboardAlignment.FixedAxis, Axis: Vector3.UnitY);

        using var system = Started(OneParticle(renderer));

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        // A camera above and to one side. The quad may turn about Y to face it, but its up must stay Y.
        builder.Build(system, new VfxCamera(new(6f, 8f, -6f), Vector3.UnitX, Vector3.UnitY), vertices);

        var up = vertices[3].Position - vertices[0].Position;

        Assert.Equal(0f, up.X, 0.0001f);
        Assert.Equal(0f, up.Z, 0.0001f);
        Assert.Equal(1f, up.Y, 0.0001f);
    }

    [Fact]
    public void RollTurnsTheQuadInItsOwnPlane() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetRotation, new Vector4(MathF.PI / 2f, MathF.PI / 2f, 0f, 0f)),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            64,
            VfxRenderer.Billboard
        );

        using var system = Started(graph);

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        builder.Build(system, Camera, vertices);

        // A quarter turn takes the bottom-left corner to where the bottom-right one was, and the quad
        // stays in the camera's plane throughout.
        Assert.Equal(new Vector3(0.5f, -0.5f, 0f), vertices[0].Position, Close);
        Assert.Equal(0f, vertices[2].Position.Z, 0.0001f);
    }

    [Fact]
    public void DepthSortingDrawsTheFurthestFirst() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(32)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-5f, -5f, -5f, 0f)) { B = new(5f, 5f, 5f, 0f) },
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.1f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            64,
            VfxRenderer.SortedBillboard
        );

        using var system = Started(graph);

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[32 * 4];

        var count = builder.Build(system, Camera, vertices);

        Assert.Equal(32, count);

        var previous = float.MaxValue;

        for (var slot = 0; slot < count; slot++) {
            var distance = Vector3.Distance(system.Particles.Position[builder.Order[slot]], Camera.Position);

            Assert.True(distance <= previous + 0.0001f, $"Particle {slot} is nearer than the one before it, at {distance} against {previous}.");
            previous = distance;
        }
    }

    [Fact]
    public void AnUnsortedRendererLeavesTheOrderAlone() {
        using var system = Started(OneParticle(VfxRenderer.Billboard));

        var builder = new VfxGeometryBuilder();
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[4];

        builder.Build(system, Camera, vertices);

        Assert.Equal(0, builder.Order[0]);
    }

    [Fact]
    public void AShortBufferWritesWholeParticles() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(10)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            64,
            VfxRenderer.Billboard
        );

        using var system = Started(graph);

        var builder = new VfxGeometryBuilder();

        // Room for three and a half quads. Writing half a quad would hand the GPU a triangle whose
        // third corner is whatever was in the buffer before.
        Span<ParticleVertex> vertices = stackalloc ParticleVertex[14];

        Assert.Equal(3, builder.Build(system, Camera, vertices));
    }

    [Fact]
    public void TheIndexPatternIsTwoTrianglesAQuad() {
        Span<uint> indices = stackalloc uint[12];

        Assert.Equal(12, VfxGeometryBuilder.WriteQuadIndices(indices, 2));

        Assert.Equal([0u, 1u, 2u, 0u, 2u, 3u], indices[..6].ToArray());
        Assert.Equal([4u, 5u, 6u, 4u, 6u, 7u], indices[6..].ToArray());
    }

    [Fact]
    public void AGraphWithNoRendererRefusesToBeDrawn() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            16
        );

        using var system = Started(graph);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[4];

        // It has no size and no colour, because it never said it would be drawn. Producing quads of
        // zero area in a colour nobody chose would be the unhelpful answer.
        Assert.Throws<InvalidOperationException>(() => builder.Build(system, Camera, vertices));
    }

    [Fact]
    public void ARendererMakesTheGraphAllocateWhatDrawingReads() {
        var simulation = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            16
        );

        var drawn = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            16,
            VfxRenderer.Billboard
        );

        Assert.False((simulation.Attributes & VfxAttribute.Colour) != 0);
        Assert.True((drawn.Attributes & VfxAttribute.Colour) != 0);
        Assert.True((drawn.Attributes & VfxAttribute.Size) != 0);

        // And a streak reads velocity even though nothing in the simulation would have.
        var streak = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            16,
            VfxRenderer.Streak()
        );

        Assert.True((streak.Attributes & VfxAttribute.Velocity) != 0);
    }

    [Fact]
    public void BuildingAllocatesNothingOnceItIsWarm() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(600f)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-5f, -5f, -5f, 0f)) { B = new(5f, 5f, 5f, 0f) },
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.2f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f))
            ],
            [],
            1024,
            VfxRenderer.SortedBillboard
        );

        using var system = new VfxSystem(graph);

        var builder = new VfxGeometryBuilder();
        var vertices = new ParticleVertex[1024 * 4];

        // Warmed so the sort's key and order arrays have been grown to capacity, which they are once
        // and not per frame.
        var allocated = Measured.Bytes(Frame, warmUp: 180, passes: 180);

        Assert.True(allocated == 0, $"Expanding and sorting a thousand particles for 180 frames allocated {allocated} bytes.");

        return;

        void Frame() {
            system.Step(1f / 60f);
            builder.Build(system, Camera, vertices);
        }
    }

    static readonly IEqualityComparer<Vector3> Close = new Tolerance();

    sealed class Tolerance : IEqualityComparer<Vector3> {
        public bool Equals(Vector3 left, Vector3 right) => Vector3.Distance(left, right) < 0.0001f;

        public int GetHashCode(Vector3 value) => 0;
    }
}
