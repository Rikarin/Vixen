// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 7's address space: the clipmap, its pages, and the snap that makes a page cacheable.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every way a virtual shadow map goes wrong is a property of these functions</b>, which is
///         why they are pure and why this file exists. A level chosen one step too coarse is a soft
///         shadow nobody can attribute to a level; a page snapped to the wrong grid is a shadow that
///         crawls when the camera moves; a page index that disagrees between the marking pass and the
///         lookup is a pixel reading another part of the world's depth, which looks for all the world
///         like a shadow of something that is not there.
///     </para>
///     <para>
///         <see cref="ShadowCascades" />' tests make the same argument about cascades, and the last
///         test here is the one that keeps the shader the thing the host is a mirror of.
///     </para>
/// </remarks>
public class VirtualShadowMapTests {
    static readonly Vector3 Sun = Vector3.Normalize(new(-0.4f, -1f, -0.3f));

    const float FirstExtent = 10f;
    const float Depth = 400f;

    static Matrix4x4 Level(int level, Vector3 camera) =>
        VirtualShadowMap.ClipmapProjection(level, FirstExtent, camera, Sun, Depth);

    // --- The clipmap ---------------------------------------------------------

    /// <summary>A level covers twice its predecessor at half the density.</summary>
    /// <remarks>
    ///     The whole shape of a clipmap, and the reason it reaches a kilometre in eight levels where a
    ///     cascade count reaches whatever four fixed volumes reach. Stated as an assertion because the
    ///     level selection below is a logarithm of exactly this ratio: if the extents were not powers of
    ///     two apart, <c>LevelFor</c> would be choosing a level that does not exist.
    /// </remarks>
    [Fact]
    public void Each_level_doubles_the_extent_and_halves_the_density() {
        for (var level = 0; level < 8; level++) {
            Assert.Equal(FirstExtent * (1 << level), VirtualShadowMap.ExtentOf(level, FirstExtent), 4);

            Assert.Equal(
                VirtualShadowMap.ExtentOf(level, FirstExtent)
                / (VirtualShadowMap.PagesPerSide * VirtualShadowMap.PageTexels),
                VirtualShadowMap.TexelOf(level, FirstExtent),
                6
            );
        }

        Assert.Equal(2f * VirtualShadowMap.TexelOf(0, FirstExtent), VirtualShadowMap.TexelOf(1, FirstExtent), 6);
    }

    /// <summary>
    ///     The level chosen is the finest whose texels are no finer than the pixel's.
    /// </summary>
    /// <remarks>
    ///     <b>No finer, and the rounding direction is the assertion.</b> A level finer than the pixel
    ///     needs is wasted pages; a level coarser is a soft edge, which is the artefact this exists to
    ///     avoid. So a footprint a hair above a level's texel size takes the next level out, and one
    ///     exactly at it does not.
    /// </remarks>
    [Fact]
    public void The_level_is_the_finest_no_coarser_than_the_pixel() {
        var first = VirtualShadowMap.TexelOf(0, FirstExtent);

        // Finer than level zero, and at it: the finest map there is.
        Assert.Equal(0, VirtualShadowMap.LevelFor(first * 0.25f, FirstExtent, 8));
        Assert.Equal(0, VirtualShadowMap.LevelFor(first, FirstExtent, 8));

        // A hair coarser rounds up, and exactly a level's size does not round past it.
        Assert.Equal(1, VirtualShadowMap.LevelFor(first * 1.01f, FirstExtent, 8));
        Assert.Equal(1, VirtualShadowMap.LevelFor(first * 2f, FirstExtent, 8));
        Assert.Equal(2, VirtualShadowMap.LevelFor(first * 2.01f, FirstExtent, 8));
        Assert.Equal(3, VirtualShadowMap.LevelFor(first * 8f, FirstExtent, 8));

        // And a pixel past the last level gets the last level, because there is nothing beyond it.
        Assert.Equal(7, VirtualShadowMap.LevelFor(first * 4096f, FirstExtent, 8));
    }

