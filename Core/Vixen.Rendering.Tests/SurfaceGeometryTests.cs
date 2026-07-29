// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     Going from the arrays a compiler produces to the interleaved vertices the shading stages read.
/// </summary>
/// <remarks>
///     A pure function of the mesh, so every case an importer can hand over is a test here rather than
///     something found by looking at a picture.
/// </remarks>
public sealed class SurfaceGeometryTests {
    [Fact]
    public void TheStrideIsWhatTheShaderDeclares() {
        // position 3 + normal 3 + tangent 4 + texcoord 2, all floats.
        Assert.Equal(48, SurfaceVertex.SizeInBytes);
        Assert.Equal(4, SurfaceVertex.Locations.Count);
        Assert.Equal(6u, SurfaceVertex.Locations[0]);
        Assert.Equal(9u, SurfaceVertex.Locations[3]);
    }

    [Fact]
    public void EveryAttributeThePresentMeshHasComesThrough() {
        var mesh = new MeshData {
            Positions = [new(1f, 2f, 3f)],
            Normals = [Vector3.UnitX],
            Tangents = [new(0f, 0f, 1f, -1f)],
            TexCoords = [new(0.25f, 0.75f)]
        };

        var vertex = Assert.Single(SurfaceGeometry.Packed(mesh));

        Assert.Equal(new Vector3(1f, 2f, 3f), vertex.Position);
        Assert.Equal(Vector3.UnitX, vertex.Normal);
        Assert.Equal(new Vector4(0f, 0f, 1f, -1f), vertex.Tangent);
        Assert.Equal(new Vector2(0.25f, 0.75f), vertex.TexCoord);
    }

    /// <remarks>
    ///     ⚠ Not zero. A zero normal makes every lighting term zero and the surface renders black, which
    ///     reads as a broken renderer rather than as missing data.
    /// </remarks>
    [Fact]
    public void AMeshWithNoNormalsFacesUpRatherThanNowhere() {
        var mesh = new MeshData { Positions = [Vector3.Zero] };

        var vertex = Assert.Single(SurfaceGeometry.Packed(mesh));

        Assert.Equal(Vector3.UnitY, vertex.Normal);
    }

    [Fact]
    public void ADegenerateNormalIsReplacedRatherThanNormalised() {
        var mesh = new MeshData { Positions = [Vector3.Zero], Normals = [Vector3.Zero] };

        var vertex = Assert.Single(SurfaceGeometry.Packed(mesh));

        Assert.Equal(Vector3.UnitY, vertex.Normal);
    }

    [Fact]
    public void ANormalThatIsNotUnitLengthIsNormalised() {
        var mesh = new MeshData { Positions = [Vector3.Zero], Normals = [new(0f, 4f, 0f)] };

        var vertex = Assert.Single(SurfaceGeometry.Packed(mesh));

        Assert.Equal(1f, vertex.Normal.Length(), 5);
    }

    /// <remarks>
    ///     The ordinary case: <c>MeshPrimitives</c> produces positions, normals and texture coordinates
    ///     and no tangents at all, so every primitive in a scene takes this path.
    /// </remarks>
    [Fact]
    public void AMeshWithNoTangentsGetsOnePerpendicularToItsNormal() {
        var mesh = new MeshData { Positions = [Vector3.Zero], Normals = [Vector3.UnitZ] };

        var vertex = Assert.Single(SurfaceGeometry.Packed(mesh));
        var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);

        Assert.Equal(1f, tangent.Length(), 5);
        Assert.Equal(0f, Vector3.Dot(tangent, Vector3.UnitZ), 5);
        Assert.Equal(1f, vertex.Tangent.W);
    }

    /// <remarks>
    ///     ⚠ <b>The top of every box.</b> Crossing against a fixed up axis gives the zero vector for a
    ///     surface facing along it, which is the one degenerate case the fill exists to avoid — so the
    ///     axis is chosen against the normal.
    /// </remarks>
    [Fact]
    public void AnUpwardFacingSurfaceStillGetsANonDegenerateTangent() {
        foreach (var normal in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ }) {
            var tangent = SurfaceGeometry.Perpendicular(normal);
            var axis = new Vector3(tangent.X, tangent.Y, tangent.Z);

            Assert.Equal(1f, axis.Length(), 5);
            Assert.Equal(0f, Vector3.Dot(axis, normal), 5);
        }
    }

    [Fact]
    public void AMeshWithNoTextureCoordinatesSamplesOneTexel() {
        var mesh = new MeshData { Positions = [Vector3.Zero], Normals = [Vector3.UnitY] };

        Assert.Equal(Vector2.Zero, Assert.Single(SurfaceGeometry.Packed(mesh)).TexCoord);
    }

    /// <remarks>
    ///     A broken import — normals for half the vertices — packs rather than throwing, because there is
    ///     no length at which refusing helps somebody more than drawing does.
    /// </remarks>
    [Fact]
    public void AnAttributeShorterThanThePositionsIsFilledForTheRest() {
        var mesh = new MeshData {
            Positions = [Vector3.Zero, Vector3.UnitX],
            Normals = [Vector3.UnitZ]
        };

        var vertices = SurfaceGeometry.Packed(mesh);

        Assert.Equal(2, vertices.Length);
        Assert.Equal(Vector3.UnitZ, vertices[0].Normal);
        Assert.Equal(Vector3.UnitY, vertices[1].Normal);
    }

    [Fact]
    public void ASpanTooShortToHoldTheMeshSaysSo() {
        var mesh = new MeshData { Positions = [Vector3.Zero, Vector3.UnitX] };

        Assert.Throws<ArgumentException>(() => SurfaceGeometry.Pack(mesh, new SurfaceVertex[1]));
    }

    // ------------------------------------------------------------------ bounds

    [Fact]
    public void BoundsComeFromTheBoxTheImporterWrote() {
        var mesh = new MeshData {
            Positions = [Vector3.Zero],
            Bounds = new(new Vector3(-2f, -2f, -2f), new Vector3(2f, 2f, 2f))
        };

        var bounds = SurfaceGeometry.BoundsOf(mesh);

        Assert.Equal(Vector3.Zero, bounds.Center);
        Assert.True(bounds.Radius >= 2f, $"a box of half-extent 2 needs a radius of at least 2, not {bounds.Radius}");
    }

    /// <remarks>
    ///     ⚠ An empty box would otherwise cull at the origin with zero radius, which is an object that
    ///     disappears rather than one drawn slightly too often.
    /// </remarks>
    [Fact]
    public void AMeshWithNoBoxIsMeasuredFromItsPositions() {
        var mesh = new MeshData { Positions = [new(0f, 0f, 0f), new(4f, 0f, 0f)] };

        var bounds = SurfaceGeometry.BoundsOf(mesh);

        Assert.True(bounds.Radius > 0f, "a mesh with positions and no box measured to nothing");
        Assert.Equal(2f, bounds.Center.X, 5);
    }

    [Fact]
    public void AMeshWithNothingInItHasNoBounds() =>
        Assert.Equal(0f, SurfaceGeometry.BoundsOf(new MeshData()).Radius);
}
