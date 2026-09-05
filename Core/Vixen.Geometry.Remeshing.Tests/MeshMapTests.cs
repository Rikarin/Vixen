// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/48 § D12's seven measurements, each against a shape whose answer is known.</summary>
/// <remarks>
///     <para>
///         <b>Every assertion here is a closed form and not a golden.</b> A baked map is a picture,
///         a picture is what nobody checks, and the three ways a mesh map is quietly wrong all
///         survive being looked at: an occlusion term computed about the <i>cage</i>'s normal instead
///         of the source's is a smoothed version of the right answer; a curvature that is out by a
///         constant factor still reads as "more curved here"; an id map filtered through the gutter
///         still looks like an id map. A sphere occludes nothing and reads <c>1/r</c>, a plane's
///         position map is exact, and an id is either one the source has or it is not.
///     </para>
///     <para>
///         ⚠ <b>Two of these fixtures exist to make an assertion able to fail.</b> The sheet under a
///         ceiling is sized so that no sample can escape past the ceiling's edge — the widest
///         direction in a 64-sample set leaves at 84.9° and travels 1.13 units before it reaches a
///         ceiling 0.1 above, and the ceiling overhangs the floor by eight — so its occlusion is
///         exactly zero rather than nearly. And the two id charts are given groups 0 and 2 with
///         nothing between them, so that a dilation which averaged instead of copying would produce
///         the id 1, which is a value the source does not contain and the test can name.
///     </para>
/// </remarks>
public class MeshMapTests {
    /// <summary>The sample set is cosine-weighted, which is what makes the plain mean the estimator.</summary>
    /// <remarks>
    ///     ⚠ <b>Verify the instrument first.</b> Every occlusion number below is the mean of a
    ///     visibility over these directions, and that mean is only ambient occlusion if the
    ///     directions carry the cosine density. A uniform hemisphere passes every geometric test in
    ///     this file — a sphere still occludes nothing, a sealed floor is still sealed — and bakes a
    ///     map that is wrong everywhere in between. The two are told apart by one number: the mean of
    ///     <c>cos θ</c> is <c>2/3</c> under the cosine density and <c>1/2</c> under a uniform one.
    /// </remarks>
    [Fact]
    public void The_hemisphere_sample_set_is_cosine_weighted_rather_than_uniform() {
        const int count = 4096;

        var mean = 0f;
        var sum = Vector3.Zero;

        for (var sample = 0; sample < count; sample++) {
            var direction = HemisphereSampler.Local(sample, count, 0.37f);

            Assert.InRange(direction.Length(), 0.999f, 1.001f);
            Assert.True(direction.Z >= 0f, $"Sample {sample} points below the tangent plane at {direction}.");

            mean += direction.Z / count;
            sum += direction / count;
        }

        // 2/3 for the cosine density, 1/2 for a uniform one, and the window excludes both mistakes.
        Assert.InRange(mean, 0.66f, 0.674f);

        // The azimuth is balanced, so the mean direction is the normal times that same 2/3.
        Assert.InRange(sum.X, -0.01f, 0.01f);
        Assert.InRange(sum.Y, -0.01f, 0.01f);
        Assert.InRange(sum.Z, 0.66f, 0.674f);
    }

    /// <summary>A convex surface occludes nothing, which is the hemisphere's analytic value.</summary>
    /// <remarks>
    ///     ⚠ <b>The sharp half of this is the self-hit, not the geometry.</b> Every ray starts on the
    ///     sphere and every ray is aimed away from it, so the analytic answer is one at every texel —
    ///     and a bake that starts its rays exactly on the surface reads a few percent short instead,
    ///     the same defect this repository has already paid for in a screen-space march. Measured
    ///     with the bias removed: 0.969 at the worst texel, which is what "a few percent" buys and
    ///     why the assertion is exact rather than close.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>Exact rather than nearly, and the sample count is why it can be.</b> The source is a
    ///     polyhedron carrying smooth normals, so a direction close enough to the horizon dips below
    ///     the facet next door and grazes it — but the widest of 64 cosine-weighted samples leaves at
    ///     84.9°, and an icosphere at this subdivision bends about 2° per facet, so no sample in the
    ///     set can reach. Measured minimum over 392 covered texels: 1. Raise the sample count far
    ///     enough and this becomes a tolerance rather than an equality, which is a fact about the
    ///     fixture and not about the bake.
    /// </remarks>
    [Fact]
    public void Ambient_occlusion_on_a_convex_sphere_is_the_open_hemisphere() {
        var maps = Sphere(MeshMaps.AmbientOcclusion);
        var occlusion = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.AmbientOcclusion);
        var seen = 0;

