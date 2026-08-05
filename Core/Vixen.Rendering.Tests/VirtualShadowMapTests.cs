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
