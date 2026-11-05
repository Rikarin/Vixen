// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Texturing.Layers;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The single undo entry, and symmetry.</summary>
public class PaintSessionTests {
    static readonly EditorContext NoContext = null!;

    const uint Opaque = 0xFF0000FFu;

    /// <summary>A drag is exactly one undo entry, and it restores the layer to the byte.</summary>
    [Fact]
    public void A_drag_is_one_undo_entry() {
        PaintImage layer = new(128, 128);

        layer.Fill(0x0A0B0C0Du);

        var original = (byte[])layer.ToTexels().Clone();
        PaintTarget target = new(layer, PaintStrokeTests.Islands(128, 128));
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(10f) with { Spacing = 0.2f }, Opaque);

        session.Move(new(20f, 64f));

        for (var step = 1; step <= 60; step++) {
            session.Move(new(20f + step, 64f));
        }

        var painted = (byte[])layer.ToTexels().Clone();
        var command = session.End("Paint");

        Assert.NotNull(command);
        Assert.NotEqual(original, painted);

        command.Undo(NoContext);

        Assert.Equal(original, layer.ToTexels());

        command.Do(NoContext);

        Assert.Equal(painted, layer.ToTexels());

        // ⚠ And it never merges. Two strokes are two undos, which is what an artist means.
        Assert.False(command.TryMergeWith(command, out _));
    }

    /// <summary>A click that painted nothing is not an undo entry.</summary>
    [Fact]
    public void A_drag_that_painted_nothing_makes_no_command() {
        var raster = new bool[64 * 64];
        PaintTarget target = new(new(64, 64), PaintCoverage.FromRaster(64, 64, raster));
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(4f), Opaque);

        session.Move(new(32f, 32f));

        Assert.True(session.IsEmpty);
        Assert.Null(session.End("Paint"));
    }

    /// <summary>Symmetry is a second stroke, and the pair is still one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>The mirrored position comes from the caller and this is why.</b> A plane mirrors a
    ///     point in object space and the mirrored point lands in a different UV island, so there is no
    ///     transform of the atlas that performs it. The surface holds the mesh and does the pick; the
    ///     session takes both hits and paints both paths.
    /// </remarks>
    [Fact]
    public void Symmetry_is_a_second_path_and_one_undo_entry() {
        PaintImage layer = new(128, 128);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(128, 128), Gutter: 0);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(6f), Opaque);

        for (var step = 0; step <= 20; step++) {
            Span<Vector2> both = [new(20f + step, 40f), new(20f + step, 90f)];

            session.MoveAll(both);
        }

        Assert.Equal(2, session.Strokes);
        Assert.Equal(0xFFu, layer.At(30, 40) >> 24);
        Assert.Equal(0xFFu, layer.At(30, 90) >> 24);

        var command = session.End("Paint");

        Assert.NotNull(command);

        command.Undo(NoContext);

        Assert.Equal(0x00u, layer.At(30, 40) >> 24);
        Assert.Equal(0x00u, layer.At(30, 90) >> 24);
    }

    /// <summary>Turning symmetry on halfway through a drag is refused rather than half-recorded.</summary>
    [Fact]
    public void A_drag_cannot_change_how_many_paths_it_has() {
        PaintTarget target = new(new(64, 64), PaintCoverage.Everywhere(64, 64));
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(4f), Opaque);

        session.Move(new(20f, 20f));

        Assert.Throws<ArgumentException>(() => {
            Span<Vector2> two = [new(21f, 20f), new(21f, 40f)];

            session.MoveAll(two);
        });
    }
}