        for (var index = 0; index < occlusion.Count; index++) {
            if (!maps.Coverage[index]) {
                continue;
            }

            Assert.Equal(1f, occlusion[index]);
            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", maps.Warnings));
    }

    /// <summary>A sheet under an overhanging ceiling reads no ambient occlusion at all.</summary>
    /// <remarks>
    ///     The other end of the range, and it is exact rather than approximate: the ceiling overhangs
    ///     the floor by far more than the widest sample in the set can travel before reaching it, so
    ///     every ray is blocked and the mean is zero rather than small.
    /// </remarks>
    [Fact]
    public void A_sheet_under_an_overhanging_ceiling_bakes_to_no_ambient_occlusion() {
        var maps = Sealed(MeshMaps.AmbientOcclusion | MeshMaps.Thickness);
        var occlusion = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.AmbientOcclusion);
        var thickness = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.Thickness);
        var seen = 0;

        for (var index = 0; index < occlusion.Count; index++) {
            if (!maps.Coverage[index]) {
                continue;
            }

            Assert.Equal(0f, occlusion[index]);

            // ⚠ The same rays turned through the surface, and the floor is a single sheet with
            // nothing under it — so a thickness that came back non-zero here would mean the inverted
            // hemisphere was not inverted at all, and was measuring the ceiling.
            Assert.Equal(0f, thickness[index]);

            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", maps.Warnings));
    }

    /// <summary>Thickness is one inside a closed sphere and zero under an open sheet.</summary>
    /// <remarks>
    ///     ⚠ <b>The pair is the test and neither half is on its own.</b> A ray entering a closed
    ///     convex surface leaves through it, so every inward ray hits and the answer is exactly one;
    ///     a single sheet has nothing behind it and the answer is exactly zero. A thickness that
    ///     forgot to invert the hemisphere would read the sphere's <i>occlusion</i> — also about one
    ///     minus nothing — and the plane is what tells the two apart.
    /// </remarks>
    [Fact]
    public void Thickness_is_one_inside_a_closed_sphere_and_zero_under_a_sheet() {
        var sphere = Sphere(MeshMaps.Thickness, occlusionRadius: 1f);
        var thickness = Assert.IsAssignableFrom<IReadOnlyList<float>>(sphere.Thickness);
        var seen = 0;

        for (var index = 0; index < thickness.Count; index++) {
            if (!sphere.Coverage[index]) {
                continue;
            }

            Assert.Equal(1f, thickness[index]);
            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", sphere.Warnings));

        var plane = Plane(MeshMaps.Thickness);
        var flat = Assert.IsAssignableFrom<IReadOnlyList<float>>(plane.Thickness);

        for (var index = 0; index < flat.Count; index++) {
            if (plane.Coverage[index]) {
                Assert.Equal(0f, flat[index]);
            }
        }
    }

    /// <summary>The bent normal is the normal where nothing blocks, and leans away where something does.</summary>
    /// <remarks>
    ///     ⚠ <b>It comes off the same rays as the occlusion, and the first assertion is what says
    ///     so.</b> A texel whose occlusion is exactly one saw no ray blocked, so the average of the
    ///     unoccluded directions is the average of <i>all</i> of them, which is the normal. The two
    ///     agreeing at that texel is not a coincidence a second, independently drawn ray set would
    ///     reproduce.
    /// </remarks>
    [Fact]
    public void The_bent_normal_is_the_normal_when_open_and_leans_away_from_an_occluder() {
        var open = Plane(MeshMaps.AmbientOcclusion | MeshMaps.BentNormal);
        var occlusion = Assert.IsAssignableFrom<IReadOnlyList<float>>(open.AmbientOcclusion);
        var bent = Assert.IsAssignableFrom<IReadOnlyList<Vector3>>(open.BentNormal);

        for (var index = 0; index < bent.Count; index++) {
            if (!open.Coverage[index]) {
                continue;
            }

            Assert.Equal(1f, occlusion[index]);
            Assert.True(bent[index].Z > 0.99f, $"An unoccluded texel's bent normal is {bent[index]}.");
        }

        var half = Overhung(MeshMaps.AmbientOcclusion | MeshMaps.BentNormal);
        var shaded = Assert.IsAssignableFrom<IReadOnlyList<float>>(half.AmbientOcclusion);
        var leaning = Assert.IsAssignableFrom<IReadOnlyList<Vector3>>(half.BentNormal);
        var mean = Vector3.Zero;
        var partial = 0;
        var seen = 0;

        for (var index = 0; index < leaning.Count; index++) {
            if (!half.Coverage[index]) {
                continue;
            }

            if (shaded[index] is > 0.05f and < 0.95f) {
                partial++;
            }

            mean += leaning[index];
            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", half.Warnings));

        // Occlusion that is neither nothing nor everything — the half of the range the two exact
        // fixtures above cannot reach, and the half a bake spends most of its texels in.
        Assert.True(partial * 2 > seen, $"Only {partial} of {seen} texels were partly occluded.");

        // The ceiling covers +x, so what is left to see is −x.
        Assert.True((mean / seen).X < -0.05f, $"The mean bent normal is {mean / seen} under a +x ceiling.");
    }

    /// <summary>A sphere of radius <i>r</i> reads a mean curvature of 1/<i>r</i>, at either scale.</summary>
    /// <remarks>
    ///     ⚠ <b>Both radii, because a curvature is one over a length and the value is <i>supposed</i>
    ///     to move with the model.</b> Every other scale test in this library asserts an answer that
    ///     does not change; this one asserts that it changes by exactly the factor it should, which
    ///     is what catches a normalisation that was added to make some other test pass.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(100f)]
    public void Curvature_of_a_sphere_of_radius_r_reads_one_over_r(float radius) {
        var maps = Sphere(MeshMaps.Curvature, radius: radius);
        var curvature = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.Curvature);
        var wanted = 1f / radius;
        var seen = 0;

        for (var index = 0; index < curvature.Count; index++) {
            if (!maps.Coverage[index]) {
                continue;
            }

            // Two percent, and the discretisation is what spends it: the operator is exact in the
            // limit and an icosphere at this subdivision is a polyhedron whose one-rings are not
            // quite regular. Measured worst texel, 1.3 % at either radius.
            Assert.InRange(curvature[index], wanted * 0.98f, wanted * 1.02f);
            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", maps.Warnings));
        Assert.InRange(maps.CurvatureRange, wanted * 0.98f, wanted * 1.02f);
    }

    /// <summary>A plane is flat everywhere, rim included.</summary>
    /// <remarks>
    ///     ⚠ <b>The rim is the assertion.</b> The cotangent operator wants a closed one-ring, and on
    ///     an open boundary the missing half is not a flat half — left in, every sheet, cut-out and
    ///     plane in a project bakes a bright border that no generator can tell from a crease.
    /// </remarks>
    [Fact]
    public void Curvature_of_a_plane_is_zero_including_along_its_open_rim() {
        var maps = Plane(MeshMaps.Curvature);
        var curvature = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.Curvature);

        for (var index = 0; index < curvature.Count; index++) {
            if (maps.Coverage[index]) {
                Assert.Equal(0f, curvature[index], 5);
            }
        }

        Assert.Equal(0f, maps.CurvatureRange, 5);
    }

    /// <summary>Position and world normal on an axis-aligned plane are exact.</summary>
    [Fact]
    public void Position_and_world_normal_on_a_plane_are_exact() {
        var maps = Plane(MeshMaps.Position | MeshMaps.WorldNormal);
        var position = Assert.IsAssignableFrom<IReadOnlyList<Vector3>>(maps.Position);
        var world = Assert.IsAssignableFrom<IReadOnlyList<Vector3>>(maps.WorldNormal);
        var seen = 0;

        for (var index = 0; index < position.Count; index++) {
            if (!maps.Coverage[index]) {
                continue;
            }

            // The sheet is the source's whole box in x and y, so the normalised point runs the full
            // unit range; the box has no extent in z at all, and an axis with no extent reads zero.
            Assert.InRange(position[index].X, 0f, 1f);
            Assert.InRange(position[index].Y, 0f, 1f);
            Assert.Equal(0f, position[index].Z);

            Assert.Equal(0f, world[index].X, 5);
            Assert.Equal(0f, world[index].Y, 5);
            Assert.Equal(1f, world[index].Z, 5);

            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", maps.Warnings));
    }

    /// <summary>On a sphere the two agree: the normalised point, undone, <i>is</i> the normal.</summary>
    /// <remarks>
    ///     <b>One assertion covering both maps and their relationship.</b> A position map off by a
    ///     translation and a normal map off by a rotation are both individually plausible; the two
    ///     being the same unit vector at every texel is a property only the right pair has.
    /// </remarks>
    [Fact]
    public void Position_and_world_normal_agree_on_a_sphere() {
        var maps = Sphere(MeshMaps.Position | MeshMaps.WorldNormal);
        var position = Assert.IsAssignableFrom<IReadOnlyList<Vector3>>(maps.Position);
        var world = Assert.IsAssignableFrom<IReadOnlyList<Vector3>>(maps.WorldNormal);
        var seen = 0;

        for (var index = 0; index < position.Count; index++) {
            if (!maps.Coverage[index]) {
                continue;
            }

            // The box is [−1, 1]³, so undoing the normalisation is 2p − 1 and lands on the sphere.
            var point = (position[index] * 2f) - Vector3.One;

            Assert.InRange(point.Length(), 0.99f, 1.01f);
            Assert.InRange(Vector3.Dot(Vector3.Normalize(point), world[index]), 0.99f, 1.01f);

            seen++;
        }

        Assert.True(seen > 0, string.Join(" · ", maps.Warnings));
    }

    /// <summary>The gutter never invents an id the source does not have.</summary>
    /// <remarks>
    ///     ⚠ <b>§ D12's named trap, and the fixture is built so it can go off.</b> The two charts
    ///     carry groups 0 and 2 and abut across a gap narrower than the gutter, so the texels between
    ///     them have a neighbour of each — which is exactly where a dilation that averaged its four
    ///     neighbours produces id 1, a material the source does not contain. Every downstream
    ///     generator keyed off the map then grows a hairline of it along every chart border.
    /// </remarks>
    [Fact]
    public void The_gutter_copies_an_id_and_never_averages_two() {
        var maps = MapBaker.Bake(
            Strips(),
            Unwrapped(Strips(), 0.02f, apart: true),
            new() { Resolution = 64, Gutter = 6, Maps = MeshMaps.Id, Space = BakeSpace.Object }
        );

        var ids = Assert.IsAssignableFrom<IReadOnlyList<int>>(maps.Ids);

        Assert.True(maps.Covered > 0, string.Join(" · ", maps.Warnings));
        Assert.True(maps.Dilated > 0, "Nothing was dilated, so the test proves nothing about the gutter.");

        var straddling = 0;

        for (var index = 0; index < ids.Count; index++) {
            Assert.True(
                ids[index] is -1 or 0 or 2,
                $"Texel {index} carries id {ids[index]}, which belongs to no chart in the source."
            );

            var column = index % maps.Resolution;

            if (maps.Coverage[index] || column == 0 || column + 1 == maps.Resolution) {
                continue;
            }

            var before = ids[index - 1];
            var after = ids[index + 1];

            if (before >= 0 && after >= 0 && before != after) {
                straddling++;

                Assert.True(
                    ids[index] == before || ids[index] == after,
                    $"Gutter texel {index} sits between ids {before} and {after} and carries "
                    + $"{ids[index]}, which is neither of them."
                );
            }
        }

        // ⚠ Without this the assertions above are satisfied by an id map that never had the case in
        // it — which is not hypothetical: the first version of this fixture left an even number of
        // texels between the charts, the two gutter fronts passed without meeting, and averaging the
        // four neighbours was green. A gutter texel with a different id on either side is the only
        // place the two rules differ, so the test has to prove one existed.
        Assert.True(straddling > 0, "No gutter texel had a different id on each side, so nothing was proved.");
    }

    /// <summary>The same face numbers, read as a guess, are not baked as material ids.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The fixture above could not have caught this, and that is the finding.</b>
    ///         <see cref="Strips" /> assigns its groups through <c>AddFace(loop, strip * 2)</c>, which
    ///         does not move <see cref="EditMesh.GroupSource" /> off the default
    ///         <see cref="MeshGroupSource.Coplanarity" /> — only <c>SetGroup</c> does — so a passing
    ///         gutter test was running the same path a coplanarity-grouped blob runs. The two are told
    ///         apart by one line, and this is it: one mesh, two sources, two different id maps.
    ///     </para>
    ///     <para>
    ///         The two strips are disconnected, so the shells the fallback labels are the same two
    ///         regions the groups named — numbered 0 and 1 rather than 0 and 2, which is what makes
    ///         "the source's own group id never reached the map" a thing this test can assert.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_guessed_grouping_is_not_baked_as_ids_and_the_bake_says_so() {
        var source = Strips();

        source.GroupSource = MeshGroupSource.Coplanarity;

        var maps = MapBaker.Bake(
            source,
            Unwrapped(Strips(), 0.02f, apart: true),
            new() { Resolution = 64, Gutter = 6, Maps = MeshMaps.Id, Space = BakeSpace.Object }
        );

        var ids = Assert.IsAssignableFrom<IReadOnlyList<int>>(maps.Ids);

        Assert.True(maps.Covered > 0, string.Join(" · ", maps.Warnings));

        // ⚠ Nothing landed in Warnings before this, which is the half that makes it silent: an id map
        // of confetti looks like an id map, and a content build has nobody watching it.
        Assert.Contains(maps.Warnings, warning => warning.Contains("group", StringComparison.Ordinal));

        var distinct = ids.Where(id => id >= 0).Distinct().Order().ToArray();

        Assert.Equal([0, 1], distinct);
    }

    /// <summary>§ D12's own input: a faceted blob bakes one id, not one per triangle.</summary>
    /// <remarks>
    ///     ⚠ <b>The measured shape of the defect.</b> <c>EditMesh.FromTriangles</c> ends in
    ///     <c>Regroup</c>, and on a faceted surface almost no two adjacent triangles are within half a
    ///     degree of coplanar — <see cref="MeshGroupSource" />'s remarks carry the number, 13 965
    ///     groups on a 25 439-triangle image-to-3D mesh. Baked as ids that is per-triangle confetti,
    ///     every generator keyed off it is noise, and <see cref="MapBaker.IdColour" /> paints each
    ///     triangle a different hue.
    /// </remarks>
    [Fact]
    public void A_faceted_source_does_not_bake_one_id_per_triangle() {
        var source = Icosphere(3, 1f);

        // What FromTriangles does at the end of every generated or sculpted mesh.
        source.Regroup();

        Assert.Equal(MeshGroupSource.Coplanarity, source.GroupSource);

        // Verify the instrument: the fixture is only the § D12 input if its own grouping is confetti.
        var groups = source.Faces.Select(face => face.Group).Distinct().Count();

        Assert.True(groups > 100, $"The fixture has {groups} groups, so it is not a faceted surface.");

        var maps = MapBaker.Bake(
            source,
            Unwrapped(Cap(Icosphere(3, 1.01f), 0.5f), 0f),
            new() { Resolution = 24, Gutter = 2, Maps = MeshMaps.Id, Space = BakeSpace.Object }
        );

        var ids = Assert.IsAssignableFrom<IReadOnlyList<int>>(maps.Ids);

        Assert.True(maps.Covered > 0, string.Join(" · ", maps.Warnings));

        var distinct = ids.Where(id => id >= 0).Distinct().ToArray();

        Assert.True(
            distinct.Length == 1,
            $"A closed sphere is one shell and baked {distinct.Length} ids, which is the confetti."
        );
    }

    /// <summary>Every material index gets a colour of its own.</summary>
    [Fact]
    public void An_id_colour_is_distinct_for_every_index_and_black_for_none() {
        Assert.Equal(Vector3.Zero, MapBaker.IdColour(-1));

        var colours = Enumerable.Range(0, 32).Select(MapBaker.IdColour).ToList();

        foreach (var colour in colours) {
            Assert.InRange(colour.X, 0f, 1f);
            Assert.InRange(colour.Y, 0f, 1f);
            Assert.InRange(colour.Z, 0f, 1f);
        }

        for (var one = 0; one < colours.Count; one++) {
            for (var two = one + 1; two < colours.Count; two++) {
                Assert.True(
                    Vector3.Distance(colours[one], colours[two]) > 0.05f,
                    $"Ids {one} and {two} both colour to about {colours[one]}."
                );
            }
        }
    }

    /// <summary>A search radius too short to reach the source moves every texel into <c>Missed</c>.</summary>
    /// <remarks>
    ///     The behaviour the guide documents, asserted rather than described: coverage is a function
    ///     of the coordinates alone and does not move, and a cage the rays cannot reach across takes
    ///     the closest-point fallback at every texel instead.
    /// </remarks>
    [Fact]
    public void A_short_search_radius_moves_texels_from_covered_into_missed() {
        var reaching = Plane(MeshMaps.None, searchRadius: 0.4f);
        var cramped = Plane(MeshMaps.None, searchRadius: 0.0005f);

        Assert.True(reaching.Covered > 0, string.Join(" · ", reaching.Warnings));
        Assert.Equal(reaching.Covered, cramped.Covered);
        Assert.True(
            reaching.Missed * 8 < reaching.Covered,
            $"{reaching.Missed} of {reaching.Covered} missed even at a radius that reaches."
        );

        Assert.Equal(cramped.Covered, cramped.Missed);
    }

    /// <summary>A map that was not asked for comes back null rather than as zeroes.</summary>
    [Fact]
    public void A_map_that_was_not_asked_for_is_null() {
        var plain = Plane(MeshMaps.None);

        Assert.Null(plain.AmbientOcclusion);
        Assert.Null(plain.BentNormal);
        Assert.Null(plain.Curvature);
        Assert.Null(plain.Thickness);
        Assert.Null(plain.Position);
        Assert.Null(plain.WorldNormal);
        Assert.Null(plain.Ids);

        var all = Plane(MeshMaps.All);

        Assert.NotNull(all.AmbientOcclusion);
        Assert.NotNull(all.BentNormal);
        Assert.NotNull(all.Curvature);
        Assert.NotNull(all.Thickness);
        Assert.NotNull(all.Position);
        Assert.NotNull(all.WorldNormal);
        Assert.NotNull(all.Ids);
    }

    /// <summary>The same source and settings bake the same seven maps, value for value.</summary>
    /// <remarks>
    ///     ⚠ <b>The content hash rests on this.</b> The occlusion estimator is the only thing in the
    ///     bake that samples, and a sampler seeded from a clock, a thread or an accumulation order
    ///     would make two builds of one asset differ — which is not a visible defect, it is a cache
    ///     that never hits.
    /// </remarks>
    [Fact]
    public void The_whole_bake_is_the_same_twice() {
        var one = Sphere(MeshMaps.All);
        var two = Sphere(MeshMaps.All);

        Assert.Equal(one.AmbientOcclusion, two.AmbientOcclusion);
        Assert.Equal(one.BentNormal, two.BentNormal);
        Assert.Equal(one.Curvature, two.Curvature);
        Assert.Equal(one.Thickness, two.Thickness);
        Assert.Equal(one.Position, two.Position);
        Assert.Equal(one.WorldNormal, two.WorldNormal);
        Assert.Equal(one.Ids, two.Ids);
        Assert.Equal(one.Normals, two.Normals);
    }

    /// <summary>A bake of a cap of an icosphere, against the whole sphere.</summary>
    /// <remarks>
    ///     ⚠ <b>The cap is scaled a hundredth clear of the source, and it has to be.</b> A cage lying
    ///     exactly on the surface has every ray rejected at its own origin — correctly, a hit at zero
    ///     distance is the origin — and the bake then measures the closest-point fallback rather than
    ///     the cast, which is a different code path from the one every assertion here is about.
    /// </remarks>
    static BakedMaps Sphere(MeshMaps maps, float radius = 1f, float occlusionRadius = 0.5f) {
        var source = Icosphere(3, radius);
        var target = Unwrapped(Cap(Icosphere(3, radius * 1.01f), radius * 0.5f), 0f);

        return MapBaker.Bake(
            source,
            target,
            new() {
                Resolution = 24,
                Gutter = 2,
                Space = BakeSpace.Object,
                Maps = maps,
                OcclusionRadius = occlusionRadius
            }
        );
    }

    /// <summary>A bake of a flat sheet onto itself, lifted so the rays travel.</summary>
    static BakedMaps Plane(MeshMaps maps, float searchRadius = 0.05f) =>
        MapBaker.Bake(
            TransferFixtures.Grid(8, 2f, _ => 0),
            Unwrapped(TransferFixtures.Grid(4, 2f, _ => 0), 0.01f),
            new() {
                Resolution = 24,
                Gutter = 2,
                Space = BakeSpace.Object,
                Maps = maps,
                SearchRadius = searchRadius
            }
        );

    /// <summary>A bake of a floor with a ceiling far wider than it, a tenth of a unit above.</summary>
    /// <remarks>
    ///     ⚠ <b>The ceiling has to be low for the occlusion to be exactly zero, and the search radius
    ///     has to be shorter than it is high.</b> The probe casts both ways and takes the nearer hit;
    ///     a radius that reaches the ceiling makes the handful of texels whose ray leaves the floor's
    ///     edge find the ceiling's <i>top</i> instead, whose hemisphere is wide open — and the test
    ///     then reports an occlusion of one at exactly the texels the fixture is least about.
    /// </remarks>
    static BakedMaps Sealed(MeshMaps maps) =>
        MapBaker.Bake(
            Roofed(-20f, 20f, 0.1f),
            Unwrapped(TransferFixtures.Grid(4, 4f, _ => 0), 0.01f),
            new() { Resolution = 24, Gutter = 2, Space = BakeSpace.Object, Maps = maps, SearchRadius = 0.001f }
        );

    /// <summary>The same floor under a ceiling covering only <c>+x</c>, and high enough to be partial.</summary>
    /// <remarks>
    ///     ⚠ <b>Two units up rather than a tenth, and the height is the whole fixture.</b> A ceiling
    ///     that nearly touches the floor occludes only the last few degrees above the horizon — a
    ///     cosine-weighted half a percent — so every texel reads one and the map has no middle for a
    ///     bent normal to lean in. Occlusion is a ratio of a distance to a height, and a fixture that
    ///     forgets that measures a two-valued function.
    /// </remarks>
    static BakedMaps Overhung(MeshMaps maps) =>
        MapBaker.Bake(
            Roofed(0f, 20f, 2f),
            Unwrapped(TransferFixtures.Grid(4, 4f, _ => 0), 0.01f),
            new() { Resolution = 24, Gutter = 2, Space = BakeSpace.Object, Maps = maps, SearchRadius = 0.001f }
        );

    /// <summary>A four-unit floor with a sheet spanning <c>[low, high]</c> in <c>x</c> above it.</summary>
    static EditMesh Roofed(float low, float high, float height) {
        var mesh = TransferFixtures.Grid(4, 4f, _ => 0);
        var corners = new int[5, 5];

        for (var i = 0; i < 5; i++) {
            for (var j = 0; j < 5; j++) {
                corners[i, j] = mesh.AddPosition(
                    new(low + ((high - low) * i / 4f), -20f + (10f * j), height)
                );
            }
        }

        for (var i = 0; i < 4; i++) {
            for (var j = 0; j < 4; j++) {
                Span<int> loop = [corners[i, j], corners[i + 1, j], corners[i + 1, j + 1], corners[i, j + 1]];

                mesh.AddFace(loop, 1);
            }
        }

        return mesh;
    }

    /// <summary>Two coplanar sheets with a gap between them, carrying assigned face groups 0 and 2.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>0 and 2, with nothing between them.</b> The id the gutter must not invent is then a
    ///         number the test can name, rather than a shade of one it cannot distinguish from rounding.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the assignment is stated, because <c>AddFace(loop, group)</c> does not state
    ///         it.</b> Only <c>SetGroup</c> moves <see cref="EditMesh.GroupSource" /> off the default
    ///         <see cref="MeshGroupSource.Coplanarity" />, so this fixture used to hand the baker a
    ///         mesh whose groups were — as far as anything downstream could tell — <c>Regroup</c>'s
    ///         guess. That is the same input a generated blob arrives with, which made a green id test
    ///         say nothing about the case it was written for. Two charts an artist gave two materials
    ///         is what this fixture means, so it says so.
    ///     </para>
    /// </remarks>
    static EditMesh Strips() {
        var mesh = new EditMesh { GroupSource = MeshGroupSource.Assigned };

        for (var strip = 0; strip < 2; strip++) {
            var corners = new int[3, 3];
            var origin = strip == 0 ? -1f : 0.1f;

            for (var i = 0; i < 3; i++) {
                for (var j = 0; j < 3; j++) {
                    corners[i, j] = mesh.AddPosition(new(origin + (0.45f * i), -1f + j, 0f));
                }
            }

            for (var i = 0; i < 2; i++) {
                for (var j = 0; j < 2; j++) {
                    Span<int> loop = [corners[i, j], corners[i + 1, j], corners[i + 1, j + 1], corners[i, j + 1]];

                    mesh.AddFace(loop, strip * 2);
                }
            }
        }

        return mesh;
    }

    /// <summary>Gives a mesh coordinates by projecting its own <c>xy</c> bounds, and lifts it clear.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the mesh's own bounds and never from the size it was built at</b> — the
    ///     mistake <c>MapBakerTests</c> records paying for, where a fixture divided by a literal and
    ///     the scale test then measured its own arithmetic. A face on the <c>−x</c> side of the
    ///     origin goes to the left chart and one on the <c>+x</c> side to the right, which leaves a
    ///     gap in the middle narrower than a gutter.
    /// </remarks>
    static EditMesh Unwrapped(EditMesh mesh, float lift, bool apart = false) {
        var bounds = mesh.Bounds;
        var size = bounds.Maximum - bounds.Minimum;
        var tall = size.Y > 0f ? size.Y : 1f;
        var split = (bounds.Minimum.X + bounds.Maximum.X) * 0.5f;
        var coordinates = new Vector2[mesh.CornerCount];

        // ⚠ Each half is normalised against its own extent, not against the mesh's. Sharing one
        // denominator puts the two charts a third of the atlas apart, the gutter never reaches
        // across, and the id test then passes without the case it exists to cover ever arising.
        //
        // ⚠ And the halves are placed to leave exactly *one* empty column between them, which is a
        // parity argument rather than a tidiness one. A four-neighbour flood fills a texel at its L1
        // distance from the nearest chart and commits the round afterwards, so across an even gap the
        // two fronts pass without ever seeing each other: every gutter texel has neighbours from one
        // chart only, an averaging dilation is indistinguishable from a copying one, and the id test
        // goes green under the sabotage it exists to catch. Measured — it did.
        var (left, right) = Halves(mesh, split);

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var point = mesh.Positions[loop[index]];
                var v = 0.02f + ((point.Y - bounds.Minimum.Y) / tall * 0.96f);

                if (!apart) {
                    var whole = (point.X - bounds.Minimum.X) / (size.X > 0f ? size.X : 1f);

                    coordinates[entry.Start + index] = new(0.05f + (whole * 0.9f), 0.05f + (v * 0.9f));

                    continue;
                }

                var side = point.X > split ? right : left;
                var along = (point.X - side.X) / (side.Y - side.X);

                coordinates[entry.Start + index] = new(
                    point.X > split ? 0.505f + (along * 0.475f) : 0.02f + (along * 0.45f),
                    v
                );
            }
        }

        mesh.SetTexCoords(coordinates);

        if (lift != 0f) {
            for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
                mesh.MovePosition(vertex, mesh.Positions[vertex] + new Vector3(0f, 0f, lift));
            }
        }

        return mesh;
    }

    /// <summary>The <c>x</c> range of the positions on each side of a split.</summary>
    static (Vector2 Left, Vector2 Right) Halves(EditMesh mesh, float split) {
        var left = new Vector2(float.MaxValue, float.MinValue);
        var right = new Vector2(float.MaxValue, float.MinValue);

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            var x = mesh.Positions[vertex].X;

            if (x > split) {
                right = new(MathF.Min(right.X, x), MathF.Max(right.Y, x));
            } else {
                left = new(MathF.Min(left.X, x), MathF.Max(left.Y, x));
            }
        }

        return (left, right);
    }

    /// <summary>The faces of a mesh whose corners all stand above a height, as a mesh of their own.</summary>
    static EditMesh Cap(EditMesh mesh, float height) {
        var kept = new EditMesh();
        var moved = new Dictionary<int, int>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face);
            var above = true;

            foreach (var corner in loop) {
                above &= mesh.Positions[corner].Z > height;
            }

            if (!above) {
                continue;
            }

            var remapped = new int[loop.Length];

            for (var index = 0; index < loop.Length; index++) {
                if (!moved.TryGetValue(loop[index], out var at)) {
                    at = kept.AddPosition(mesh.Positions[loop[index]]);
                    moved[loop[index]] = at;
                }

                remapped[index] = at;
            }

            kept.AddFace(remapped, mesh.Faces[face].Group);
        }

        return kept;
    }

    /// <summary>A subdivided icosahedron, welded, at a radius.</summary>
    /// <remarks>
    ///     ⚠ <b>An icosphere and not a latitude–longitude sphere, because the curvature assertion is
    ///     a discretisation error away from the answer.</b> A UV sphere's triangles degenerate into
    ///     slivers at its poles, its one-rings are wildly irregular there, and the cotangent operator
    ///     reports that irregularity as curvature — which would make the tolerance a description of
    ///     the fixture rather than of the measurement.
    /// </remarks>
    static EditMesh Icosphere(int subdivisions, float radius) {
        var t = (1f + MathF.Sqrt(5f)) / 2f;

        var points = new List<Vector3> {
            new(-1f, t, 0f), new(1f, t, 0f), new(-1f, -t, 0f), new(1f, -t, 0f),
            new(0f, -1f, t), new(0f, 1f, t), new(0f, -1f, -t), new(0f, 1f, -t),
            new(t, 0f, -1f), new(t, 0f, 1f), new(-t, 0f, -1f), new(-t, 0f, 1f)
        };

        var faces = new List<(int A, int B, int C)> {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
            (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
            (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)
        };

        for (var round = 0; round < subdivisions; round++) {
            var middles = new Dictionary<(int Low, int High), int>();
            var split = new List<(int A, int B, int C)>();

            foreach (var (a, b, c) in faces) {
                var ab = Middle(points, middles, a, b);
                var bc = Middle(points, middles, b, c);
                var ca = Middle(points, middles, c, a);

                split.Add((a, ab, ca));
                split.Add((b, bc, ab));
                split.Add((c, ca, bc));
                split.Add((ab, bc, ca));
            }

            faces = split;
        }

        var mesh = new EditMesh();

        foreach (var point in points) {
            mesh.AddPosition(Vector3.Normalize(point) * radius);
        }

        foreach (var (a, b, c) in faces) {
            Span<int> loop = [a, b, c];

            mesh.AddFace(loop);
        }

        return mesh;
    }

    /// <summary>The shared midpoint of an edge, added once however many faces ask for it.</summary>
    static int Middle(List<Vector3> points, Dictionary<(int Low, int High), int> middles, int a, int b) {
        var key = a < b ? (a, b) : (b, a);

        if (middles.TryGetValue(key, out var at)) {
            return at;
        }

        points.Add((points[a] + points[b]) * 0.5f);
        middles[key] = points.Count - 1;

        return points.Count - 1;
    }
}
