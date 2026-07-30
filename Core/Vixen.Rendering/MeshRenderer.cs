// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>One corner of a world-space triangle.</summary>
/// <param name="Position">Where it is.</param>
/// <param name="Normal">Which way the surface faces there. Expected to be unit length.</param>
/// <param name="Colour">What colour it is, linear and straight-alpha.</param>
/// <remarks>
///     Forty bytes, and a colour per vertex for the reason <see cref="LineVertex" /> gives: it is what
///     lets one buffer hold geometry that belongs to twenty different objects, each its own colour,
///     without a material or a descriptor set between them.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal, Color4 Colour);

/// <summary>The shaders a <see cref="MeshRenderer" /> is built from.</summary>
/// <remarks>
///     Supplied rather than compiled here, the same seam <see cref="LineShaders" /> describes: turning
///     source into modules belongs to <c>Vixen.Shaders</c> and, once it lands, to Raven.
/// </remarks>
/// <param name="Vertex">The vertex stage.</param>
/// <param name="Fragment">The fragment stage.</param>
public readonly record struct MeshShaders(ShaderHandle Vertex, ShaderHandle Fragment) {
    /// <summary>
    ///     Where the vertex stage reads <see cref="MeshVertex" />'s three attributes: position,
    ///     normal, then colour.
    /// </summary>
    /// <remarks>
    ///     Left unset for a stage compiled from the GLSL beside this file, whose attributes are at
    ///     0, 1 and 2. A Raven stage declaring two streams has them at 2, 3 and 4 — see
    ///     <see cref="VertexLocations" /> for why the number belongs to the shader.
    /// </remarks>
    public VertexLocations Locations { get; init; }
}

/// <summary>Draws world-space triangles: a block-out primitive, a debug hull, a preview.</summary>
/// <remarks>
///     <para>
///         <b><see cref="LineRenderer" /> for surfaces, and deliberately its twin.</b> Same ring of
///         host-visible buffers, same one draw call a frame, same absence of a model matrix and of
///         anything per-object. What a caller hands over is triangles that are already where they go,
///         so a scene of twenty primitives is one buffer write rather than twenty draws with twenty
///         descriptor sets behind them.
///     </para>
///     <para>
///         ⚠ <b>This is not the mesh path.</b> The renderer proper is <c>RenderSystem</c> and its
///         features: culling, sorting, instancing, materials, a pipeline per effect permutation. That
///         is what a game draws its world with, and this would be a bad substitute for it — the
///         vertices go through the CPU every frame, so the cost is linear in <em>vertices</em> rather
///         than in objects. What it is good for is what a debug line list is good for: geometry that
///         belongs to a tool rather than to a scene, where being able to draw anything at all with no
///         material system attached is worth more than throughput.
///     </para>
///     <para>
///         ⚠ <b>And a scene's shapes are not that, which is what
///         <see cref="MeshInstanceRenderer" /> is for.</b> The distinction is whether the geometry
///         changes: a gizmo handle is a different size and colour at every camera position and there
///         is one of it, so rebuilding it per frame is the cheap answer. A block-out mesh is the same
///         geometry for as long as nobody edits it, and there are thousands, so paying per vertex per
///         frame for it is the thing <c>docs/plan/24-blockout-tools.md</c> § B1 called a blocker.
///     </para>
///     <para>
///         ⚠ <b>Indexed, unlike the line renderer, and it has to be.</b> A sphere's vertices are
///         shared by six triangles each; expanding them would sextuple the bytes that cross the bus
///         every frame for geometry that has not moved. The index buffer is a second ring, written in
///         the same call, and a caller appending a second mesh has to offset its indices — which
///         <see cref="Upload" /> does not do for them, because a caller building a frame's geometry
///         knows where each mesh started and this does not.
///     </para>
///     <para>
///         ⚠ <b>Two-sided, and lit as though it were not.</b> Which winding is front depends on the
///         projection, the viewport's Y direction and the handedness of whatever produced the
///         geometry, and a tool renderer that draws nothing at all when one of the three disagrees is
///         a bad way to find that out. The fragment stage flips the normal for a back face instead, so
///         the inside of an open shape is lit rather than black.
///     </para>
/// </remarks>
public sealed class MeshRenderer : IDisposable {
    readonly IGraphicsDevice device;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;
    readonly PipelineHandle overlayPipeline;
    readonly int slots;

