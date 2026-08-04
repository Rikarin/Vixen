// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 6's rasterizer, checked against the definition of what a rasterizer is.
/// </summary>
/// <remarks>
///     <para>
///         <b>Improvement 4 of <c>docs/plan/22-virtualized-geometry.md</c>, and the reason it names a CPU
///         reference rasterizer specifically.</b> The two ways a rasterizer is wrong are both invisible
///         in a still: cover one pixel too many along a shared edge and the seam is drawn twice, which
///         for a visibility buffer means an identity decided by whichever atomic won; cover one too few
///         and there is a line of background through a solid surface. Neither reads as a rasterizer bug
///         — they read as a mesh, a driver or a camera.
///     </para>
///     <para>
///         So the assertions here are against the <em>definition</em> rather than against a second
///         implementation: a pixel centre is inside a triangle or it is not, and two triangles sharing
///         an edge cover every pixel of their union exactly once. <see cref="SoftwareRaster" /> is the
///         transliteration of <c>ClusterSoftwareRaster.rvn</c>, and the last test here is what keeps the
///         shader the thing it is a transliteration of.
///     </para>
/// </remarks>
public class SoftwareRasterTests {
    static readonly Int2 Screen = new(64, 64);

    /// <summary>A clip-space position from a pixel-space one, which is <c>Project</c> inverted.</summary>
    /// <remarks>
    ///     Written as the inverse rather than as its own convention, because half of what these tests
    ///     assert is where a triangle lands — and a fixture that derived "where it should land" a second
    ///     way would be testing the two derivations against each other.
    /// </remarks>
    static Vector4 Clip(float px, float py, float depth = 0.5f, float w = 1f) =>
        new(
            ((px / Screen.X * 2f) - 1f) * w,
            -(((py / Screen.Y * 2f) - 1f)) * w,
            depth * w,
            w
        );

    /// <summary>Which pixels a triangle covered, from a buffer only it wrote into.</summary>
    static bool[] Covered(Vector4 a, Vector4 b, Vector4 c, uint identity = 1u) {
        var depths = new ulong[Screen.X * Screen.Y];
        SoftwareRaster.Rasterize(a, b, c, Screen, identity, depths);

        return [.. depths.Select(key => key != 0uL)];
    }

    // --- Coverage against the definition -------------------------------------

    /// <summary>
    ///     A pixel is covered exactly when its centre is inside the triangle.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The oracle is the sign test and nothing else — a point is inside a triangle when it is on
    ///         the same side of all three edges — which is a statement about geometry rather than about
    ///         the rasterizer. Pixels exactly <em>on</em> an edge are excluded from the comparison here
    ///         and are the whole subject of the next test, because that is where a rule rather than a
    ///         definition decides.
    ///     </para>
    ///     <para>
    ///         Randomised over the triangle's shape, because the interesting failures are at the shape
    ///         boundaries: a sliver, a triangle smaller than a pixel — which is the whole regime this
    ///         raster exists for — and one that hangs off the screen.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pixel_is_covered_exactly_when_its_centre_is_inside() {
        var random = new Random(20260804);

