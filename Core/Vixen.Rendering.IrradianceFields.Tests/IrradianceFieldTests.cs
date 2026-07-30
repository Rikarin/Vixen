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
        Assert.Equal(new Vector3(1f, 1f, 0.5f), field.FinestProbeSpacing);
    }

    /// <summary>
    ///     <b>A coarse brick is the same sixty-four probes over more world.</b> That is the whole
    ///     bargain refinement makes: memory where geometry is, and nothing where there is only air.
    /// </summary>
    [Fact]
    public void ACoarseBrickSpreadsTheSameProbesFurther() {
        var field = Field();

        Assert.Equal(field.FinestProbeSpacing * 4f, field.ProbeSpacingOf(4));
    }

    /// <summary>
    ///     <b>The geometric fact the whole scheme rests on.</b> A brick's fifth probe is not near its
    ///     neighbour's first — it <i>is</i> its neighbour's first, at the same world position, when the
    ///     two are the same size. That is what makes the border a copy rather than an estimate.
    /// </summary>
    [Fact]
    public void ABricksLastProbeIsItsNeighboursFirst() {
        var field = Field();

        field.AllocateAll();

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var first));
        Assert.True(field.Indirection.TryBrick(new(1, 0, 0), out var second));
        Assert.True(field.Indirection.TryBrick(new(1, 1, 1), out var diagonal));

        for (var y = 0; y <= 4; y++) {
            for (var z = 0; z <= 4; z++) {
                Assert.Equal(field.ProbePosition(first, 4, y, z), field.ProbePosition(second, 0, y, z));
            }
        }

        Assert.Equal(field.ProbePosition(first, 4, 4, 4), field.ProbePosition(diagonal, 0, 0, 0));
    }

    [Fact]
    public void ProbesStartAtTheCornerOfTheirBrick() {
        var field = Field();

        field.AllocateAll();

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var first));
        Assert.True(field.Indirection.TryBrick(new(2, 1, 1), out var last));

        Assert.Equal(new Vector3(-3f, 1f, 2f), field.ProbePosition(first, 0, 0, 0));
        Assert.Equal(new Vector3(9f, 9f, 6f), field.ProbePosition(last, 4, 4, 4));
    }

    /// <summary>
    ///     <b>The seam test.</b> Trilinear interpolation reproduces a linear function exactly, so a
    ///     field filled from one has a closed-form answer everywhere — including on and either side of
    ///     a brick boundary, which is the one place a storage scheme with borders can differ from one
    ///     without them. Any error left is a probe read from the wrong place.
    /// </summary>
    /// <param name="refined">
    ///     Whether the field mixes brick sizes. It is the same assertion either way and that is the
    ///     point: a border between two bricks of one size is a copy, a border across a change of size
    ///     is a sample of the neighbour's own field, and both have to land on the same linear answer or
    ///     there is a seam exactly where the refinement changes — which is next to geometry.
    ///     <para>
    ///         The refined case is what found that <see cref="IrradianceField.SyncBorders" /> has an
    ///         order to it. A fine brick's border interpolates a coarse neighbour at a position that
    ///         can fall in that neighbour's own border plane, so the coarse bricks have to be finished
    ///         first — and computing everything before writing anything, which is the obvious way to
    ///         make a pass order-independent, is exactly what breaks it.
    ///     </para>
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALinearFieldIsReproducedExactlyAcrossABrickBoundary(bool refined) {
        var field = refined ? Mixed() : Filled();

        foreach (var point in Interior(field)) {
            Assert.Equal(Probes.Ramp(point), Sampled(field, point), 3);
        }
    }

    /// <summary>
    ///     And the same field without its borders synced is wrong at exactly the boundaries — which is
    ///     what a seam is. Written down because the border plane is the part of the layout that looks
    ///     like padding and is not.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutBordersTheSeamShows(bool refined) {
        var field = refined ? Mixed(sync: false) : Filled(sync: false);
        var worst = 0f;

        foreach (var point in Interior(field)) {
            worst = MathF.Max(worst, MathF.Abs(Probes.Ramp(point) - Sampled(field, point)));
        }

        Assert.True(worst > 1f, $"the unsynced field was only {worst} out, so the borders did nothing");
    }

    /// <summary>A synced border between equal bricks holds the neighbour's probe, not something like it.</summary>
    [Fact]
    public void ABorderHoldsTheNeighboursOwnProbe() {
        var field = Filled();

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var first));
        Assert.True(field.Indirection.TryBrick(new(1, 0, 0), out var second));
        Assert.True(field.Indirection.TryBrick(new(1, 1, 1), out var diagonal));

        for (var y = 0; y < 4; y++) {
            for (var z = 0; z < 4; z++) {
                Assert.Equal(field.GetProbe(second, 0, y, z), field.GetProbe(first, 4, y, z));
            }
        }

        Assert.Equal(field.GetProbe(diagonal, 0, 0, 0), field.GetProbe(first, 4, 4, 4));
    }

    /// <summary>
    ///     At the edge of the field a border repeats the brick's own last probe, because there is
    ///     nothing beyond it to copy. The lighting stops changing rather than falling to black, which
    ///     is what the alternative looks like: a dark rind one probe thick around everything.
    /// </summary>
    [Fact]
    public void AtTheEdgeABorderRepeatsWhatItHas() {
        var field = Filled();

        Assert.True(field.Indirection.TryBrick(new(2, 1, 1), out var last));
        Assert.Equal(field.GetProbe(last, 3, 3, 3), field.GetProbe(last, 4, 4, 4));
    }

    /// <summary>
    ///     A border texel can be a copy of another border texel, and the copy has to read one this
    ///     same sync already wrote — never what the pool held before the sync began.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The case is structural, not exotic: at the grid's outer face a border position clamps
    ///         back inside, so the lookup lands in a <i>face</i> neighbour with a local coordinate of
    ///         exactly one, and the same-size copy reaches that neighbour's own border plane. An edge
    ///         texel of a brick on the grid's top row does this on every sync.
    ///     </para>
    ///     <para>
    ///         Every border texel is poisoned before the sync, so a sync that defers a whole size
    ///         class at once — every value computed before any is written, the obvious way to make a
    ///         pass order-independent — copies the poison into those edge texels, deterministically.
    ///         Committing faces, then edges, then the corner is what this asserts, and it is also the
    ///         order the device repair dispatches; the two have to agree because the device is checked
    ///         against this class.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ASyncNeverCopiesABorderItHasNotWrittenYet() {
        const float Poison = 4096f;

        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(4), new(new(4)));

        field.AllocateAll();

        foreach (var brick in field.Bricks) {
            for (var z = 0; z <= 4; z++) {
                for (var y = 0; y <= 4; y++) {
                    for (var x = 0; x <= 4; x++) {
                        if (x < 4 && y < 4 && z < 4) {
                            field.SetProbe(
                                brick, x, y, z,
                                Probes.Of(Probes.Ramp(field.ProbePosition(brick, x, y, z)))
                            );
                        } else {
                            field.Pool[brick.Slot, x, y, z] = Probes.Of(Poison);
                        }
                    }
                }
            }
        }

        field.SyncBorders();

        var interior = 0;

        foreach (var brick in field.Bricks) {
            for (var z = 0; z <= 4; z++) {
                for (var y = 0; y <= 4; y++) {
                    for (var x = 0; x <= 4; x++) {
                        var value = field.GetProbe(brick, x, y, z).Value();

                        Assert.True(
                            MathF.Abs(value) < Poison / 2f,
                            $"texel {x},{y},{z} of the brick at {brick.Cell} still carries the poison, "
                            + "so the sync copied a border texel it had not written yet"
                        );

                        // And where a real neighbour exists, the copy is the field's own answer at
                        // that position — on the FIRST sync, not eventually.
                        var position = field.ProbePosition(brick, x, y, z);

                        if (position.X < 2f && position.Y < 2f && position.Z < 2f) {
                            Assert.Equal(Probes.Ramp(position), value, 2);
                            interior++;
                        }
                    }
                }
            }
        }

        Assert.True(interior > 0, "no border texel was interior, so the positive half asserted nothing");
    }

    /// <summary>Borders are not data, so a filler cannot write one.</summary>
    [Fact]
    public void AFillerCannotWriteABorder() {
        var field = Field();

        field.AllocateAll();

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var brick));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => field.SetProbe(brick, 4, 0, 0, IrradianceProbe.Empty)
        );
    }

    [Fact]
    public void OnlyWhatIsAskedForIsAllocated() {
        var field = Field();

        Assert.Equal(2, field.Allocate(new(new(-3f, 1f, 2f), new(2f, 4f, 3f))));
        Assert.Equal(2, field.BrickCount);
        Assert.Equal(2, field.Pool.Count);

        // Asking again for the same region changes nothing — a cell that has a brick keeps it.
        Assert.Equal(0, field.Allocate(new(new(-3f, 1f, 2f), new(2f, 4f, 3f))));
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
        Assert.Equal(3, field.BrickCount);
        Assert.False(field.TryAllocate(new(1, 1, 1), 1, out _));
    }

    [Fact]
    public void ReleasingGivesTheSlotBackAndForgetsTheCell() {
        var field = Field();

        Assert.True(field.TryAllocate(new(1, 0, 0), 1, out var brick));
        Assert.True(field.Release(new(1, 0, 0)));

        Assert.False(field.Indirection[new(1, 0, 0)].HasBrick);
        Assert.False(field.Pool.IsAllocated(brick.Slot));
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

    /// <summary>Splitting a brick makes eight of half its size, in its own footprint.</summary>
    [Fact]
    public void SplittingMakesEightOfHalfTheSize() {
        var field = Coarse();

        Assert.Equal(8, field.BrickCount);

        Assert.Equal(8, field.Split(new(0, 0, 0)));
        Assert.Equal(15, field.BrickCount);

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var child));
        Assert.Equal(1, child.Size);

        Assert.True(field.Indirection.TryBrick(new(2, 0, 0), out var untouched));
        Assert.Equal(2, untouched.Size);

        // Already as fine as the grid goes.
        Assert.Equal(0, field.Split(new(0, 0, 0)));
    }

    /// <summary>
    ///     <b>The parent's probes are discarded rather than interpolated down.</b> Interpolating would
    ///     make eight children that agree with a coarser answer than any of them should hold, and a
    ///     filler would then be converging toward the truth from something that already looks
    ///     converged. Empty is honest.
    /// </summary>
    [Fact]
    public void SplittingDiscardsWhatTheParentSaw() {
        var field = Coarse();

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var parent));
        field.SetProbe(parent, 1, 1, 1, Probes.Of(9f));

        field.Split(new(0, 0, 0));

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var child));

        for (var index = 0; index < 4; index++) {
            Assert.Equal(IrradianceProbe.Empty, field.GetProbe(child, index, index, index));
        }
    }

    /// <summary>Refinement splits what overlaps and leaves the rest coarse — which is the whole point.</summary>
    [Fact]
    public void RefiningSplitsOnlyWhatOverlaps() {
        var field = Coarse();

        Assert.Equal(8, field.Refine(new(new(0.5f), new(1.5f))));

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var near));
        Assert.True(field.Indirection.TryBrick(new(3, 3, 3), out var far));

        Assert.Equal(1, near.Size);
        Assert.Equal(2, far.Size);
        Assert.Equal(15, field.BrickCount);
    }

    /// <summary>Refining to a size a brick already is does nothing at all.</summary>
    [Fact]
    public void RefiningToWhatIsAlreadyThereChangesNothing() {
        var field = Coarse();

        Assert.Equal(0, field.Refine(field.Bounds, 2));
        Assert.Equal(8, field.BrickCount);
    }

    [Fact]
    public void ABrickSizeHasToBeAPowerOfTwo() {
        var field = Coarse();

        Assert.Throws<ArgumentOutOfRangeException>(() => field.Allocate(field.Bounds, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => field.Refine(field.Bounds, 0));
    }

    /// <summary>
    ///     The normal bias is measured in the brick's own probe spacing, so a coarse region pushes the
    ///     lookup further than a fine one — the ambiguity out there is wider in exactly that ratio.
    /// </summary>
    [Fact]
    public void TheBiasScalesWithTheBrickUnderTheSurface() {
        var field = Coarse();

        field.Refine(new(new(0.5f), new(1.5f)));

        foreach (var brick in field.Bricks) {
            for (var z = 0; z < 4; z++) {
                for (var y = 0; y < 4; y++) {
                    for (var x = 0; x < 4; x++) {
                        field.SetProbe(brick, x, y, z, Probes.Of(field.ProbePosition(brick, x, y, z).X));
                    }
                }
            }
        }

        field.SyncBorders();
        field.NormalBias = 1f;

        // A size-one brick covers two world units, so its probes are half a unit apart.
        Assert.True(field.TrySample(new(1f, 1f, 1f), new(1, 0, 0), out var fine));
        Assert.Equal(1.5f, fine.Value(), 3);

        // A size-two brick covers four, so its probes are one unit apart.
        Assert.True(field.TrySample(new(5f, 5f, 5f), new(1, 0, 0), out var coarse));
        Assert.Equal(6f, coarse.Value(), 3);
    }

    /// <summary>Eight bricks of size two over a four-cell grid, so there is something to refine.</summary>
    static IrradianceField Coarse() {
        var field = new IrradianceField(new BoundingBox(new(0f), new(8f)), new(4));

        field.AllocateAll(2);

        return field;
    }

    /// <summary>A field of one brick size, every probe carrying the ramp of where it stands.</summary>
    static IrradianceField Filled(bool sync = true) {
        var field = Field();

        field.AllocateAll();

        return Ramped(field, sync);
    }

    /// <summary>A field of two brick sizes, refined in two opposite corners so both adjacencies occur.</summary>
    /// <remarks>
    ///     Both directions matter and they take different code paths: a coarse brick borrowing from a
    ///     fine one has a border plane spanning several neighbours, and a fine brick borrowing from a
    ///     coarse one lands between that neighbour's probes.
    /// </remarks>
    static IrradianceField Mixed(bool sync = true) {
        var field = Coarse();

        field.Refine(new(new(0.5f), new(1.5f)));
        field.Refine(new(new(4.5f), new(5.5f)));

        return Ramped(field, sync);
    }

    /// <summary>Fills every owned probe of every brick from <see cref="Probes.Ramp" />.</summary>
    static IrradianceField Ramped(IrradianceField field, bool sync) {
        foreach (var brick in field.Bricks) {
            for (var z = 0; z < 4; z++) {
                for (var y = 0; y < 4; y++) {
                    for (var x = 0; x < 4; x++) {
                        field.SetProbe(brick, x, y, z, Probes.Of(Probes.Ramp(field.ProbePosition(brick, x, y, z))));
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
    ///     the scheme does not claim. The rind is as wide as the coarsest brick's probes are apart.
    /// </remarks>
    static IEnumerable<Vector3> Interior(IrradianceField field) {
        var bounds = field.Bounds;
        var coarsest = 1;

        foreach (var brick in field.Bricks) {
            coarsest = Math.Max(coarsest, brick.Size);
        }

        var limit = bounds.Maximum - field.ProbeSpacingOf(coarsest);

        for (var i = 0; i <= 12; i++) {
            for (var j = 0; j <= 6; j++) {
                for (var k = 0; k <= 6; k++) {
                    // Deliberately fractional steps as well as whole ones, so samples land on probes,
                    // on brick boundaries, and between both.
                    var t = new Vector3(i * 0.97f, j * 1.31f, k * 0.63f);

                    yield return Vector3.Min(bounds.Minimum + t, limit);
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