    BufferHandle vertices;
    BufferHandle indices;
    long vertexCapacity;
    long indexCapacity;
    int slot;
    bool disposed;

    /// <summary>How many vertices fit in one frame's region.</summary>
    public int VertexCapacity => (int) (vertexCapacity / Marshal.SizeOf<MeshVertex>());

    /// <summary>How many indices fit in one frame's region.</summary>
    public int IndexCapacity => (int) (indexCapacity / sizeof(uint));

    /// <summary>How many regions the ring has.</summary>
    public int Regions => slots;

    /// <summary>Which region the last upload wrote.</summary>
    public int Region => slot;

    /// <summary>How many indices the last upload held.</summary>
    public int Count { get; private set; }

    /// <summary>How many draws the last record issued.</summary>
    public int Draws { get; private set; }

    /// <summary>How much of the last upload was dropped for want of room, in indices.</summary>
    /// <remarks>
    ///     ⚠ <b>Dropped rather than grown</b>, for the reason <see cref="LineRenderer.Dropped" />
    ///     gives — and dropped in whole triangles, so what is left is geometry rather than geometry
    ///     with a corner missing.
    /// </remarks>
    public int Dropped { get; private set; }

    /// <summary>Which way the light comes from, in world space.</summary>
    /// <remarks>
    ///     ⚠ <b>A direction and not a light.</b> There is no lighting model here and there should not
    ///     be one: this exists so that a solid shape reads as a solid shape rather than as a
    ///     silhouette, which one fixed key direction and a little ambient is enough for. Anything that
    ///     wants shadows, or a light somebody placed, wants <c>RenderSystem</c>.
    /// </remarks>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.35f));

    /// <summary>How much light a surface facing away still receives, from zero to one.</summary>
    public float Ambient { get; set; } = 0.35f;

    /// <summary>Builds the pipeline a frame's triangles are drawn with.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The two stages.</param>
    /// <param name="output">What it draws into.</param>
    /// <param name="vertexCapacity">How many vertices one frame may hold.</param>
    /// <param name="indexCapacity">How many indices one frame may hold.</param>
    public MeshRenderer(
        IGraphicsDevice device,
        MeshShaders shaders,
        RenderOutput output,
        int vertexCapacity = 1 << 16,
        int indexCapacity = 1 << 18
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(indexCapacity);

        this.device = device;
        slots = Math.Max(1, device.FramesInFlight);

        shaders.Locations.Require(3, nameof(MeshRenderer));

        // Eighty bytes: the view-projection, then the light direction and the ambient term as one
        // vector four. Under the hundred and twenty-eight every Vulkan implementation guarantees, so
        // this needs nothing asked of the device.
        layout = device.CreatePipelineLayout(
            new([], [new(ShaderStage.Vertex | ShaderStage.Fragment, 0, 80)], "mesh")
        );

        pipeline = Pipeline(shaders, output, DepthStencilState.Default, "mesh");

        // ⚠ A second pipeline differing only in the depth test, and it is the same pair
        // <see cref="LineRenderer" /> has for the same reason: a gizmo has to be reachable through
        // the thing it is moving, and a solid handle that is *also* occluded is worse than a wire one
        // — it disappears completely rather than showing through faintly. Depth is not written
        // either, so a handle drawn over a cube does not then punch a hole in anything drawn after it.
        overlayPipeline = Pipeline(shaders, output, DepthStencilState.Disabled, "mesh overlay");

        Resize(vertexCapacity, indexCapacity);
    }

