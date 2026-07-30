// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     A mesh in front of a camera has clusters the traversal accepts.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim every other traversal test assumes and none of them makes.</b>
///         <c>GpuClusterCullingTests</c> compares the walk against a brute-force cut over hand-built
///         scenes — which is the right test of the <em>arithmetic</em> and says nothing about whether a
///         real mesh, flattened the way a registration flattens one and viewed through a camera a frame
///         actually has, produces a single visible cluster.
///     </para>
///     <para>
///         It is the host half of what <c>VirtualGeometryGoldenTests</c> asks on a device: that fixture
///         found the visibility buffer uniformly empty, and the two possibilities were a shader that
///         rejects everything and a fixture that fed it something wrong. This runs the same inputs
///         through the host mirror, so whichever it is, one of the two fails.
///     </para>
/// </remarks>
public sealed class ClusterTraversalAcceptsTests {
    /// <summary>A plane four units across, four units in front of the camera, is visible.</summary>
    [Fact]
    public void A_mesh_in_front_of_the_camera_has_visible_clusters() {
        var scene = Scene(out var instance);
        var view = View();

        var result = GpuClusterCulling.Traverse(scene, instance, view, _ => true);

        Assert.True(
            result.Visible.Length > 0,
            $"The traversal accepted no cluster of {scene.Clusters.Length}, with {scene.Roots.Length} "
            + $"roots and {result.Requests.Length} page requests."
        );
    }

    /// <summary>And the brute-force cut over the same scene agrees that something is visible.</summary>
    /// <remarks>
    ///     The oracle beside the walk, so "nothing is visible" cannot be blamed on the walk when it is
    ///     the scene or the camera. <c>Cut</c> ignores residency and tests every cluster directly.
    /// </remarks>
    [Fact]
    public void The_brute_force_cut_agrees_something_is_visible() {
        var scene = Scene(out var instance);

        Assert.NotEmpty(GpuClusterCulling.Cut(scene, instance, View()));
    }

    /// <summary>The plane, flattened exactly as a registration flattens one.</summary>
    static ClusterScene Scene(out CullInstance instance) {
        var (mesh, pages) = Plane();
        var scene = GpuClusterCulling.Flatten(mesh, pages);

        instance = new() {
            FirstCluster = 0,
            ClusterCount = (uint)scene.Clusters.Length,
            FirstRoot = 0,
            RootCount = (uint)scene.Roots.Length,
            Position = Vector3.Zero,
            Scale = 1f,
            StagesLow = 1u,
            Flags = GpuCulling.Alive,
            FirstBone = GpuCulling.NoBones
        };

        return scene;
    }

    /// <summary>The camera the golden fixture uses, packed as the frame packs one.</summary>
    static CullView View() {
        const float FieldOfView = MathF.PI / 3f;

        var viewProjection = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f))
            * Matrix4x4.PerspectiveFieldOfView(FieldOfView, 1f, 0.1f, 1000f);

        var render = new RenderView("camera") {
            Position = Vector3.Zero,
            ViewProjection = viewProjection,
            ScreenHeightScale = 1f / MathF.Tan(FieldOfView * 0.5f),
            Stages = new(1u)
        };

        var packed = GpuCulling.Pack(render, 0, 0, default, 0);

        packed.ErrorScale = GpuClusterCulling.ErrorScaleFor(render.ScreenHeightScale, 128);
        packed.ErrorThreshold = 1f;

        return packed;
    }

    /// <summary>A tessellated plane four units across, four units down the negative z axis.</summary>
    static (MeshletMesh Mesh, MeshletPageSet Pages) Plane(int segments = 16) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var y = 0; y <= segments; y++) {
            for (var x = 0; x <= segments; x++) {
                positions.Add(
                    new((((float)x / segments) - 0.5f) * 4f, (((float)y / segments) - 0.5f) * 4f, -4f)
                );
            }
        }

        for (var y = 0; y < segments; y++) {
            for (var x = 0; x < segments; x++) {
                var a = (y * (segments + 1)) + x;
                var c = a + segments + 1;

                indices.AddRange([a, a + 1, c]);
                indices.AddRange([a + 1, c + 1, c]);
            }
        }

        var input = new MeshletBuildInput { Positions = [.. positions], Indices = [.. indices] };
        var mesh = MeshletBuilder.Build(input);

        return (mesh, MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 8 * 1024 }));
    }
}
