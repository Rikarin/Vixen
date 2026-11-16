// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Interchange;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     A file's cameras (Vixen#1648), read through Assimp from a glTF written by hand, so the numbers are glTF's: each
///     camera stands where its node puts it, in the space the model's bounds are in.
/// </summary>
public sealed class ModelReaderCameraTests {
    /// <summary>
    ///     A perspective camera moved back along z and turned a quarter about y looks along -x from where it stands, and
    ///     its vertical angle and aspect come back as the file wrote them. ⚠ Assimp keeps the horizontal angle whole
    ///     while its header says half; taken as half, this angle reads 1.91.
    /// </summary>
    [Fact]
    public void APerspectiveCameraStandsWhereItsNodePutsIt() {
        var camera = Assert.Single(Read(Perspective, Turned).Cameras);

        Assert.Equal("Shot", camera.Name);
        Assert.Equal(new Vector3(0f, 2f, 5f), camera.Position);
        Assert.Equal(-1f, camera.Forward.X, 4);
        Assert.Equal(0f, camera.Forward.Z, 4);
        Assert.Equal(1f, camera.Up.Y, 4);
        Assert.Equal(0.8f, camera.VerticalFieldOfView, 4);
        Assert.Equal(1.5f, camera.AspectRatio, 4);
        Assert.Equal((0.1f, 100f), (camera.Near, camera.Far));
        Assert.False(camera.Orthographic);
    }

    /// <summary>An orthographic camera says how tall a slab it sees, which is twice glTF's <c>ymag</c>.</summary>
    [Fact]
    public void AnOrthographicCameraSaysHowMuchItSees() {
        var camera = Assert.Single(Read(Orthographic, """{ "camera": 0, "translation": [0, 0, 10] }""").Cameras);

        Assert.True(camera.Orthographic);
        Assert.Equal(0f, camera.VerticalFieldOfView);
        Assert.Equal(4f, camera.OrthographicHeight, 4);
        Assert.Equal(-1f, camera.Forward.Z, 4);
    }

    /// <summary>The read's scale moves a camera as it moves the model, so the two still meet.</summary>
    [Fact]
    public void TheReadsScaleMovesTheCameraWithTheModel() {
        var camera = Assert.Single(Read(Perspective, Turned, 0.5f).Cameras);

        Assert.Equal(new Vector3(0f, 1f, 2.5f), camera.Position);
        Assert.Equal(0.05f, camera.Near, 5);
    }

    [Fact]
    public void AFileWithoutCamerasHasNone() => Assert.Empty(Read(null, """{ "translation": [0, 0, 1] }""").Cameras);

    /// <summary>Stands at (0, 2, 5) and turns a quarter about y, so it looks along -x.</summary>
    const string Turned =
        """{ "name": "Shot", "camera": 0, "translation": [0, 2, 5], "rotation": [0, 0.70710678, 0, 0.70710678] }""";

    const string Perspective =
        """{ "name": "Shot", "type": "perspective", "perspective": { "yfov": 0.8, "aspectRatio": 1.5, "znear": 0.1, "zfar": 100 } }""";

    const string Orthographic =
        """{ "type": "orthographic", "orthographic": { "xmag": 3, "ymag": 2, "znear": 0.1, "zfar": 50 } }""";

    /// <summary>
    ///     A triangle at the root, and a second node holding <paramref name="camera" /> placed by
    ///     <paramref name="node" />.
    /// </summary>
    static ReadModel Read(string? camera, string node, float scale = 1f) {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        ushort[] indices = [0, 1, 2];
        var buffer = new byte[36 + 6];
        Buffer.BlockCopy(positions, 0, buffer, 0, 36);
        Buffer.BlockCopy(indices, 0, buffer, 36, 6);
        var cameras = camera is null ? string.Empty : $"\"cameras\": [{camera}],";
        var json = $$"""
                     {
                       "asset": { "version": "2.0" },
                       "scene": 0,
                       "scenes": [{ "nodes": [0, 1] }],
                       "nodes": [{ "mesh": 0 }, {{node}}],
                       {{cameras}}
                       "meshes": [{ "name": "Tri", "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1 }] }],
                       "buffers": [{
                         "byteLength": {{buffer.Length}},
                         "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}"
                       }],
                       "bufferViews": [
                         { "buffer": 0, "byteOffset": 0, "byteLength": 36 },
                         { "buffer": 0, "byteOffset": 36, "byteLength": 6 }
                       ],
                       "accessors": [
                         { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                         { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
                       ]
                     }
                     """;
        return ModelReader.Read(Encoding.UTF8.GetBytes(json), ".gltf", "Tri", new ModelReadOptions { Scale = scale });
    }
}
