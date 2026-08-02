// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The bake's caller — [docs/plan/31 § T7]'s owed half.</summary>
/// <remarks>
///     ⚠ <b><see cref="ImpostorBake" /> took its draw as a delegate and nobody ever passed one.</b>
///     Its own tests bake twenty-five cells with a callback that appends to a list — which proves the
///     viewports and the cameras and says nothing about whether anything is ever drawn into them. What
///     is asserted here is the pipeline, the vertex buffer and the draw arriving inside the bake's
///     pass.
/// </remarks>
public sealed class ImpostorCapturePassTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static ImpostorAtlas Layout(int side = 3, int cellSize = 64, int padding = 4) =>
        new(new ImpostorGrid(side), cellSize, padding);

    ImpostorCapturePass Pass(int cells) =>
        new(
            device,
            device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "capture.vs"),
            device.CreateShader(ShaderStage.Fragment, [5, 6, 7, 8], "capture.fs"),
            cells
        );

    /// <summary>A triangle, packed the way the pass wants it.</summary>
    ImpostorMesh Mesh() {
        Span<Vector3> positions = [new(-1f, 0f, 0f), new(1f, 0f, 0f), new(0f, 2f, 0f)];
        Span<Vector3> normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)];

        var bytes = new byte[positions.Length * ImpostorCapturePass.VertexSizeInBytes];

        ImpostorCapturePass.Pack(positions, normals, bytes);

        var vertices = device.CreateBuffer(
            new(bytes.Length, BufferUsage.Vertex, MemoryAccess.HostUpload, "capture vertices")
        );

        var indices = device.CreateBuffer(
            new(3 * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, "capture indices")
        );

        device.Write(vertices, 0, bytes);
        device.Write(indices, 0, MemoryMarshal.AsBytes<uint>([0, 1, 2]));

        return new(vertices, indices, 3, new(0f, 1f, 0f), 2f);
    }

    /// <summary>Every cell of the atlas gets the mesh drawn into it.</summary>
    [Fact]
    public void EveryCellIsPhotographed() {
        var atlas = Layout();

        using var bake = new ImpostorBake(device, atlas);
        using var pass = Pass(atlas.Grid.CellCount);

        var commands = device.BeginCommandList();

        var drawn = pass.Bake(commands, bake, Mesh());

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Equal(9, drawn);
        Assert.Equal(9, pass.CellsDrawn);
        Assert.Equal(9, device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed).Count);
    }

    /// <summary>And each of those draws is bound, rather than issued into nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Nine draws with no pipeline is what a callback that only counted cells produces</b>,
    ///     and on this device it is indistinguishable from nine that work.
    /// </remarks>
    [Fact]
    public void EachDrawHasItsPipelineAndItsGeometry() {
        var atlas = Layout(side: 2);

        using var bake = new ImpostorBake(device, atlas);
        using var pass = Pass(atlas.Grid.CellCount);

        var commands = device.BeginCommandList();

        pass.Bake(commands, bake, Mesh());

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorder = device.Recorder!;

        Assert.Equal(4, recorder.OfKind(RecordedCommandKind.BindPipeline).Count);
        Assert.Equal(4, recorder.OfKind(RecordedCommandKind.BindDescriptorSet).Count);
        Assert.Equal(4, recorder.OfKind(RecordedCommandKind.BindVertexBuffer).Count);
        Assert.Equal(4, recorder.OfKind(RecordedCommandKind.BindIndexBuffer).Count);
    }

    /// <summary>The whole bake is one render pass, which is what the caller must not break.</summary>
    /// <remarks>
    ///     ⚠ <b>A caller that opened its own pass per cell would clear and resolve the atlas once per
    ///     cell</b> — the cost <see cref="ImpostorBake.Record" />'s single pass exists to avoid, and it
    ///     would also clear away every cell baked before it.
    /// </remarks>
    [Fact]
    public void TheCaptureStaysInsideTheBakesOnePass() {
        var atlas = Layout();

        using var bake = new ImpostorBake(device, atlas);
        using var pass = Pass(atlas.Grid.CellCount);

        var commands = device.BeginCommandList();

        pass.Bake(commands, bake, Mesh());

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));
    }

    /// <summary>A mesh with nothing in it bakes nothing, rather than recording empty draws.</summary>
    [Fact]
    public void AnEmptyMeshIsNotPhotographed() {
        var atlas = Layout();

        using var bake = new ImpostorBake(device, atlas);
        using var pass = Pass(atlas.Grid.CellCount);

        Assert.Equal(0, pass.Bake(device.BeginCommandList(), bake, default));
    }

    /// <summary>A vertex with no normal is given one rather than a zero.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero normalises to a NaN, and a NaN in the normal atlas survives the dilation and
    ///     the whole mip chain.</b> One bad vertex turns a whole impostor black at a distance, which
    ///     is a very long way from the vertex that caused it.
    /// </remarks>
    [Fact]
    public void AMeshWithNoNormalsGetsUsableOnes() {
        Span<Vector3> positions = [new(0f, 0f, 0f), new(1f, 0f, 0f)];

        var bytes = new byte[positions.Length * ImpostorCapturePass.VertexSizeInBytes];

        Assert.Equal(bytes.Length, ImpostorCapturePass.Pack(positions, [], bytes));

        var normal = MemoryMarshal.Read<Vector3>(bytes.AsSpan(12));

        Assert.Equal(new Vector3(0f, 1f, 0f), normal);

        // And a zero normal that *was* supplied is replaced too, which is the case a broken import
        // produces rather than an absent array.
        Span<Vector3> zeroed = [Vector3.Zero, Vector3.Zero];

        ImpostorCapturePass.Pack(positions, zeroed, bytes);

        Assert.Equal(new Vector3(0f, 1f, 0f), MemoryMarshal.Read<Vector3>(bytes.AsSpan(12)));
    }

    /// <summary>A cell's constants are its own, at its own aligned offset.</summary>
    /// <remarks>
    ///     ⚠ <b>One block reused would bake every cell with whichever camera was written last.</b>
    ///     That is sixty-four photographs of a tree from one angle, in an atlas whose whole purpose
    ///     is that they differ — and it looks like an impostor that does not turn.
    /// </remarks>
    [Fact]
    public void EachCellsCameraHasItsOwnAlignedBlock() {
        Assert.Equal(0, ImpostorCapturePass.BlockAlignment % 256);
        Assert.True(ImpostorCapturePass.BlockAlignment >= Vixen.Shaders.Generated.ImpostorCaptureKeys.ConstantBufferSize);
    }
}
