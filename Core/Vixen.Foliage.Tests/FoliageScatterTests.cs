// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>The scatter and its placement rules — [docs/plan/31 § The palette] and § T5.</summary>
public sealed class FoliageScatterTests {
    static (FoliageVolume Volume, int Type) Built(FoliageType? type = null) {
        var volume = new FoliageVolume();

        return (volume, volume.AddType(type ?? Types.Tree));
    }

    // --- Placing ------------------------------------------------------------

    [Fact]
    public void A_stamp_places_instances_on_the_ground_under_it() {
        var (volume, type) = Built();

        var result = FoliageScatter.Stamp(volume, type, Ground.Flat, new(50f, 50f), radius: 10f);

        Assert.True(result.Placed > 0, "a stamp on open flat ground should place something.");
        Assert.Equal(result.Placed, volume.InstanceCount);

        foreach (var chunk in volume.Chunks) {
            foreach (var instance in chunk.Instances) {
                Assert.InRange(Vector2.Distance(new(instance.Position.X, instance.Position.Z), new(50f, 50f)), 0f, 10f);
            }
        }
    }

    /// <summary>The same stamp twice produces the same trees.</summary>
    /// <remarks>
    ///     ⚠ <b>[§ D8]'s requirement, and it is what makes an undone-and-redone stroke replay.</b> The
    ///     positions are a hash of the seed and the candidate index, so they do not depend on an
    ///     iteration order, on how many instances came before, or on which machine ran it.
    /// </remarks>
    [Fact]
    public void The_same_seed_scatters_the_same_forest() {
        var (left, leftType) = Built();
        var (right, rightType) = Built();

        FoliageScatter.Stamp(left, leftType, Ground.Flat, new(50f, 50f), 12f, seed: 1234u);
        FoliageScatter.Stamp(right, rightType, Ground.Flat, new(50f, 50f), 12f, seed: 1234u);

        Assert.Equal(Positions(left), Positions(right));
    }

    [Fact]
    public void A_different_seed_scatters_a_different_one() {
        var (left, leftType) = Built();
        var (right, rightType) = Built();

        FoliageScatter.Stamp(left, leftType, Ground.Flat, new(50f, 50f), 12f, seed: 1u);
        FoliageScatter.Stamp(right, rightType, Ground.Flat, new(50f, 50f), 12f, seed: 2u);

        Assert.NotEqual(Positions(left), Positions(right));
    }

    /// <summary>No two instances of a type land closer than its spacing.</summary>
    /// <remarks>
    ///     ⚠ <b>Including within one stamp.</b> A spacing check that only asked the volume would pass
    ///     every candidate of a stamp, because none of them is in it yet — which draws as a mat of
    ///     overlapping trunks wherever the brush was held still.
    /// </remarks>
    [Fact]
    public void Nothing_lands_closer_than_the_types_spacing() {
        var (volume, type) = Built(Types.Tree with { Density = 2f, Radius = 4f });

        for (var pass = 0; pass < 5; pass++) {
            FoliageScatter.Stamp(volume, type, Ground.Flat, new(50f, 50f), 15f, seed: (uint)pass);
        }

        var placed = Positions(volume);

        Assert.True(placed.Count > 4, "the stamp should have placed a few.");

        for (var a = 0; a < placed.Count; a++) {
            for (var b = a + 1; b < placed.Count; b++) {
                var apart = Vector2.Distance(placed[a], placed[b]);

                Assert.True(apart >= 4f - 1e-3f, $"two instances landed {apart} m apart.");
            }
        }
    }

    [Fact]
    public void Strength_scales_how_many_candidates_a_stamp_generates() {
        var (full, fullType) = Built();
        var (half, halfType) = Built();

        var whole = FoliageScatter.Stamp(full, fullType, Ground.Flat, new(50f, 50f), 20f);
        var part = FoliageScatter.Stamp(half, halfType, Ground.Flat, new(50f, 50f), 20f, strength: 0.5f);

        Assert.True(part.Considered < whole.Considered);
        Assert.True(part.Considered > 0);
    }