        for (var trial = 0; trial < 200; trial++) {
            var a = new Vector2(random.NextSingle() * 80f - 8f, random.NextSingle() * 80f - 8f);
            var b = a + new Vector2((random.NextSingle() - 0.5f) * 40f, (random.NextSingle() - 0.5f) * 40f);
            var c = a + new Vector2((random.NextSingle() - 0.5f) * 40f, (random.NextSingle() - 0.5f) * 40f);

            var covered = Covered(Clip(a.X, a.Y), Clip(b.X, b.Y), Clip(c.X, c.Y));

            for (var y = 0; y < Screen.Y; y++) {
                for (var x = 0; x < Screen.X; x++) {
                    var point = new Vector2(x + 0.5f, y + 0.5f);

                    var e0 = SoftwareRaster.Edge(a, b, point);
                    var e1 = SoftwareRaster.Edge(b, c, point);
                    var e2 = SoftwareRaster.Edge(c, a, point);

                    // Exactly on an edge is the tie rule's business, not the definition's.
                    if (e0 == 0f || e1 == 0f || e2 == 0f) {
                        continue;
                    }

                    var inside = (e0 > 0f && e1 > 0f && e2 > 0f) || (e0 < 0f && e1 < 0f && e2 < 0f);

                    Assert.Equal(inside, covered[(y * Screen.X) + x]);
                }
            }
        }
    }

    /// <summary>
    ///     Two triangles sharing a diagonal cover every pixel of their quad exactly once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The assertion the top-left rule exists for, and the diagonal is chosen to run through
    ///         pixel centres.</b> A quad from a corner to a corner in whole pixels has the line
    ///         <c>y = x</c> passing exactly through <c>(0.5, 0.5)</c>, <c>(1.5, 1.5)</c> and every centre
    ///         after them — so every one of those pixels has an edge function of exactly zero for both
    ///         triangles, and which of them takes it is decided by a rule rather than by arithmetic.
    ///     </para>
    ///     <para>
    ///         Sabotage: accepting every zero — <c>edge &gt;= 0</c> instead of the top-left test — makes
    ///         the diagonal appear in both sets and the disjointness assertion fail. Rejecting every zero
    ///         makes it appear in neither and the coverage assertion fail. Both directions are what the
    ///         two assertions below are for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_triangles_of_a_quad_cover_every_pixel_exactly_once() {
        var lower = Covered(Clip(0f, 0f), Clip(16f, 16f), Clip(0f, 16f));
        var upper = Covered(Clip(0f, 0f), Clip(16f, 0f), Clip(16f, 16f));

        var both = 0;
        var neither = 0;

        for (var y = 0; y < 16; y++) {
            for (var x = 0; x < 16; x++) {
                var index = (y * Screen.X) + x;

                if (lower[index] && upper[index]) {
                    both++;
                }

                if (!lower[index] && !upper[index]) {
                    neither++;
                }
            }
        }

        Assert.Equal(0, both);
        Assert.Equal(0, neither);

        // And nothing outside the quad, so "exactly once" is about the quad rather than about a
        // rasterizer that filled the screen.
        for (var index = 0; index < lower.Length; index++) {
            var inside = index % Screen.X < 16 && index / Screen.X < 16;

            Assert.Equal(inside, lower[index] || upper[index]);
        }
    }

    /// <summary>
    ///     Reversing the winding covers the same pixels.
    /// </summary>
    /// <remarks>
    ///     Not a symmetry for its own sake. <see cref="GpuClusterRaster" />'s pipeline is two-sided —
    ///     the normal cone has already rejected the clusters that face away as a whole, and a
    ///     back-facing triangle inside a surviving one is drawn on both paths or on neither — so a
    ///     software raster that culled by winding would draw less than the hardware one did, which is a
    ///     hole that appears only where the routing threshold happens to fall.
    /// </remarks>
    [Fact]
    public void Winding_does_not_change_which_pixels_are_covered() {
        var forward = Covered(Clip(4f, 4f), Clip(40f, 12f), Clip(12f, 44f));
        var reversed = Covered(Clip(4f, 4f), Clip(12f, 44f), Clip(40f, 12f));

        Assert.Equal(forward, reversed);
        Assert.True(forward.Count(covered => covered) > 128);
    }

    // --- Depth ---------------------------------------------------------------

    /// <summary>
    ///     The nearer surface wins, whichever order the two are rasterized in.
    /// </summary>
    /// <remarks>
    ///     What the 64-bit <c>atomicMax</c> buys and the reason the phase was blocked on it: with
    ///     thirty-two bits the word holds a depth <em>or</em> an identity, and resolving both takes two
    ///     passes over the same triangles. Reverse-Z makes the nearer surface the larger number, so one
    ///     <c>max</c> decides it and the identity rides along.
    /// </remarks>
    [Fact]
    public void The_nearer_surface_wins_whichever_order_they_are_drawn() {
        var near = (Clip(0f, 0f, 0.8f), Clip(32f, 0f, 0.8f), Clip(0f, 32f, 0.8f));
        var far = (Clip(0f, 0f, 0.2f), Clip(32f, 0f, 0.2f), Clip(0f, 32f, 0.2f));

        foreach (var flipped in (bool[])[false, true]) {
            var depths = new ulong[Screen.X * Screen.Y];

            if (flipped) {
                SoftwareRaster.Rasterize(far.Item1, far.Item2, far.Item3, Screen, 7u, depths);
                SoftwareRaster.Rasterize(near.Item1, near.Item2, near.Item3, Screen, 3u, depths);
            } else {
                SoftwareRaster.Rasterize(near.Item1, near.Item2, near.Item3, Screen, 3u, depths);
                SoftwareRaster.Rasterize(far.Item1, far.Item2, far.Item3, Screen, 7u, depths);
            }

            // A pixel comfortably inside both.
            var key = depths[(4 * Screen.X) + 4];

            Assert.Equal(3u, GpuClusterSoftwareRaster.IdentityOf(key));
            Assert.Equal(0.8f, GpuClusterSoftwareRaster.DepthOf(key), 4);
        }
    }

    /// <summary>
    ///     The interpolated depth is the triangle's own plane, not a corner's.
    /// </summary>
    /// <remarks>
    ///     <c>z/w</c> is affine in screen space, which is the one attribute a rasterizer may interpolate
    ///     without the perspective divide <c>VisibilityResolve</c> does for everything else — so a plane
    ///     tilted in depth has to come back as the plane. Written with a <c>w</c> that varies across the
    ///     triangle, because a fixture where every corner has <c>w = 1</c> cannot tell the affine
    ///     interpolation of <c>z/w</c> from the affine interpolation of <c>z</c>.
    /// </remarks>
    [Fact]
    public void The_resolved_depth_is_the_triangles_own_plane() {
        // Three corners with three different w, so the divide matters.
        var a = Clip(2f, 2f, 0.9f, 1f);
        var b = Clip(60f, 2f, 0.3f, 3f);
        var c = Clip(2f, 60f, 0.6f, 2f);

        var depths = new ulong[Screen.X * Screen.Y];
        SoftwareRaster.Rasterize(a, b, c, Screen, 1u, depths);

        // The plane through the three (screen x, screen y, z/w) points, solved directly.
        var p0 = SoftwareRaster.Project(a, Screen);
        var p1 = SoftwareRaster.Project(b, Screen);
        var p2 = SoftwareRaster.Project(c, Screen);

        var area = SoftwareRaster.Edge(p0, p1, p2);

        for (var y = 0; y < Screen.Y; y++) {
            for (var x = 0; x < Screen.X; x++) {
                var key = depths[(y * Screen.X) + x];

                if (key == 0uL) {
                    continue;
                }

                var point = new Vector2(x + 0.5f, y + 0.5f);

                var expected =
                    ((SoftwareRaster.Edge(p1, p2, point) * (a.Z / a.W))
                        + (SoftwareRaster.Edge(p2, p0, point) * (b.Z / b.W))
                        + (SoftwareRaster.Edge(p0, p1, point) * (c.Z / c.W)))
                    / area;

                // The tolerance is the key's own quantization and nothing else: a depth is stored as a
                // fraction of 2^32 − 256, so anything above that is the interpolation disagreeing.
                Assert.Equal(expected, GpuClusterSoftwareRaster.DepthOf(key), 5);
            }
        }
    }

    /// <summary>
    ///     The packed word orders by depth first and carries the identity underneath.
    /// </summary>
    /// <remarks>
    ///     <b>Unsigned, and that is the assertion that matters.</b> The top bit of a depth key is data,
    ///     so a signed maximum would resolve the far half of the range backwards — correct for every
    ///     scene that never gets there, and wrong for the ones that do.
    /// </remarks>
    [Fact]
    public void The_key_orders_by_depth_and_carries_the_identity() {
        Assert.True(GpuClusterSoftwareRaster.Key(0.6f, 1u) > GpuClusterSoftwareRaster.Key(0.4f, 0xFFFFFFu));
        Assert.True(GpuClusterSoftwareRaster.Key(1f, 0u) > GpuClusterSoftwareRaster.Key(0.99f, 0xFFFFFFu));

        // The half of the range a signed compare would invert.
        Assert.True(GpuClusterSoftwareRaster.Key(0.9f, 1u) > GpuClusterSoftwareRaster.Key(0.1f, 1u));

        Assert.Equal(0u, GpuClusterSoftwareRaster.Key(0f, 0u));
        Assert.Equal(42u, GpuClusterSoftwareRaster.IdentityOf(GpuClusterSoftwareRaster.Key(0.5f, 42u)));
        Assert.Equal(0.5f, GpuClusterSoftwareRaster.DepthOf(GpuClusterSoftwareRaster.Key(0.5f, 42u)), 6);
    }

    // --- A real cut ----------------------------------------------------------

    /// <summary>
    ///     A whole cut of a closed mesh rasterizes without a hole in it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The crack detector phases 1 to 3 use, pointed at the raster.</b> Those check that a cut
    ///         is closed as a <em>mesh</em> — every edge has two triangles on it — and this checks that
    ///         drawing that mesh leaves no pixel of it unpainted. They are different failures: a cut can
    ///         be watertight and still be drawn with a seam, because whether two triangles meeting along
    ///         an edge cover the pixels on that edge is a property of the fill rule and not of the
    ///         geometry.
    ///     </para>
    ///     <para>
    ///         A sphere, because a closed surface has an interior with no legitimate holes in it: any
    ///         uncovered pixel strictly inside the silhouette is a seam. The silhouette itself is
    ///         excluded, since a pixel there is genuinely half in and half out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tie rule is not what this catches</b>, and that was checked rather than assumed:
    ///         a sphere's vertices land on pixel centres essentially never, so both tie-rule sabotages
    ///         leave this passing and fail the quad above. What this catches is the rest of the fill —
    ///         a bounding box snapped the wrong way, a winding flip that drops a cluster, a depth
    ///         interpolation that puts the far hemisphere in front — over real geometry rather than over
    ///         a fixture triangle.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_whole_cut_of_a_sphere_rasterizes_without_a_hole() {
        var input = Sphere(24, 48);
        var mesh = MeshletBuilder.Build(input);
        var cut = MeshletCut.SelectByError(mesh, 0.0001f);

        Assert.NotEmpty(cut);

        var screen = new Int2(96, 96);
        var depths = new ulong[screen.X * screen.Y];
        var corners = new int[GpuClusterRaster.MaximumTriangles * 3];

        // A camera looking at a sphere of radius one from four away, with the projection folded in: the
        // sphere covers most of the frame, which is what makes an interior hole visible as one.
        var camera = Matrix4x4.LookAt(new(0f, 0f, 4f), Vector3.Zero, new(0f, 1f, 0f))
            * Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 100f);

        var slot = 0u;

        foreach (var index in cut) {
            var meshlet = mesh.Meshlets[index];
            mesh.GetTriangles(meshlet, corners);

            for (var triangle = 0; triangle < meshlet.TriangleCount; triangle++) {
                SoftwareRaster.Rasterize(
                    Project(input.Positions[corners[(triangle * 3) + 0]], camera),
                    Project(input.Positions[corners[(triangle * 3) + 1]], camera),
                    Project(input.Positions[corners[(triangle * 3) + 2]], camera),
                    screen,
                    GpuClusterRaster.Pack(slot, (uint)triangle),
                    depths
                );
            }

            slot++;
        }

        var covered = depths.Count(key => key != 0uL);
        Assert.True(covered > 1000, $"The cut covered {covered} pixels, which is not a sphere.");

        // A pixel is interior when its four neighbours are covered too; an interior pixel that is not
        // itself covered is a seam between two clusters.
        var holes = 0;

        for (var y = 1; y < screen.Y - 1; y++) {
            for (var x = 1; x < screen.X - 1; x++) {
                var at = (y * screen.X) + x;

                if (depths[at] != 0uL) {
                    continue;
                }

                if (depths[at - 1] != 0uL
                    && depths[at + 1] != 0uL
                    && depths[at - screen.X] != 0uL
                    && depths[at + screen.X] != 0uL) {
                    holes++;
                }
            }
        }

        Assert.Equal(0, holes);
    }

    static Vector4 Project(Vector3 position, in Matrix4x4 viewProjection) =>
        Matrix4x4.TransformVector4(new(position, 1f), viewProjection);

    /// <summary>A UV sphere, which is the fixture every phase of this system uses.</summary>
    static MeshletBuildInput Sphere(int rings, int segments) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var ring = 0; ring <= rings; ring++) {
            var phi = MathF.PI * ring / rings;

            for (var segment = 0; segment <= segments; segment++) {
                var theta = 2f * MathF.PI * segment / segments;

                positions.Add(
                    new(
                        MathF.Sin(phi) * MathF.Cos(theta),
                        MathF.Cos(phi),
                        MathF.Sin(phi) * MathF.Sin(theta)
                    )
                );
            }
        }

        for (var ring = 0; ring < rings; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var a = (ring * (segments + 1)) + segment;
                var b = a + segments + 1;

                indices.AddRange([a, b, a + 1]);
                indices.AddRange([a + 1, b, b + 1]);
            }
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }

    // --- The shader ----------------------------------------------------------

    /// <summary>
    ///     The shader still contains the arithmetic the mirror mirrors.
    /// </summary>
    /// <remarks>
    ///     The gap every mirror has, and the same defence <c>GpuClusterCullingTests</c> keeps for the
    ///     traversal: a transliteration checked against an oracle says the host's copy is right and says
    ///     nothing about whether the shader is still the thing it is a copy of.
    /// </remarks>
    [Fact]
    public void The_shader_rasterizes_what_the_host_says_it_does() {
        var source = Source("Pipeline", "ClusterSoftwareRaster.rvn");

        // One shader, two dispatches — the arrangement Culling.rvn already has, and Raven's rule.
        Assert.Contains("[Permutation] val Merge: bool", source, StringComparison.Ordinal);

        // The atomic the whole phase was blocked on, at the width that makes it one pass.
        Assert.Contains("var depths: RWBuffer<uint64>", source, StringComparison.Ordinal);
        Assert.Contains("atomicMax(depths[", source, StringComparison.Ordinal);

        // The coverage rule, and the tie rule that stops a shared edge being drawn twice.
        Assert.Contains("static func Edge(", source, StringComparison.Ordinal);
        Assert.Contains("static func TopLeft(", source, StringComparison.Ordinal);

        // The suffix of the visible list, which is what the traversal routes here.
        Assert.Contains("visible[Cull.VisibleSoftware]", source, StringComparison.Ordinal);
        Assert.Contains("visible.Length - 1 - int(index)", source, StringComparison.Ordinal);

        // And the merge, which is what makes the two rasters one picture rather than two.
        Assert.Contains("hardwareDepth.Load(", source, StringComparison.Ordinal);
        Assert.Contains("identities.Store(", source, StringComparison.Ordinal);
        Assert.Contains("depths[index] = Software.Empty()", source, StringComparison.Ordinal);
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
