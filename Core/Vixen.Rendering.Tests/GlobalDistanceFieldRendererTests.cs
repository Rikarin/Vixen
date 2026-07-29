// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The node that keeps the clipmap over the camera, on the device and named in the frame's set.
/// </summary>
/// <remarks>
///     Everything it sequences is tested elsewhere — the composite against closed forms, the upload
///     against the recorded command stream. What is left, and all this asserts, is <i>when</i> it does
///     them: the recomposite is the most expensive thing in a frame and a still camera must not pay
///     for it.
/// </remarks>
public class GlobalDistanceFieldRendererTests {
    [Fact]
    public void TheFirstFrameCompositesAndUploads() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out var scene);
        var context = Context(device);

        Record(node, context);

        Assert.Equal(1, node.Composites);
        Assert.NotNull(node.Texture);
        Assert.Equal(1, node.Texture!.Uploads);
        Assert.True(node.Field!.HasContent);
    }

    /// <summary>
    ///     The point of snapping the levels, cashed in. A camera that has not crossed a cell boundary
    ///     would get the same numbers back from a composite that costs every cell of every level.
    /// </summary>
    [Fact]
    public void AStillCameraCompositesOnceAndThenNeverAgain() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        for (var frame = 0; frame < 10; frame++) {
            Record(node, context);
        }

        Assert.Equal(1, node.Composites);
        Assert.Equal(1, node.Texture!.Uploads);
    }

    [Fact]
    public void MovingLessThanACellChangesNothing() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        node.ViewPosition = new Vector3(node.Field!.CellSizeOf(0) * 0.3f, 0, 0);
        Record(node, context);

        Assert.Equal(1, node.Composites);
    }

    [Fact]
    public void CrossingACellRecomposites() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        node.ViewPosition = new Vector3(node.Field!.CellSizeOf(0), 0, 0);
        Record(node, context);

        Assert.Equal(2, node.Composites);
        Assert.Equal(2, node.Texture!.Uploads);
    }

    /// <summary>
    ///     Comparing the instances themselves every frame would cost more than the comparison saves,
    ///     so the list carries a version and whoever changes it says so.
    /// </summary>
    [Fact]
    public void ChangingTheInstancesNeedsTheVersionBumpedToBeSeen() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);

        node.Instances.Add(DistanceFieldInstance.At(Sphere(), Vector3.Zero));
        Record(node, context);

        Assert.Equal(1, node.Composites);

        node.InstancesVersion++;
        Record(node, context);

        Assert.Equal(2, node.Composites);
    }

    /// <summary>
    ///     The names are the frame's answer to "where is the clipmap now", so they go in every frame
    ///     even when nothing was recomposited — a set rebuilt for some other reason would otherwise
    ///     bind whatever the last frame left.
    /// </summary>
    [Fact]
    public void TheNamesAreWrittenEvenOnAFrameThatCompositedNothing() {
        using var device = new NullDevice(new() { Record = true });
        using var node = Node(device, out var scene);
        var context = Context(device);

        Record(node, context);
        scene.Parameters.Clear();
        Record(node, context);

        Assert.Equal(1, node.Composites);
        Assert.True(scene.Parameters.Has(ParameterKeys.New<float>("ForwardPlus.distanceFieldVolumes[0].maxDistance")));
    }

    [Fact]
    public void ANodeWithNoClipmapDoesNothingAtAll() {
        using var device = new NullDevice(new() { Record = true });
        using var node = new GlobalDistanceFieldRenderer();
        var context = Context(device);

        Record(node, context);

        Assert.Equal(0, node.Composites);
        Assert.Null(node.Texture);
    }

    [Fact]
    public void DisposingReleasesTheMirror() {
        using var device = new NullDevice(new() { Record = true });
        var node = Node(device, out _);
        var context = Context(device);

        Record(node, context);
        node.Dispose();

        Assert.Null(node.Texture);

        node.Dispose();
    }

    static GlobalDistanceFieldRenderer Node(NullDevice device, out SceneConstants scene) {
        scene = new(device, "ForwardPlus");

        return new() {
            Field = new GlobalDistanceField(8, 4f, 2),
            SceneConstants = scene,
            ViewPosition = Vector3.Zero,
            Parallel = false
        };
    }

    static MeshDistanceField Sphere() {
        var (vertices, indices) = Icosahedron();

        return MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 4, SignRayCount = 8 });
    }

    /// <summary>The cheapest closed mesh that is not degenerate. The bake is not what is under test.</summary>
    static (Vector3[] Vertices, int[] Indices) Icosahedron() {
        Vector3[] vertices = [
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)
        ];

        int[] indices = [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3];

        return (vertices, indices);
    }

    static RenderDrawContext Context(NullDevice device) =>
        new(device.BeginCommandList(), new EffectSystem()) { Device = device };

    /// <summary>
    ///     Driven directly rather than through a compositor. The phase methods are
    ///     <c>protected internal</c> and this assembly is a friend, so what is under test is the node
    ///     and not the graph that would otherwise have to be stood up around it.
    /// </summary>
    static void Record(GlobalDistanceFieldRenderer node, RenderDrawContext context) =>
        node.Record(null!, context);
}
