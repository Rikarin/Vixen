// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.DistanceFields.Tests;

/// <summary>
///     Keeping what a movement did not invalidate.
/// </summary>
/// <remarks>
///     The one claim worth making, and everything here is a way of making it: a scrolled level is
///     <i>identical</i> to a recomputed one. Not close — identical, cell for cell, because a kept cell
///     is the same world position looking at the same instances, and the composite is a pure function
///     of exactly those two things. Anything less than identity means the optimisation changed the
///     picture, which is the only way this can be wrong and the only thing worth testing.
/// </remarks>
public class ScrollTests {
    [Fact]
    public void AScrolledLevelIsIdenticalToARecomputedOne() {
        var instances = Instances();
        var cell = new GlobalDistanceField(16, 4f, 2).CellSizeOf(0);

        var scrolled = new GlobalDistanceField(16, 4f, 2);
        scrolled.Update(Vector3.Zero, instances, parallel: false);
        scrolled.Update(new(cell * 3f, cell, -cell * 2f), instances, parallel: false, scroll: true);

        var fresh = new GlobalDistanceField(16, 4f, 2);
        fresh.Update(new(cell * 3f, cell, -cell * 2f), instances, parallel: false);

        for (var level = 0; level < 2; level++) {
            Assert.Equal(fresh.BoundsOf(level).Minimum, scrolled.BoundsOf(level).Minimum);

            for (var z = 0; z < 16; z++) {
                for (var y = 0; y < 16; y++) {
                    for (var x = 0; x < 16; x++) {
                        Assert.Equal(fresh[level, x, y, z], scrolled[level, x, y, z]);
                    }
                }
            }
        }
    }

    [Fact]
    public void AOneCellStepKeepsNearlyAllOfALevel() {
        var instances = Instances();
        var field = new GlobalDistanceField(16, 4f, 1);

        field.Update(Vector3.Zero, instances, parallel: false);

        Assert.Equal(0, field.Reused);

        field.Update(new(field.CellSizeOf(0), 0, 0), instances, parallel: false, scroll: true);

        // One slab of 16 x 16 recomputed out of 16³.
        Assert.Equal(15L * 16 * 16, field.Reused);
    }

    [Fact]
    public void AStepPastTheWholeLevelKeepsNothing() {
        var instances = Instances();
        var field = new GlobalDistanceField(16, 4f, 1);

        field.Update(Vector3.Zero, instances, parallel: false);
        field.Update(new(100f, 0, 0), instances, parallel: false, scroll: true);

        Assert.Equal(0, field.Reused);
    }

    /// <summary>
    ///     Off by default, because this cannot tell whether the instances changed and a kept cell that
    ///     should not have been is geometry left behind where it used to be.
    /// </summary>
    [Fact]
    public void WithoutAskingForItNothingIsKept() {
        var instances = Instances();
        var field = new GlobalDistanceField(16, 4f, 1);

        field.Update(Vector3.Zero, instances, parallel: false);
        field.Update(new(field.CellSizeOf(0), 0, 0), instances, parallel: false);

        Assert.Equal(0, field.Reused);
    }

    /// <summary>The first update has nothing to keep, however it is asked.</summary>
    [Fact]
    public void TheFirstUpdateKeepsNothing() {
        var field = new GlobalDistanceField(16, 4f, 1);

        field.Update(Vector3.Zero, Instances(), parallel: false, scroll: true);

        Assert.Equal(0, field.Reused);
    }

    /// <summary>
    ///     A parallel scroll and a serial one are the same field, for the same reason a parallel
    ///     composite is: a cell is written by whoever owns it and read by nobody.
    /// </summary>
    [Fact]
    public void AParallelScrollIsIdenticalToASerialOne() {
        var instances = Instances();
        var step = new Vector3(new GlobalDistanceField(16, 4f, 1).CellSizeOf(0) * 2f, 0, 0);

        var parallel = new GlobalDistanceField(16, 4f, 1);
        parallel.Update(Vector3.Zero, instances);
        parallel.Update(step, instances, scroll: true);

        var serial = new GlobalDistanceField(16, 4f, 1);
        serial.Update(Vector3.Zero, instances, parallel: false);
        serial.Update(step, instances, parallel: false, scroll: true);

        for (var z = 0; z < 16; z++) {
            for (var y = 0; y < 16; y++) {
                for (var x = 0; x < 16; x++) {
                    Assert.Equal(serial[0, x, y, z], parallel[0, x, y, z]);
                }
            }
        }
    }

    static DistanceFieldInstance[] Instances() {
        var (vertices, indices) = Shapes.Sphere(1f, 12, 16);
        var sphere = MeshDistanceFieldBaker.Bake(vertices, indices, new() { Resolution = 10, SignRayCount = 8 });

        return [
            DistanceFieldInstance.At(sphere, new(1f, 0f, 0f)),
            DistanceFieldInstance.At(sphere, new(-2f, 1f, 1.5f))
        ];
    }
}
