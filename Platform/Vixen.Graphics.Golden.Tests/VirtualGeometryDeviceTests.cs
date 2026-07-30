// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Engine.Renderer;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.VirtualGeometry;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The whole virtualized stack, on a device, driven by a document.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim nothing had ever made: that it runs.</b> Every stage of virtualized geometry
///         was built and tested — the DAG, the pages, the residency, the traversal, the raster, the
///         binning, the resolve — and the assembled system had never executed anywhere, because nothing
///         assembled it. <c>new RenderSystem()</c> appeared only in test projects and the artefacts a
///         build wrote were read by nothing.
///     </para>
///     <para>
///         So this is deliberately not a picture test. It builds a mesh, serialises it exactly as an
///         import does, loads it back through <see cref="VirtualGeometryContent" />, places it in a
///         scene and runs a documented frame through <see cref="SceneRenderHost" /> on real Vulkan.
///         What it asserts is that the traversal dispatched, the streaming loop asked for pages and got
///         them, and the frame recorded and submitted without the validation layer objecting. Phase 4's
///         golden image is the picture, and it is still owed.
///     </para>
/// </remarks>
public sealed class VirtualGeometryDeviceTests {
    /// <summary>
    ///     A mesh loaded from its artefacts is traversed, streamed and drawn on a device.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Several frames rather than one, because the streaming loop is a loop: a traversal asks
    ///         for the pages a cut wanted and did not have, the request comes back after the frame that
    ///         made it was submitted, and the pages arrive for a later frame. One frame draws the root
    ///         page and reports a page request, which is the system working and not the system finished.
    ///     </para>
    ///     <para>
    ///         <b>Every assertion here failed before the two links landed</b>, and would have failed by
    ///         not compiling: there was no way to load an artefact and no way to run a frame.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AVirtualizedMeshIsTraversedAndDrawn() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();
        effects.AddProvider(new Compiling(loader));

        var pipelines = new ComputePipelineCache(device);
        using var geometry = new VirtualGeometrySystem(device, slots: 64, pageSize: PageSize);

        geometry.Effects = effects;
        geometry.Pipelines = pipelines;
        geometry.Modules = new EffectPipelineDescriber(device);

        using var host = new SceneRenderHost(device, effects);

        geometry.Register(host.System);
        geometry.Supply(host.Builder);

        // The camera the traversal projects error against. Straight down the negative z axis, which is
        // where the mesh is put below — a view looking away from it is a frame that culls everything
        // and passes every assertion about validation.
        var look = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        host.Builder.Views["Camera"] = new("camera") {
            Position = Vector3.Zero,
            Frustum = new(look * projection),
            ScreenHeightScale = 1f
        };

        host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        host.FrameSize = new(Fixture.Side, Fixture.Side);

        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            Fixture.Side,
            Fixture.Side,
            TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.Storage,
            Name: "SceneColour"
        );

        var colour = device.CreateTexture(description);
        var view = device.CreateTextureView(colour);

        host.Import("SceneColour", new(colour, view, description));

        // The mesh, through the same two artefacts a build ships and the same loader a game would use.
        var (mesh, pages) = BuildMesh();

        var registered = geometry.Content(
            0,
            Serializer.ToBytes(mesh),
            Serializer.ToBytes(pages.WithoutData()),
            new MemoryStream(pages.Data)
        );

        // Every stage the document declared, so the instance is not filtered out of the traversal by a
        // mask nobody meant to set. The traversal tests the stage mask first, exactly as the object cull
        // does, so an empty one is a mesh that never reaches the frustum test.
        var stages = host.Builder.Stages.Values.Aggregate(
            RenderStageMask.None,
            (mask, stage) => mask | stage.Mask
        );

        var id = host.System.Objects.Add(new() { Bounds = new(new(0f, 0f, -4f), 2f), Stages = stages });

        host.System.Objects.Data.Data(geometry.Feature.Draws)[id.Index] = new() {
            Mesh = registered,
            Position = new(0f, 0f, -4f),
            Scale = 1f
        };

        geometry.Feature.ScreenHeight = Fixture.Side;

        for (var frame = 0; frame < Frames; frame++) {
            var list = device.BeginCommandList();

            Assert.True(host.Draw(list));

            list.Finish();
            device.GraphicsQueue.Submit([list]);
            device.GraphicsQueue.WaitIdle();
        }

        Assert.Equal(Frames, host.FrameCount);

        // The traversal ran on the device rather than being skipped for a missing variant, an unresolved
        // effect or an empty instance list — each of which is a frame that draws nothing and reports it
        // nowhere, and each of which this suite exists to tell apart from a picture being wrong.
        Assert.True(geometry.Visibility.TraversedOnDevice, "The cluster traversal never dispatched.");

        // And the streaming loop closed: something asked for pages and something serviced them. A mesh
        // whose pages never arrive still draws — at its coarsest level, for ever — so "resident" is the
        // assertion and "no crash" is not.
        Assert.True(
            geometry.Residency.ResidentPages > 0,
            "No page ever became resident, so the frame drew nothing below the pinned root."
        );

        device.Destroy(view);
        device.Destroy(colour);
    }

    /// <summary>How many frames the streaming loop is given to settle.</summary>
    /// <remarks>
    ///     Four: one to traverse and request, one for the service to place what was asked for, and two
    ///     of margin. The loop is deliberately a frame late — the requests come back from a dispatch
    ///     nothing waited for — which is the trade phase 2 makes and the reason one frame is not a test.
    /// </remarks>
    const int Frames = 4;

    /// <summary>Small pages, so a modest mesh still needs more than the pinned root.</summary>
    const int PageSize = 8 * 1024;

    /// <summary>
    ///     The virtualized path as a document says it: traverse, then draw and shade.
    /// </summary>
    /// <remarks>
    ///     The point of writing it as YAML here rather than assembling nodes in C# is that this is the
    ///     configuration a project ships. A test that built the tree by hand would prove the nodes work
    ///     and leave the document format — the thing an author edits — untested against a device.
    /// </remarks>
    const string Document = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled, Storage
          - name: SceneDepth
            format: Depth32Float
            usage: DepthStencilTarget, Sampled
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !ClusterCulling
              name: Traversal
            - !VisibilityBuffer
              name: Visibility
              view: Camera
              depth: SceneDepth
              colour: SceneColour
        """;

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so this may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>A tessellated plane, cut into clusters and paged the way a build does.</summary>
    static (MeshletMesh Mesh, MeshletPageSet Pages) BuildMesh(int segments = 32) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var y = 0; y <= segments; y++) {
            for (var x = 0; x <= segments; x++) {
                positions.Add(new(((float)x / segments) - 0.5f, ((float)y / segments) - 0.5f, 0f));
            }
        }

        for (var y = 0; y < segments; y++) {
            for (var x = 0; x < segments; x++) {
                var a = (y * (segments + 1)) + x;
                var b = a + 1;
                var c = a + segments + 1;
                var d = c + 1;

                indices.AddRange([a, c, b]);
                indices.AddRange([b, c, d]);
            }
        }

        var input = new MeshletBuildInput { Positions = [.. positions], Indices = [.. indices] };
        var mesh = MeshletBuilder.Build(input);

        return (mesh, MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = PageSize }));
    }
}