    /// <summary>Writes a frame's triangles into the next region of the ring.</summary>
    /// <param name="mesh">The vertices.</param>
    /// <param name="triangles">Three indices per triangle, into <paramref name="mesh" />.</param>
    /// <remarks>
    ///     ⚠ <b>A frame that overflows either buffer is truncated to whole triangles and to the
    ///     vertices they name.</b> Half a triangle draws a corner joined to whatever came next, and an
    ///     index past the end of the vertex buffer is a read out of bounds — undefined behaviour
    ///     rather than a missing shape.
    /// </remarks>
    public void Upload(ReadOnlySpan<MeshVertex> mesh, ReadOnlySpan<uint> triangles) {
        ObjectDisposedException.ThrowIf(disposed, this);

        // Advanced here rather than in `Record`, for the reason LineRenderer.Upload gives: the region
        // moved on to is the one used `slots` frames ago, which the device has finished with.
        slot = (slot + 1) % slots;

        var vertexCount = Math.Min(mesh.Length, VertexCapacity);
        var fits = Math.Min(triangles.Length, IndexCapacity) / 3 * 3;

        // ⚠ Every index that names a vertex the truncation dropped would read past the end, so the
        // cut has to be made from the vertices as well as from the indices. Kept as a forward scan
        // that stops at the first triangle it cannot draw: a caller builds a frame's geometry mesh
        // by mesh, so what a vertex overflow costs is the tail — and searching backwards for the
        // last drawable triangle would be quadratic on the frame that overflowed worst.
        var indexCount = 0;

        for (var index = 0; index + 2 < fits; index += 3) {
            if (triangles[index] >= (uint) vertexCount
                || triangles[index + 1] >= (uint) vertexCount
                || triangles[index + 2] >= (uint) vertexCount) {
                break;
            }

            indexCount = index + 3;
        }

        Dropped = triangles.Length - indexCount;
        Count = indexCount;

        if (vertexCount > 0) {
            device.Write(vertices, (long) slot * vertexCapacity, MemoryMarshal.AsBytes(mesh[..vertexCount]));
        }

        if (indexCount > 0) {
            device.Write(indices, (long) slot * indexCapacity, MemoryMarshal.AsBytes(triangles[..indexCount]));
        }
    }

    /// <summary>Draws what the last upload wrote.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="viewProjection">The world-to-clip matrix of the view being drawn.</param>
    /// <param name="depthTested">
    ///     Whether the triangles are hidden by geometry in front of them, and write depth themselves.
    /// </param>
    public void Record(ICommandList commands, in Matrix4x4 viewProjection, bool depthTested = true) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (Count == 0) {
            return;
        }

        Span<Matrix4x4> matrix = [viewProjection];
        Span<Vector4> light = [new(LightDirection, Ambient)];

        commands.BindPipeline(depthTested ? pipeline : overlayPipeline);
        commands.PushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, MemoryMarshal.AsBytes(matrix));
        commands.PushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 64, MemoryMarshal.AsBytes(light));

        // Both bindings move with the region, so the draw's first index and vertex offset stay zero.
        commands.BindVertexBuffer(0, vertices, (long) slot * vertexCapacity);
        commands.BindIndexBuffer(indices, IndexFormat.UInt32, (long) slot * indexCapacity);
        commands.DrawIndexed(Count);

        Draws = 1;
    }

    /// <summary>One of the two pipelines, which differ only in what they do with depth.</summary>
    PipelineHandle Pipeline(MeshShaders shaders, RenderOutput output, DepthStencilState depth, string name) =>
        device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [
                    new(
                        output.ColourCount > 0 ? output.ColourFormats[0] : PixelFormat.Rgba8UNorm,
                        BlendState.PremultipliedAlpha
                    )
                ],
                [
                    new(
                        Marshal.SizeOf<MeshVertex>(),
                        [
                            new(shaders.Locations[0], VertexFormat.Float32X3, 0),
                            new(shaders.Locations[1], VertexFormat.Float32X3, 12),
                            new(shaders.Locations[2], VertexFormat.Float32X4, 24)
                        ]
                    )
                ],
                PrimitiveTopology.TriangleList,
                Rasterizer: RasterizerState.TwoSided,

                // ⚠ The world pipeline tests *and writes*, which is where this differs from the line
                // one. A solid shape has to occlude what is behind it, including the grid — a mesh
                // that only tested depth would have the floor grid drawn straight through it.
                DepthStencil: depth,
                DepthFormat: output.DepthFormat,
                SampleCount: output.SampleCount,
                Name: name
            )
        );

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        device.Destroy(pipeline);
        device.Destroy(overlayPipeline);
        device.Destroy(layout);

        if (vertices.IsValid) {
            device.Destroy(vertices);
        }

        if (indices.IsValid) {
            device.Destroy(indices);
        }
    }

    void Resize(int vertexCount, int indexCount) {
        vertexCapacity = (long) vertexCount * Marshal.SizeOf<MeshVertex>();
        indexCapacity = (long) indexCount * sizeof(uint);

        vertices = device.CreateBuffer(
            new(vertexCapacity * slots, BufferUsage.Vertex, MemoryAccess.HostUpload, "mesh vertices")
        );

        indices = device.CreateBuffer(
            new(indexCapacity * slots, BufferUsage.Index, MemoryAccess.HostUpload, "mesh indices")
        );
    }
}