    /// <summary>Density is per square metre, so a bigger brush places proportionally more.</summary>
    /// <remarks>
    ///     A density expressed per stamp would thin out as the brush grew, which is the version an
    ///     artist reports as "the big brush does not work".
    /// </remarks>
    [Fact]
    public void A_brush_twice_as_wide_considers_four_times_as_many() {
        var settings = Types.Tree;

        Assert.InRange(settings.CandidatesFor(20f), settings.CandidatesFor(10f) * 4 - 2, settings.CandidatesFor(10f) * 4 + 2);
    }

    // --- The rules that refuse ----------------------------------------------

    [Fact]
    public void Nothing_is_placed_where_there_is_no_ground() {
        var (volume, type) = Built();
        var nothing = new Ground(hit: _ => false);

        var result = FoliageScatter.Stamp(volume, type, nothing, new(50f, 50f), 10f);

        Assert.Equal(0, result.Placed);
        Assert.True(result.Considered > 0);
        Assert.Equal(0, volume.InstanceCount);
    }

    [Theory]
    [InlineData(0.1f, true)]
    [InlineData(0.6f, true)]
    [InlineData(1.2f, false)]
    public void Ground_outside_the_slope_range_is_refused(float slope, bool accepted) {
        var (volume, type) = Built(Types.Tree with { MinSlope = 0f, MaxSlope = MathF.PI / 4f });

        var result = FoliageScatter.Stamp(volume, type, Ground.Sloped(slope), new(50f, 50f), 12f);

        Assert.Equal(accepted, result.Placed > 0);
    }

    [Fact]
    public void Ground_outside_the_altitude_range_is_refused() {
        var (volume, type) = Built(Types.Tree with { MinAltitude = 100f, MaxAltitude = 200f });

        Assert.Equal(0, FoliageScatter.Stamp(volume, type, Ground.At(50f), new(50f, 50f), 12f).Placed);
        Assert.True(FoliageScatter.Stamp(volume, type, Ground.At(150f), new(50f, 50f), 12f).Placed > 0);
    }

    /// <summary>A layer filter only spawns where that ground is painted enough.</summary>
    [Fact]
    public void A_layer_filter_refuses_ground_that_is_not_painted_with_it() {
        var (volume, type) = Built(Types.Tree with { LayerFilter = "Grass", LayerThreshold = 0.5f });

        // Painted on the left half only.
        var patchy = new Ground(weight: at => at.X < 50f ? 1f : 0f);

        FoliageScatter.Stamp(volume, type, patchy, new(50f, 50f), 20f);

        Assert.True(volume.InstanceCount > 0);
        Assert.All(Positions(volume), at => Assert.True(at.X < 50f, $"one landed at x = {at.X}."));
    }

    /// <summary>A type with no filter never asks what is painted.</summary>
    [Fact]
    public void A_type_with_no_filter_spawns_anywhere_there_is_ground() {
        var (volume, type) = Built();
        var painted = new Ground(weight: _ => 0f);

        Assert.True(FoliageScatter.Stamp(volume, type, painted, new(50f, 50f), 12f).Placed > 0);
    }

    [Fact]
    public void A_refusal_says_which_rule_refused() {
        var (volume, type) = Built(Types.Tree with { MaxSlope = 0.1f });

        var refusal = FoliageScatter.Consider(
            volume,
            type,
            volume.Palette[type],
            Ground.Sloped(1f),
            new(10f, 10f),
            [],
            hash: 1u,
            out _
        );

        Assert.Equal(FoliageScatter.Refusal.Slope, refusal);
    }

    // --- What an instance becomes -------------------------------------------

    [Fact]
    public void Every_instance_is_scaled_inside_the_types_range() {
        var (volume, type) = Built(Types.Tree with { MinScale = 0.5f, MaxScale = 2f, Density = 0.5f });

        FoliageScatter.Stamp(volume, type, Ground.Flat, new(50f, 50f), 15f);

        var scales = volume.Chunks.SelectMany(chunk => chunk.Instances).Select(instance => instance.Scale).ToList();

        Assert.NotEmpty(scales);
        Assert.All(scales, scale => Assert.InRange(scale, 0.5f, 2f));

        // And the range is used rather than one value being drawn every time.
        Assert.True(scales.Distinct().Count() > 1, "every instance came out the same size.");
    }

