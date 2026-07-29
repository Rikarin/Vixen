// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Shapes uploaded once and drawn once per entity, which is what a viewport costs.
/// </summary>
/// <remarks>
///     <para>
///         <strong><c>docs/blockout-tools.md</c> § B1 is the specification this file is the test
///         of.</strong> Its complaint about the path this replaces was not that it was slow: "every
///         mesh in the viewport goes through the CPU every frame", with a cache keyed by primitive
///         kind, so a hundred cubes were one mesh and a hundred <em>edited</em> meshes were a hundred
///         rebuilds a frame — "a drag that redraws at four frames a second is not a slow tool, it is a
///         tool nobody can aim".
///     </para>
///     <para>
///         So what these assert is the shape of the work rather than a picture: one draw per shape, one
///         instance per entity, one pair of buffers behind all of them, and the geometry crossing the
///         bus once. The picture itself is the vertex stage's, and
///         <c>Editor/Vixen.Editor.App/Shaders/MeshInstanced.rvn</c> is where it is written.
///     </para>
/// </remarks>
public sealed class MeshInstanceRendererTests {
    static readonly RenderOutput Output = new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float);

    /// <summary>Two shapes with different vertex counts, so an offset that is wrong shows.</summary>
    static MeshData Cube => MeshPrimitives.Cube();

    static MeshData Sphere => MeshPrimitives.Sphere(12, 6);

    [Fact]
    public void A_hundred_entities_of_one_shape_are_one_draw_of_a_hundred_instances() {
        using var fixture = new Fixture();

        Assert.True(fixture.Renderer.TryRegister(Cube, out var cube));

        var instances = new MeshInstance[100];

        for (var index = 0; index < instances.Length; index++) {
            var at = Matrix4x4.FromTranslation(new Vector3(index * 2f, 0f, 0f));

            instances[index] = MeshInstance.Of(at, Color4.White);
        }

        fixture.Renderer.Upload(instances, [new(cube, 0, instances.Length, Edges: false)]);
        fixture.Draw();

        var draw = Assert.Single(fixture.Draws);

        Assert.Equal(Cube.Indices.Length, draw.A);
        Assert.Equal(100, draw.B);
        Assert.Equal(cube.Slice.FirstIndex, draw.C);
        Assert.Equal(cube.Slice.BaseVertex, draw.D);
        Assert.Equal(0, draw.E);

        Assert.Equal(100, fixture.Renderer.Count);
        Assert.Equal(1, fixture.Renderer.Draws);
        Assert.Equal(0, fixture.Renderer.Dropped);
        Assert.Equal(Cube.TriangleCount * 100, fixture.Renderer.Triangles);
    }

    /// <summary>Two shapes are two draws that bind nothing between them.</summary>
    /// <remarks>
    ///     ⚠ <strong>The claim <see cref="GeometryBuffer" /> exists for, applied to a viewport.</strong>
    ///     Both draws name the same vertex buffer and the same index buffer and differ only in the
    ///     numbers in their arguments — so the second shape's vertex offset has to be past the first
    ///     shape's vertices. Without it both draw the first shape, which reads as the wrong entities
    ///     being the wrong objects rather than as a buffer bug.
    /// </remarks>
    [Fact]
    public void Two_shapes_are_two_draws_out_of_one_pair_of_buffers() {
        using var fixture = new Fixture();

        Assert.True(fixture.Renderer.TryRegister(Cube, out var cube));
        Assert.True(fixture.Renderer.TryRegister(Sphere, out var sphere));

        fixture.Renderer.Upload(
            [
                MeshInstance.Of(Matrix4x4.Identity, Color4.White),
                MeshInstance.Of(Matrix4x4.FromTranslation(new Vector3(4f, 0f, 0f)), Color4.White)
            ],
            [new(cube, 0, 1, Edges: false), new(sphere, 1, 1, Edges: false)]
        );

        fixture.Draw();

        Assert.Equal(2, fixture.Draws.Count);
        Assert.Equal(0, cube.Slice.BaseVertex);
        Assert.Equal(Cube.VertexCount, sphere.Slice.BaseVertex);

        Assert.Equal(Cube.VertexCount, fixture.Draws[1].D);

        // One pipeline for both, so the buffers are bound once rather than per draw: two vertex
        // bindings and one index binding for the whole frame.
        Assert.Equal(2, fixture.CountOf(RecordedCommandKind.BindVertexBuffer));
        Assert.Equal(1, fixture.CountOf(RecordedCommandKind.BindIndexBuffer));
        Assert.Equal(1, fixture.CountOf(RecordedCommandKind.BindPipeline));
    }

    /// <summary>A wireframe is the same vertices in a second index range.</summary>
    /// <remarks>
    ///     Which is what keeps a view mode from costing a second buffer, a second upload or a device
    ///     feature — <c>FillMode.Wireframe</c> needs <c>fillModeNonSolid</c>, which is optional in
    ///     Vulkan and absent on most tiled GPUs.
    /// </remarks>
    [Fact]
    public void A_shapes_edges_are_an_index_range_after_its_triangles() {
        using var fixture = new Fixture();

        Assert.True(fixture.Renderer.TryRegister(Cube, out var cube));

        Assert.Equal(Cube.Indices.Length, cube.TriangleIndices);
        Assert.True(cube.EdgeIndices > 0);
        Assert.Equal(0, cube.EdgeIndices % 2);

        fixture.Renderer.Upload(
            [MeshInstance.Of(Matrix4x4.Identity, Color4.White), MeshInstance.Of(Matrix4x4.Identity, Color4.White)],
            [new(cube, 0, 1, Edges: false), new(cube, 1, 1, Edges: true)]
        );

        fixture.Draw();

        Assert.Equal(2, fixture.Draws.Count);

        var wires = Assert.Single(fixture.Draws, draw => draw.A == cube.EdgeIndices);

        Assert.Equal(cube.Slice.FirstIndex + cube.TriangleIndices, wires.C);
        Assert.Equal(cube.Slice.BaseVertex, wires.D);

        // Two topologies cannot share a pipeline, so this is the one thing that costs a second
        // binding — and the geometry is still the geometry that was uploaded once.
        Assert.Equal(2, fixture.CountOf(RecordedCommandKind.BindPipeline));
        Assert.Equal(cube.SegmentCount, fixture.Renderer.Segments);
    }

    /// <summary>The geometry crosses the bus once, however many frames draw it.</summary>
    [Fact]
    public void Registering_stages_a_copy_and_drawing_again_stages_nothing() {
        using var fixture = new Fixture();

        Assert.True(fixture.Renderer.TryRegister(Cube, out var cube));

        // A vertex copy and an index copy, recorded where a transfer is legal.
        Assert.Equal(2, fixture.Flush());
        Assert.Equal(0, fixture.Flush());

        fixture.Renderer.Upload([MeshInstance.Of(Matrix4x4.Identity, Color4.White)], [new(cube, 0, 1, Edges: false)]);
        fixture.Draw();

        Assert.Equal(0, fixture.Flush());
        Assert.Equal(1, fixture.Renderer.Shapes);
    }

    /// <summary>An overflowing frame loses whole batches rather than parts of one.</summary>
    /// <remarks>
    ///     ⚠ <strong>Half a batch is a draw whose instances read past the end of the region</strong>,
    ///     which is undefined behaviour rather than a missing object. The count is what makes the
    ///     truncation visible instead of a picture quietly missing its end.
    /// </remarks>
    [Fact]
    public void A_batch_that_does_not_fit_the_ring_is_dropped_whole() {
        using var fixture = new Fixture(instances: 4);

        Assert.True(fixture.Renderer.TryRegister(Cube, out var cube));

        var instances = new MeshInstance[6];
        Array.Fill(instances, MeshInstance.Of(Matrix4x4.Identity, Color4.White));

        fixture.Renderer.Upload(instances, [new(cube, 0, 3, Edges: false), new(cube, 3, 3, Edges: false)]);
        fixture.Draw();

        var draw = Assert.Single(fixture.Draws);

        Assert.Equal(3, draw.B);
        Assert.Equal(3, fixture.Renderer.Count);
        Assert.Equal(3, fixture.Renderer.Dropped);
        Assert.Equal(Cube.TriangleCount * 3, fixture.Renderer.Triangles);
    }

    /// <summary>A batch naming a shape that was never registered draws nothing.</summary>
    /// <remarks>
    ///     The case a full geometry buffer produces: the renderer refuses the registration, and a
    ///     caller that carried on regardless would otherwise issue a draw of zero indices from offset
    ///     zero — which is the first shape in the buffer, drawn as though it were this one.
    /// </remarks>
    [Fact]
    public void An_unregistered_shape_is_not_drawn() {
        using var fixture = new Fixture();

        fixture.Renderer.Upload(
            [MeshInstance.Of(Matrix4x4.Identity, Color4.White)],
            [new(default, 0, 1, Edges: false)]
        );

        fixture.Draw();

        Assert.Empty(fixture.Draws);
        Assert.Equal(0, fixture.Renderer.Draws);
    }

    /// <summary>The normal matrix is the inverse transpose, per entity rather than per vertex.</summary>
    /// <remarks>
    ///     A cube scaled <c>4 1 1</c> transformed by its own matrix comes out with normals that are no
    ///     longer perpendicular to their faces, and the shading then slides across the object as it is
    ///     scaled — which reads as the light moving. This is the same assertion the editor's collector
    ///     makes about what it hands over, made about the value the renderer's struct builds.
    /// </remarks>
    [Fact]
    public void The_normal_matrix_keeps_normals_perpendicular_under_a_non_uniform_scale() {
        var scale = Matrix4x4.FromScale(new Vector3(4f, 1f, 1f));
        var transform = scale * Matrix4x4.FromTranslation(new Vector3(3f, 0f, 0f));
        var instance = MeshInstance.Of(transform, Color4.White);

        foreach (var source in Cube.Normals) {
            var normal = Vector3.Normalize(Matrix4x4.TransformDirection(source, instance.Normals));

            Assert.Equal(1f, normal.Length(), 3);
            Assert.Equal(1f, MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z), 3);
        }

        // ⚠ A matrix that cannot be inverted is passed through as itself rather than refused: that is
        // a zero scale, where the entity has no visible surface and any normal will do.
        var flat = MeshInstance.Of(Matrix4x4.FromUniformScale(0f), Color4.White);

        Assert.Equal(Matrix4x4.FromUniformScale(0f), flat.Normals);
    }

    /// <summary>The two structs are laid out where the vertex layout says they are.</summary>
    /// <remarks>
    ///     ⚠ <strong>What a wrong offset produces is not a validation error.</strong> The pipeline's
    ///     attribute offsets are written out by hand — they are the half of a vertex layout a shader has
    ///     no opinion about — so a field reordered or a type widened here is a stage reading a
    ///     transform's second row as its first, or a colour as a normal, on every driver, silently.
    ///     These are the numbers <c>MeshInstanceRenderer.Pipeline</c> passes, asserted from the other
    ///     side.
    /// </remarks>
    [Fact]
    public void The_instance_and_the_shape_vertex_are_laid_out_where_the_pipeline_says() {
        Assert.Equal(24, Marshal.SizeOf<MeshShapeVertex>());
        Assert.Equal(160, Marshal.SizeOf<MeshInstance>());

        var vertex = new MeshShapeVertex(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f));
        var lanes = Floats(in vertex);

        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f], lanes.ToArray());

        var instance = new MeshInstance(
            Matrix4x4.FromTranslation(new Vector3(7f, 8f, 9f)),
            Matrix4x4.FromUniformScale(2f),
            new Color4(0.1f, 0.2f, 0.3f, 0.4f),
            new Vector4(2.5f, 2f, 1f, 0f)
        );

        lanes = Floats(in instance);

        // ⚠ The transform's fourth *row* is its translation, at float twelve. The four per-instance
        // matrix attributes are the matrix's rows because `Matrix4x4` is row-major under the row-vector
        // convention — read as columns, every object would be drawn under the transpose of its own
        // transform, which upright unrotated ones survive looking correct.
        Assert.Equal([7f, 8f, 9f, 1f], lanes[12..16].ToArray());

        // The normal matrix at sixty-four, its three read rows at 64, 80 and 96.
        Assert.Equal(2f, lanes[16]);
        Assert.Equal(2f, lanes[21]);
        Assert.Equal(2f, lanes[26]);

        // Then the colour, then the style: an outline width, a bias, a flat-lighting flag and a
        // colour-by-normal flag, in the order the shader's four lanes are documented in.
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], lanes[32..36].ToArray());
        Assert.Equal([2.5f, 2f, 1f, 0f], lanes[36..40].ToArray());
    }

    /// <summary>One value's bytes, as the floats a vertex attribute would be fetched from.</summary>
    static ReadOnlySpan<float> Floats<T>(in T value)
        where T : struct =>
        MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>A device, a renderer over it, and the commands one frame recorded.</summary>
    /// <remarks>
    ///     The Null backend refuses a draw outside a render pass, and a copy inside one, which is what
    ///     keeps this fixture honest about where each half of a frame belongs: the geometry flush before
    ///     the pass, the draws inside it.
    /// </remarks>
    sealed class Fixture : IDisposable {
        readonly NullDevice device = new(new() { Record = true });
        readonly TextureViewHandle view;

        public Fixture(int instances = 1 << 10) {
            var target = device.CreateTexture(
                new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget, Name: "target")
            );

            view = device.CreateTextureView(target);

            Renderer = new(
                device,
                new(
                    device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "instanced vertex"),
                    device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "instanced fragment")
                ),
                Output,
                instances,
                vertexCapacity: 1 << 12,
                indexCapacity: 1 << 14
            );
        }

        public MeshInstanceRenderer Renderer { get; }

        /// <summary>Every draw the last <see cref="Draw" /> recorded, in order.</summary>
        public IReadOnlyList<RecordedCommand> Draws { get; private set; } = [];

        public void Dispose() {
            Renderer.Dispose();
            device.Dispose();
        }

        /// <summary>Records the pending geometry copies, and answers how many there were.</summary>
        public int Flush() {
            using var list = device.BeginCommandList();

            var copies = Renderer.Flush(list);

            list.Finish();
            device.GraphicsQueue.Submit([list]);

            return copies;
        }

        /// <summary>Records one frame's draws into a pass.</summary>
        public void Draw() {
            device.Recorder!.Clear();

            using var list = device.BeginCommandList();

            Renderer.Flush(list);
            list.BeginRenderPass(new([new(view)], name: "scene"));
            Renderer.Record(list, View);
            list.EndRenderPass();
            list.Finish();

            device.GraphicsQueue.Submit([list]);

            Draws = device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed);
        }

        public int CountOf(RecordedCommandKind kind) => device.Recorder!.CountOf(kind);

        static MeshInstanceView View =>
            new(
                Matrix4x4.Identity,
                new Vector3(0f, 0f, 10f),
                -Vector3.UnitZ,
                0.05f,
                Orthographic: false,
                PixelScale: 0.002f
            );
    }
}
