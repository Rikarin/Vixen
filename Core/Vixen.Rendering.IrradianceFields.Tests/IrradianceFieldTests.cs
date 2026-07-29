// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>The field as a whole: where its probes are, and whether a sample crosses a brick cleanly.</summary>
public class IrradianceFieldTests {
    /// <summary>Three bricks along X and two along the others, over a box that is not at the origin.</summary>
    /// <remarks>
    ///     Not at the origin, and not cubic, because an offset dropped from a probe position is
    ///     invisible in a field whose minimum is zero, and an axis swapped is invisible in a cube.
    /// </remarks>
    static IrradianceField Field() =>
        new(new BoundingBox(new(-3f, 1f, 2f), new(9f, 9f, 6f)), new(3, 2, 2));

    [Fact]
    public void ABrickSpansFourGapsNotFive() {
        var field = Field();

        Assert.Equal(new Vector3(4f, 4f, 2f), field.Indirection.CellSize);
        Assert.Equal(new Vector3(1f, 1f, 0.5f), field.ProbeSpacing);
    }

    /// <summary>
    ///     <b>The geometric fact the whole scheme rests on.</b> A brick's fifth probe is not near its
    ///     neighbour's first — it <i>is</i> its neighbour's first, at the same world position. That is
    ///     what makes the border a copy rather than an estimate, and it is why a seam cannot survive a
    ///     correct border sync.
    /// </summary>
    [Fact]
    public void ABricksLastProbeIsItsNeighboursFirst() {
        var field = Field();

        for (var y = 0; y <= 4; y++) {
            for (var z = 0; z <= 4; z++) {
                Assert.Equal(
                    field.ProbePosition(new(0, 0, 0), 4, y, z),
                    field.ProbePosition(new(1, 0, 0), 0, y, z)
                );
            }
        }

        Assert.Equal(
            field.ProbePosition(new(0, 0, 0), 4, 4, 4),
            field.ProbePosition(new(1, 1, 1), 0, 0, 0)
        );
    }

    [Fact]
    public void ProbesStartAtTheCornerOfTheirCell() {
        var field = Field();

        Assert.Equal(new Vector3(-3f, 1f, 2f), field.ProbePosition(new(0, 0, 0), 0, 0, 0));
        Assert.Equal(new Vector3(9f, 9f, 6f), field.ProbePosition(new(2, 1, 1), 4, 4, 4));
    }

    /// <summary>
    ///     <b>The seam test.</b> Trilinear interpolation reproduces a linear function exactly, so a
    ///     field filled from one has a closed-form answer everywhere — including on and either side of
    ///     a brick boundary, which is the one place a storage scheme with borders can differ from one
    ///     without them. Any error left is a probe read from the wrong place.
    /// </summary>
    [Fact]
    public void ALinearFieldIsReproducedExactlyAcrossABrickBoundary() {
        var field = Filled();

        foreach (var point in Interior(field)) {
            Assert.Equal(Probes.Ramp(point), Sampled(field, point), 3);
        }
    }

    /// <summary>
    ///     And the same field without its borders synced is wrong at exactly the boundaries — which is
    ///     what a seam is. Written down because the border plane is the part of the layout that looks
    ///     like padding and is not.
    /// </summary>
    [Fact]
    public void WithoutBordersTheSeamShows() {
        var field = Filled(sync: false);
        var worst = 0f;

        foreach (var point in Interior(field)) {
            worst = MathF.Max(worst, MathF.Abs(Probes.Ramp(point) - Sampled(field, point)));
        }

        Assert.True(worst > 1f, $"the unsynced field was only {worst} out, so the borders did nothing");
    }

    /// <summary>A synced border holds the neighbour's probe, not something like it.</summary>
    [Fact]
    public void ABorderHoldsTheNeighboursOwnProbe() {
        var field = Filled();

        for (var y = 0; y < 4; y++) {
            for (var z = 0; z < 4; z++) {
                Assert.Equal(
                    field.GetProbe(new(1, 0, 0), 0, y, z),
                    field.GetProbe(new(0, 0, 0), 4, y, z)
                );
            }
        }

        Assert.Equal(
            field.GetProbe(new(1, 1, 1), 0, 0, 0),
            field.GetProbe(new(0, 0, 0), 4, 4, 4)
        );
    }

    /// <summary>
    ///     At the edge of the field a border repeats the brick's own last probe, because there is
    ///     nothing beyond it to copy. The lighting stops changing rather than falling to black, which
    ///     is what the alternative looks like: a dark rind one probe thick around everything.
    /// </summary>
    [Fact]
    public void AtTheEdgeABorderRepeatsWhatItHas() {
        var field = Filled();

        Assert.Equal(field.GetProbe(new(2, 1, 1), 3, 3, 3), field.GetProbe(new(2, 1, 1), 4, 4, 4));
    }