    /// <summary>
    ///     A camera that moved less than a page leaves every page exactly where it was.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The property the whole caching story rests on, and the one that separates this from a
    ///         cascade.</b> A cascade snaps its centre to a <em>texel</em> so the sampling grid does not
    ///         slide under stationary geometry; this snaps to a <em>page</em> so that a page's world
    ///         footprint is bit-identical from one frame to the next — which is what lets a page already
    ///         drawn stay drawn.
    ///     </para>
    ///     <para>
    ///         Sabotage: snapping to a texel instead leaves the projection changing on almost every
    ///         frame, <c>VirtualShadowRenderer</c> invalidates that level's every page, and the level
    ///         redraws itself entirely — a virtual shadow map with none of the point of one, and a
    ///         picture nobody can tell apart from the working version.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_camera_that_moved_less_than_a_page_does_not_move_the_projection() {
        var page = VirtualShadowMap.ExtentOf(0, FirstExtent) / VirtualShadowMap.PagesPerSide;
        var (right, up, _) = VirtualShadowMap.Basis(Sun);

        // Placed at the centre of a cell of the light's own grid, so a nudge of a tenth of a page
        // stays inside it. A camera at an arbitrary point is a camera that may be a hair from a
        // boundary, and this test would then be asserting where the boundary happens to be rather
        // than that there is a grid at all — the sweep below covers the crossing.
        var origin = (right * 5.5f * page) + (up * -3.5f * page) + (Sun * 2.5f * page);
        var reference = Level(0, origin);

        // A tenth of a page along each of the light's own axes, which is the movement a walking camera
        // makes several times a frame.
        var nudged = origin + (right * page * 0.1f) + (up * page * 0.1f);

        Assert.Equal(reference, Level(0, nudged));

