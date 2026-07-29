// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>One end of a line segment, in world space.</summary>
/// <param name="Position">Where it is.</param>
/// <param name="Colour">What colour it is there, linear and straight-alpha.</param>
/// <remarks>
///     A colour per <i>vertex</i> rather than per segment, so a line can fade along its length —
///     which is what a grid line disappearing into the distance and a velocity vector shaded by
///     speed both are. A segment whose ends agree costs the same to draw.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct LineVertex(Vector3 Position, Color4 Colour);

/// <summary>The shaders a <see cref="LineRenderer" /> is built from.</summary>
/// <remarks>
///     Supplied rather than compiled here, the same seam <c>UiShaders</c> describes: turning source
///     into modules belongs to <c>Vixen.Shaders</c> and, once it lands, to Raven. What this must not
///     do is grow a compiler.
/// </remarks>
/// <param name="Vertex">The vertex stage.</param>
/// <param name="Fragment">The fragment stage.</param>
public readonly record struct LineShaders(ShaderHandle Vertex, ShaderHandle Fragment);

/// <summary>Draws world-space line segments: a grid, a gizmo, a debug ray, a collider's outline.</summary>
/// <remarks>
///     <para>
///         <b>The pipeline <c>DebugDraw</c>'s geometry comes out of, and the editor's grid with it.</b>
///         It is deliberately not tied to either: what arrives here is a span of vertices in world
///         space, so the editor's grid, a gizmo's arms and a physics engine's contact normals all go
///         through one pipeline rather than three. <c>Vixen.Engine.Renderer</c> is the piece that
///         turns an accumulator into that span.
///     </para>
///     <para>
///         <b>No model matrix and no per-object anything.</b> A line is already where it is. That is
///         what makes the whole of a frame's lines one draw call and one buffer write, however many
///         things produced them — and it is why the vertex is twenty-eight bytes with nothing in it
///         but a position and a colour.
///     </para>
///     <para>
///         ⚠ <b>Host-visible buffers, rewritten every frame, ringed per frame in flight.</b> Same
///         arrangement and same reasoning as <c>UiRenderer</c>: this geometry is read once by one
///         draw and thrown away, so a staging copy would add a transfer and a barrier to save
///         nothing — and writing one region while the GPU still reads it for an unfinished frame is
///         what makes a grid flicker.
///     </para>
///     <para>
///         ⚠ <b>Line width is one pixel and is not settable.</b> <c>lineWidth</c> above one needs a
///         device feature that is optional in Vulkan and absent on most tiled GPUs, so a renderer
///         that offered it would draw different pictures on different machines. A thick line is a
///         quad, and that belongs to whoever wants one rather than here.
///     </para>
/// </remarks>
public sealed class LineRenderer : IDisposable {
    readonly IGraphicsDevice device;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;
    readonly PipelineHandle overlayPipeline;
    readonly int slots;

    BufferHandle vertices;
    long capacity;
    int slot;
    bool disposed;

    /// <summary>How many vertices fit in one frame's region.</summary>
    public int Capacity => (int) (capacity / Marshal.SizeOf<LineVertex>());

    /// <summary>How many regions the ring has.</summary>
    public int Regions => slots;

    /// <summary>Which region the last upload wrote.</summary>
    public int Region => slot;

    /// <summary>How many vertices the last upload held.</summary>
    public int Count { get; private set; }

    /// <summary>How many draws the last record issued.</summary>
    public int Draws { get; private set; }

    /// <summary>How many vertices the last upload dropped for want of room.</summary>
    /// <remarks>
    ///     ⚠ <b>Dropped rather than grown, and counted rather than thrown.</b> A frame that produced
    ///     more lines than fit is a debug overlay somebody left on, not a bug to take the process
    ///     down for — and growing the buffer mid-frame would mean recreating it while the GPU reads
    ///     it. The count is what makes the truncation visible instead of a picture that is quietly
    ///     missing its end.
    /// </remarks>
    public int Dropped { get; private set; }

