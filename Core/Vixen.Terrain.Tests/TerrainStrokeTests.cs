// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>A stroke as one undoable command — [docs/plan/31 § D11].</summary>
public sealed class TerrainStrokeTests {
    static TerrainDescription Shape() =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static (Terrain Terrain, TerrainEditLayer Layer) Build() {
        var terrain = new Terrain(Shape());
        return (terrain, terrain.AddLayer("Sculpt"));
    }

    static TerrainBrush Brush(float radius = 4f) =>
        TerrainBrush.Default with { Radius = radius, Strength = 1f, Falloff = 0.5f, Spacing = 0.5f };

    /// <summary>Snapshots the whole composite, for an exact before-and-after comparison.</summary>
    static ushort[] Snapshot(Terrain terrain) {
        terrain.Resolve();
        return terrain.Composite.Span.ToArray();
    }

    /// <summary>Drags a brush across the terrain, recording the stroke.</summary>
    static (TerrainStroke Stroke, TerrainStrokeRedo Redo) Drag(
        Terrain terrain,
        TerrainEditLayer layer,
        params Vector2[] path
    ) {
        var brush = Brush();
        var stroke = new TerrainStroke(terrain, layer);
        var walker = new BrushStroke(brush);
        var stamps = new List<BrushStamp>();

        foreach (var point in path) {
            walker.MoveTo(point, stamps);
        }

        foreach (var stamp in stamps) {
            // Record first, apply second. The other order records what the kernel wrote and gives an
            // undo that restores the stroke — which is why TerrainStroke.Record exists.
            stroke.Record(brush, stamp);
            TerrainSculpt.Sculpt(terrain, layer, brush, stamp, 8f);
        }

        return (stroke, stroke.Capture());
    }

    [Fact]
    public void UndoingAStrokePutsTheTerrainBackExactly() {
        var (terrain, layer) = Build();
        var before = Snapshot(terrain);

        var (stroke, _) = Drag(terrain, layer, new(10f, 10f), new(30f, 22f));

        Assert.False(stroke.IsEmpty);
        Assert.NotEqual(before, Snapshot(terrain));

        stroke.Undo();

        Assert.Equal(before, Snapshot(terrain));
    }

    [Fact]
    public void RedoingPutsTheStrokeBackExactly() {
        var (terrain, layer) = Build();

        var (stroke, redo) = Drag(terrain, layer, new(10f, 10f), new(30f, 22f));
        var after = Snapshot(terrain);

        stroke.Undo();
        redo.Redo();

        Assert.Equal(after, Snapshot(terrain));
    }

    [Fact]
    public void UndoAndRedoAlternateWithoutDrift() {
        var (terrain, layer) = Build();
        var before = Snapshot(terrain);

        var (stroke, redo) = Drag(terrain, layer, new(12f, 12f), new(26f, 26f));
        var after = Snapshot(terrain);

        for (var cycle = 0; cycle < 8; cycle++) {
            stroke.Undo();
            Assert.Equal(before, Snapshot(terrain));

            redo.Redo();
            Assert.Equal(after, Snapshot(terrain));
        }
    }

    [Fact]
    public void ThreeStrokesUndoOneAtATimeInReverseOrder() {
        // Merging is off: two strokes are two undos, which is what an artist means by "undo that"
        // and what every paint application does.
        var (terrain, layer) = Build();

        var states = new List<ushort[]> { Snapshot(terrain) };
        var strokes = new List<TerrainStroke>();

        foreach (var at in new[] { 10f, 20f, 30f }) {
            var (stroke, _) = Drag(terrain, layer, new(at, 16f), new(at + 4f, 16f));
            strokes.Add(stroke);
            states.Add(Snapshot(terrain));
        }

        for (var index = strokes.Count - 1; index >= 0; index--) {
            strokes[index].Undo();
            Assert.Equal(states[index], Snapshot(terrain));
        }
    }

    /// <summary>
    ///     A drag over the same ground records what it was before the <em>first</em> crossing.
    /// </summary>
    /// <remarks>
    ///     The reason <c>Extend</c> uses <c>TryAdd</c>. Re-recording on a later crossing would make
    ///     undo restore the middle of the stroke, which looks like undo half-working — the worst
    ///     possible failure, because it is not obviously broken.
    /// </remarks>
    [Fact]
    public void ADragCrossingItsOwnPathStillUndoesToTheStart() {
        var (terrain, layer) = Build();
        var before = Snapshot(terrain);

        var (stroke, _) = Drag(
            terrain, layer,
            new(16f, 10f), new(16f, 24f), new(10f, 17f), new(24f, 17f), new(16f, 10f)
        );

        stroke.Undo();
        Assert.Equal(before, Snapshot(terrain));
    }

