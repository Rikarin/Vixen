// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering.Water;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     The upload, and the two ways it can be quietly wrong — [docs/plan/35 § D3].
/// </summary>
/// <remarks>
///     <para>
///         The picture is identical either way, which is the point of testing the command stream
///         rather than the texture: a copy recorded every frame is a megabyte a frame across the bus
///         to say what it already said, and a staging buffer rewritten under an in-flight copy is a
///         torn upload on exactly the machines nobody develops on.
///     </para>
///     <para>
///         Both assertions read the Null device's recorder, which is what it exists for — the stream
///         a test asserts on is the stream a GPU would have seen, in submission order.
///     </para>
/// </remarks>
public sealed class WaterInfoTextureTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    static WaterZone Zone => WaterZone.Default with { Extent = 64f, Resolution = 17 };

    static WaterZoneState State() {
        var state = new WaterZoneState(Zone);

        state.Update(Vector2.Zero, new FlatWaterGround(-2f));

        return state;
    }

    void Record(WaterInfoTexture texture) {
        using var list = device.BeginCommandList();

        texture.Record(list);
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <summary>
    ///     ⚠ A frame with nothing newly staged records no copy.
    /// </summary>
    /// <remarks>
    ///     The guard has to be "was something staged since the last record", not "does a staging
    ///     buffer exist" — the buffer exists forever after the first upload, so the latter re-copies
    ///     the full texture every frame from then on, which is the exact per-frame bus cost
    ///     <see cref="WaterInfoTexture.UploadCount" /> exists to make visible and the picture never
    ///     shows.
    /// </remarks>
    [Fact]
    public void A_frame_with_nothing_newly_staged_records_no_copy() {
        var state = State();

        using var texture = new WaterInfoTexture(device, Zone);

        Assert.True(texture.Stage(state));
        Record(texture);

        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));

        // A hundred frames of nothing rasterising: nothing staged, and nothing recorded.
        for (var frame = 0; frame < 100; frame++) {
            Assert.False(texture.Stage(state));
            Record(texture);
        }

        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.CopyBufferToTexture));
        Assert.Equal(1, texture.UploadCount);
    }

    /// <summary>
    ///     ⚠ Consecutive uploads ride different staging buffers.
    /// </summary>
    /// <remarks>
    ///     The host writes the staging buffer the moment a rasterisation is staged, while the copy
    ///     recorded the frame before may still be in flight on the device — TerrainRenderer's exact
    ///     bug class, fixed there with a FramesInFlight ring and rung the same way here. One buffer
    ///     written at offset zero would tear the previous frame's upload on any machine where the GPU
    ///     runs behind the CPU, which is every machine.
    /// </remarks>
    [Fact]
    public void Consecutive_uploads_ride_different_staging_buffers() {
        var state = State();

        using var texture = new WaterInfoTexture(device, Zone);

        Assert.True(texture.Stage(state));
        Record(texture);

        state.Invalidate();
        state.Update(Vector2.Zero, new FlatWaterGround(-2f));

        Assert.True(texture.Stage(state));
        Record(texture);

        var copies = device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture);

        Assert.Equal(2, copies.Count);
        Assert.NotEqual(copies[0].A, copies[1].A);
    }
}