    /// <summary>Builds the pipeline a frame's lines are drawn with.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The two stages.</param>
    /// <param name="output">What it draws into.</param>
    /// <param name="capacity">How many vertices one frame may hold.</param>
    public LineRenderer(IGraphicsDevice device, LineShaders shaders, RenderOutput output, int capacity = 1 << 16) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        this.device = device;
        slots = Math.Max(1, device.FramesInFlight);

        layout = device.CreatePipelineLayout(
            new([], [new(ShaderStage.Vertex, 0, 64)], "line")
        );

        pipeline = Pipeline(shaders, output, depthTest: true, "line");

        // ⚠ A second pipeline differing only in the depth test, because a gizmo has to be reachable
        // through the thing it is moving. An editor that hid its handles behind geometry is one where
        // the only way to grab an axis is to orbit until nothing is in front of it.
        overlayPipeline = Pipeline(shaders, output, depthTest: false, "line overlay");

        Resize(capacity);
    }

    /// <summary>Writes a frame's lines into the next region of the ring.</summary>
    /// <param name="segments">The vertices, two per segment.</param>
    /// <remarks>
    ///     ⚠ <b>An odd number of vertices draws one fewer segment.</b> The topology pairs them, so a
    ///     caller that emitted half a line gets the half dropped rather than a segment joining it to
    ///     whatever came next — which is what the alternative, rounding up, would draw.
    /// </remarks>
    public void Upload(ReadOnlySpan<LineVertex> segments) {
        ObjectDisposedException.ThrowIf(disposed, this);

        // ⚠ Advanced here rather than in Record, because this is the call that writes: the region
        // moved on to is the one used `slots` frames ago, which the device has finished with.
        slot = (slot + 1) % slots;

        var fits = Math.Min(segments.Length, Capacity) & ~1;

        Dropped = segments.Length - fits;
        Count = fits;

        if (fits == 0) {
            return;
        }

        device.Write(vertices, (long) slot * capacity, MemoryMarshal.AsBytes(segments[..fits]));
    }

    /// <summary>Draws what the last upload wrote.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="viewProjection">The world-to-clip matrix of the view being drawn.</param>
    /// <param name="depthTested">Whether the lines are hidden by geometry in front of them.</param>
    public void Record(ICommandList commands, in Matrix4x4 viewProjection, bool depthTested = true) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (Count == 0) {
            return;
        }

        Span<Matrix4x4> matrix = [viewProjection];

        commands.BindPipeline(depthTested ? pipeline : overlayPipeline);
        commands.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes(matrix));

        // The vertex binding moves with the region, so the draw's first vertex stays zero — the same
        // arrangement that makes UiRenderer's ring cost nothing downstream.
        commands.BindVertexBuffer(0, vertices, (long) slot * capacity);
        commands.Draw(Count);

        Draws = 1;
    }

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
    }

    void Resize(int count) {
        capacity = (long) count * Marshal.SizeOf<LineVertex>();

        vertices = device.CreateBuffer(
            new(capacity * slots, BufferUsage.Vertex, MemoryAccess.HostUpload, "lines")
        );
    }

    PipelineHandle Pipeline(LineShaders shaders, RenderOutput output, bool depthTest, string name) =>
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
                        Marshal.SizeOf<LineVertex>(),
                        [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, 12)]
                    )
                ],
                PrimitiveTopology.LineList,

                // Culling is meaningless for a line — it has no facing — and leaving it on is the
                // kind of state that draws nothing on one backend and everything on another.
                Rasterizer: RasterizerState.TwoSided,

                // ⚠ Tested and not written. A line that wrote depth would occlude the geometry it is
                // drawn over, which for a one-pixel grid line means a one-pixel hole in whatever is
                // behind it. The comparison is the engine's reversed one, which the depth-only pass
                // and every other stage already agree on.
                DepthStencil: depthTest ? DepthStencilState.TestOnly : DepthStencilState.Disabled,
                DepthFormat: output.DepthFormat,
                SampleCount: output.SampleCount,
                Name: name
            )
        );
}
