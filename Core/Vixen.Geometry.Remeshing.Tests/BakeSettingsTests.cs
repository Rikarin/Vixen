// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The bake's per-map settings, each against an answer worked out before the bake ran.</summary>
/// <remarks>
///     ⚠
///     <b>
///         Every setting here is a number that changes a picture, and a picture that looks plausible
///         is what a setting nothing reads produces.
///     </b>
///     So each test sets up a scene where the setting's
///     effect has a closed form or a sign, and asserts that rather than "it is different".
/// </remarks>
public class BakeSettingsTests {
    /// <summary>A source face turned away from the target is passed through when backfaces are ignored.</summary>
    /// <remarks>
    ///     A sheet facing down sits just under the target and a sheet facing up a little further above
    ///     it. The nearer of the two is the wrong way round, so without the setting it wins and the map
    ///     is a dent; with it the ray goes through and finds the sheet that faces the target's way.
    /// </remarks>
    [Fact]
    public void Ignoring_backfaces_passes_a_source_face_turned_away_and_finds_the_one_behind() {
        var source = Sheets((-0.02f, false), (0.05f, true));
        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);

        BakeSettings settings = new() { Resolution = 16, Gutter = 1, Space = BakeSpace.Object, SearchRadius = 0.1f };

        var nearer = MapBaker.Bake(source, target, settings);
        var culled = MapBaker.Bake(source, target, settings with { IgnoreBackfaces = true });

        var (seenNearer, seenCulled) = (0, 0);

        foreach (var index in Covered(nearer)) {
            Assert.True(nearer.Normals[index].Z < -0.99f, $"Texel {index} baked {nearer.Normals[index]}.");
            seenNearer++;
        }

        foreach (var index in Covered(culled)) {
            Assert.True(culled.Normals[index].Z > 0.99f, $"Texel {index} baked {culled.Normals[index]}.");
            Assert.InRange(culled.Displacement[index], 0.049f, 0.051f);
            seenCulled++;
        }

