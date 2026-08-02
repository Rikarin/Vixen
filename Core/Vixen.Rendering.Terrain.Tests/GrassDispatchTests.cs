// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     The grass scatter's device half, against a recording device — [docs/plan/31 § T6]'s owed item.
/// </summary>
/// <remarks>
///     What a headless device can say: what was bound, how many groups were dispatched, how many
///     bytes moved and in what order. What it cannot say is whether the blades land where the CPU
///     reference puts them, which needs a compute queue — so the record layouts are held against the
///     reference instead, because those are what a disagreement would travel through.
/// </remarks>
public sealed class GrassDispatchTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static GrassType Meadow =>
        GrassType.Of("Meadow") with { Layer = "Grass", Density = 4f, Jitter = 0.8f };

    static FoliageCellGrid Grid => new(32f);

    GrassDispatch Build(int capacity = 8, int bladesPerCell = 256) =>
        new(
            device,
            device.CreateShader(ShaderStage.Compute, [1, 2, 3, 4], "grass.scatter.cs"),
            device.CreateShader(ShaderStage.Compute, [5, 6, 7, 8], "grass.arguments.cs"),
            capacity,
            bladesPerCell
        );

    GrassTerrainSource Source() =>
        new(
            device.CreateTextureView(
                device.CreateTexture(new(PixelFormat.R16UNorm, 64, 64, TextureUsage.Sampled, Name: "heights"))
            ),
            device.CreateTextureView(
                device.CreateTexture(new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.Sampled, Name: "weights"))
            ),
            device.CreateSampler(new()),
            device.CreateSampler(new()),
            WeightChannel: 1,
            HeightMapSize: new(64f, 64f),
            HeightRange: new(-100f, 100f),
            MetresPerQuad: 1f,
            Origin: Vector3.Zero
        );

    static GrassSlot[] Resident(int count, int firstSlot = 0) =>
        [.. Enumerable.Range(0, count).Select(index => new GrassSlot(new(index, 0), firstSlot + index))];

    /// <summary>The records the shader reads are the size the shader declares.</summary>
    /// <remarks>
    ///     ⚠ <b>A stride that disagrees does not fail.</b> It reads cell one out of the middle of cell
    ///     zero, which draws as a field of grass in the wrong place that moves when anything else
    ///     changes — <c>CullInstance</c>'s own remarks record learning this the hard way.
    /// </remarks>
    [Fact]
    public void TheRecordsAreTheSizeTheShaderDeclares() {
        Assert.Equal(16, GrassCellRecord.SizeInBytes);
        Assert.Equal(48, GrassInstanceRecord.SizeInBytes);

        // Which is a stored instance plus the per-instance parameters, and that is the point: a seam
        // test can compare bytes rather than interpretations.
        Assert.Equal(FoliageStore.InstanceBytes + Features.InstanceParameters.SizeInBytes, GrassInstanceRecord.SizeInBytes);
    }

    /// <summary>A blade from the reference packs to the record the dispatch writes.</summary>
    [Fact]
    public void ABladeFromTheReferencePacksToTheDeviceRecord() {
        var blades = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(2, -3), Grid, new Flat(), blades);

        Assert.NotEmpty(blades);

        var record = GrassInstanceRecord.Of(blades[0]);
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<GrassInstanceRecord>(in record));

        Assert.Equal(48, bytes.Length);
        Assert.Equal(blades[0].Instance.Position, record.Position);
        Assert.Equal(blades[0].WindPhase, record.WindPhase);
        Assert.Equal(0f, record.Padding0);
    }

    [Fact]
    public void PreparingWritesOneRecordPerResidentCell() {
        using var pass = Build();

        var covered = pass.Prepare(Meadow, Grid, Resident(5), Source());

        Assert.Equal(5, covered);
        Assert.Equal(5, pass.CellCount);
        Assert.Equal(0, pass.Refused);
    }

    /// <summary>A cell's run is filed under its ring slot, not its place in this frame's list.</summary>
    /// <remarks>
    ///     ⚠ <b>A cell keeps its buffer across frames — that is what the ring is.</b> Filing its run
    ///     under the loop index would move every blade the moment a nearer cell arrived and pushed it
    ///     down the list, which reads as the whole field jumping whenever the camera moves.
    /// </remarks>
    [Fact]
    public void ARunIsFiledUnderTheRingSlot() {
        using var pass = Build(capacity: 8, bladesPerCell: 256);

        // Slots 3, 4, 5 — deliberately not 0, 1, 2.
        pass.Prepare(Meadow, Grid, Resident(3, firstSlot: 3), Source());

        var (offset, length) = pass.RunOf(3);

        Assert.Equal(3L * 256 * GrassInstanceRecord.SizeInBytes, offset);
        Assert.Equal(256L * GrassInstanceRecord.SizeInBytes, length);
    }

    /// <summary>More cells than the dispatch holds is announced rather than silent.</summary>
    [Fact]
    public void ADispatchThatRunsOutSaysSo() {
        using var pass = Build(capacity: 4);

        var covered = pass.Prepare(Meadow, Grid, Resident(9), Source());

        Assert.Equal(4, covered);
        Assert.Equal(5, pass.Refused);
    }

    /// <summary>The dispatch covers every candidate slot of every cell, and no more.</summary>
    [Fact]
    public void TheDispatchCoversTheCandidateGrid() {
        using var pass = Build();
        var type = Meadow;

        pass.Prepare(type, Grid, Resident(3), Source());

        var side = type.GridOf(Grid.CellSize);
        var groups = (side + GrassDispatch.GroupSize - 1) / GrassDispatch.GroupSize;

        Assert.Equal((groups, groups, 3), pass.Groups);

        // Every candidate is covered — the tail is the shader's own bounds test, not a lost slot.
        Assert.True(groups * GrassDispatch.GroupSize >= side);
        Assert.True((groups - 1) * GrassDispatch.GroupSize < side);
    }

    /// <summary>Nothing resident is nothing recorded.</summary>
    [Fact]
    public void AnEmptyRingRecordsNothing() {
        using var pass = Build();

        pass.Prepare(Meadow, Grid, [], Source());

        var commands = device.BeginCommandList();

        Assert.Equal(0, pass.Record(commands));

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));
    }

    /// <summary>The counters are zeroed before the dispatch, not after.</summary>
    /// <remarks>
    ///     ⚠ <b>A pass that cleared afterwards leaves the buffer holding last frame's numbers for
    ///     anything that reads it in between</b> — and what reads it is the indirect draw, which would
    ///     then draw a cell's previous population out of its current bytes.
    /// </remarks>
    [Fact]
    public void TheCountersAreClearedBeforeTheDispatch() {
        using var pass = Build();

        pass.Prepare(Meadow, Grid, Resident(3), Source());
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        pass.Record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorded = device.Recorder.Commands;
        var copy = recorded.ToList().FindIndex(entry => entry.Kind == RecordedCommandKind.CopyBuffer);
        var dispatch = recorded.ToList().FindIndex(entry => entry.Kind == RecordedCommandKind.Dispatch);

        Assert.True(copy >= 0, "the counters were never cleared.");
        Assert.True(dispatch >= 0, "nothing was dispatched.");
        Assert.True(copy < dispatch, "the counters were cleared after the dispatch rather than before.");
    }

    /// <summary>And the draw is fenced off from the write.</summary>
    [Fact]
    public void TheDrawIsFencedOffFromTheWrite() {
        using var pass = Build();

        pass.Prepare(Meadow, Grid, Resident(2), Source());
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        pass.Record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var barriers = device.Recorder.Commands.Count(entry => entry.Kind == RecordedCommandKind.Barrier);

        Assert.True(barriers >= 2, $"only {barriers} barriers around a compute write two things read.");
    }

    [Fact]
    public void AShaderWithNoComputeStageIsRefused() {
        var valid = device.CreateShader(ShaderStage.Compute, [1], "grass.cs");

        Assert.Contains(
            "compute stage",
            Assert.Throws<ArgumentException>(() => new GrassDispatch(device, default, valid)).Message,
            StringComparison.Ordinal
        );

        Assert.Throws<ArgumentException>(() => new GrassDispatch(device, valid, default));
    }

    [Fact]
    public void UsingItAfterDisposalIsRefused() {
        var pass = Build();

        pass.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pass.Prepare(Meadow, Grid, Resident(1), Source()));
    }

    /// <summary>The arguments are a second dispatch, after the scatter and before the draw.</summary>
    /// <remarks>
    ///     ⚠ <b>It cannot be folded into the scatter.</b> The invocation that claims slot zero does
    ///     not know how many will follow it, and an indirect draw needs the final count — so the
    ///     argument write has to be after every candidate of the cell has retired, which is a second
    ///     dispatch by definition.
    /// </remarks>
    [Fact]
    public void TheArgumentsAreASecondDispatchOverTheCells() {
        using var pass = Build(capacity: 8);

        pass.Prepare(Meadow, Grid, Resident(3), Source());
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        pass.Record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorded = device.Recorder.Commands.ToList();
        var dispatches = recorded.FindAll(entry => entry.Kind == RecordedCommandKind.Dispatch);
        var first = recorded.FindIndex(entry => entry.Kind == RecordedCommandKind.Dispatch);
        var second = recorded.FindLastIndex(entry => entry.Kind == RecordedCommandKind.Dispatch);
        var between = recorded
            .GetRange(first + 1, second - first - 1)
            .Count(entry => entry.Kind == RecordedCommandKind.Barrier);

        Assert.Equal(2, dispatches.Count);
        Assert.True(between > 0, "the argument phase reads what the scatter wrote, unfenced.");
    }

    /// <summary>A cell's command starts with the mesh's template and a zero instance count.</summary>
    /// <remarks>
    ///     ⚠ <b>Zeroed rather than guessed.</b> A cell whose argument write never lands draws nothing
    ///     instead of drawing whatever the previous frame's count happened to be.
    /// </remarks>
    [Fact]
    public void TheTemplateIsTheMeshAndTheDeviceFillsTheRest() {
        using var pass = Build();

        pass.Mesh = new() { IndexCount = 36u, FirstIndex = 4u, VertexOffset = 8u, InstanceCount = 99u };

        pass.Prepare(Meadow, Grid, Resident(2), Source());

        Assert.Equal(20, GrassDispatch.DrawCommandBytes);
        Assert.Equal(0L, pass.CommandOf(0));
        Assert.Equal(20L, pass.CommandOf(1));
    }

    /// <summary>One indirect draw per resident cell, at that cell's own command.</summary>
    [Fact]
    public void OneDrawPerCellIsIssued() {
        using var pass = Build();

        pass.Prepare(Meadow, Grid, Resident(4), Source());
        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        commands.BeginRenderPass(new([new(Target())], name: "Grass"));

        Assert.Equal(4, pass.RecordDraws(commands));

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Equal(4, device.Recorder.Commands.Count(entry => entry.Kind == RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>And nothing resident is nothing drawn.</summary>
    [Fact]
    public void AnEmptyRingDrawsNothing() {
        using var pass = Build();

        pass.Prepare(Meadow, Grid, [], Source());

        var commands = device.BeginCommandList();

        commands.BeginRenderPass(new([new(Target())], name: "Grass"));

        Assert.Equal(0, pass.RecordDraws(commands));

        commands.EndRenderPass();
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexedIndirect));
    }

    /// <summary>Somewhere to draw into, because no API allows a draw outside a render pass.</summary>
    TextureViewHandle Target() =>
        device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget, Name: "grass target"))
        );

    /// <summary>Flat ground everywhere, so the reference produces blades to pack.</summary>
    sealed class Flat : IFoliageSurface {
        public FoliageSurface SampleAt(Vector2 position, string layer) =>
            new(new(position.X, 0f, position.Y), Vector3.UnitY, 1f, true);
    }
}
