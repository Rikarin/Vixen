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