        Assert.True(seenNearer > 0 && seenCulled > 0, string.Join(" · ", culled.Warnings));
    }

    /// <summary>The front and rear distances reach apart, and the nearer hit is nearer in length.</summary>
    /// <remarks>
    ///     ⚠ <b>The second half is the defect a fraction comparison has.</b> The source is 0.3 above and
    ///     0.2 below; with 0.5 in front and 0.25 behind, the hit above is 60% of its ray and the one
    ///     below 80% of its — so comparing fractions picks the further one.
    /// </remarks>
    [Fact]
    public void Front_and_rear_distances_reach_apart_and_the_nearer_surface_wins_by_length() {
        var below = Sheets((-0.2f, true));
        var both = Sheets((-0.2f, true), (0.3f, true));
        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);

        BakeSettings settings = new() {
            Resolution = 16,
            Gutter = 1,
            Space = BakeSpace.Object,
            Units = BakeUnits.Absolute,
            SearchRadius = 0.5f,
            RearDistance = 0.1f
        };

        var short_ = MapBaker.Bake(below, target, settings);

        Assert.True(short_.Covered > 0, string.Join(" · ", short_.Warnings));
        Assert.Equal(short_.Covered, short_.Missed);

        var reaching = MapBaker.Bake(both, target, settings with { RearDistance = 0.25f });

        Assert.Equal(0, reaching.Missed);

        foreach (var index in Covered(reaching)) {
            Assert.InRange(reaching.Displacement[index], -0.201f, -0.199f);
        }
    }

    /// <summary>An absolute distance is a length: the same setting misses the same model ten times larger.</summary>
    [Fact]
    public void An_absolute_distance_is_a_length_and_does_not_grow_with_the_model() {
        BakeSettings settings = new() {
            Resolution = 16,
            Gutter = 1,
            Space = BakeSpace.Object,
            Units = BakeUnits.Absolute,
            SearchRadius = 0.3f
        };

        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);
        var near = MapBaker.Bake(Sheets((0.2f, true)), target, settings);
        var far = MapBaker.Bake(
            TransferFixtures.Scaled(Sheets((0.2f, true)), 10f),
            TransferFixtures.Scaled(MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f), 10f),
            settings
        );

        Assert.Equal(0, near.Missed);
        Assert.True(far.Covered > 0, string.Join(" · ", far.Warnings));
        Assert.Equal(far.Covered, far.Missed);

        // And the relative default reaches both, because a fraction of the diagonal grows with it.
        var relative = settings with { Units = BakeUnits.Relative, SearchRadius = 0.1f };

        Assert.Equal(
            0,
            MapBaker.Bake(
                    TransferFixtures.Scaled(Sheets((0.2f, true)), 10f),
                    TransferFixtures.Scaled(
                        MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f),
                        10f
                    ),
                    relative
                )
                .Missed
        );
    }

    /// <summary>A narrow cone does not see a ceiling that only a wide one reaches.</summary>
    /// <remarks>
    ///     The ceiling starts one unit to the side of the left of the floor and two units up, so from
    ///     there it is at least 26.6° off the normal: a 20° cone sees open sky and the hemisphere does not.
    /// </remarks>
    [Fact]
    public void A_narrow_occlusion_cone_misses_a_ceiling_off_to_the_side() {
        var wide = Overhung(new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion });
        var narrow = Overhung(new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, OcclusionSpread = 20f });

        var seen = 0;

        foreach (var index in Covered(wide).Where(index => Column(wide, index) < 6)) {
            Assert.True(wide.AmbientOcclusion![index] < 1f, $"Texel {index} saw the whole hemisphere.");
            Assert.Equal(1f, narrow.AmbientOcclusion![index]);
            seen++;
        }

        Assert.True(seen > 0, "No texel lay clear of the ceiling, so the test proves nothing.");
    }

    /// <summary>With a linear falloff, a ceiling at height h reads the mean of h/(R·cos θ).</summary>
    /// <remarks>
    ///     ⚠ <b>A closed form.</b> A cosine-distributed ray meets a ceiling <i>h</i> up at
    ///     <c>h / cos θ</c>, and the mean of <c>1 / cos θ</c> under that density is two — so the open
    ///     fraction is <c>2h / R</c> where it was exactly zero with no falloff. Sixty-four midpoint
    ///     samples of an integrand that blows up at the horizon read it a few percent low.
    /// </remarks>
    [Fact]
    public void A_linear_falloff_lets_a_distant_occluder_count_for_less() {
        var hard = Sealed(new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion });
        var linear = Sealed(
            new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, OcclusionFalloff = OcclusionFalloff.Linear }
        );
        var smooth = Sealed(
            new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, OcclusionFalloff = OcclusionFalloff.Smooth }
        );

        var reach = 0.5f * Vector3.Distance(new(-20f, -20f, 0f), new(20f, 20f, 0.1f));
        var wanted = 2f * 0.1f / reach;
        var seen = 0;

        foreach (var index in Covered(hard)) {
            Assert.Equal(0f, hard.AmbientOcclusion![index]);
            Assert.InRange(linear.AmbientOcclusion![index], wanted * 0.85f, wanted * 1.05f);

            // A smoothstep stays near one for a near hit, so near contact is darker than linear.
            Assert.True(smooth.AmbientOcclusion![index] < linear.AmbientOcclusion[index]);
            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", hard.Warnings));
    }

    /// <summary>A minimum distance skips an occluder nearer than it and keeps one beyond it.</summary>
    /// <remarks>
    ///     The ceiling is a tenth up. Skipping hits nearer than one unit leaves only the rays within
    ///     about six degrees of the horizon, whose slant distance is longer than that — and of
    ///     sixty-four cosine samples exactly one is that low.
    /// </remarks>
    [Fact]
    public void A_minimum_occluder_distance_skips_what_is_nearer() {
        var reach = Vector3.Distance(new(-20f, -20f, 0f), new(20f, 20f, 0.1f));
        var skipped = Sealed(
            new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, OcclusionMinimum = 1f / reach }
        );
        var seen = 0;

        foreach (var index in Covered(skipped)) {
            Assert.Equal(63f / 64f, skipped.AmbientOcclusion![index], 4);
            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", skipped.Warnings));
    }

    /// <summary>A minimum occluder distance at or past the occlusion radius is refused.</summary>
    /// <remarks>
    ///     Every ray would start past its own reach and see nothing, so the occlusion would be white and
    ///     the bent normal the normal, under a ceiling a tenth up that makes them nearly black. Thickness
    ///     reads the radius only, so the same minimum is no reason to refuse it.
    /// </remarks>
    [Theory]
    [InlineData(MeshMaps.AmbientOcclusion, 0.5f)]
    [InlineData(MeshMaps.AmbientOcclusion, 2f)]
    [InlineData(MeshMaps.BentNormal, 0.5f)]
    public void A_minimum_occluder_distance_at_or_past_the_radius_is_refused(MeshMaps maps, float minimum) {
        BakeSettings settings =
            new() { Resolution = 24, Maps = maps, OcclusionRadius = 0.5f, OcclusionMinimum = minimum };

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => Sealed(settings));
        Assert.Equal("settings", refused.ParamName);

        Assert.NotEmpty(Covered(Sealed(settings with { Maps = MeshMaps.Thickness })));
    }

    /// <summary>An occlusion ray ignoring backfaces passes through a ceiling seen from behind.</summary>
    /// <remarks>The ceiling faces up, so the floor's rays meet the back of it.</remarks>
    [Fact]
    public void An_occlusion_ray_ignoring_backfaces_sees_through_a_ceiling_from_behind() {
        var culled = Sealed(
            new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, OcclusionIgnoresBackfaces = true }
        );

        Assert.NotEmpty(Covered(culled));
        Assert.All(Covered(culled), index => Assert.Equal(1f, culled.AmbientOcclusion![index]));
    }

    /// <summary>Occlusion within a part leaves out the ceiling, which is another part.</summary>
    [Fact]
    public void Occlusion_within_a_part_leaves_out_every_other_part() {
        // The floor's sixteen faces come first in the source, then the ceiling's sixteen.
        BakeParts parts = new([..Enumerable.Repeat(0, 16), ..Enumerable.Repeat(1, 16)], [..Enumerable.Repeat(0, 16)]);

        var whole = Sealed(new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, Parts = parts });
        var within = Sealed(
            new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, Parts = parts, OcclusionWithinPart = true }
        );

        Assert.NotEmpty(Covered(whole));
        Assert.All(Covered(whole), index => Assert.Equal(0f, whole.AmbientOcclusion![index]));
        Assert.All(Covered(within), index => Assert.Equal(1f, within.AmbientOcclusion![index]));
    }

    /// <summary>A ground plane under a sphere darkens its underside, and a plane out of reach does nothing.</summary>
    [Fact]
    public void A_ground_plane_occludes_and_one_out_of_reach_does_not() {
        var open = SphereOcclusion(new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion });
        var grounded = SphereOcclusion(new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, GroundPlane = true });
        var far = SphereOcclusion(
            new() { Resolution = 24, Maps = MeshMaps.AmbientOcclusion, GroundPlane = true, GroundOffset = -10f }
        );

        Assert.All(Covered(open), index => Assert.Equal(1f, open.AmbientOcclusion![index]));
        Assert.Contains(Covered(grounded), index => grounded.AmbientOcclusion![index] < 0.9f);
        Assert.All(Covered(far), index => Assert.Equal(1f, far.AmbientOcclusion![index]));
    }

    /// <summary>Normalised thickness reads one at its thickest, and keeps the ratios.</summary>
    [Fact]
    public void Normalised_thickness_is_one_at_its_thickest() {
        var plain = SphereOcclusion(new() { Resolution = 24, Maps = MeshMaps.Thickness, OcclusionRadius = 1f });
        var normalised = SphereOcclusion(
            new() { Resolution = 24, Maps = MeshMaps.Thickness, OcclusionRadius = 1f, ThicknessNormalised = true }
        );

        var largest = Covered(plain).Max(index => plain.Thickness![index]);

        Assert.True(largest is > 0.3f and < 0.5f, $"The plain map's largest value is {largest}.");
        Assert.Equal(1f, Covered(normalised).Max(index => normalised.Thickness![index]));

        foreach (var index in Covered(plain)) {
            Assert.Equal(plain.Thickness![index] / largest, normalised.Thickness![index], 5);
        }
    }

    /// <summary>A height map's range is what the setting says: the measured largest, the rays' reach, or a number.</summary>
    [Fact]
    public void The_height_range_is_measured_the_ray_distance_or_custom() {
        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);
        BakeSettings settings = new() {
            Resolution = 16,
            Gutter = 1,
            Units = BakeUnits.Absolute,
            SearchRadius = 0.5f,
            RearDistance = 0.3f
        };

        var measured = MapBaker.Bake(Sheets((0.1f, true)), target, settings);
        var rays = MapBaker.Bake(Sheets((0.1f, true)), target, settings with { HeightRange = HeightRange.RayDistance });
        var custom = MapBaker.Bake(
            Sheets((0.1f, true)),
            target,
            settings with { HeightRange = HeightRange.Custom, HeightScale = 2f }
        );

        Assert.Equal(0.1f, measured.DisplacementRange, 3);
        Assert.Equal(0.5f, rays.DisplacementRange, 5);
        Assert.Equal(2f, custom.DisplacementRange, 5);
    }

    /// <summary>A mesh with one assigned material keeps its shells; asking for the groups gets the one.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect: every model file's reader assigns every face its material</b>, so a prop
    ///     with one material was one assigned group, and its id map one flat colour with every shell
    ///     lost. The strips are two shells of the one group.
    /// </remarks>
    [Fact]
    public void One_assigned_material_keeps_its_shells_unless_the_groups_are_asked_for() {
        var source = MeshMapTests.Strips();

        for (var face = 0; face < source.FaceCount; face++) {
            source.SetGroup(face, 0);
        }

        var target = MeshMapTests.Unwrapped(MeshMapTests.Strips(), 0.02f, true);
        BakeSettings settings = new() { Resolution = 64, Gutter = 2, Maps = MeshMaps.Id, Space = BakeSpace.Object };

        var automatic = MapBaker.Bake(source, target, settings);
        var groups = MapBaker.Bake(source, target, settings with { IdSource = BakeIdSource.Groups });

        Assert.Equal([0, 1], Ids(automatic));
        Assert.Equal([0], Ids(groups));
    }

    /// <summary>Shells asked for are shells even over assigned groups, and never read as packed colours.</summary>
    [Fact]
    public void Shells_asked_for_are_shells_and_never_packed_colours() {
        var source = MeshMapTests.Strips();
        var target = MeshMapTests.Unwrapped(MeshMapTests.Strips(), 0.02f, true);
        BakeSettings settings = new() {
            Resolution = 64,
            Gutter = 2,
            Maps = MeshMaps.Id,
            Space = BakeSpace.Object,
            GroupsAreColours = true
        };

        var groups = MapBaker.Bake(source, target, settings);
        var shells = MapBaker.Bake(source, target, settings with { IdSource = BakeIdSource.Shells });

        Assert.Equal([0, 2], Ids(groups));
        Assert.True(groups.IdsAreColours);
        Assert.Equal([0, 1], Ids(shells));
        Assert.False(shells.IdsAreColours);
    }

    /// <summary>The hue-shift and grey palettes rank the ids present and spread them evenly.</summary>
    [Fact]
    public void The_hue_shift_and_grey_palettes_spread_the_ids_present_evenly() {
        int[] ids = [40, 5, 9, -1, 9];

        var grey = MapBaker.IdColours(ids, false, IdPalette.Grey);

        Assert.Equal(new(1f), grey[0]);
        Assert.Equal(new(1f / 3f), grey[1]);
        Assert.Equal(new(2f / 3f), grey[2]);
        Assert.Equal(Vector3.Zero, grey[3]);

        var hues = MapBaker.IdColours(ids, false, IdPalette.HueShift);

        // Three ids, a third of the wheel apart: red, then green, then blue lead.
        Assert.True(hues[1].X > hues[1].Y && hues[1].X > hues[1].Z, $"{hues[1]}");
        Assert.True(hues[2].Y > hues[2].X && hues[2].Y > hues[2].Z, $"{hues[2]}");
        Assert.True(hues[0].Z > hues[0].X && hues[0].Z > hues[0].Y, $"{hues[0]}");
        Assert.Equal(hues[2], hues[4]);
        Assert.Equal(Vector3.Zero, hues[3]);

        Assert.Equal(MapBaker.IdColour(9), MapBaker.IdColours(ids, false, IdPalette.Random)[2]);
    }

    /// <summary>A studio's own endings match its parts, each side by its own.</summary>
    [Fact]
    public void Part_names_match_by_the_endings_each_side_is_given() {
        Assert.Equal("bolt", BakeParts.Key("Bolt_HP", ["_hp"]));
        Assert.Equal("bolt_hp", BakeParts.Key("Bolt_HP", ["_high"]));

        var parts = BakeParts.ByName(
            [("bolt_hipoly", 2), ("nut_low", 1)],
            [("bolt_lopoly", 3), ("nut_low", 1)],
            ["_hipoly"],
            ["_lopoly", "_low"]
        );

        Assert.Equal([0, 0, 1], parts.Source);

        // ⚠ `nut_low` is not the high end of anything with these endings, so the low `nut` matches no source.
        Assert.Equal([0, 0, 0, -1], parts.Target);
    }

    /// <summary>The cone and uniform samplers draw from the densities they claim.</summary>
    /// <remarks>
    ///     The mean of <c>cos θ</c> is two thirds under the cosine density and a half under the uniform
    ///     one, and nothing leaves a cone of 30° when that is the spread.
    /// </remarks>
    [Fact]
    public void The_cone_and_uniform_samplers_draw_from_their_densities() {
        const int count = 4096;
        var cosine = Enumerable.Range(0, count)
            .Select(i => HemisphereSampler.Local(i, count, 0f, 1f, false))
            .ToArray();
        var uniform = Enumerable.Range(0, count)
            .Select(i => HemisphereSampler.Local(i, count, 0f, 1f, true))
            .ToArray();
        var spread = MathF.Pow(MathF.Sin(MathF.PI / 6f), 2f);
        var cone = Enumerable.Range(0, count)
            .Select(i => HemisphereSampler.Local(i, count, 0.3f, spread, false))
            .ToArray();

        Assert.Equal(2f / 3f, cosine.Average(static direction => direction.Z), 3);
        Assert.Equal(0.5f, uniform.Average(static direction => direction.Z), 3);
        Assert.All(
            cone,
            static direction => Assert.True(direction.Z >= MathF.Cos(MathF.PI / 6f) - 1e-5f, $"{direction}")
        );
        Assert.All(uniform, static direction => Assert.Equal(1f, direction.Length(), 4));

        for (var index = 0; index < 64; index++) {
            Assert.Equal(
                HemisphereSampler.Local(index, 64, 0.25f),
                HemisphereSampler.Local(index, 64, 0.25f, 1f, false)
            );
        }
    }

    /// <summary>An atlas wider than it is tall bakes at its own shape, each texel where the square bake puts it.</summary>
    /// <remarks>
    ///     A texel's centre in row y of a 48 by 16 bake is the centre of row 3y + 1 of the 48 by 48 one, so the two
    ///     bakes measure the same place there and must hold the same value.
    /// </remarks>
    [Fact]
    public void A_bake_wider_than_it_is_tall_holds_what_a_square_one_does_at_the_same_place() {
        var source = Sheets((0.05f, true));
        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);
        BakeSettings settings = new() {
            Resolution = 48,
            Gutter = 0,
            Space = BakeSpace.Object,
            SearchRadius = 0.1f,
            Maps = MeshMaps.Position
        };

        var square = MapBaker.Bake(source, target, settings);
        var wide = MapBaker.Bake(source, target, settings with { Height = 16 });

        Assert.Equal(16, wide.Rows);
        Assert.Equal(48 * 16, wide.Coverage.Count);
        Assert.Equal(48 * 16, wide.Normals.Count);
        Assert.Equal(48 * 16, wide.Position!.Count);

        // ⚠ A taller texel overlaps more triangles than the square one at its centre, and the lowest of them owns
        // it, so near a triangle's edge the two may measure from neighbouring triangles: within a texel's reach,
        // and not always exactly the same place.
        var (compared, agreed) = (0, 0);

        foreach (var index in Covered(wide)) {
            var (x, y) = (index % 48, index / 48);
            var same = (3 * y + 1) * 48 + x;

            if (!square.Coverage[same]) {
                continue;
            }

            compared++;
            agreed += Vector3.Distance(square.Position![same], wide.Position[index]) < 0.03f ? 1 : 0;
        }

        Assert.True(compared > 100, $"Only {compared} texels were covered in both.");
        Assert.True(agreed >= compared * 9 / 10, $"{agreed} of {compared} texels agree with the square bake.");
    }

    /// <summary>The position map's range turns the same point into the value its closed form says.</summary>
    /// <remarks>
    ///     The source is a four-unit sheet from -2 to 2 at a height of 0.05, so its box is 4 by 4 by nothing: the cube
    ///     divides by 4 from -2, the sphere by the box's diagonal, √32, from the middle, and none keeps the point.
    /// </remarks>
    [Fact]
    public void The_position_range_places_a_point_by_the_box_the_cube_the_sphere_or_not_at_all() {
        var source = Sheets((0.05f, true));
        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);
        BakeSettings settings = new() {
            Resolution = 16,
            Gutter = 0,
            Space = BakeSpace.Object,
            SearchRadius = 0.1f,
            Maps = MeshMaps.Position
        };

        var raw = MapBaker.Bake(source, target, settings with { PositionRange = PositionRange.None });
        var cube = MapBaker.Bake(source, target, settings with { PositionRange = PositionRange.Cube });
        var sphere = MapBaker.Bake(source, target, settings with { PositionRange = PositionRange.Sphere });
        var diagonal = MathF.Sqrt(32f);
        var seen = 0;

        foreach (var index in Covered(raw)) {
            var point = raw.Position![index];

            Assert.InRange(point.Z, 0.049f, 0.051f);
            Assert.InRange(point.X, -1.01f, 1.01f);
            Assert.InRange(cube.Position![index].X, (point.X + 2f) / 4f - 0.001f, (point.X + 2f) / 4f + 0.001f);
            Assert.InRange(cube.Position[index].Z, -0.001f, 0.001f);
            Assert.InRange(sphere.Position![index].Y, point.Y / diagonal + 0.499f, point.Y / diagonal + 0.501f);
            Assert.InRange(sphere.Position[index].Z, 0.499f, 0.501f);
            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", raw.Warnings));
    }

    /// <summary>
    ///     Every range says the box its values span, and that box turns a value back into the point it was: what a decal
    ///     or a path on the model reads the map through (Patina #133).
    /// </summary>
    [Fact]
    public void Every_position_range_says_the_box_that_turns_a_value_back_into_its_point() {
        var source = Sheets((0.05f, true));
        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);
        BakeSettings settings = new() {
            Resolution = 16,
            Gutter = 0,
            Space = BakeSpace.Object,
            SearchRadius = 0.1f,
            Maps = MeshMaps.Position
        };

        var raw = MapBaker.Bake(source, target, settings with { PositionRange = PositionRange.None });

        Assert.Equal(new(Vector3.Zero, Vector3.One), raw.PositionBox);

        foreach (var range in Enum.GetValues<PositionRange>()) {
            var baked = MapBaker.Bake(source, target, settings with { PositionRange = range });
            var box = baked.PositionBox;
            var seen = 0;

            foreach (var index in Covered(raw)) {
                var back = box.Minimum + baked.Position![index] * (box.Maximum - box.Minimum);

                Assert.True(
                    Vector3.Distance(back, raw.Position![index]) < 0.001f,
                    $"{range}: {back} is not {raw.Position[index]}."
                );
                seen++;
            }

            Assert.True(seen > 0, string.Join(" · ", baked.Warnings));
        }
    }

    /// <summary>A bake's planes carry its height, so a writer lays them out as wide as they are.</summary>
    [Fact]
    public void A_wide_bakes_planes_say_their_height() {
        var source = Sheets((0.05f, true));
        var target = MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);
        var wide = MapBaker.Bake(
            source,
            target,
            new() {
                Resolution = 16,
                Height = 8,
                Gutter = 1,
                Space = BakeSpace.Object,
                SearchRadius = 0.1f
            }
        );

        foreach (var plane in MeshMapPlanes.Of(wide)) {
            Assert.Equal(16, plane.Resolution);
            Assert.Equal(8, plane.Rows);
            Assert.Equal(16 * 8 * plane.Channels, plane.Samples.Length);
        }
    }

    static IEnumerable<int> Covered(BakedMaps maps) =>
        Enumerable.Range(0, maps.Coverage.Count).Where(index => maps.Coverage[index]);

    static int Column(BakedMaps maps, int index) => index % maps.Resolution;

    static int[] Ids(BakedMaps maps) => [..maps.Ids!.Where(static id => id >= 0).Distinct().Order()];

    /// <summary>Flat four-unit sheets at heights, each facing up or down: wider than every target, so no ray falls off one.</summary>
    static EditMesh Sheets(params (float Z, bool Up)[] sheets) {
        var mesh = new EditMesh();

        foreach (var (z, up) in sheets) {
            var corners = new int[5, 5];

            for (var i = 0; i < 5; i++) {
                for (var j = 0; j < 5; j++) {
                    corners[i, j] = mesh.AddPosition(new(i - 2f, j - 2f, z));
                }
            }

            for (var i = 0; i < 4; i++) {
                for (var j = 0; j < 4; j++) {
                    Span<int> loop = up
                        ? [corners[i, j], corners[i + 1, j], corners[i + 1, j + 1], corners[i, j + 1]]
                        : [corners[i, j], corners[i, j + 1], corners[i + 1, j + 1], corners[i + 1, j]];

                    mesh.AddFace(loop);
                }
            }
        }

        return mesh;
    }

    /// <summary>The floor under a ceiling a tenth up and far wider than it, which <see cref="MeshMapTests" /> also bakes.</summary>
    static BakedMaps Sealed(BakeSettings settings) =>
        MapBaker.Bake(
            MeshMapTests.Roofed(-20f, 20f, 0.1f),
            MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 4f, static _ => 0), 0.01f),
            settings with { Resolution = 24, Gutter = 2, Space = BakeSpace.Object, SearchRadius = 0.001f }
        );

    /// <summary>The floor under a ceiling two units up that covers only <c>x ≥ 0</c>.</summary>
    static BakedMaps Overhung(BakeSettings settings) =>
        MapBaker.Bake(
            MeshMapTests.Roofed(0f, 20f, 2f),
            MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 4f, static _ => 0), 0.01f),
            settings with { Resolution = 24, Gutter = 2, Space = BakeSpace.Object, SearchRadius = 0.001f }
        );

    /// <summary>A cap of a unit icosphere against the whole sphere, which is convex and occludes nothing.</summary>
    static BakedMaps SphereOcclusion(BakeSettings settings) =>
        MapBaker.Bake(
            MeshMapTests.Icosphere(3, 1f),
            MeshMapTests.Unwrapped(MeshMapTests.Cap(MeshMapTests.Icosphere(3, 1.01f), 0.5f), 0f),
            settings with { Resolution = 24, Gutter = 2, Space = BakeSpace.Object }
        );
}
