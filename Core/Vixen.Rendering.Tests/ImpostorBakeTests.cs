// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The bake itself, against a recording device — [docs/plan/31 § T7]'s owed item.</summary>
/// <remarks>
///     What a headless device can say: that every cell is drawn, into the right rectangle, with the
///     right camera, inside one render pass. What it cannot say is what the pixels look like, which
///     needs a rasteriser — and the arithmetic those pixels come from is <see cref="ImpostorTests" />'
///     already.
/// </remarks>
public sealed class ImpostorBakeTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    static ImpostorAtlas Layout(int side = 5, int cellSize = 64, int padding = 4) =>
        new(new ImpostorGrid(side), cellSize, padding);

    [Fact]
    public void EveryCellOfTheGridIsBaked() {
        using var bake = new ImpostorBake(device, Layout());

        var seen = new List<ImpostorCell>();
        var commands = device.BeginCommandList();

        var baked = bake.Record(commands, Vector3.Zero, 4f, (_, cell) => seen.Add(cell.Cell));

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Equal(25, baked);
        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
        Assert.Equal(25, bake.CellsBaked);
    }

    /// <summary>One render pass for the whole atlas, not one per cell.</summary>
    /// <remarks>
    ///     ⚠ <b>A pass each would clear and store the whole atlas eighty-one times</b> to bake one
    ///     tree, which on a tiler is eighty-one full-frame resolves. The clear happens once and the
    ///     viewport moves.
    /// </remarks>
    [Fact]
    public void ItIsOneRenderPassAndOneViewportPerCell() {
        using var bake = new ImpostorBake(device, Layout());

        var commands = device.BeginCommandList();

        bake.Record(commands, Vector3.Zero, 4f, (_, _) => { });

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var recorded = device.Recorder!.Commands;

        Assert.Single(recorded, entry => entry.Kind == RecordedCommandKind.BeginRenderPass);
        Assert.Single(recorded, entry => entry.Kind == RecordedCommandKind.EndRenderPass);
        Assert.Equal(25, recorded.Count(entry => entry.Kind == RecordedCommandKind.SetViewport));
        Assert.Equal(25, recorded.Count(entry => entry.Kind == RecordedCommandKind.SetScissor));
    }

    /// <summary>And the draw happens inside it, where a draw is allowed to be.</summary>
    [Fact]
    public void TheDrawIsInsideThePass() {
        using var bake = new ImpostorBake(device, Layout(side: 2));

        var commands = device.BeginCommandList();

        bake.Record(commands, Vector3.Zero, 4f, (list, _) => list.Draw(3));

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        // The null device refuses a draw outside a render pass, so reaching here is the assertion —
        // and this counts them to say the callback was actually reached.
        Assert.Equal(4, device.Recorder!.Commands.Count(entry => entry.Kind == RecordedCommandKind.Draw));
    }

    /// <summary>A cell draws into its own rectangle, inset from its neighbours by the gutter.</summary>
    /// <remarks>
    ///     ⚠ <b>The inset is the atlas's, and applying it twice is the mistake.</b>
    ///     <see cref="ImpostorAtlas.RectOf" /> already excludes the gutter; padding it again draws the
    ///     tree into the middle four-fifths of its cell, which is not wrong enough to look wrong — it
    ///     is a silhouette a few per cent small, uniformly, which reads as the impostor sitting at a
    ///     slightly different distance than the mesh it replaces.
    /// </remarks>
    [Fact]
    public void ACellDrawsInsideItsOwnGutter() {
        var atlas = Layout(side: 3, cellSize: 64, padding: 4);
        using var bake = new ImpostorBake(device, atlas);

        var cell = new ImpostorCell(1, 2);
        var viewport = bake.ViewportOf(cell);

        // The atlas's rect, and not one texel further in.
        Assert.Equal((1 * 64) + 4, viewport.X);
        Assert.Equal((2 * 64) + 4, viewport.Y);
        Assert.Equal(atlas.DrawSize, viewport.Width);
        Assert.Equal(atlas.DrawSize, viewport.Height);

        // Strictly inside the cell it belongs to, on all four sides.
        Assert.True(viewport.X > 1 * 64);
        Assert.True(viewport.Y > 2 * 64);
        Assert.True(viewport.X + viewport.Width < 2 * 64);
        Assert.True(viewport.Y + viewport.Height < 3 * 64);
    }

    /// <summary>Two cells never overlap, gutters included.</summary>
    [Fact]
    public void NoTwoCellsShareATexel() {
        var atlas = Layout(side: 4, cellSize: 32, padding: 2);
        using var bake = new ImpostorBake(device, atlas);

        var rects = new List<ScissorRect>();

        for (var z = 0; z < 4; z++) {
            for (var x = 0; x < 4; x++) {
                rects.Add(bake.ScissorOf(new(x, z)));
            }
        }

        foreach (var (first, index) in rects.Select((rect, index) => (rect, index))) {
            foreach (var second in rects.Skip(index + 1)) {
                var apart = first.X + first.Width <= second.X
                    || second.X + second.Width <= first.X
                    || first.Y + first.Height <= second.Y
                    || second.Y + second.Height <= first.Y;

                Assert.True(apart, $"{first} and {second} share texels.");
            }
        }
    }

    /// <summary>The camera a cell is baked with is the grid's own, for that cell's direction.</summary>
    [Fact]
    public void TheCameraIsTheCellsOwnDirection() {
        var atlas = Layout(side: 5);
        using var bake = new ImpostorBake(device, atlas);

        var views = new Dictionary<ImpostorCell, ImpostorView>();
        var commands = device.BeginCommandList();

        bake.Record(commands, new(1f, 2f, 3f), 6f, (_, cell) => views[cell.Cell] = cell.View);

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        foreach (var (cell, view) in views) {
            Assert.Equal(atlas.Grid.DirectionOf(cell), view.Direction);
            Assert.Equal(6f, view.Radius);
        }
    }

    /// <summary>One radius for every cell, which is what stops the impostor breathing.</summary>
    /// <remarks>
    ///     Fitting each view's own extent would pack the atlas better and would make the same vertex
    ///     a different number of texels from the centre in each cell — so a blend that moves between
    ///     cells would scale the tree.
    /// </remarks>
    [Fact]
    public void EveryCellIsBakedAtTheSameRadius() {
        using var bake = new ImpostorBake(device, Layout());

        var radii = new HashSet<float>();
        var commands = device.BeginCommandList();

        bake.Record(commands, Vector3.Zero, 7.5f, (_, cell) => radii.Add(cell.View.Radius));

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        Assert.Single(radii);
        Assert.Equal(7.5f, radii.First());
    }

    /// <summary>The atlas stops its mip chain where the gutter stops protecting it.</summary>
    [Fact]
    public void TheAtlasKeepsOnlyTheSafeMips() {
        var atlas = Layout(side: 9, cellSize: 128, padding: 4);
        using var bake = new ImpostorBake(device, atlas);

        Assert.Equal(atlas.MipLevels, bake.Atlas.MipLevels);
        Assert.Equal(8, atlas.MipLevels);
        Assert.Equal(1152, atlas.Resolution);
    }

    // --- Finishing ----------------------------------------------------------

    /// <summary>A bake without the finishing shaders is still a bake, and says so when asked to finish.</summary>
    [Fact]
    public void AnUnfinishedAtlasRefusesToFinish() {
        using var bake = new ImpostorBake(device, Layout());
        using var commands = device.BeginCommandList();

        Assert.Throws<InvalidOperationException>(() => bake.Finish(commands));
    }

    [Fact]
    public void FinishingNeedsBothStages() {
        using var bake = new ImpostorBake(device, Layout());
        var valid = device.CreateShader(ShaderStage.Compute, [1], "finish.cs");

        Assert.Throws<ArgumentException>(() => bake.Finishing(default, valid));
        Assert.Throws<ArgumentException>(() => bake.Finishing(valid, default));
    }

    /// <summary>Dilate first, then reduce, and one dispatch per level of each atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>The order is the whole point.</b> Reducing an undilated level averages the
    ///     silhouette's edge with transparent black, so the fringe the dilation exists to remove is
    ///     baked into every level below — and each level halves it into a wider band. Dilating
    ///     afterwards would fix level 0 and nothing else.
    /// </remarks>
    [Fact]
    public void FinishingDilatesThenReducesEveryLevelOfBothAtlases() {
        var atlas = Layout(side: 3, cellSize: 32, padding: 2);
        using var bake = Finished(atlas);

        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        var dispatches = bake.Finish(commands);

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        // Two atlases, every level of each.
        Assert.Equal(2 * atlas.MipLevels, dispatches);
        Assert.Equal(dispatches, bake.Dispatches);
        Assert.Equal(dispatches, device.Recorder.Commands.Count(entry => entry.Kind == RecordedCommandKind.Dispatch));

        // The first pipeline bound is the dilation and the second is the reduce — one dilate per
        // atlas, at level 0, and everything after it a reduce.
        var pipelines = device.Recorder.Commands
            .Where(entry => entry.Kind == RecordedCommandKind.BindPipeline)
            .Select(entry => entry.A)
            .ToArray();

        Assert.Equal(dispatches, pipelines.Length);
        Assert.NotEqual(pipelines[0], pipelines[1]);
        Assert.Equal(pipelines[0], pipelines[atlas.MipLevels]);
    }

    /// <summary>Every dispatch is fenced from the one it reads.</summary>
    /// <remarks>
    ///     ⚠ <b>A level cannot be read until the whole of the level above it is written, and a
    ///     workgroup can only wait for itself.</b> That is why it is a dispatch per level rather than
    ///     one with a loop, and why the barriers are here rather than implied.
    /// </remarks>
    [Fact]
    public void EveryLevelIsFencedFromTheOneItReads() {
        var atlas = Layout(side: 2, cellSize: 16, padding: 2);
        using var bake = Finished(atlas);

        device.Recorder!.Clear();

        var commands = device.BeginCommandList();

        bake.Finish(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        var barriers = device.Recorder.Commands.Count(entry => entry.Kind == RecordedCommandKind.Barrier);

        Assert.Equal(2 * atlas.MipLevels, barriers);
    }

    ImpostorBake Finished(ImpostorAtlas atlas) {
        var bake = new ImpostorBake(device, atlas);

        bake.Finishing(
            device.CreateShader(ShaderStage.Compute, [1], "impostor.dilate.cs"),
            device.CreateShader(ShaderStage.Compute, [2], "impostor.reduce.cs")
        );

        return bake;
    }

    [Fact]
    public void ABakeOfNothingIsRefused() {
        using var bake = new ImpostorBake(device, Layout());

        var commands = device.BeginCommandList();

        Assert.Throws<ArgumentOutOfRangeException>(() => bake.Record(commands, Vector3.Zero, 0f, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => bake.Record(commands, Vector3.Zero, 1f, null!));
    }

    [Fact]
    public void UsingItAfterDisposalIsRefused() {
        var bake = new ImpostorBake(device, Layout());

        bake.Dispose();

        using var commands = device.BeginCommandList();

        Assert.Throws<ObjectDisposedException>(() => bake.Record(commands, Vector3.Zero, 1f, (_, _) => { }));
    }
}
