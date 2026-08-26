// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

public class GlobalDistanceFieldTests {
    static MeshDistanceField UnitSphere() {
        var (vertices, indices) = Shapes.Sphere(1f, 32, 64);

        return MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 24 });
    }

    [Fact]
    public void LevelsDoubleInExtentAndKeepTheirResolution() {
        var clipmap = new GlobalDistanceField(16, 4f, 4);

        Assert.Equal(4f, clipmap.ExtentOf(0));
        Assert.Equal(8f, clipmap.ExtentOf(1));
        Assert.Equal(32f, clipmap.ExtentOf(3));

        // Same memory each, twice the world, so twice the cell.
        Assert.Equal(clipmap.CellSizeOf(0) * 2f, clipmap.CellSizeOf(1), 5);
    }

    /// <summary>
    ///     The defect this prevents is the one shadow cascades have: a grid centred exactly on a
    ///     moving camera slides under static geometry, and a wall that has not moved changes its
    ///     distance every frame.
    /// </summary>
    [Fact]
    public void ALevelSnapsToItsOwnCellGridRatherThanToTheCamera() {
        var clipmap = new GlobalDistanceField(16, 4f, 2);
        var cell = clipmap.CellSizeOf(0);

        clipmap.Update(Vector3.Zero, [DistanceFieldInstance.At(UnitSphere(), new(0, 0, 0))]);
        var before = clipmap.BoundsOf(0).Minimum;

        // A tenth of a cell is not a cell, so nothing may move.
        clipmap.Update(new(cell * 0.1f, 0, 0), [DistanceFieldInstance.At(UnitSphere(), new(0, 0, 0))]);

        Assert.Equal(before.X, clipmap.BoundsOf(0).Minimum.X, 4);

        // A whole cell is, and it moves by exactly one.
        clipmap.Update(new(cell, 0, 0), [DistanceFieldInstance.At(UnitSphere(), new(0, 0, 0))]);

        Assert.Equal(before.X + cell, clipmap.BoundsOf(0).Minimum.X, 4);
    }

    [Fact]
    public void SnappingIsToWholeCellsInEveryDirection() {
        Assert.Equal(new Vector3(2, 0, -2), GlobalDistanceField.Snap(new(2.4f, 0.4f, -1.6f), 2f));
        Assert.Equal(Vector3.Zero, GlobalDistanceField.Snap(new(0.9f, -0.9f, 0.9f), 2f));
    }

    [Fact]
    public void OneInstanceComesBackThroughTheClipmap() {
        var clipmap = new GlobalDistanceField(32, 4f, 2);
        var instance = DistanceFieldInstance.At(UnitSphere(), Vector3.Zero);

        clipmap.Update(Vector3.Zero, [instance]);

        Assert.True(clipmap.Sample(Vector3.Zero) < 0, "the centre of the sphere reads as outside it");
        Assert.Equal(0f, clipmap.Sample(new(1f, 0, 0)), 1);
        Assert.True(clipmap.Sample(new(2f, 0, 0)) > 0);
    }

    /// <summary>
    ///     The composite is a minimum, so two spheres are whichever is nearer — and never something
    ///     between them.
    /// </summary>
    [Fact]
    public void TwoInstancesComposeAsTheNearerOfTheTwo() {
        var field = UnitSphere();

        // A tight level, because the midpoint between two objects is a ridge of the distance
        // function and a trilinear read of a ridge lands under it — the same property the box's
        // centre has. The cell size is therefore the tolerance, below.
        var clipmap = new GlobalDistanceField(64, 2f, 1);

        // Close enough that their bounds overlap at the origin, so the point between them is a
        // reading from both fields rather than a bound substituted for either.
        DistanceFieldInstance[] instances = [
            DistanceFieldInstance.At(field, new(-1.2f, 0, 0)),
            DistanceFieldInstance.At(field, new(1.2f, 0, 0))
        ];

        clipmap.Update(Vector3.Zero, instances);

        Assert.True(clipmap.Sample(new(-1.2f, 0, 0)) < 0);
        Assert.True(clipmap.Sample(new(1.2f, 0, 0)) < 0);

        // Halfway between, both surfaces are 0.2 away. The answer is the nearer one, not their sum
        // and not their average.
        Assert.Equal(0.2f, clipmap.Sample(Vector3.Zero), clipmap.CellSizeOf(0));
    }

    /// <summary>
    ///     What the safe bound costs, stated as a number so it is a decision rather than a surprise.
    /// </summary>
    [Fact]
    public void TheBoundIsLooseInTheGapBetweenObjects() {
        var field = UnitSphere();
        var clipmap = new GlobalDistanceField(48, 6f, 1);

        DistanceFieldInstance[] instances = [
            DistanceFieldInstance.At(field, new(-3, 0, 0)),
            DistanceFieldInstance.At(field, new(3, 0, 0))
        ];

        clipmap.Update(Vector3.Zero, instances);

        // The origin is outside both instances' bounds, so neither field was read there: the
        // composite gets √(f² + t²) instead, which for these bounds is about 1.65 against a truth of
        // 2. Under by a sixth — a tracer takes an extra step or two crossing the gap and never
        // misses the surface. Buying it back means a bound that knows the shape inside the box, and
        // nothing does.
        var here = clipmap.Sample(Vector3.Zero);

        Assert.True(here <= 2f, $"the composite said {here}, which overstates the true 2");
        Assert.True(here > 1.3f, $"the composite said {here}, which is looser than the bound allows");
    }

    /// <summary>
    ///     Outside an instance's bounds the field knows nothing, so the composite substitutes a
    ///     bound. It must never claim a surface is further away than it is — that is the direction a
    ///     tracer cannot survive.
    /// </summary>
    [Fact]
    public void TheCompositeNeverOverstatesADistance() {
        var clipmap = new GlobalDistanceField(24, 8f, 1);
        var instance = DistanceFieldInstance.At(UnitSphere(), Vector3.Zero);

        clipmap.Update(Vector3.Zero, [instance]);

        for (var z = 0; z < 24; z++) {
            for (var y = 0; y < 24; y++) {
                for (var x = 0; x < 24; x++) {
                    var bounds = clipmap.BoundsOf(0);
                    var cell = bounds.Size / 23f;
                    var point = bounds.Minimum + (cell * new Vector3(x, y, z));
                    var truth = Shapes.SphereDistance(point, 1f);

                    // Clamped values are the level saying "at least this far", which is still true.
                    Assert.True(
                        clipmap[0, x, y, z] <= truth + 0.01f,
                        $"at {point} the composite said {clipmap[0, x, y, z]} but the truth is {truth}"
                    );
                }
            }
        }
    }

    [Fact]
    public void TheBoundOutsideAnInstanceIsContinuousAcrossItsBoundary() {
        // √(f² + t²) is exactly f at t = 0, so a query crossing into an instance's bounds does not
        // see the value step. A plain distance-to-box would drop to zero there and make every
        // tracer crawl around every object.
        var clipmap = new GlobalDistanceField(48, 8f, 1);
        var instance = DistanceFieldInstance.At(UnitSphere(), Vector3.Zero);

        clipmap.Update(Vector3.Zero, [instance]);

        var edge = instance.WorldBounds.Maximum.X;
        var inside = clipmap.Sample(new(edge - 0.05f, 0, 0));
        var outside = clipmap.Sample(new(edge + 0.05f, 0, 0));

        Assert.True(MathF.Abs(outside - inside) < 0.2f, $"{inside} then {outside} across the boundary");
    }

    [Fact]
    public void AFarLevelClampsRatherThanReportingInfinity() {
        var clipmap = new GlobalDistanceField(16, 4f, 2);

        clipmap.Update(Vector3.Zero, [DistanceFieldInstance.At(UnitSphere(), Vector3.Zero)]);

        // A corner of the coarse level is well past the sphere and past what the level can measure.
        var corner = clipmap.BoundsOf(1).Maximum;

        Assert.Equal(clipmap.MaxDistanceOf(1), clipmap.Sample(corner), 3);
        Assert.True(float.IsFinite(clipmap.Sample(corner)));
    }

    [Fact]
    public void APointOutsideEveryLevelGetsTheCoarsestClamp() {
        var clipmap = new GlobalDistanceField(16, 4f, 2);

        clipmap.Update(Vector3.Zero, [DistanceFieldInstance.At(UnitSphere(), Vector3.Zero)]);

        Assert.False(clipmap.TryLevelFor(new(1000, 0, 0), out _));
        Assert.Equal(clipmap.MaxDistanceOf(1), clipmap.Sample(new(1000, 0, 0)), 3);
    }

    [Fact]
    public void TheFinestLevelCoveringAPointIsTheOneUsed() {
        var clipmap = new GlobalDistanceField(16, 4f, 3);

        clipmap.Update(Vector3.Zero, [DistanceFieldInstance.At(UnitSphere(), Vector3.Zero)]);

        Assert.True(clipmap.TryLevelFor(Vector3.Zero, out var nearest));
        Assert.Equal(0, nearest);

        Assert.True(clipmap.TryLevelFor(new(6, 0, 0), out var middle));
        Assert.Equal(1, middle);

        Assert.True(clipmap.TryLevelFor(new(12, 0, 0), out var far));
        Assert.Equal(2, far);
    }

    [Fact]
    public void AParallelUpdateIsIdenticalToASerialOne() {
        var field = UnitSphere();

        DistanceFieldInstance[] instances = [
            DistanceFieldInstance.At(field, new(-2, 0, 0)),
            new(field, new(2, 1, 0), Quaternion.FromAxisAngle(Vector3.UnitY, 0.3f), 1.5f)
        ];

        var parallel = new GlobalDistanceField(16, 6f, 2);
        var serial = new GlobalDistanceField(16, 6f, 2);

        parallel.Update(new(0.5f, 0, 0), instances);
        serial.Update(new(0.5f, 0, 0), instances, parallel: false);

        for (var level = 0; level < 2; level++) {
            for (var z = 0; z < 16; z++) {
                for (var y = 0; y < 16; y++) {
                    for (var x = 0; x < 16; x++) {
                        Assert.Equal(parallel[level, x, y, z], serial[level, x, y, z]);
                    }
                }
            }
        }
    }

    [Fact]
    public void AnEmptyWorldIsUniformlyAsFarAsTheLevelCanSee() {
        var clipmap = new GlobalDistanceField(8, 4f, 1);

        clipmap.Update(Vector3.Zero, []);

        Assert.True(clipmap.HasContent);

        for (var z = 0; z < 8; z++) {
            for (var y = 0; y < 8; y++) {
                for (var x = 0; x < 8; x++) {
                    Assert.Equal(clipmap.MaxDistanceOf(0), clipmap[0, x, y, z]);
                }
            }
        }
    }

    /// <summary>
    ///     A refresh that has run every slice and not been published has changed nothing a reader
    ///     can see.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The double buffer, stated as the property it exists for.</b> A caller that does not
    ///         wait for the composite is a caller whose frame reads this clipmap while the composite
    ///         is running — uploading its cells, and naming its box into a descriptor set — so
    ///         "nothing moves until Publish" is not an implementation detail but the whole contract.
    ///     </para>
    ///     <para>
    ///         ⚠ And all three together. The cells, the box and the view position are three
    ///         derivations of where the clipmap is, read by three different things: a box that
    ///         advanced ahead of its cells is a shader told exactly where to look in a volume holding
    ///         somewhere else.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARefreshChangesNothingUntilItIsPublished() {
        var clipmap = new GlobalDistanceField(8, 4f, 2);
        DistanceFieldInstance[] instances = [DistanceFieldInstance.At(UnitSphere(), new(0, 0, 0))];

        clipmap.Update(Vector3.Zero, instances);

        var box = clipmap.BoundsOf(0);
        var cells = clipmap.LevelData(0).ToArray();
        var moved = new Vector3(clipmap.CellSizeOf(0) * 3f, 0, 0);

        var refresh = clipmap.BeginUpdate(moved, instances);

        Assert.True(clipmap.IsRefreshing);

        for (var slice = 0; slice < refresh.SliceCount; slice++) {
            refresh.Composite(slice);
        }

        // Every cell of the new composite is written and not one of them is visible.
        Assert.Equal(box, clipmap.BoundsOf(0));
        Assert.Equal(Vector3.Zero, clipmap.ViewPosition);
        Assert.Equal(cells, clipmap.LevelData(0).ToArray());

        refresh.Publish();

        Assert.False(clipmap.IsRefreshing);
        Assert.Equal(moved, clipmap.ViewPosition);
        Assert.NotEqual(box, clipmap.BoundsOf(0));
        Assert.NotEqual(cells, clipmap.LevelData(0).ToArray());
    }

    /// <summary>
    ///     The deferred composite and the inline one are the same arithmetic, not two copies of it.
    /// </summary>
    [Fact]
    public void ASlicedRefreshLandsWhereUpdateWouldHave() {
        DistanceFieldInstance[] instances = [DistanceFieldInstance.At(UnitSphere(), new(0.4f, 0, 0))];

        var whole = new GlobalDistanceField(8, 4f, 2);
        var sliced = new GlobalDistanceField(8, 4f, 2);

        whole.Update(new(0.5f, 0, 0), instances);

        var refresh = sliced.BeginUpdate(new(0.5f, 0, 0), instances);

        for (var slice = refresh.SliceCount - 1; slice >= 0; slice--) {
            refresh.Composite(slice);
        }

        refresh.Publish();

        for (var level = 0; level < 2; level++) {
            for (var z = 0; z < 8; z++) {
                for (var y = 0; y < 8; y++) {
                    for (var x = 0; x < 8; x++) {
                        Assert.Equal(whole[level, x, y, z], sliced[level, x, y, z]);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     There is one spare buffer per level, so there can be one refresh.
    /// </summary>
    [Fact]
    public void ASecondRefreshIsRefusedRatherThanSharingTheBufferWithTheFirst() {
        var clipmap = new GlobalDistanceField(8, 4f, 2);

        clipmap.BeginUpdate(Vector3.Zero, []);

        Assert.Throws<InvalidOperationException>(() => clipmap.BeginUpdate(Vector3.One, []));
    }

    [Theory]
    [InlineData(1, 4f, 4)]
    [InlineData(16, 0f, 4)]
    [InlineData(16, 4f, 0)]
    public void AClipmapThatCannotExistIsRejected(int resolution, float extent, int levels) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GlobalDistanceField(resolution, extent, levels)
        );
}