    /// <summary>Borders are not data, so a filler cannot write one.</summary>
    [Fact]
    public void AFillerCannotWriteABorder() {
        var field = Field();

        field.AllocateAll();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => field.SetProbe(new(0, 0, 0), 4, 0, 0, IrradianceProbe.Empty)
        );
    }

    [Fact]
    public void WritingToACellWithNoBrickIsRefused() {
        var field = Field();

        Assert.Throws<InvalidOperationException>(
            () => field.SetProbe(new(0, 0, 0), 0, 0, 0, IrradianceProbe.Empty)
        );
    }

    [Fact]
    public void OnlyWhatIsAskedForIsAllocated() {
        var field = Field();

        Assert.Equal(2, field.Allocate(new(new(-3f, 1f, 2f), new(2f, 4f, 3f))));
        Assert.Equal(2, field.Indirection.Occupancy);
        Assert.Equal(2, field.Pool.Count);

        // Asking again for the same region changes nothing — a cell that has a brick keeps it.
        Assert.Equal(2, field.Allocate(new(new(-3f, 1f, 2f), new(2f, 4f, 3f))));
        Assert.Equal(2, field.Pool.Count);

        Assert.Equal(0, field.Allocate(new(new(100f), new(200f))));
    }

    [Fact]
    public void AFieldRunsOutOfPoolRatherThanFailing() {
        var field = new IrradianceField(
            new BoundingBox(new(0f), new(4f)),
            new(2),
            new IrradianceBrickPool(new(1, 1, 3))
        );

        Assert.Equal(3, field.AllocateAll());
        Assert.Equal(3, field.Indirection.Occupancy);
        Assert.False(field.TryAllocate(new(1, 1, 1), out _));
    }

    [Fact]
    public void ReleasingGivesTheSlotBackAndForgetsTheCell() {
        var field = Field();

        Assert.True(field.TryAllocate(new(1, 0, 0), out var slot));
        Assert.True(field.Release(new(1, 0, 0)));

        Assert.Equal(IrradianceIndirection.Empty, field.Indirection[new(1, 0, 0)]);
        Assert.False(field.Pool.IsAllocated(slot));
        Assert.False(field.Release(new(1, 0, 0)));
    }

    [Fact]
    public void OutsideTheFieldThereIsNoAnswer() {
        var field = Filled();

        Assert.False(field.TrySample(new(-4f, 2f, 3f), out _));
        Assert.Equal(Vector3.Zero, field.Irradiance(new(-4f, 2f, 3f), new(0, 1, 0)));
    }

    /// <summary>A cell with no brick answers nothing, even with bricks all around it.</summary>
    [Fact]
    public void AHoleInTheFieldAnswersNothing() {
        var field = Filled();

        field.Release(new(1, 0, 0));

        Assert.False(field.TrySample(new(2f, 2f, 3f), out _));
    }

    /// <summary>A field whose every probe carries <see cref="Probes.Ramp" /> of where it stands.</summary>
    static IrradianceField Filled(bool sync = true) {
        var field = Field();
        var resolution = field.Indirection.Resolution;

        field.AllocateAll();

        for (var cz = 0; cz < resolution.Z; cz++) {
            for (var cy = 0; cy < resolution.Y; cy++) {
                for (var cx = 0; cx < resolution.X; cx++) {
                    var cell = new Int3(cx, cy, cz);

                    for (var z = 0; z < 4; z++) {
                        for (var y = 0; y < 4; y++) {
                            for (var x = 0; x < 4; x++) {
                                field.SetProbe(cell, x, y, z, Probes.Of(Probes.Ramp(field.ProbePosition(cell, x, y, z))));
                            }
                        }
                    }
                }
            }
        }

        if (sync) {
            field.SyncBorders();
        }

        return field;
    }

    /// <summary>
    ///     Points to compare against the closed form — everywhere except the outermost probe spacing.
    /// </summary>
    /// <remarks>
    ///     The last brick's border plane has no neighbour to copy, so it repeats rather than
    ///     continuing the ramp. Beyond the last <i>owned</i> probe the field is a constant
    ///     extrapolation by design, and asserting a linear answer there would be asserting something
    ///     the scheme does not claim.
    /// </remarks>
    static IEnumerable<Vector3> Interior(IrradianceField field) {
        var bounds = field.Bounds;
        var limit = bounds.Maximum - field.ProbeSpacing;

        for (var i = 0; i <= 12; i++) {
            for (var j = 0; j <= 6; j++) {
                for (var k = 0; k <= 6; k++) {
                    // Deliberately fractional steps as well as whole ones, so samples land on probes,
                    // on brick boundaries, and between both.
                    var t = new Vector3(i * 0.97f, j * 1.31f, k * 0.63f);
                    var point = Vector3.Min(bounds.Minimum + t, limit);

                    yield return point;
                }
            }
        }
    }

    /// <summary>The number a filled field holds at a point.</summary>
    static float Sampled(IrradianceField field, Vector3 point) {
        Assert.True(field.TrySample(point, out var probe), $"{point} was outside the field");

        return probe.Value();
    }
}
