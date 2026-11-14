// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>A stamp laid on the picture against one laid on the surface, on a sheet whose atlas is square to it.</summary>
public class PaintStampAlignmentTests {
    const int Side = 256;
    const uint Red = 0xFF0000FFu;

    /// <summary>Where (-0.5, 0, 0) on the sheet lands in the atlas: 64 texels to a unit both ways.</summary>
    const int Column = 57;

    const int Row = 89;

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 24f, Flow = 1f, Falloff = 0f };

    /// <summary>A sheet two units square in the XY plane, laid on one island at 64 texels to a unit.</summary>
    static PaintMeshSurface Sheet() =>
        new(
            PaintProjection.Over(
                [new(-1f, -1f, 0f), new(0f, -1f, 0f), new(0f, 1f, 0f), new(-1f, 1f, 0f)],
                [new(0.1f, 0.1f), new(0.35f, 0.1f), new(0.35f, 0.6f), new(0.1f, 0.6f)],
                [0, 1, 2, 0, 2, 3]
            ),
            Side,
            Side
        );

    /// <summary>A camera four units off, turned about the sheet's up by an angle from square on.</summary>
    static PaintView Turned(float angle) {
        var (sine, cosine) = MathF.SinCos(angle);

        return new(
            new(sine * 4f - 0.5f, 0f, cosine * 4f),
            new(cosine, 0f, -sine),
            new(0f, 1f, 0f),
            new(-sine, 0f, -cosine),
            2f * MathF.Atan(0.5f),
            512,
            512
        );
    }

    /// <summary>How many texels across and down one stamp at (-0.5, 0, 0) paints.</summary>
    static (int Across, int Down) Stamp(PaintView view, PaintStampAlignment alignment) {
        var surface = Sheet();
        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(
            image,
            surface.Coverage,
            Hard with { Alignment = alignment },
            Red,
            0,
            placement: surface.Through(view)
        );

        stroke.MoveTo(view.Project(new(-0.5f, 0f, 0f)));

        var across = 0;
        var down = 0;

        for (var at = 0; at < Side; at++) {
            across += image.At(at, Row) != 0u ? 1 : 0;
            down += image.At(Column, at) != 0u ? 1 : 0;
        }

        return (across, down);
    }

    /// <summary>The stamp's radius on the sheet, in texels, for a camera four units off.</summary>
    static float Expected(PaintView view) => Hard.Radius * view.PerPixel(4f) * 64f;

    /// <summary>
    ///     Seen sixty degrees from square, a stamp on the picture is twice as long across the slope as down it, and the
    ///     same stamp laid on the surface is round there and as wide as a stamp seen square on.
    /// </summary>
    [Fact]
    public void A_stamp_laid_on_the_surface_is_round_on_a_face_that_turns_away() {
        var side = Turned(MathF.PI / 3f);
        var radius = Expected(side);
        var camera = Stamp(side, PaintStampAlignment.Camera);
        var laid = Stamp(side, PaintStampAlignment.Tangent);

        Assert.True(camera.Across > camera.Down * 1.7f, $"On the picture: {camera}.");
        Assert.InRange(laid.Down, 2f * radius - 3f, 2f * radius + 3f);
        Assert.InRange(laid.Across, laid.Down - 3, laid.Down + 3);
    }

    /// <summary>Seen square on, the plane under the stamp is the picture's, so the two alignments paint alike.</summary>
    [Fact]
    public void A_stamp_on_a_face_seen_square_on_is_the_same_either_way() {
        var square = Turned(0f);
        var camera = Stamp(square, PaintStampAlignment.Camera);
        var laid = Stamp(square, PaintStampAlignment.Tangent);

        Assert.InRange(laid.Across, camera.Across - 2, camera.Across + 2);
        Assert.InRange(laid.Down, camera.Down - 2, camera.Down + 2);
    }

    /// <summary>A mirrored copy of a stamp laid on the surface is round too: the plane follows the copy's camera.</summary>
    [Fact]
    public void A_mirrored_stamp_laid_on_the_surface_keeps_its_shape() {
        var view = Turned(MathF.PI / 3f);
        var radius = Expected(view);
        var mirrored = view.Mirrored(new(new(0f, 1f, 0f), 0f));
        var laid = Stamp(mirrored, PaintStampAlignment.Tangent);

        Assert.InRange(laid.Down, 2f * radius - 3f, 2f * radius + 3f);
        Assert.InRange(laid.Across, laid.Down - 3, laid.Down + 3);
    }
}
