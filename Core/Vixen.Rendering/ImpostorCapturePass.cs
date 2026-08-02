// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>The geometry one bake photographs.</summary>
/// <param name="Vertices">Position and normal, interleaved, twenty-four bytes a vertex.</param>
/// <param name="Indices">Its index buffer, 32-bit.</param>
/// <param name="IndexCount">How many indices to draw.</param>
/// <param name="Centre">Where the mesh's bounding sphere is, in the mesh's own space.</param>
/// <param name="Radius">And how far it reaches. Positive.</param>
/// <remarks>
///     ⚠ <b>The bounding sphere is the mesh's rather than the bake's, and both halves of the atlas
///     depend on it.</b> Every cell of a grid is fitted to one radius — see
///     <see cref="ImpostorBake.Record" /> — so a radius that is too small clips the silhouette on
///     every view at once, and one that is too large shrinks the tree inside its cell and spends the
///     atlas on empty texels.
/// </remarks>
public readonly record struct ImpostorMesh(
    BufferHandle Vertices,
    BufferHandle Indices,
    int IndexCount,
    Vector3 Centre,
    float Radius
);

/// <summary>Draws a mesh into an impostor atlas: the caller <see cref="ImpostorBake" /> wants.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T7]'s missing half.</b> <see cref="ImpostorBake" /> owns the atlas, the
///         viewports and the two colour targets, and takes the draw as a delegate on purpose — a bake
///         renders the mesh with whatever material the caller has, and a class that owned a pipeline
///         would be a class that decided what a tree looks like. What was missing was anybody with a
///         pipeline to hand it, so the bake had no caller at all.
///     </para>
///     <para>
///         ⚠ <b>The albedo is a constant, and that is a stated simplification rather than an
///         oversight.</b> A tree is bark and leaves — two materials over one mesh — and a faithful
///         bake draws it once per material through the same material system the level uses. What this
///         produces is the correct <em>silhouette</em> with a flat colour, which is the half of an
///         impostor a forest four hundred metres away actually reads; the alpha is the coverage, and
///         the coverage is what the far field is made of.
///     </para>
///     <para>
///         ⚠ <b>One descriptor set per cell, not one per bake.</b> Each cell is a different camera
///         and the camera is in the uniform block, so a bake of an 8×8 grid writes sixty-four blocks
///         into one buffer and binds a different offset per draw. Rewriting one block per cell would
///         be rewriting a buffer the pass before it is still reading.
///     </para>
/// </remarks>
public sealed class ImpostorCapturePass : IDisposable {
    /// <summary>How many bytes one vertex of a captured mesh is.</summary>
    /// <remarks>Three floats of position and three of normal. No UVs, because the albedo is flat.</remarks>
    public const int VertexSizeInBytes = 24;

    /// <summary>What one cell's constant block is aligned to.</summary>
    /// <remarks>
    ///     ⚠ <b>256, which is the largest <c>minUniformBufferOffsetAlignment</c> in practice and is
    ///     therefore the one number that is right everywhere.</b> The RHI does not report the device's
    ///     own, and a bake is sixty-four blocks of a hundred and forty-four bytes — so over-aligning
    ///     costs seven kilobytes once and asking would cost an RHI addition for it.
    /// </remarks>
    public const int BlockAlignment = 256;

    readonly IGraphicsDevice device;
    readonly BufferHandle constants;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;
    readonly DescriptorSetHandle[] sets;
    readonly int stride;

    bool disposed;

    /// <summary>Creates the pipeline a bake draws with.</summary>
    /// <param name="device">The device.</param>
    /// <param name="vertex">The <c>ImpostorCapture</c> vertex stage.</param>
    /// <param name="fragment">And its fragment stage.</param>
    /// <param name="cells">How many cells the atlas has, which is how many sets are needed.</param>
    /// <param name="colourFormat">What the albedo atlas is stored as.</param>
    /// <param name="normalFormat">And the normal atlas.</param>
    /// <param name="depthFormat">And the depth target.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    /// <exception cref="ArgumentException">A stage is not valid.</exception>
    public ImpostorCapturePass(
        IGraphicsDevice device,
        ShaderHandle vertex,
        ShaderHandle fragment,
        int cells,
        PixelFormat colourFormat = PixelFormat.Rgba8UNorm,
        PixelFormat normalFormat = PixelFormat.Rgba8UNorm,
        PixelFormat depthFormat = PixelFormat.Depth32Float
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);

        if (!vertex.IsValid || !fragment.IsValid) {
            throw new ArgumentException("A capture needs both a vertex and a fragment stage.", nameof(vertex));
        }

        this.device = device;

        // ⚠ Aligned up to `BlockAlignment`, because an offset into a uniform buffer has a
        // device-imposed granularity. An offset a device refuses is a descriptor write that fails,
        // and a block read from an address the host did not write is a cell baked with another
        // cell's camera — sixty-four photographs of a tree from one angle.
        stride = ((ImpostorCaptureKeys.ConstantBufferSize + BlockAlignment - 1) / BlockAlignment) * BlockAlignment;