    [Fact]
    public void TheRecordIsSizedToWhatWasTouchedRatherThanToTheTerrain() {
        var (terrain, layer) = Build();

        var (stroke, _) = Drag(terrain, layer, new Vector2(16f, 16f));

        Assert.False(stroke.Rect.IsEmpty);

        // A single stamp of radius 4 on a 63-square terrain: a small fraction of it.
        Assert.True(stroke.Rect.Count < terrain.Description.SampleCount / 8);
        Assert.Equal(stroke.Rect.Count, stroke.RecordedSamples);
        Assert.True(stroke.Bytes < 8_000);
    }

    /// <summary>
    ///     The record covers what a kernel <em>read</em>, not only what it wrote.
    /// </summary>
    /// <remarks>
    ///     Smoothing and erosion read a sample beyond their footprint. A record sized to the write
    ///     restores a rectangle whose border still holds post-stroke values, and the next smooth over
    ///     the same place pulls them back in — an undo that looks complete and leaves a ring.
    /// </remarks>
    [Fact]
    public void TheRecordIsGrownByTheNeighbourMarginASmoothReads() {
        var (terrain, layer) = Build();

        var brush = Brush();
        var stamp = new BrushStamp(new(16f, 16f));
        var written = TerrainSculpt.AffectedRect(terrain.Description, brush, stamp);

        var stroke = new TerrainStroke(terrain, layer);
        Assert.Equal(written, stroke.Record(brush, stamp));

        Assert.Equal(written.X - TerrainSculpt.NeighbourMargin, stroke.Rect.X);
        Assert.Equal(written.Width + (TerrainSculpt.NeighbourMargin * 2), stroke.Rect.Width);
    }

    [Fact]
    public void UndoingASmoothRestoresItExactly() {
        var (terrain, layer) = Build();

        // A spike first, so there is something to smooth.
        layer.SetDelta(16, 16, 20_000);
        terrain.InvalidateAll();
        var before = Snapshot(terrain);

        var brush = Brush(radius: 6f);
        var stamp = new BrushStamp(new(16f, 16f));
        var stroke = new TerrainStroke(terrain, layer);

        stroke.Record(brush, stamp);
        TerrainSculpt.Smooth(terrain, layer, brush, stamp);

        Assert.NotEqual(before, Snapshot(terrain));

        stroke.Undo();
        Assert.Equal(before, Snapshot(terrain));
    }

    [Fact]
    public void AStrokeThatTouchedNothingIsEmptyAndUndoesToNothing() {
        var (terrain, layer) = Build();
        var before = Snapshot(terrain);

        var stroke = new TerrainStroke(terrain, layer);
        stroke.Extend(TerrainRect.Empty);

        Assert.True(stroke.IsEmpty);
        Assert.True(stroke.Rect.IsEmpty);

        stroke.Undo();
        Assert.Equal(before, Snapshot(terrain));
    }

    [Fact]
    public void AStrokeNearTheEdgeIsClippedRatherThanRunningOff() {
        var (terrain, layer) = Build();
        var before = Snapshot(terrain);

        var (stroke, _) = Drag(terrain, layer, new(0f, 0f), new(3f, 3f));

        Assert.True(stroke.Rect.X >= 0);
        Assert.True(stroke.Rect.Z >= 0);

        stroke.Undo();
        Assert.Equal(before, Snapshot(terrain));
    }

    [Fact]
    public void AStrokeOnASecondLayerLeavesTheFirstAlone() {
        var terrain = new Terrain(Shape());
        var lower = terrain.AddLayer("Lower");
        var upper = terrain.AddLayer("Upper");

        TerrainSculpt.Sculpt(terrain, lower, Brush(), new(new(16f, 16f)), 12f);
        terrain.Resolve();
        var lowerDelta = lower.DeltaAt(16, 16);

        var (stroke, _) = Drag(terrain, upper, new(16f, 16f), new(20f, 16f));
        stroke.Undo();
        terrain.Resolve();

        Assert.Equal(lowerDelta, lower.DeltaAt(16, 16));
        Assert.Equal(0, upper.DeltaAt(16, 16));
    }
}
