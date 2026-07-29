// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.IrradianceFields;
using Xunit;

namespace Tests;

/// <summary>
///     Where a field is fine, and why — doc 19 § 3's "from renderer bounds".
/// </summary>
/// <remarks>
///     Device-free like everything else about the storage, and for the same reason: which bricks a
///     policy splits has a closed-form answer, so it can be asserted rather than looked at.
/// </remarks>
public class IrradianceRefinementPolicyTests {
    /// <summary>Eight cells across, so a size-eight brick covers it and can be split three times.</summary>
    static IrradianceField Field() {
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(8), new(new(8)));

        field.AllocateAll(8);

        return field;
    }

    /// <summary>The bricks a field holds, by size.</summary>
    static Dictionary<int, int> Sizes(IrradianceField field) {
        Dictionary<int, int> counts = [];

        foreach (var brick in field.Bricks) {
            counts[brick.Size] = counts.GetValueOrDefault(brick.Size) + 1;
        }

        return counts;
    }

    [Fact]
    public void AFieldWithNoBandsIsLeftAlone() {
        var field = Field();
        var policy = new IrradianceRefinementPolicy();

        Assert.Equal(0, policy.Apply(field, [new BoundingBox(new(-1f), new(1f))]));
        Assert.Equal(1, field.BrickCount);
    }

    [Fact]
    public void AFieldWithNoRenderersIsLeftAlone() {
        var field = Field();
        var policy = new IrradianceRefinementPolicy { Bands = { new(0f, 1) } };

        Assert.Equal(0, policy.Apply(field, []));
        Assert.Equal(1, field.BrickCount);
    }

    /// <summary>
    ///     A renderer in one corner refines that corner and leaves the rest coarse.
    /// </summary>
    /// <remarks>
    ///     The claim the whole policy exists to make. A field refined everywhere is the field it
    ///     already was, only more expensive; the point is that resolution follows geometry.
    /// </remarks>
    [Fact]
    public void OnlyTheBricksNearAGeometryAreSplit() {
        var field = Field();
        var policy = new IrradianceRefinementPolicy { Bands = { new(0f, 1) } };

        // A box wholly inside the field's near-lower-left octant.
        Assert.True(policy.Apply(field, [new BoundingBox(new(-3.5f), new(-3f))]) > 0);

        var sizes = Sizes(field);

        Assert.True(sizes.TryGetValue(1, out var finest) && finest > 0, "nothing reached the finest size");
        Assert.True(sizes.Keys.Count > 1, "the whole field was refined, so nothing was left coarse");

        // And what is fine is where the renderer is. A brick of size one whose bounds do not touch the
        // box would be resolution spent on empty air.
        var region = new BoundingBox(new(-4f), new(-2.9f));

        foreach (var brick in field.Bricks) {
            if (brick.Size == 1) {
                Assert.True(
                    field.BrickBounds(brick).Intersects(region),
                    $"a brick at {brick.Cell} is finest and nowhere near the geometry"
                );
            }
        }
    }

    /// <summary>
    ///     <b>Bands grade the field, and they do it coarsest first whatever order they are written in.</b>
    /// </summary>
    /// <remarks>
    ///     <c>Refine</c> splits only bricks larger than its target, so a narrow fine band applied before
    ///     a wide coarse one leaves the coarse band nothing to do — every brick it would have touched is
    ///     already finer. Sorting on use is what makes the list order a presentation choice rather than
    ///     a correctness one, and this is the assertion that says so.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeSizesStandAtOnce(bool reversed) {
        var field = Field();
        var policy = new IrradianceRefinementPolicy();

        if (reversed) {
            policy.Bands.Add(new(0.1f, 1));
            policy.Bands.Add(new(1f, 4));
        } else {
            policy.Bands.Add(new(1f, 4));
            policy.Bands.Add(new(0.1f, 1));
        }

        // ⚠ Off the octant boundary. A box straddling the origin touches all eight children of the
        // root brick at once, so the wide band refines everything and there is no coarse remainder to
        // observe — the fixture would then be asserting about its own geometry rather than the policy.
        policy.Apply(field, [new BoundingBox(new(-2.6f), new(-2.4f))]);

        var sizes = Sizes(field);

        Assert.True(sizes.ContainsKey(1), "nothing reached the finest size");
        Assert.True(sizes.ContainsKey(4), $"no medium band survived: {string.Join(", ", sizes.Keys)}");
    }

    /// <summary>And a second pass over the same scene changes nothing.</summary>
    /// <remarks>
    ///     What makes this callable every frame. A policy that kept producing bricks would be one whose
    ///     answer depends on how many times it has run, which is a field that drifts rather than one
    ///     that settles.
    /// </remarks>
    [Fact]
    public void ApplyingTwiceMakesNothingTheSecondTime() {
        var field = Field();
        var policy = new IrradianceRefinementPolicy { Bands = { new(1f, 2), new(0f, 1) } };
        IReadOnlyList<BoundingBox> bounds = [new BoundingBox(new(-1f), new(1f))];

        var first = policy.Apply(field, bounds);
        var count = field.BrickCount;

        Assert.True(first > 0);
        Assert.Equal(0, policy.Apply(field, bounds));
        Assert.Equal(count, field.BrickCount);
    }

    /// <summary>
    ///     <b>A pool too small to hold the answer gives a coarser field, not an exception.</b>
    /// </summary>
    /// <remarks>
    ///     Doc 19 § 7 lists sparse residency as optional precisely because a fixed pool works: running
    ///     out means the furthest bricks are not resident, which is a quality reduction. This is that
    ///     promise, asserted — a policy asking for a hundred times what the pool holds still leaves a
    ///     field somebody can sample.
    /// </remarks>
    [Fact]
    public void APoolTooSmallDegradesRatherThanThrows() {
        // Room for four bricks and a policy that would want hundreds.
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(8), new(new Int3(4, 1, 1)));

        field.AllocateAll(8);

        var policy = new IrradianceRefinementPolicy { Bands = { new(100f, 1) } };

        policy.Apply(field, [new BoundingBox(new(-4f), new(4f))]);

        Assert.True(field.BrickCount > 0, "the field lost every brick it had");
        Assert.True(field.BrickCount <= field.Pool.Capacity, "more bricks than the pool has slots");

        // And every brick it does hold is a brick somebody can sample: a slot the indirection names
        // has to be one the pool actually allocated.
        foreach (var brick in field.Bricks) {
            Assert.True(field.Pool.IsAllocated(brick.Slot), $"cell {brick.Cell} names unallocated slot {brick.Slot}");
        }
    }
}