        constants = device.CreateBuffer(
            new((long)stride * cells, BufferUsage.Uniform, MemoryAccess.HostUpload, "impostor capture constants")
        );

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(
                        ImpostorCaptureKeys.ConstantBufferBinding,
                        DescriptorKind.UniformBuffer,
                        ShaderStage.Vertex | ShaderStage.Fragment
                    )
                ],
                "impostor capture"
            )
        );

        layout = device.CreatePipelineLayout(new([setLayout], [], "impostor capture"));

        pipeline = device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(colourFormat, BlendState.Opaque), new(normalFormat, BlendState.Opaque)],
                [
                    new(
                        VertexSizeInBytes,
                        [
                            new(ImpostorCaptureKeys.PositionLocation, VertexFormat.Float32X3, 0),
                            new(ImpostorCaptureKeys.NormalLocation, VertexFormat.Float32X3, 12)
                        ]
                    )
                ],
                PrimitiveTopology.TriangleList,

                // ⚠ Two-sided, and a bake is the one place that is unarguable. A leaf card is a
                // single quad and half a tree's cards face away from any given cell; culling them
                // photographs a tree with holes in it, which then blend into the far field for ever.
                RasterizerState.Default with { Cull = CullMode.None },
                DepthStencilState.Default,
                depthFormat,
                1,
                "impostor capture"
            )
        );

        sets = new DescriptorSetHandle[cells];

        for (var index = 0; index < cells; index++) {
            sets[index] = device.CreateDescriptorSet(setLayout);
        }
    }

    /// <summary>What the mesh is photographed as, until a bake resolves its material.</summary>
    public Color4 BaseColour { get; set; } = new(0.35f, 0.45f, 0.25f, 1f);

    /// <summary>How many cells the last <see cref="Bake" /> drew into.</summary>
    public int CellsDrawn { get; private set; }

    /// <summary>Photographs a mesh into every cell of an atlas.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="bake">The atlas being filled.</param>
    /// <param name="mesh">What to photograph.</param>
    /// <returns>How many cells were drawn.</returns>
    /// <exception cref="ArgumentNullException">There is no command list or no bake.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    /// <remarks>
    ///     ⚠ <b>The model matrix is the identity and the fitting is in the view.</b>
    ///     <see cref="ImpostorView.For" /> already places the camera against the mesh's own centre and
    ///     radius, so a second normalisation here would apply the fit twice — a tree a quarter of the
    ///     size it should be, in every cell, which reads as an atlas resolution problem.
    /// </remarks>
    public int Bake(ICommandList commands, ImpostorBake bake, in ImpostorMesh mesh) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(bake);
        ObjectDisposedException.ThrowIf(disposed, this);

        CellsDrawn = 0;

        if (mesh.IndexCount <= 0 || !mesh.Vertices.IsValid || !mesh.Indices.IsValid) {
            return 0;
        }

        var vertices = mesh.Vertices;
        var indices = mesh.Indices;
        var indexCount = mesh.IndexCount;
        var colour = BaseColour;
        var side = bake.Atlas.Grid.Side;

        var drawn = bake.Record(
            commands,
            mesh.Centre,
            mesh.Radius,
            (list, cell) => {
                var slot = (cell.Cell.Z * side) + cell.Cell.X;

                if (slot >= sets.Length) {
                    return;
                }

                var block = new byte[ImpostorCaptureKeys.ConstantBufferSize];

                new ImpostorCaptureConstants {
                    ViewViewProjection = cell.View.View * cell.View.Projection,
                    ViewModel = Matrix4x4.Identity,
                    BaseColour = new(colour.R, colour.G, colour.B, colour.A)
                }.Write(block);

                device.Write(constants, (long)slot * stride, block);

                device.UpdateDescriptorSet(
                    sets[slot],
                    [
                        DescriptorWrite.Uniform(
                            ImpostorCaptureKeys.ConstantBufferBinding,
                            constants,
                            (long)slot * stride,
                            ImpostorCaptureKeys.ConstantBufferSize
                        )
                    ]
                );

                list.BindPipeline(pipeline);
                list.BindDescriptorSet(DescriptorSetSlot.PerMaterial, sets[slot]);
                list.BindVertexBuffer(0, vertices);
                list.BindIndexBuffer(indices, IndexFormat.UInt32);
                list.DrawIndexed(indexCount, 1);
            }
        );

        CellsDrawn = drawn;

        return drawn;
    }

    /// <summary>Packs positions and normals the way <see cref="ImpostorMesh" /> wants them.</summary>
    /// <param name="positions">One per vertex.</param>
    /// <param name="normals">The same, or empty for a mesh with none.</param>
    /// <param name="into">Where to put the bytes; at least <c>positions.Length × 24</c>.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room, or the counts disagree.</exception>
    /// <remarks>
    ///     ⚠ <b>A mesh with no normals gets <c>+Y</c> rather than a zero.</b> A zero normal
    ///     normalises to a NaN, and a NaN written into the normal atlas is a texel that stays NaN
    ///     through the dilation and the whole mip chain — one bad vertex turning a whole impostor
    ///     black at a distance.
    /// </remarks>
    public static int Pack(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, Span<byte> into) {
        if (normals.Length > 0 && normals.Length != positions.Length) {
            throw new ArgumentException(
                $"{positions.Length} positions and {normals.Length} normals is not a mesh.",
                nameof(normals)
            );
        }

        var required = positions.Length * VertexSizeInBytes;

        if (into.Length < required) {
            throw new ArgumentException($"Packing {positions.Length} vertices needs {required} bytes.", nameof(into));
        }

        for (var index = 0; index < positions.Length; index++) {
            var at = index * VertexSizeInBytes;
            var normal = normals.Length > 0 ? normals[index] : new Vector3(0f, 1f, 0f);

            if (normal.LengthSquared() <= 0f) {
                normal = new(0f, 1f, 0f);
            }

            MemoryMarshal.Write(into[at..], positions[index]);
            MemoryMarshal.Write(into[(at + 12)..], normal);
        }

        return required;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in sets) {
            device.Destroy(set);
        }

        device.Destroy(constants);
        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
    }
}