        // And a whole page along one of them moves it, or the snap would be a projection that never
        // follows the camera at all.
        Assert.NotEqual(reference, Level(0, origin + (right * page * 1.5f)));
    }

    /// <summary>
    ///     Walking along the light does not refit a level once per lateral page — task #124's blink.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The third axis is not a page axis.</b> A level's page grid quantises the two axes it
    ///         is made of, and the test above is about those. The third is the depth the level's box
    ///         spans — <see cref="Depth" /> for every level alike, with no page structure in it — and
    ///         it used to be snapped to the <em>lateral page size</em> as well. That made level zero's
    ///         near plane step every 0.3 m of walking and level seven's every 40 m, a hundred and
    ///         twenty-eight to one on an axis the two share, and a near plane that steps shifts every
    ///         stored depth in the level, so <c>VirtualShadowRenderer.Fit</c> threw the level away.
    ///     </para>
    ///     <para>
    ///         Measured in sample 13 on a forty-second circular walk before the fix: 23.2 pages
    ///         invalidated per frame against a budget that redraws sixteen, so the map could not
    ///         converge while the camera moved — a page absent at shading falls through to the
    ///         cascades, and the two disagree, which is the blink a person sees.
    ///     </para>
    ///     <para>
    ///         Sabotage: return the lateral page from <see cref="VirtualShadowMap.DepthStep" /> and
    ///         this fails at level zero on the first metre — which is exactly the shipped behaviour it
    ///         replaced, and a defect no picture of a settled frame can show.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Walking_along_the_light_refits_on_the_depth_ranges_scale_not_the_pages() {
        // A metre along the light is three pages of level zero and a fortieth of level seven's, so
        // under the old rule this walk refitted the finest level three times and the coarsest never.
        var start = (Sun * 100f) + new Vector3(3f, 1.5f, -7f);
        var metre = Sun;

        for (var level = 0; level < 8; level++) {
            var step = VirtualShadowMap.DepthStep(level, FirstExtent, Depth);

            Assert.True(
                step >= Depth / VirtualShadowMap.PagesPerSide,
                $"level {level} steps its near plane every {step} m, finer than the depth range's own {Depth / VirtualShadowMap.PagesPerSide} m"
            );

            // The projection is a function of the cell, so counting distinct cells over the walk
            // counts refits without comparing matrices for equality across a basis change.
            var cells = new HashSet<(int, int, int)>();

            for (var centimetre = 0; centimetre <= 100; centimetre++) {
                cells.Add(
                    VirtualShadowMap.ClipmapCell(level, FirstExtent, start + (metre * (centimetre * 0.01f)), Sun, Depth)
                );
            }

            // One metre of walking crosses at most one boundary of a step that is 12.5 m or coarser.
            Assert.True(cells.Count <= 2, $"level {level} refitted {cells.Count} times over one metre along the light");
        }

        // And it still follows the camera, or the box would drift off the world: half the coarsest
        // step of walking is enough to move the finest level's near plane.
        var far = start + (metre * VirtualShadowMap.DepthStep(0, FirstExtent, Depth) * 2f);

        Assert.NotEqual(
            VirtualShadowMap.ClipmapCell(0, FirstExtent, start, Sun, Depth),
            VirtualShadowMap.ClipmapCell(0, FirstExtent, far, Sun, Depth)
        );
    }

    /// <summary>
    ///     A page lies wholly inside one page of the next level out, whatever the camera is doing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The property <c>VirtualShadowLookup.Sample</c>'s fallback is only coherent because
    ///         of.</b> When the page a pixel wants is absent, the lookup asks the next level out —
    ///         and that is a well-posed question only if the finer page's whole world footprint sits
    ///         inside a single coarser page. It does, and not by luck: both levels snap their centre to
    ///         a whole page of their own grid, and a coarse page is exactly two fine ones across, so the
    ///         coarse grid's lines are a subset of the fine grid's however the camera moves.
    ///     </para>
    ///     <para>
    ///         Sabotage: snap either level to a texel rather than a page — or make the extents anything
    ///         but powers of two apart — and a fine page straddles two coarse ones. The fallback then
    ///         reads whichever the page *centre* landed in, which is a shadow taken from up to half a
    ///         coarse page away: right at the frame's fine detail, wrong at its edges, and only where a
    ///         page happened to be missing. Nothing about that picture names this function.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_page_lies_wholly_inside_one_page_of_the_next_level_out() {
        var (right, up, _) = VirtualShadowMap.Basis(Sun);

        // Off the lattice deliberately, and several places along it: the claim is about every camera
        // position rather than a favourable one, and each of these snaps the two levels differently.
        foreach (var offset in (float[])[0f, 0.37f, 1.5f, 4.2f, 11.9f]) {
            var camera = (right * offset) + (up * offset * 0.5f) + (Sun * offset * 0.25f);

            for (var level = 0; level < 7; level++) {
                var fine = Level(level, camera);
                var coarse = Level(level + 1, camera);

                Assert.True(Matrix4x4.Invert(fine, out var inverse));

                for (var y = 0; y < VirtualShadowMap.PagesPerSide; y++) {
                    for (var x = 0; x < VirtualShadowMap.PagesPerSide; x++) {
                        // ⚠ The four corners a *thousandth* of a page inside, and the margin is the
                        // whole strength of this test. Enough to be off the seam — a corner exactly on
                        // one resolves to whichever side the arithmetic lands, which is not the claim
                        // — and no more, because a texel snap misaligns the two grids by around one
                        // fine texel, a hundred and twenty-eighth of a page. A margin of a fiftieth,
                        // which is the comfortable-looking number to write, is wider than the fault
                        // and passes cheerfully with the snap sabotaged.
                        var parents = new HashSet<Int2>();

                        foreach (var (u, v) in ((float, float)[])[(0.001f, 0.001f), (0.999f, 0.001f), (0.001f, 0.999f), (0.999f, 0.999f)]) {
                            var world = Unproject(inverse, (x + u) / VirtualShadowMap.PagesPerSide, (y + v) / VirtualShadowMap.PagesPerSide);

                            // A fine page always lands somewhere in the coarser level, which covers
                            // twice its extent about the same camera.
                            Assert.True(VirtualShadowMap.PageOf(coarse, world, out var parent));
                            parents.Add(parent);
                        }

                        Assert.Single(parents);
                    }
                }
            }
        }
    }

    /// <summary>Where a map's own UV puts a point in the world, at the middle of its depth range.</summary>
    /// <remarks>
    ///     The depth is arbitrary and that is the point: every clipmap level is orthographic along the
    ///     same snapped light, so where a point lands in a level's page grid does not depend on how far
    ///     along the light it is. A perspective map would need the real depth, which is why this helper
    ///     is here rather than in <see cref="VirtualShadowMap" />.
    /// </remarks>
    static Vector3 Unproject(in Matrix4x4 inverse, float u, float v) {
        // NdcToUv's convention, run backwards — v is flipped and u is not. A helper that agreed with
        // itself but not with Transform.NdcToUv would assert a nesting the shaders never see.
        var ndc = new Vector4((u * 2f) - 1f, -((v * 2f) - 1f), 0.5f, 1f);
        var world = Matrix4x4.TransformVector4(ndc, inverse);

        return new Vector3(world.X, world.Y, world.Z) / world.W;
    }

    /// <summary>
    ///     A sun drifting less than the snap does not move the projection; past it, it does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The page snap's missing half, stated the same way the camera's is above. The light
    ///         direction enters the matrix raw, so before <see cref="VirtualShadowMap.SnapDirection" />
    ///         a sun that moved at all — sample 13's orbits deliberately — changed every level's
    ///         projection every frame, every resident page was invalidated every frame, and the redraw
    ///         budget never caught up: pages piled up allocated and owed while the uploaded table
    ///         carried nothing. Snapped, the fitted direction holds still between steps and the pages
    ///         drawn against it stay true.
    ///     </para>
    ///     <para>
    ///         Anchored on the lattice for the camera test's reason: a direction an arbitrary
    ///         hair from a cell boundary would make this a test of where the boundary happens to be.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_sun_drifting_less_than_the_snap_does_not_move_the_projection() {
        const float Step = 0.5f;

        // On the lattice, so the cell is centred on it and a drift under a quarter step stays put.
        var anchored = VirtualShadowMap.SnapDirection(Sun, Step);

        // A tenth of a degree of azimuth — thirty frames of a thirty-second orbit.
        var drifted = Turned(anchored, 0.1f);

        Assert.Equal(anchored, VirtualShadowMap.SnapDirection(drifted, Step));

        Assert.Equal(
            VirtualShadowMap.ClipmapProjection(0, FirstExtent, new(7f, 1f, 2f), anchored, Depth),
            VirtualShadowMap.ClipmapProjection(
                0,
                FirstExtent,
                new(7f, 1f, 2f),
                VirtualShadowMap.SnapDirection(drifted, Step),
                Depth
            )
        );

        // Past the step it moves, or the snap would be a shadow that never follows the sun at all.
        Assert.NotEqual(anchored, VirtualShadowMap.SnapDirection(Turned(anchored, 2f), Step));

        // And zero disables it: the direction comes back normalized and otherwise untouched.
        Assert.Equal(Vector3.Normalize(drifted), VirtualShadowMap.SnapDirection(drifted, 0f));
    }

    /// <summary>A direction swung about the up axis by some degrees, elevation kept.</summary>
    static Vector3 Turned(Vector3 direction, float degrees) {
        var azimuth = MathF.Atan2(direction.Z, direction.X) + (degrees * (MathF.PI / 180f));
        var elevation = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        var planar = MathF.Cos(elevation);

        return new(MathF.Cos(azimuth) * planar, MathF.Sin(elevation), MathF.Sin(azimuth) * planar);
    }

    // --- Pages ---------------------------------------------------------------

    /// <summary>
    ///     Every page of a level round-trips: a point inside it is placed in it, and only in it.
    /// </summary>
    /// <remarks>
    ///     The agreement the marking pass and the lookup both depend on. They compute the page from the
    ///     same projection by the same arithmetic, so what can be wrong is the arithmetic itself — an
    ///     inverted y, an off-by-one at a boundary, a grid that does not tile the map — and each of
    ///     those is a pixel asking for one page and reading another.
    /// </remarks>
    [Fact]
    public void A_point_inside_a_page_is_placed_in_that_page() {
        var camera = new Vector3(3f, 2f, -5f);
        var projection = Level(2, camera);

        Assert.True(Matrix4x4.Invert(projection, out var inverse));

        for (var y = 0; y < VirtualShadowMap.PagesPerSide; y += 5) {
            for (var x = 0; x < VirtualShadowMap.PagesPerSide; x += 5) {
                // The centre of page (x, y), in the map's own UV, taken back to the world.
                var u = (x + 0.5f) / VirtualShadowMap.PagesPerSide;
                var v = (y + 0.5f) / VirtualShadowMap.PagesPerSide;

                var ndc = new Vector4((u * 2f) - 1f, 1f - (v * 2f), 0.5f, 1f);
                var world = Matrix4x4.TransformVector4(ndc, inverse);

                Assert.True(
                    VirtualShadowMap.PageOf(projection, new(world.X / world.W, world.Y / world.W, world.Z / world.W), out var page),
                    $"The centre of page ({x}, {y}) is outside the map it came from."
                );

                Assert.Equal(new Int2(x, y), page);
            }
        }
    }

    /// <summary>
    ///     A point the map does not cover is refused rather than clamped.
    /// </summary>
    /// <remarks>
    ///     <b>Containment and not merely a coordinate.</b> A position outside the map projects to a
    ///     coordinate that is a perfectly ordinary number, and using it addresses a page fitted somewhere
    ///     else entirely — a shadow drawn in the right place from the wrong part of the world. That is
    ///     the defect <c>ClusteredShading.CascadeContaining</c> records having carried for its whole
    ///     life, and this is the same test one level down.
    /// </remarks>
    [Fact]
    public void A_point_outside_the_map_has_no_page() {
        var projection = Level(0, Vector3.Zero);
        var extent = VirtualShadowMap.ExtentOf(0, FirstExtent);
        var (right, _, _) = VirtualShadowMap.Basis(Sun);

        Assert.True(VirtualShadowMap.PageOf(projection, Vector3.Zero, out _));
        Assert.False(VirtualShadowMap.PageOf(projection, right * extent, out _));

        // And along the light, past the box's own depth: a caster behind the near plane is a position
        // the projection wraps around rather than clamps.
        Assert.False(VirtualShadowMap.PageOf(projection, Sun * (Depth * 2f), out _));
        Assert.False(VirtualShadowMap.PageOf(projection, Sun * (-Depth * 2f), out _));
    }

    /// <summary>
    ///     The page projection puts that page, and only that page, on the whole target.
    /// </summary>
    /// <remarks>
    ///     What a draw into a physical page needs: the page's rectangle of the map's clip space scaled
    ///     up to fill a viewport. It is the exact inverse of the tile fold a cascade atlas uses, and it
    ///     is written out rather than expressed through it — so this composes the pair and asserts the
    ///     identity, which is the check that reading the arithmetic cannot give you.
    /// </remarks>
    [Fact]
    public void A_pages_projection_fills_the_target_with_that_page() {
        var projection = Level(1, new(7f, 1f, 2f));

        Assert.True(Matrix4x4.Invert(projection, out var inverse));

        foreach (var page in (Int2[])[new(0, 0), new(31, 0), new(0, 31), new(31, 31), new(17, 5)]) {
            var window = VirtualShadowMap.PageProjection(projection, page);

            // The centre of the page, in world space, has to land at the middle of the target.
            var u = (page.X + 0.5f) / VirtualShadowMap.PagesPerSide;
            var v = (page.Y + 0.5f) / VirtualShadowMap.PagesPerSide;

            var ndc = new Vector4((u * 2f) - 1f, 1f - (v * 2f), 0.5f, 1f);
            var back = Matrix4x4.TransformVector4(ndc, inverse);
            var world = new Vector3(back.X / back.W, back.Y / back.W, back.Z / back.W);

            var landed = Matrix4x4.TransformVector4(new(world, 1f), window);

            Assert.Equal(0f, landed.X / landed.W, 3);
            Assert.Equal(0f, landed.Y / landed.W, 3);

            // And the depth is untouched, because a page moves where a texel is and not how far away
            // it is — a window that scaled z would put every page's casters at a different range.
            var straight = Matrix4x4.TransformVector4(new(world, 1f), projection);

            Assert.Equal(straight.Z / straight.W, landed.Z / landed.W, 5);
        }
    }

    /// <summary>Every page of every map has an index of its own.</summary>
    /// <remarks>
    ///     A clipmap level and a spot light share one address space, so the thing that could be wrong is
    ///     two maps overlapping in it — which is one light's shadow drawn out of another's page.
    /// </remarks>
    [Fact]
    public void Every_map_owns_a_disjoint_run_of_the_address_space() {
        var seen = new HashSet<int>();

        for (var map = 0; map < VirtualShadowMap.MaxLevels; map++) {
            var first = (uint)(map * VirtualShadowMap.PagesPerMap);

            for (var y = 0; y < VirtualShadowMap.PagesPerSide; y++) {
                for (var x = 0; x < VirtualShadowMap.PagesPerSide; x++) {
                    var index = VirtualShadowMap.IndexOf(first, new(x, y));

                    Assert.InRange(index, 0, VirtualShadowMap.MaxPages - 1);
                    Assert.True(seen.Add(index), $"Map {map}'s page ({x}, {y}) collides with another map's.");
                }
            }
        }

        Assert.Equal(VirtualShadowMap.MaxPages, seen.Count);
    }

    // --- The shader ----------------------------------------------------------

    /// <summary>
    ///     The shaders still address pages the way the host says they do.
    /// </summary>
    /// <remarks>
    ///     The gap every mirror has. A transliteration checked against an oracle says the host's copy is
    ///     right and says nothing about whether the shader is still the thing it is a copy of — and here
    ///     there are two shaders to keep in step, because the pass that <em>asks</em> for a page and the
    ///     lookup that <em>reads</em> one have to agree about which page it is.
    /// </remarks>
    [Fact]
    public void The_shaders_address_pages_the_way_the_host_does() {
        var shared = Source("VirtualShadows", "VirtualShadows.rvn");
        var mark = Source("Pipeline", "VirtualShadowMark.rvn");

        // The constants both sides size the table with.
        Assert.Contains($"const val PageTexels = {VirtualShadowMap.PageTexels}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val PagesPerSide = {VirtualShadowMap.PagesPerSide}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val MaxLevels = {VirtualShadowMap.MaxLevels}", shared, StringComparison.Ordinal);
        Assert.Contains("const val PageAbsent = 0xFFFFFFFFu", shared, StringComparison.Ordinal);

        // One derivation of the UV, which is what stops the marking pass and the lookup disagreeing
        // about which page a point is in.
        Assert.Contains("static func MapUv(", shared, StringComparison.Ordinal);
        Assert.Contains("Transform.NdcToUv(ndc)", shared, StringComparison.Ordinal);

        // The level selection, and the containment test that is not a clamp.
        Assert.Contains("static func LevelFor(", shared, StringComparison.Ordinal);
        Assert.Contains("static func WorldTexelSize(", shared, StringComparison.Ordinal);

        // And the marking pass's own half: the frame's depth, and one atomic per pixel.
        Assert.Contains("Transform.UvDepthToWorld(", mark, StringComparison.Ordinal);
        Assert.Contains("atomicOr(marks[", mark, StringComparison.Ordinal);

        // The sky is skipped, or the coarsest level of the clipmap is allocated for every pixel of it.
        Assert.Contains("if (depth <= 0f) {", mark, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The pass that asks for a page and the lookup that reads one measure the same distance.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A page a pixel never asked for is a page no budget will ever draw for it.</b>
    ///         <c>VirtualShadowMark.Main</c> takes its footprint from
    ///         <c>length(world - viewPosition)</c> — the radial distance, which is the one a pixel's
    ///         angular footprint actually scales with — and <c>ClusteredShading.Shadow</c> handed the
    ///         lookup <c>ClusterGrid.DepthOf</c>, the view-space <em>depth</em>. Those differ by the
    ///         cosine of the angle off the view axis: at sample 13's 60° vertical field over 16:9 the
    ///         corner of the screen is 1.55 times further along the ray than it is down the axis, and
    ///         <c>Vsm.LevelFor</c> is a <c>ceil(log2(·))</c>, so up to a whole level.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The lookup asked for the finer one</b> — depth is the smaller number — so over a
    ///         wide band of the screen it looked up a page nothing had marked, and the fallback to
    ///         <c>level + 1</c> then answered, at half the resolution the pixel had asked for, from
    ///         precisely the page the marking pass had asked for. That is why the fallback measured as
    ///         useful and why "85% of successful fallbacks land on a page another pixel marked this
    ///         frame" was true: most of what it covered for was this mismatch, not the annulus
    ///         geometry it was credited to.
    ///     </para>
    ///     <para>
    ///         Asserted on the sources because no host arithmetic can see it — both sides call the same
    ///         <c>Vsm.LevelFor</c> and differ only in what they hand it, which is a fact about two call
    ///         sites in two files.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_marking_pass_and_the_lookup_measure_the_same_distance() {
        var shared = Source("VirtualShadows", "VirtualShadows.rvn");
        var mark = Source("Pipeline", "VirtualShadowMark.rvn");
        var shading = Source("Pipeline", "ClusteredShading.rvn");

        // The marking pass: radial, from the eye to the world point the depth decoded to.
        Assert.Contains(
            "Vsm.WorldTexelSize(length(world - viewPosition), screenHeightScale, screen.y)",
            mark,
            StringComparison.Ordinal
        );

        // The lookup: whatever it was handed, and it is named for the quantity rather than for the
        // axis — a parameter called `viewDepth` is what let a depth be passed for a distance.
        Assert.Contains(
            "func Sample(positionWS: float3, n: float3, NdotL: float, viewDistance: float)",
            shared,
            StringComparison.Ordinal
        );

        Assert.Contains(
            "Vsm.WorldTexelSize(viewDistance, shadowScreenHeightScale, shadowScreenHeight)",
            shared,
            StringComparison.Ordinal
        );

        // And the one call site that decides which of the two the lookup is handed.
        Assert.Contains(
            "DirectionalShadow(p.positionWS, n, NdotL, length(positionVS))",
            shading,
            StringComparison.Ordinal
        );

        // The cascade splits stay on the depth, because that is what they were fitted in — the fix
        // is that one quantity stopped standing in for the other, not that the other went away.
        Assert.Contains(
            "val viewDepth = ClusterGrid.DepthOf(positionVS)",
            shading,
            StringComparison.Ordinal
        );
    }

    /// <summary>The lookup's own bias defaults are a distance over the shipped box, not a raw number.</summary>
    /// <remarks>
    ///     The declaration is what a host that publishes nothing gets, so it is where the hundredfold
    ///     bias lived: 0.002 and 0.004 against a four hundred metre level are 0.8 m and 1.6 m per unit
    ///     of slope, where <c>ShadowMapRenderer</c>'s own are 0.008 m and 0.01 m.
    ///     <see cref="VirtualShadowMap.DepthScale" /> is the conversion, and this pins the sources to
    ///     it so the declaration and the node cannot drift apart.
    /// </remarks>
    [Fact]
    public void The_lookups_declared_bias_is_the_cascades_bias_over_the_shipped_depth_range() {
        var shared = Source("VirtualShadows", "VirtualShadows.rvn");
        var scale = VirtualShadowMap.DepthScale(400f);

        // 0.008 m and 0.01 m over four hundred, which is what the node publishes at its defaults.
        Assert.Equal(0.00002f, 0.008f * scale, 7);
        Assert.Equal(0.000025f, 0.01f * scale, 7);

        Assert.Contains("shadowPageConstantBias: float = 0.00002f", shared, StringComparison.Ordinal);
        Assert.Contains("shadowPageSlopeBias: float = 0.000025f", shared, StringComparison.Ordinal);
    }

    /// <summary>A shipped shader's source, found by walking up rather than by counting directories.</summary>
    static string Source(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }
}
