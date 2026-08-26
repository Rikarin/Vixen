// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     These types exist to be written into the object database and read back on another machine, so
///     what is worth asserting is that they survive the trip. Before this, nothing in the repository
///     had ever serialised a <see cref="Vector3" /> — the mathematics assembly carried
///     <c>[DataContract]</c> on every type and never referenced the generator, so a chunk holding one
///     would have failed at run time with "no serializer is registered".
/// </summary>
public sealed class ModelDataTests {
    [Fact]
    public void AMeshSurvivesTheObjectDatabase() {
        var mesh = new MeshData {
            Name = "Hero_Body",
            Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [new(1, 0, 0, -1), new(1, 0, 0, -1), new(1, 0, 0, -1)],
            TexCoords = [new(0, 0), new(1, 0), new(0, 1)],
            Indices = [0, 1, 2],
            MaterialIndex = 2,
            Bounds = new(new(0, 0, 0), new(1, 1, 0))
        };

        var loaded = Serializer.Read<MeshData>(Serializer.ToBytes(mesh));

        Assert.Equal("Hero_Body", loaded.Name);
        Assert.Equal(mesh.Positions, loaded.Positions);
        Assert.Equal(mesh.Tangents, loaded.Tangents);
        Assert.Equal(mesh.TexCoords, loaded.TexCoords);
        Assert.Equal(mesh.Indices, loaded.Indices);
        Assert.Equal(2, loaded.MaterialIndex);
        Assert.Equal(mesh.Bounds, loaded.Bounds);
    }

    /// <summary>
    ///     A chunk written when the type still carried colours and a second UV set is refused rather
    ///     than misread, and the message says what to do about it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole safety argument for removing two members from the middle of a
    ///     serialized record.</b> Members are written positionally, so dropping two shifts the seven
    ///     after them — and a reader that took the old bytes at face value would hand
    ///     <c>Indices</c> a coordinate array, which is a mesh drawn from garbage rather than an
    ///     error. The generated reader's member-count guard is what makes that impossible; the
    ///     recovery is a re-import, which <c>ModelImporter</c>'s version bump forces anyway.
    /// </remarks>
    [Fact]
    public void AChunkFromWhenTheTypeCarriedColoursIsRefusedRatherThanMisread() {
        // Fourteen members, which is what the record had with TexCoords1 and Colors in it. Only the
        // count matters here — the reader refuses before it reads a single one.
        var bytes = Serializer.ToBytes(new MeshData { Positions = [Vector3.Zero] });
        var forged = bytes.ToArray();

        forged[1] = 14;

        var thrown = Assert.Throws<SerializationException>(() => Serializer.Read<MeshData>(forged));

        Assert.Contains("14 members", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The sign in <c>W</c> is the whole reason tangents are a <see cref="Vector4" />, and a
    ///     round trip that dropped it would leave every normal-mapped surface lit from the wrong
    ///     side along one axis.
    /// </summary>
    [Fact]
    public void ATangentKeepsItsBitangentSign() {
        var mesh = new MeshData { Tangents = [new(0, 1, 0, -1), new(0, 1, 0, 1)] };
        var loaded = Serializer.Read<MeshData>(Serializer.ToBytes(mesh));

        Assert.Equal(-1, loaded.Tangents[0].W);
        Assert.Equal(1, loaded.Tangents[1].W);
    }

    [Fact]
    public void AnEmptyAttributeStaysEmptyRatherThanBecomingNull() {
        var loaded = Serializer.Read<MeshData>(Serializer.ToBytes(new MeshData()));

        Assert.Empty(loaded.Normals);
        Assert.False(loaded.IsSkinned);
        Assert.False(loaded.IsMorphed);
    }

    /// <summary>
    ///     A blend shape survives the chunk, quantised bits and all.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The assertion is on the dequantised delta rather than on the <c>short[]</c></b>,
    ///     because that is what would be wrong if the array were written as an object array instead of
    ///     a blittable one and came back byte-swapped on a big-endian target. Equal shorts and an
    ///     unequal delta is not a state that can happen; an unequal delta is the one worth naming.
    /// </remarks>
    [Fact]
    public void ABlendShapeSurvivesTheChunk() {
        var mesh = new MeshData {
            Positions = [Vector3.Zero, Vector3.UnitX],
            MorphTargets = [
                MorphTargetData.Encode("jawOpen", [1], [new(0.5f, -0.25f, 0f)], [new(0f, 0f, 0.5f)])
            ]
        };

        var loaded = Serializer.Read<MeshData>(Serializer.ToBytes(mesh));
        var target = Assert.Single(loaded.MorphTargets);

        Assert.True(loaded.IsMorphed);
        Assert.Equal("jawOpen", target.Name);
        Assert.Equal([1], target.Indices);
        Assert.Equal(mesh.MorphTargets[0].PositionDelta(0), target.PositionDelta(0));
        Assert.Equal(mesh.MorphTargets[0].NormalDelta(0), target.NormalDelta(0));
    }

    [Fact]
    public void AModelKeepsItsHierarchyAndItsNamedParts() {
        var model = new ModelData {
            Name = "Hero",
            Nodes = [
                new() { Name = "Root", Parent = -1 },
                new() { Name = "Spine", Parent = 0, Transform = Matrix4x4.FromTranslation(new(0, 1, 0)) }
            ],
            Parts = [new() { Mesh = "Hero_Body", Node = 1, Material = 0 }],
            Materials = ["Skin"],
            Skeleton = "Hero_Skeleton",
            Animations = ["Idle", "Run"]
        };

        var loaded = Serializer.Read<ModelData>(Serializer.ToBytes(model));

        Assert.Equal(["Root", "Spine"], loaded.Nodes.Select(node => node.Name));
        Assert.Equal(0, loaded.Nodes[1].Parent);
        Assert.Equal(model.Nodes[1].Transform, loaded.Nodes[1].Transform);
        Assert.Equal("Hero_Body", Assert.Single(loaded.Parts).Mesh);
        Assert.Equal(["Idle", "Run"], loaded.Animations);
    }

    [Fact]
    public void AClipKeepsItsQuaternionKeys() {
        var clip = new AnimationClipData {
            Name = "Idle",
            Duration = 1.5f,
            Channels = [
                new() {
                    Target = "Spine",
                    RotationTimes = [0f, 1.5f],
                    Rotations = [Quaternion.Identity, Quaternion.FromAxisAngle(Vector3.UnitY, 1f)]
                }
            ]
        };

        var loaded = Serializer.Read<AnimationClipData>(Serializer.ToBytes(clip));
        var channel = Assert.Single(loaded.Channels);

        Assert.Equal(1.5f, loaded.Duration);
        Assert.Equal("Spine", channel.Target);
        Assert.Equal(clip.Channels[0].Rotations, channel.Rotations);
        Assert.Empty(channel.Positions);
    }

    [Fact]
    public void ASkeletonKeepsItsInverseBindPoses() {
        var skeleton = new SkeletonData {
            Name = "Hero_Skeleton",
            Joints = [
                new() { Name = "Root", Parent = -1 },
                new() { Name = "Spine", Parent = 0, InverseBindPose = Matrix4x4.FromScale(new(2, 2, 2)) }
            ]
        };

        var loaded = Serializer.Read<SkeletonData>(Serializer.ToBytes(skeleton));

        Assert.Equal(skeleton.Joints[1].InverseBindPose, loaded.Joints[1].InverseBindPose);
    }
}