    /// <summary>Alignment is a fraction of the way to the normal, not a flag.</summary>
    /// <remarks>
    ///     ⚠ A tree leaning ten per cent into a hill reads as growth; a tree lying flat on it reads
    ///     as felled. The setting has to be continuous, so a half-aligned instance sits between
    ///     upright and flat rather than at one of them.
    /// </remarks>
    [Fact]
    public void Alignment_leans_an_instance_partway_towards_the_normal() {
        var slope = Ground.Sloped(0.8f);
        var normal = slope.SampleAt(new(0f, 0f), "").Normal;

        var upright = Tilt(Types.Rock with { AlignToNormal = 0f, RandomYaw = false }, slope);
        var half = Tilt(Types.Rock with { AlignToNormal = 0.5f, RandomYaw = false }, slope);
        var flat = Tilt(Types.Rock with { AlignToNormal = 1f, RandomYaw = false }, slope);

        Assert.Equal(0f, upright, 2);
        Assert.Equal(0.8f, flat, 2);
        Assert.InRange(half, 0.2f, 0.7f);

        Assert.True(normal.Y < 1f);
    }

    [Fact]
    public void A_type_with_random_yaw_turns_its_instances_and_one_without_does_not() {
        var (turned, turnedType) = Built(Types.Tree with { RandomYaw = true, Density = 0.5f });
        var (fixedYaw, fixedType) = Built(Types.Tree with { RandomYaw = false, Density = 0.5f });

        FoliageScatter.Stamp(turned, turnedType, Ground.Flat, new(50f, 50f), 15f);
        FoliageScatter.Stamp(fixedYaw, fixedType, Ground.Flat, new(50f, 50f), 15f);

        var headings = turned.Chunks.SelectMany(chunk => chunk.Instances)
            .Select(instance => instance.Rotation)
            .Distinct()
            .Count();

        Assert.True(headings > 1);

        Assert.All(
            fixedYaw.Chunks.SelectMany(chunk => chunk.Instances),
            instance => Assert.Equal(Quaternion.Identity, instance.Rotation)
        );
    }

    /// <summary>Two draws from one hash are not correlated.</summary>
    /// <remarks>
    ///     ⚠ <b>Slicing the streams out of one hash's bits gives the yaw and the scale correlated low
    ///     bits</b>, which shows up as every large tree facing the same way — a pattern an artist sees
    ///     immediately and cannot describe. Re-hashing per stream is what avoids it.
    /// </remarks>
    [Fact]
    public void The_scale_and_the_heading_of_an_instance_are_independent() {
        var settings = Types.Tree with { MinScale = 0f, MaxScale = 1f, RandomYaw = true };
        var ground = new FoliageSurface(Vector3.Zero, Vector3.UnitY, 1f, true);

        var large = 0;
        var largeAndTurned = 0;

        for (var index = 0; index < 4_000; index++) {
            var instance = FoliageScatter.Place(settings, ground, FoliageScatter.Hash(7u, index));

            if (instance.Scale <= 0.5f) {
                continue;
            }

            large++;

            // ⚠ Which half of the circle it *faces*, from the forward vector — not from the
            // quaternion's Y term, which for a yaw of θ is sin(θ/2) and is never negative over a
            // full turn. That measures nothing and passes whatever the hash does.
            if (Quaternion.Transform(Vector3.UnitZ, instance.Rotation).X >= 0f) {
                largeAndTurned++;
            }
        }

        Assert.True(large > 1_000, "half of four thousand should have been large.");

        // Independent means about half of the large ones face each way. A correlated pair comes out
        // near 0 or near 1.
        Assert.InRange(largeAndTurned / (float)large, 0.4f, 0.6f);
    }

    static float Tilt(FoliageType settings, Ground surface) {
        var ground = surface.SampleAt(new(0f, 0f), "");
        var instance = FoliageScatter.Place(settings, ground, hash: 11u);
        var up = Quaternion.Transform(Vector3.UnitY, instance.Rotation);

        return MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(up), Vector3.UnitY), -1f, 1f));
    }

    static List<Vector2> Positions(FoliageVolume volume) =>
        [
            .. volume.Chunks
                .OrderBy(chunk => chunk.Cell.Z)
                .ThenBy(chunk => chunk.Cell.X)
                .SelectMany(chunk => chunk.Instances)
                .Select(instance => new Vector2(instance.Position.X, instance.Position.Z))
        ];
}
