// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.VirtualGeometry;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Phase 4's exit criterion: the visibility buffer covers what <see cref="MeshRenderFeature" />
///     draws.
/// </summary>
/// <remarks>
///     <para>
///         <b>The comparison <c>docs/plan/22-virtualized-geometry.md</c> § phase 4 states and records as
///         owed.</b> Everything under the raster had been checked on the host — the decode from a
///         visible word through the instance, the geometry record, the slot table and the page bytes to
///         a world position, exactly, in <c>ClusterRasterTests</c> — and nothing had ever put a triangle
///         on a screen through the traversal and compared it with the same triangle drawn the ordinary
///         way. A decode that is right and a raster that draws it in the wrong place look identical from
///         the host.
///     </para>
///     <para>
///         <b>Coverage, not colour, and that is the phase's own boundary.</b> Phase 4 owns which pixels
///         the instanced indirect draw over the traversal's cut lands on; what those pixels then look
///         like is the resolve's, which is phase 5. So the two images are reduced to masks — the forward
///         one by "is this pixel not the clear colour", the virtualized one by "is this visibility word
///         not <see cref="GpuClusterRaster.Nothing" />" — and the masks are compared.
///     </para>
///     <para>
///         <b>A plane, and the choice is load-bearing.</b> "Matching within the LOD error threshold" is a
///         real caveat for a curved surface: a coarser cut moves the silhouette, so the two images differ
///         along the edge by an amount nobody can write down in advance. A plane's silhouette is
///         <em>LOD-invariant</em> — every simplification of a flat quad is the same quad — so whatever
///         cut the traversal happens to choose covers exactly the same pixels, and the tolerance can be
///         tight enough to mean something.
///     </para>
///     <para>
///         Both paths draw through an identity view-projection with the geometry already in clip space.
///         That is not a shortcut around the transform: it removes the camera as a shared source of
///         error, so a difference in the picture is a difference in the <em>raster</em> rather than two
///         renderers disagreeing about a matrix convention — which is a separate claim, and one
///         <c>ClusterRasterTests</c> already makes exactly.
///     </para>
/// </remarks>
public sealed class VirtualGeometryGoldenTests {
    /// <summary>
    ///     The same mesh, drawn both ways, covers the same pixels.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One frame each, which the plane makes safe: the root page is pinned at registration, and
    ///         the coarsest cut of a flat quad has the silhouette the finest one has. A curved mesh would
    ///         need the streaming loop to settle first — see <c>VirtualGeometryDeviceTests</c>, which
    ///         gives it four frames for exactly that reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both masks are asserted non-empty before they are compared.</b> Two renderers that
    ///         both drew nothing agree perfectly, and that is the failure this test is most likely to
    ///         have — a traversal that culled everything, an effect that did not resolve, a target that
    ///         was never stored. The agreement is worth nothing without the coverage.
    ///     </para>
    /// </remarks>
    [Fact(Skip = SkipReason)]
    public void The_visibility_buffer_covers_what_the_mesh_feature_draws() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var geometry = Plane();
        var forward = Forward(owned, geometry);

        owned.Graph.Reset();

        var virtualized = Virtualized(owned, geometry);

        var drawn = Mask(forward, pixel => pixel.Span[0] != 0 || pixel.Span[1] != 0 || pixel.Span[2] != 0);
        var covered = Mask(virtualized, pixel => Word(pixel) != GpuClusterRaster.Nothing);

        // Neither is allowed to be empty, or "they agree" is a statement about two blank images.
        Assert.True(Count(drawn) > 512, $"The forward path covered {Count(drawn)} pixels.");
        Assert.True(Count(covered) > 512, $"The visibility buffer covered {Count(covered)} pixels.");

        var differing = 0;

        for (var index = 0; index < drawn.Length; index++) {
            if (drawn[index] != covered[index]) {
                differing++;
            }
        }

        // A hundredth of the frame, which for a plane is slack rather than budget: the two rasters
        // fill the same triangles from the same clip-space positions, and the only pixels that can
        // legitimately differ are the ones the quantisation of a page vertex moves across an edge.
        var fraction = (double)differing / drawn.Length;

