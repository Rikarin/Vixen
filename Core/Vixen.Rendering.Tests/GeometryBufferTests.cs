// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Many meshes in one pair of buffers — the other half of the sentence about what a draw binds
///     per object.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/23-bindless-materials.md</c> opens with "<c>MeshRenderFeature</c> binds a vertex
///         buffer, an index buffer and a material set per object", and a draw that binds anything per
///         object cannot be merged with its neighbour. Material records removed the third. This is
///         the first two.
///     </para>
///     <para>
///         ⚠ <strong>One buffer per vertex layout, and not by convention.</strong> A draw's
///         <c>vertexOffset</c> is a vertex <em>count</em> that the GPU multiplies by the pipeline's
///         stride. Two formats in one buffer would each be read at the other's stride, so the stride
///         belongs to the buffer.
///     </para>
/// </remarks>
public sealed class GeometryBufferTests : IDisposable {
    const int Stride = 32;

    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    GeometryBuffer Build(int vertices = 1024, int indices = 4096) =>
        new(device, Stride, vertices, indices);

    /// <summary>Writing past the staging region is refused before it is attempted, not thrown for.</summary>
    /// <remarks>
    ///     ⚠ <b>The crash this replaced was a viewport opening a block-out scene.</b> Every shape a
    ///     frame meets for the first time is staged, the region holds one flush's worth and can only
    ///     be grown while nothing refers to it — so the tenth wall in a room threw in the middle of a
    ///     frame that had already recorded half its draws. A caller can ask instead.
    /// </remarks>
    [Fact]
    public void A_full_staging_region_answers_rather_than_throwing() {
        using var geometry = Build(vertices: 1 << 16, indices: 1 << 16);

        var block = new byte[32 * 1024];

        // ⚠ Room for the first write however large it is, because nothing refers to an empty region
        // and it will simply be grown. That is what makes a single mesh bigger than the whole region
        // registrable at all rather than permanently deferred.
        Assert.True(geometry.CanStage(long.MaxValue / 2));

        Assert.True(geometry.TryAllocate(block.Length / Stride, 0, out var first));

        geometry.Write(first, block, []);

        Assert.True(geometry.CanStage(16 * 1024));
        Assert.True(geometry.TryAllocate(block.Length / Stride, 0, out var second));

        geometry.Write(second, block, []);

        // Two thirds of a sixty-four kilobyte region are gone, so the third does not fit — and saying
        // so is the whole point.
        Assert.False(geometry.CanStage(block.Length));
        Assert.Equal(64 * 1024, geometry.PendingBytes);
    }

    /// <summary>A reservation grows the region while nothing is using it, and declines afterwards.</summary>
    [Fact]
    public void A_reservation_grows_an_idle_region_and_refuses_a_busy_one() {
        using var geometry = Build(vertices: 1 << 16, indices: 1 << 16);

        Assert.True(geometry.Reserve(4 << 20));
        Assert.True(geometry.StagingCapacity >= 4 << 20);

        var block = new byte[32 * 1024];

        Assert.True(geometry.TryAllocate(block.Length / Stride, 0, out var slice));

        geometry.Write(slice, block, []);

        // ⚠ Refused once something is staged rather than throwing, because growing would abandon the
        // bytes a recorded copy points at — the caller's answer is to flush and submit first, and a
        // false is how it is told so.
        Assert.False(geometry.Reserve(64 << 20));
        Assert.True(geometry.Reserve(1 << 10));
    }

