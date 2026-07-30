// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.IrradianceFields;
using Xunit;

namespace Tests;

/// <summary>Bricks merging back when nothing needs them fine — the ratchet's other direction.</summary>
/// <remarks>
///     Device-free like the refinement tests, and held to the same kind of closed form: which octets
///     merge, what the merged probes hold, and what a moved scene gives back all have exact answers.
/// </remarks>
public class IrradianceCoarseningTests {
    /// <summary>Eight cells across a ±4 box — one world unit per cell, corners at ±4.</summary>
    static IrradianceField Field() =>
        new(new BoundingBox(new(-4f), new(4f)), new(8), new(new(8)));

    /// <summary>The bricks a field holds, by size.</summary>
    static Dictionary<int, int> Sizes(IrradianceField field) {
        Dictionary<int, int> counts = [];

        foreach (var brick in field.Bricks) {
            counts[brick.Size] = counts.GetValueOrDefault(brick.Size) + 1;
        }

        return counts;
    }

    [Fact]
    public void AnAlignedOctetMergesBack() {
        var field = Field();

        field.AllocateAll(2);
        Assert.Equal(64, field.BrickCount);

        // Split the corner octet, then merge it back: the split's exact inverse.
        field.Refine(new BoundingBox(new(-3.9f), new(-3.5f)), 1);
        Assert.Equal(71, field.BrickCount);

        Assert.True(field.TryMerge(new(0, 0, 0)));
        Assert.Equal(64, field.BrickCount);

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var merged));
        Assert.Equal(2, merged.Size);
    }

    [Fact]
    public void TheSubsampleKeepsWhatStoodThere() {
        var field = Field();

        field.AllocateAll(2);
        field.Refine(new BoundingBox(new(-3.9f), new(-3.5f)), 1);

        // Every fine probe holds its own position, so after the merge each parent probe must hold
        // the position it stands on — a copy of something real, not an average of neighbours.
        foreach (var brick in field.Bricks) {
            if (brick.Size != 1) {
                continue;
            }

            for (var z = 0; z < IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y < IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x < IrradianceBrickPool.BrickResolution; x++) {
                        var position = field.ProbePosition(brick, x, y, z);

                        field.SetProbe(
                            brick,
                            x,
                            y,
                            z,
                            new(new(position, Vector3.Zero, Vector3.Zero, Vector3.Zero), 1f, 1f)
                        );
                    }
                }
            }
        }

        Assert.True(field.TryMerge(new(0, 0, 0)));
        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var merged));

        for (var z = 0; z < IrradianceBrickPool.BrickResolution; z++) {
            for (var y = 0; y < IrradianceBrickPool.BrickResolution; y++) {
                for (var x = 0; x < IrradianceBrickPool.BrickResolution; x++) {
                    var probe = field.GetProbe(merged, x, y, z);
                    var expected = field.ProbePosition(merged, x, y, z);

                    Assert.Equal(1f, probe.Validity);
                    Assert.Equal(expected.X, probe.Radiance.L00.X, 1e-5f);
                    Assert.Equal(expected.Y, probe.Radiance.L00.Y, 1e-5f);
                    Assert.Equal(expected.Z, probe.Radiance.L00.Z, 1e-5f);
                }
            }
        }
    }

    [Fact]
    public void AMixedOctetRefuses() {
        var field = Field();

        field.AllocateAll(4);
        field.Split(new(0, 0, 0));
        field.Split(new(2, 0, 0));

        var before = field.BrickCount;

        // The sibling at (2,0,0) is now eight singles — a grandchild somebody asked for, and a
        // merge over it would be refinement undone at a distance.
        Assert.False(field.TryMerge(new(0, 0, 0)));
        Assert.Equal(before, field.BrickCount);
    }

    [Fact]
    public void MisalignmentAndNonOriginsRefuse() {
        var field = Field();

        field.AllocateAll(2);

        // (1,1,1) is inside the corner brick but is not its origin.
        Assert.False(field.TryMerge(new(1, 1, 1)));

        // (2,0,0) is a brick's origin, but a size-four brick cannot start there.
        Assert.False(field.TryMerge(new(2, 0, 0)));
        Assert.Equal(64, field.BrickCount);
    }

    [Fact]
    public void ThePolicyGivesSlotsBackWhenGeometryMovesOn() {
        var field = Field();

        field.AllocateAll(4);

        var policy = new IrradianceRefinementPolicy { Bands = { new(0.5f, 1) }, CoarsenTo = 4 };
        var near = new BoundingBox(new(-3.6f), new(-3.4f));
        var far = new BoundingBox(new(3.4f), new(3.6f));

        policy.Apply(field, [near]);

        var refined = Sizes(field);

        Assert.True(refined.ContainsKey(1), "the band refined nothing, so there is nothing to give back");
        Assert.Equal(0, policy.Coarsened);

        // The geometry moves to the opposite corner: the old region merges all the way back and
        // the new one refines — a symmetric scene, so the size census must match exactly.
        policy.Apply(field, [far]);

        Assert.True(policy.Coarsened > 0, "nothing merged after the geometry left");
        Assert.Equal(refined, Sizes(field));

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var corner));
        Assert.Equal(4, corner.Size);
    }

    [Fact]
    public void OneApplyNeverUndoesItself() {
        var field = Field();

        field.AllocateAll(4);

        var policy = new IrradianceRefinementPolicy { Bands = { new(0.5f, 1) }, CoarsenTo = 4 };
        var box = new BoundingBox(new(-3.6f), new(-3.4f));

        policy.Apply(field, [box]);
        Assert.Equal(0, policy.Coarsened);

        // Steady state: the same scene neither refines nor merges.
        policy.Apply(field, [box]);
        Assert.Equal(0, policy.Refined);
        Assert.Equal(0, policy.Coarsened);
    }

    [Fact]
    public void AMergeThatAddsNoCoverageRefuses() {
        // One brick covering the whole grid: every would-be sibling hangs past the edge, so a merge
        // would rename the brick to double its size and cover not one cell more — and the next call
        // would double it again, without end. The first version of coarsening did exactly that, by
        // way of stale candidates, until a brick claimed to be thirty-two thousand cells across.
        var field = Field();

        field.AllocateAll(8);

        Assert.Equal(1, field.BrickCount);
        Assert.False(field.TryMerge(new(0, 0, 0)));

        Assert.True(field.Indirection.TryBrick(new(0, 0, 0), out var brick));
        Assert.Equal(8, brick.Size);
    }

    [Fact]
    public void CoarseningStopsAtItsOwnCeiling() {
        // The other half of the same defect: a candidate consumed by an earlier merge in the same
        // pass must not be followed to its now-coarser brick, or each stale entry merges one level
        // past what the snapshot vetted and the ceiling means nothing.
        var field = Field();

        field.AllocateAll(2);

        var policy = new IrradianceRefinementPolicy { CoarsenTo = 4 };

        policy.Apply(field, []);

        foreach (var brick in field.Bricks) {
            Assert.Equal(4, brick.Size);
        }

        Assert.Equal(8, field.BrickCount);
    }

    [Fact]
    public void AnEmptySceneOwesEverySlotBack() {
        var field = Field();

        field.AllocateAll(4);

        var policy = new IrradianceRefinementPolicy { Bands = { new(0.5f, 1) }, CoarsenTo = 4 };

        policy.Apply(field, [new BoundingBox(new(-3.6f), new(-3.4f))]);
        policy.Apply(field, []);

        Assert.True(policy.Coarsened > 0);
        Assert.Equal(8, field.BrickCount);
        Assert.Equal(new Dictionary<int, int> { [4] = 8 }, Sizes(field));
    }
}
