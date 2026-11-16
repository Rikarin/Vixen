// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using AssimpCamera = Silk.NET.Assimp.Camera;
using AssimpNode = Silk.NET.Assimp.Node;
using AssimpScene = Silk.NET.Assimp.Scene;

namespace Vixen.Interchange;

/// <summary>
///     A camera a model file holds, placed in the model's world as its node places it, so it sees the model as
///     <c>ModelData.Bounds</c> has it.
/// </summary>
/// <param name="Name">What the file calls it.</param>
/// <param name="Position">Where it stands, in the model's units after the read's scale.</param>
/// <param name="Forward">The unit direction it looks in.</param>
/// <param name="Up">The unit direction that is up in its picture, square to <paramref name="Forward" />.</param>
public sealed record ModelCamera(string Name, Vector3 Position, Vector3 Forward, Vector3 Up) {
    /// <summary>The whole vertical angle it sees, in radians; zero for an orthographic camera.</summary>
    public float VerticalFieldOfView { get; init; }

    /// <summary>Its picture's width over its height, or zero when the file does not say.</summary>
    public float AspectRatio { get; init; }

    /// <summary>The nearest distance it draws, in the model's units.</summary>
    public float Near { get; init; }

    /// <summary>The farthest distance it draws.</summary>
    public float Far { get; init; }

    /// <summary>
    ///     How tall the slab an orthographic camera sees is, in the model's units, or zero for a camera with
    ///     perspective.
    /// </summary>
    public float OrthographicHeight { get; init; }

    /// <summary>Whether it sees in parallel lines rather than in perspective.</summary>
    public bool Orthographic => OrthographicHeight > 0f;
}

/// <summary>The cameras of a scene Assimp read (Vixen#1648), each where its node puts it.</summary>
static class ModelCameras {
    /// <summary>Each camera of the scene that a node places, in the file's order.</summary>
    /// <remarks>
    ///     ⚠ <b>Assimp's horizontal angle is the whole angle,</b> whatever its header says of half: its glTF and FBX
    ///     readers both store the full one, so the vertical angle is worked out from it as a whole angle. A camera no
    ///     node is named after is in no place and is left out.
    /// </remarks>
    public static unsafe ModelCamera[] Read(AssimpScene* scene, float scale) {
        if (scene->MNumCameras == 0) {
            return [];
        }

        Dictionary<string, Matrix4x4> placed = new(StringComparer.Ordinal);
        Place(scene->MRootNode, Matrix4x4.Identity, scale, placed);
        List<ModelCamera> cameras = [];
        for (var at = 0u; at < scene->MNumCameras; at++) {
            var camera = scene->MCameras[at];
            if (placed.TryGetValue(camera->MName.AsString, out var world)) {
                cameras.Add(Of(camera, world, scale));
            }
        }

        return [..cameras];
    }

    static unsafe ModelCamera Of(AssimpCamera* camera, Matrix4x4 world, float scale) {
        var position = Matrix4x4.TransformPosition(
            new Vector3(camera->MPosition.X, camera->MPosition.Y, camera->MPosition.Z) * scale,
            world
        );
        var forward = Direction(camera->MLookAt.X, camera->MLookAt.Y, camera->MLookAt.Z, world, -Vector3.UnitZ);
        var up = Direction(camera->MUp.X, camera->MUp.Y, camera->MUp.Z, world, Vector3.UnitY);
        // Square to the direction of view, as a picture's up is, even where a node's transform shears.
        up = Vector3.Normalize(up - forward * Vector3.Dot(up, forward));
        var aspect = camera->MAspect > 0f ? camera->MAspect : 0f;
        var orthographic = camera->MOrthographicWidth > 0f;
        var vertical = orthographic || camera->MHorizontalFOV <= 0f
            ? 0f
            : 2f * MathF.Atan(MathF.Tan(camera->MHorizontalFOV * 0.5f) / (aspect > 0f ? aspect : 1f));
        return new(Name(camera), position, forward, up) {
            VerticalFieldOfView = vertical,
            AspectRatio = aspect,
            Near = camera->MClipPlaneNear * scale,
            Far = camera->MClipPlaneFar * scale,
            // Assimp's orthographic width is half the slab's, as glTF's xmag is.
            OrthographicHeight = orthographic
                ? 2f * camera->MOrthographicWidth * scale / (aspect > 0f ? aspect : 1f)
                : 0f
        };
    }

    static unsafe string Name(AssimpCamera* camera) => camera->MName.AsString is { Length: > 0 } name ? name : "Camera";

    /// <summary>A direction the camera's node turns, or <paramref name="fallback" /> turned for a zero one.</summary>
    static Vector3 Direction(float x, float y, float z, Matrix4x4 world, Vector3 fallback) {
        var local = new Vector3(x, y, z);
        var turned = Matrix4x4.TransformDirection(local.LengthSquared() > 1e-12f ? local : fallback, world);
        return turned.LengthSquared() > 1e-12f ? Vector3.Normalize(turned) : fallback;
    }

    /// <summary>Every node's world transform, by name; the first of two nodes with one name keeps it.</summary>
    static unsafe void Place(AssimpNode* node, Matrix4x4 parent, float scale, Dictionary<string, Matrix4x4> placed) {
        var world = ModelReader.Convert(node->MTransformation, scale) * parent;
        placed.TryAdd(node->MName.AsString, world);
        for (var child = 0u; child < node->MNumChildren; child++) {
            Place(node->MChildren[child], world, scale, placed);
        }
    }
}