    /// <summary>Two meshes share the handles and differ only in their offsets.</summary>
    /// <remarks>
    ///     The whole claim in one assertion. Two <c>MeshDraw</c>s naming the same two buffers is what
    ///     lets the draw loop bind them once; the offsets are what keeps them different meshes.
    /// </remarks>
    [Fact]
    public void Two_meshes_share_the_buffers_and_differ_in_their_offsets() {
        using var geometry = Build();

        Assert.True(geometry.TryAllocate(64, 96, out var first));
        Assert.True(geometry.TryAllocate(32, 48, out var second));

        var one = default(MeshDraw);
        var two = default(MeshDraw);

        geometry.Apply(ref one, first);
        geometry.Apply(ref two, second);

        Assert.Equal(one.VertexBuffer, two.VertexBuffer);
        Assert.Equal(one.IndexBuffer, two.IndexBuffer);

        Assert.Equal(0, one.VertexOffset);
        Assert.Equal(64, two.VertexOffset);
        Assert.Equal(0, one.FirstIndex);
        Assert.Equal(96, two.FirstIndex);

        Assert.Equal(96, one.Count);
        Assert.Equal(48, two.Count);
    }

    /// <summary>Space comes back, and comes back joined up.</summary>
    /// <remarks>
    ///     <strong>What a wrong answer costs.</strong> Coalescing is not tidiness. Without it a
    ///     buffer that loaded and unloaded a level twice holds one free range per mesh, and refuses a
    ///     large allocation while reporting itself almost entirely free — which reads as a capacity
    ///     bug and is not one. The fragment count is the only way to tell the two apart from outside.
    /// </remarks>
    [Fact]
    public void Freeing_gives_the_space_back_joined_up() {
        using var geometry = Build(vertices: 300);

        Assert.True(geometry.TryAllocate(100, 0, out var first));
        Assert.True(geometry.TryAllocate(100, 0, out var second));
        Assert.True(geometry.TryAllocate(100, 0, out var third));

        Assert.Equal(300, geometry.UsedVertices);
        Assert.False(geometry.TryAllocate(1, 0, out _));

        // The middle one first, so the two releases either side of it each have a neighbour to join.
        geometry.Free(second);
        geometry.Free(first);
        geometry.Free(third);

        Assert.Equal(0, geometry.UsedVertices);
        Assert.Equal(0, geometry.SliceCount);
        Assert.Equal(1, geometry.VertexFragmentCount);

        // And the proof that the count is not a bookkeeping figure: the whole buffer is allocatable
        // again in one piece, which three separate free ranges could not satisfy.
        Assert.True(geometry.TryAllocate(300, 0, out _));
    }

    /// <summary>A range that fills a gap joins both of its neighbours.</summary>
    /// <remarks>
    ///     The case a single-direction coalesce leaves half-done, and the one that would otherwise
    ///     never be noticed: the count is two rather than three, which still looks like it worked.
    /// </remarks>
    [Fact]
    public void A_freed_range_joins_both_neighbours() {
        using var geometry = Build(vertices: 300);

        geometry.TryAllocate(100, 0, out var first);
        geometry.TryAllocate(100, 0, out var middle);
        geometry.TryAllocate(100, 0, out var last);

        geometry.Free(first);
        geometry.Free(last);
        Assert.Equal(2, geometry.VertexFragmentCount);

        geometry.Free(middle);
        Assert.Equal(1, geometry.VertexFragmentCount);
    }

    /// <summary>A mesh refused index space does not keep its vertex space.</summary>
    /// <remarks>
    ///     ⚠ <strong>The leak with no symptom.</strong> A half-allocation leaves vertices reserved to
    ///     a mesh that does not exist, and nothing observes it until the buffer fills — some minutes
    ///     and some hundred meshes after the frame that caused it.
    /// </remarks>
    [Fact]
    public void A_refused_allocation_keeps_nothing() {
        using var geometry = Build(vertices: 1000, indices: 10);

        Assert.False(geometry.TryAllocate(100, 64, out var slice));
        Assert.False(slice.IsValid);

        Assert.Equal(0, geometry.UsedVertices);
        Assert.Equal(1, geometry.VertexFragmentCount);
        Assert.True(geometry.TryAllocate(1000, 0, out _));
    }