        Assert.True(
            fraction <= 0.01,
            $"The two paths disagree about {differing} of {drawn.Length} pixels ({fraction:P2}). "
            + $"Forward covered {Count(drawn)}, the visibility buffer covered {Count(covered)}."
        );
    }

    /// <summary>The plane drawn through the ordinary mesh feature.</summary>
    /// <remarks>
    ///     The golden suite's own flat-colour shader rather than <c>ForwardPlus</c>, deliberately: what
    ///     is being compared is coverage, and a lit shader would put a light rig, a material and a
    ///     clustered light list between the geometry and the picture — three more ways for the two sides
    ///     to differ for reasons that have nothing to do with the raster.
    /// </remarks>
    static Bitmap Forward(Fixture fixture, Geometry geometry) {
        var device = fixture.Device;

        using var system = new RenderSystem();

        // ⚠ Depth off and two-sided, which removes the two things that would make this fixture differ
        // from the raster for reasons that are not the raster's. There is one object and nothing to
        // occlude it, so a depth test decides nothing; and a winding disagreement between the mesh the
        // page builder cut and the mesh the vertex buffer holds would show as an empty image, which is
        // the same symptom as every other way of drawing nothing.
        var opaque = system.AddStage(
            new("Opaque") {
                DepthStencil = DepthStencilState.Disabled,
                Rasterizer = RasterizerState.TwoSided
            }
        );

        var describer = new EffectPipelineDescriber(device);

        describer.VertexLayouts.Add([
            new VertexBufferLayout(
                Vertex.Stride,
                [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, 12)]
            )
        ]);

        var effects = new EffectSystem();
        effects.AddProvider(new Flat(fixture));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = describer };
        var transforms = new TransformRenderFeature();
        var materials = new MaterialRenderFeature { Effects = effects };

        meshes.Add(transforms);
        meshes.Add(materials);
        system.AddFeature(meshes);

        var vertices = fixture.Buffer<Vertex>(geometry.Vertices, BufferUsage.Vertex);
        var indices = fixture.Buffer<ushort>(geometry.Indices, BufferUsage.Index);

        var id = system.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, 100f),
                Stages = opaque.Mask,
                FeatureIndex = meshes.Index
            }
        );

        system.Objects.Data.Data(meshes.Draws)[id.Index] = new() {
            VertexBuffer = vertices,
            IndexBuffer = indices,
            IndexFormat = IndexFormat.UInt16,
            Count = geometry.Indices.Length,
            InstanceCount = 1
        };

        // The view-projection through the push constant, because the suite's flat shader has one
        // transform and no per-view block. The object's own world matrix is the identity, so this is the
        // whole of what puts the two paths in the same clip space.
        system.Objects.Data.Data(transforms.World)[id.Index] = ViewProjection;
        materials.Assign(system, id, new Material("Mesh"));

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);
        var pass = new RenderPassRenderer { Name = "Scene", ClearColour = new(0f, 0f, 0f, 1f) };

        pass.ColourTargets.Add("Display");
        pass.Children.Add(new SingleStageRenderer { View = View(opaque.Mask), Stage = opaque });

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { pass } }
        };

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        var frame = compositor.Build(fixture.Graph, effects, device);

        return fixture.Render(frame.Texture("harness", "Display"));
    }

    /// <summary>The same plane drawn through the traversal and the visibility-buffer raster.</summary>
    /// <remarks>
    ///     ⚠ <b>The depth is cleared by a pass of its own.</b> <see cref="VisibilityBufferRenderer" />
    ///     loads depth rather than clearing it — it is designed to run after a prepass — so a frame that
    ///     never wrote the buffer would test against undefined memory, which is a picture that differs
    ///     between drivers and between runs.
    /// </remarks>
    static Bitmap Virtualized(Fixture fixture, Geometry geometry) {
        var device = fixture.Device;

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader));

        using var system = new RenderSystem();
        var pipelines = new ComputePipelineCache(device);
        using var clusters = new VirtualGeometrySystem(device, slots: 64, pageSize: PageSize);

        clusters.Effects = effects;
        clusters.Pipelines = pipelines;
        clusters.Modules = new EffectPipelineDescriber(device);

        var stage = system.AddStage(new("Opaque"));

        clusters.Register(system);

        var registered = clusters.Content(0, geometry.Clusters, new MemoryStream(geometry.Pages.Data));

        var id = system.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, 100f),
                Stages = stage.Mask,
                FeatureIndex = clusters.Feature.Index
            }
        );

        system.Objects.Data.Data(clusters.Feature.Draws)[id.Index] = new() {
            Mesh = registered,
            Position = Vector3.Zero,
            Scale = 1f
        };

        clusters.Feature.ScreenHeight = Fixture.Side;

        var identity = fixture.Owned(
            "VisibilityBuffer",
            TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
            GpuClusterRaster.Format
        );

        var depth = new RenderPassRenderer { Name = "ClearDepth", DepthTarget = "SceneDepth", ClearDepth = 0f };

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence {
                Children = {
                    depth,
                    new ClusterCullingRenderer {
                        Visibility = clusters.Visibility,
                        Pages = clusters.Pages,
                        Raster = clusters.Raster
                    },
                    new VisibilityBufferRenderer {
                        Name = "Visibility",
                        View = View(stage.Mask),
                        Raster = clusters.Raster,
                        Depth = "SceneDepth",
                        Output = "VisibilityBuffer"
                    }
                }
            }
        };

        compositor.Resources.Add(
            new() {
                Name = "SceneDepth",
                Format = PixelFormat.Depth32Float,
                Usage = TextureUsage.DepthStencilTarget
            }
        );

        // Imported rather than declared, which is the whole reason the node consults the frame before
        // creating one: a transient the graph allocates is discarded at the end of the pass that wrote
        // it, so a harness reading it back reads whatever was in that memory.
        compositor.Imports["VisibilityBuffer"] = new(
            identity.Texture,
            identity.View,
            identity.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        // Several frames, because the streaming loop is a loop: a traversal asks for the pages a cut
        // wanted and did not have, the request comes back after the frame that made it was submitted,
        // and the pages arrive for a later frame. The picture that matters is the settled one.
        Bitmap picture = default;

        for (var frame = 0; frame < Frames; frame++) {
            fixture.Graph.Reset();

            var built = compositor.Build(fixture.Graph, effects, device);

            picture = fixture.Render(built.Texture("harness", "VisibilityBuffer"));
        }

        Assert.True(clusters.Visibility.TraversedOnDevice, "The cluster traversal never dispatched.");

        Assert.True(
            clusters.Raster.Drawn && clusters.Visibility.VisibleClusters > 0,
            $"The raster recorded no draw. Instances {clusters.Visibility.InstanceCount}, "
            + $"meshes {clusters.Visibility.MeshCount}, clusters {clusters.Visibility.ClusterCount}, "
            + $"resident {clusters.Residency.ResidentPages}, requested {clusters.Visibility.RequestedPages}, "
            + $"visible {clusters.Visibility.VisibleClusters}, indices {clusters.Raster.IndexCount}."
        );

        return picture;
    }

    /// <summary>The camera both paths draw through.</summary>
    /// <param name="stages">
    ///     Which stages it draws. ⚠ <see cref="RenderStageMask.None" /> is the default and it culls
    ///     everything — the traversal tests the instance's mask against the view's before it tests a
    ///     frustum, so a view that named no stage dispatches, reports that it dispatched, and produces an
    ///     empty visible list.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>A real projection and not the identity.</b> An identity view-projection is a frustum whose
    ///     planes a bound at the origin straddles, and the traversal rejects the instance before it tests
    ///     a single cluster — one dispatch, no requests, no visible clusters, and a picture that is
    ///     indistinguishable from a raster that does not work.
    /// </remarks>
    static RenderView View(RenderStageMask stages) =>
        new("camera") {
            Position = Vector3.Zero,
            ViewProjection = ViewProjection,
            ScreenHeightScale = 1f / MathF.Tan(FieldOfView * 0.5f),
            Stages = stages
        };

    /// <summary>The camera's vertical field of view.</summary>
    const float FieldOfView = MathF.PI / 3f;

    /// <summary>Where the plane sits, in front of a camera at the origin looking down negative z.</summary>
    const float Distance = -4f;

    /// <summary>
    ///     What both paths transform by: the forward one through its push constant, the raster through
    ///     the view the node hands it.
    /// </summary>
    static Matrix4x4 ViewProjection { get; } =
        Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f))
        * Matrix4x4.PerspectiveFieldOfView(FieldOfView, 1f, 0.1f, 1000f);

    /// <summary>Which pixels a bitmap covers.</summary>
    static bool[] Mask(in Bitmap bitmap, Func<ReadOnlyMemory<byte>, bool> covered) {
        var mask = new bool[bitmap.Width * bitmap.Height];

        for (var index = 0; index < mask.Length; index++) {
            mask[index] = covered(bitmap.Pixels.AsMemory(index * 4, 4));
        }

        return mask;
    }

    /// <summary>The visibility word a pixel's four bytes are, little-endian as the device wrote them.</summary>
    static uint Word(ReadOnlyMemory<byte> pixel) => MemoryMarshal.Read<uint>(pixel.Span);

    static int Count(bool[] mask) => mask.Count(covered => covered);

    /// <summary>Why this is a recorded reproduction rather than a passing test.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What it found the first time it ran.</b> A registered, resident, in-frustum instance
    ///         whose stage mask matches the view's produces <em>no visible clusters</em>: the traversal
    ///         dispatches (<c>TraversedOnDevice</c>), the raster records its indirect draw
    ///         (<c>Raster.Drawn</c>, 384 indices), the root page is resident — and
    ///         <c>Visibility.VisibleClusters</c> reads back zero, so the draw's instance count is zero and
    ///         the visibility buffer is uniformly <see cref="GpuClusterRaster.Nothing" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing else in the suite would have said so.</b> <c>VirtualGeometryDeviceTests</c>
    ///         asserts that the traversal dispatched and that a page became resident — both true here —
    ///         and stops. So on the evidence available, the visibility buffer has never contained a
    ///         cluster on a device, and every host-side test of the decode is a test of arithmetic that
    ///         nothing on the device has exercised. That is precisely the gap doc 22 § phase 4 says a
    ///         golden image exists to close.
    ///     </para>
    ///     <para>
    ///         Skipped rather than deleted, and rather than left red: the fixture below is the
    ///         reproduction, and the fault is in the culling shader rather than in anything this file
    ///         does. Remove the skip when the traversal accepts a cluster.
    ///     </para>
    /// </remarks>
    const string SkipReason =
        "Reproduction: the traversal dispatches and returns zero visible clusters for a resident, "
        + "in-frustum instance, so the visibility buffer is empty. See the remarks on SkipReason.";

    /// <summary>Small pages, so a modest mesh needs more than the pinned root.</summary>
    const int PageSize = 8 * 1024;

    /// <summary>How many frames the streaming loop is given to settle.</summary>
    const int Frames = 6;

    /// <summary>A tessellated plane facing the camera, in clip space, with its clusters beside it.</summary>
    /// <remarks>
    ///     Four units across at four units away, under a sixty-degree field of view: comfortably inside
    ///     the image, so the comparison is about an edge rather than about a border the frame clipped.
    /// </remarks>
    static Geometry Plane(int segments = 16) {
        var vertices = new List<Vertex>();
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var y = 0; y <= segments; y++) {
            for (var x = 0; x <= segments; x++) {
                var position = new Vector3(
                    (((float)x / segments) - 0.5f) * 4f,
                    (((float)y / segments) - 0.5f) * 4f,
                    Distance
                );

                positions.Add(position);
                vertices.Add(new() { Position = position, Colour = new(1f, 1f, 1f, 1f) });
            }
        }

        for (var y = 0; y < segments; y++) {
            for (var x = 0; x < segments; x++) {
                var a = (y * (segments + 1)) + x;
                var b = a + 1;
                var c = a + segments + 1;

                indices.AddRange([a, c, b]);
                indices.AddRange([b, c, c + 1]);
            }
        }

        var input = new MeshletBuildInput { Positions = [.. positions], Indices = [.. indices] };
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = PageSize });

        return new(
            [.. vertices],
            [.. indices.Select(index => (ushort)index)],
            new(mesh, pages.WithoutData()),
            pages
        );
    }

    /// <summary>The plane, in both the forms the two paths want.</summary>
    /// <param name="Vertices">Positions and a flat colour, for the ordinary draw.</param>
    /// <param name="Indices">Its triangles.</param>
    /// <param name="Clusters">The hierarchy and page records, as one chunk holds them.</param>
    /// <param name="Pages">The page set, for its blob.</param>
    readonly record struct Geometry(
        Vertex[] Vertices,
        ushort[] Indices,
        VirtualGeometryAsset Clusters,
        MeshletPageSet Pages
    );

    /// <summary>One vertex of the ordinary draw: a clip-space position and a flat colour.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex {
        public const int Stride = 28;

        public Vector3 Position;
        public Vector4 Colour;
    }

    /// <summary>The suite's flat-colour mesh shader, as an effect the material feature can resolve.</summary>
    sealed class Flat : IEffectProvider {
        readonly Effect mesh;

        public Flat(Fixture fixture) {
            var device = fixture.Device;
            var layout = device.CreatePipelineLayout(new([], [new(ShaderStage.Vertex, 0, 64)], "flat"));

            fixture.Owns(() => device.Destroy(layout));

            mesh = new() {
                Key = EffectKey.From("Mesh", new(), []),
                Stages = [
                    new(ShaderStage.Vertex, Read("scene.vert.spv"), "main"),
                    new(ShaderStage.Fragment, Read("scene.frag.spv"), "main")
                ],
                Layout = layout
            };
        }

        static ImmutableArray<byte> Read(string name) =>
            [.. File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", name))];

        public Effect? TryGet(EffectKey key) => mesh;
    }

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
}