    /// <summary>Bytes that do not fill the slice are refused rather than written short.</summary>
    /// <remarks>
    ///     A short write leaves the tail of a mesh as whatever occupied that space before — which
    ///     draws, as a piece of the previous level welded to this one, where a refusal names the
    ///     mesh.
    /// </remarks>
    [Fact]
    public void A_slice_takes_exactly_its_own_bytes() {
        using var geometry = Build();
        geometry.TryAllocate(4, 6, out var slice);

        Assert.Throws<ArgumentException>(
            () => geometry.Write(slice, new byte[3 * Stride], new byte[6 * sizeof(uint)])
        );

        Assert.Throws<ArgumentException>(
            () => geometry.Write(slice, new byte[4 * Stride], new byte[5 * sizeof(uint)])
        );

        geometry.Write(slice, new byte[4 * Stride], new byte[6 * sizeof(uint)]);
    }

    /// <summary>A flush records one copy per staged write, at the offsets the slices reserved.</summary>
    /// <remarks>
    ///     The offsets are the assertion. A copy landing at the wrong destination is a mesh drawn
    ///     with another's vertices, which no API checks and which is a picture rather than an error.
    /// </remarks>
    [Fact]
    public void A_flush_copies_each_mesh_to_its_own_offset() {
        using var geometry = Build();

        geometry.TryAllocate(4, 6, out var first);
        geometry.TryAllocate(4, 6, out var second);

        geometry.Write(first, new byte[4 * Stride], new byte[6 * sizeof(uint)]);
        geometry.Write(second, new byte[4 * Stride], new byte[6 * sizeof(uint)]);

        using var list = device.BeginCommandList();
        Assert.Equal(4, geometry.Flush(list));
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var copies = device.Recorder!.OfKind(RecordedCommandKind.CopyBuffer);
        Assert.Equal(4, copies.Count);

        // The second mesh's vertices start where the first mesh's ended, in bytes. D is the
        // destination offset — see NullCommandList.CopyBuffer for the field order.
        Assert.Equal(4, second.BaseVertex);
        Assert.Contains(copies, copy => copy.D == (long)second.BaseVertex * Stride);
        Assert.Contains(copies, copy => copy.D == (long)second.FirstIndex * sizeof(uint));

        // And every copy reads from the staging buffer rather than from one of the two targets.
        Assert.All(copies, copy => Assert.NotEqual((long)geometry.Vertices.Value.Packed, copy.A));
    }

    /// <summary>A flush with nothing staged records nothing, barriers included.</summary>
    /// <remarks>
    ///     Barriers are not free — each is a stall the driver inserts — so a settled scene must not
    ///     be paying for two of them a frame to copy nothing.
    /// </remarks>
    [Fact]
    public void A_flush_with_nothing_staged_records_nothing() {
        using var geometry = Build();

        using var list = device.BeginCommandList();
        Assert.Equal(0, geometry.Flush(list));
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.CopyBuffer));
        Assert.Equal(0, device.Recorder.CountOf(RecordedCommandKind.Barrier));
    }

    /// <summary>The buffers are device-local, which is the reason staging exists at all.</summary>
    /// <remarks>
    ///     <para>
    ///         Host-visible geometry is geometry the GPU reads across the bus every frame. The point
    ///         of this class is geometry that does not, so the memory kind is part of what it is
    ///         rather than a tuning choice — and a test that only checked the offsets would pass for
    ///         a version that had quietly given it up.
    ///     </para>
    ///     <para>
    ///         Asserted through the refusal rather than by reading the description back, because the
    ///         refusal is the behaviour: a device-local buffer is one <c>Write</c> cannot reach, and
    ///         that is precisely why <c>Write</c> here means "stage" and needs a <c>Flush</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_geometry_lives_in_device_memory() {
        using var geometry = Build();

        Assert.Throws<InvalidOperationException>(() => device.Write(geometry.Vertices, 0, new byte[4]));
        Assert.Throws<InvalidOperationException>(() => device.Write(geometry.Indices, 0, new byte[4]));
    }
}
