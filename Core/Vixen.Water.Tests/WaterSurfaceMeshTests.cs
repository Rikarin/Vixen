// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     The surface mesh — [docs/plan/35 § D4], and W4's exit criteria.
/// </summary>
/// <remarks>
///     <para>
///         <b>Written before the renderer, for [§ Part 4]'s reason.</b> A crack or a pop found by eye
///         is found at one camera position, attributed to the wrong thing, and worked around with a
///         fudge factor that then lives forever — Unreal's <c>Max Wave Height Offset</c> is what that
///         looks like after five years. Both properties here are functions of the node's extent and
///         the view's distance, so both are arithmetic and neither needs a device.
///     </para>
///     <para>
///         ⚠ <b>Water makes the crack worse than the terrain does, not better.</b> A crack in a
///         terrain shows a sliver of skybox for a frame; a crack in a flat specular surface shows a
///         bright line that reads as a rendering artefact from four hundred metres.
///     </para>
/// </remarks>
public sealed class WaterSurfaceMeshTests {
    static WaterFieldDescription Window(int resolution = 129, float extent = 256f) =>
        new() { Origin = Vector2.Zero, Extent = extent, Resolution = resolution };

    /// <summary>A square lake with its low corner at a place.</summary>
    static WaterBody Square(Vector2 low, float side, float surface, float depth, float falloff) {
        var spline = new Spline(
            Spline.SmoothTangents(
                [
                    new(low.X, surface, low.Y),
                    new(low.X + side, surface, low.Y),
                    new(low.X + side, surface, low.Y + side),
                    new(low.X, surface, low.Y + side)
                ],
                closed: true,
                tension: 1f
            ),
            closed: true
        );

        return new(WaterBodyKind.Lake, spline, defaults: new() { Depth = depth }) {
            SurfaceHeight = surface,
            ShoreFalloff = falloff,
            BedRamp = MathF.Max(falloff * 2f, 1f)
        };
    }

    /// <summary>A field entirely covered by one lake, so coverage never prunes the descent.</summary>
    static WaterFieldPyramid Everywhere(in WaterFieldDescription window, float surface = 10f) {
        var field = new WaterField(window);

        field.Rasterize([Square(new(-4000f, -4000f), 8000f, surface, 20f, 4f)], new FlatWaterGround(surface - 20f));

        var pyramid = new WaterFieldPyramid(window.Resolution);
        pyramid.Build(field);

        return pyramid;
    }

    static WaterSurfaceMesh Mesh(int gridQuads = 8, int resolution = 129, float extent = 256f) {
        var window = Window(resolution, extent);
        var mesh = new WaterSurfaceMesh(window, TerrainLodRanges.Default with { NearRange = 32f }, gridQuads);

        mesh.Update(Everywhere(window), 0f);

        return mesh;
    }

    // --- The morph, which is where the cracks are ---------------------------

    /// <summary>
    ///     A fine patch's shared edge lands exactly on its coarse neighbour's vertices.
    /// </summary>
    /// <remarks>
    ///     [31 § Part 4]'s test, transferred to water — which is the return on there being one
    ///     quadtree. The finer patch is at the far end of its band, so it is fully morphed; the
    ///     coarser one has just taken over, so it is not morphed at all. Every position the finer
    ///     patch puts on the shared edge must be one the coarser patch also has.
    /// </remarks>
    [Fact]
    public void AFinePatchsSharedEdgeLandsExactlyOnItsCoarseNeighboursVertices() {
        var mesh = Mesh();

        // Level 1 spans 16 quads, level 2 spans 32. Side by side along X, sharing x = 16.
        var fine = new TerrainLodNode(0, 0, 16, 1, 1f);
        var coarse = new TerrainLodNode(16, 0, 32, 2, 0f);

        var coarseEdge = new HashSet<float>();

        for (var grid = 0; grid <= mesh.GridQuads; grid++) {
            coarseEdge.Add(MathF.Round(mesh.GroundOf(coarse, 0, grid).Y, 4));
        }

        for (var grid = 0; grid <= mesh.GridQuads; grid++) {
            var ground = mesh.GroundOf(fine, mesh.GridQuads, grid);

            Assert.Equal(16f * mesh.MetresPerQuad, ground.X, 4);
            Assert.Contains(MathF.Round(ground.Y, 4), coarseEdge);
        }
    }

    /// <summary>
    ///     And the displaced positions agree too, which is the property that actually matters.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one the terrain does not have to prove.</b> A Gerstner wave displaces horizontally
    ///     as well as vertically, so two nodes agreeing about the undisplaced position is necessary and
    ///     not obviously sufficient. It is sufficient because the displacement is a pure function of
    ///     the undisplaced position and the time — but "obviously" is what the fudge factor is made of,
    ///     so it is asserted against a real sea state rather than reasoned about.
    /// </remarks>
    [Fact]
    public void TheDisplacedEdgeAgreesAcrossALevelBoundary() {
        var mesh = Mesh();
        var waves = new GerstnerWave[16];
        var count = WaterWaveSpectrum.Default.Generate(waves);
        var evaluator = new WaterEvaluator(null, waves.AsSpan(0, count), WaterAttenuation.Default);

        var fine = new TerrainLodNode(0, 0, 16, 1, 1f);
        var coarse = new TerrainLodNode(16, 0, 32, 2, 0f);

        var coarseEdge = new Dictionary<string, Vector3>();

        for (var grid = 0; grid <= mesh.GridQuads; grid++) {
            var (position, _) = mesh.PositionOf(coarse, 0, grid, in evaluator, 3.5f);
            coarseEdge[Key(mesh.GroundOf(coarse, 0, grid))] = position;
        }

        for (var grid = 0; grid <= mesh.GridQuads; grid++) {
            var ground = mesh.GroundOf(fine, mesh.GridQuads, grid);
            var (position, _) = mesh.PositionOf(fine, mesh.GridQuads, grid, in evaluator, 3.5f);

            Assert.True(coarseEdge.TryGetValue(Key(ground), out var theirs), "the edges do not share a vertex.");
            Assert.Equal(theirs, position);
        }

        static string Key(Vector2 ground) =>
            $"{MathF.Round(ground.X, 3)}|{MathF.Round(ground.Y, 3)}";
    }

    [Fact]
    public void AFullyMorphedPatchHasHalfItsResolution() {
        var mesh = Mesh();
        var node = new TerrainLodNode(0, 0, 16, 1, 1f);
        var distinct = new HashSet<float>();

        for (var grid = 0; grid <= mesh.GridQuads; grid++) {
            distinct.Add(MathF.Round(mesh.GroundOf(node, grid, 0).X, 4));
        }

        Assert.Equal((mesh.GridQuads / 2) + 1, distinct.Count);
    }

    /// <summary>A vertex moves continuously as the camera does — the pop a screenshot will not catch.</summary>
    [Fact]
    public void AVertexMovesContinuouslyAsTheMorphRuns() {
        var mesh = Mesh();
        var step = mesh.MetresPerQuad * 32f / mesh.GridQuads;
        var previous = mesh.GroundOf(new(0, 0, 32, 2, 0f), 3, 3);

        for (var at = 1; at <= 200; at++) {
            var ground = mesh.GroundOf(new(0, 0, 32, 2, at / 200f), 3, 3);

            Assert.True(
                Vector2.Distance(previous, ground) < step * 0.02f,
                $"the vertex jumped at morph {at / 200f}."
            );

            previous = ground;
        }
    }

    // --- The finest spacing is the field's ----------------------------------

    /// <summary>
    ///     One vertex per texel at level zero, which is the whole reason the root is sized this way.
    /// </summary>
    /// <remarks>
    ///     Any other choice makes the surface either carry detail the field cannot supply — a
    ///     tessellation that interpolates a shoreline the info texture never resolved — or throw away
    ///     detail it can.
    /// </remarks>
    [Theory]
    [InlineData(129, 256f)]
    [InlineData(257, 512f)]
    [InlineData(513, 2048f)]
    public void TheFinestQuadIsOneTexelOfTheField(int resolution, float extent) {
        var window = Window(resolution, extent);
        var mesh = new WaterSurfaceMesh(window, TerrainLodRanges.Default);

        Assert.Equal(window.MetresPerTexel, mesh.MetresPerQuad, 5);
    }

    // --- Coverage, which is what prunes the tree ----------------------------

    /// <summary>A zone with a small lake in one corner does not descend into the dry three quarters.</summary>
    [Fact]
    public void TheDescentPrunesWhereThereIsNoWater() {
        var window = Window();
        var field = new WaterField(window);

        field.Rasterize([Square(new(24f, 24f), 24f, 5f, 3f, 2f)], new FlatWaterGround(0f));

        var pyramid = new WaterFieldPyramid(window.Resolution);
        pyramid.Build(field);

        var mesh = new WaterSurfaceMesh(window, TerrainLodRanges.Default with { NearRange = 32f });
        mesh.Update(pyramid, 0f);

        var nodes = new List<TerrainLodNode>();
        mesh.Select(new(36f, 40f, 36f), Everything(), nodes);

        Assert.NotEmpty(nodes);

        // Every selected node touches the pond's own corner of the window, with a node's slack for
        // the conservative rounding the predicate does on purpose.
        foreach (var node in nodes) {
            var low = node.X * mesh.MetresPerQuad;
            var high = (node.X + node.Quads) * mesh.MetresPerQuad;

            Assert.True(high >= 20f && low <= 52f, $"a node at [{low}, {high}] is nowhere near the pond.");
        }
    }

    /// <summary>The predicate over-reports rather than under-reports, which is the safe direction.</summary>
    /// <remarks>
    ///     ⚠ Pruning happens before the children are visited, so a rectangle answered from its centre
    ///     would remove a shoreline running through a corner — and the water would end in a straight
    ///     edge halfway across a tile.
    /// </remarks>
    [Fact]
    public void ARectangleWithOneWetCornerReadsAsCovered() {
        var window = Window();
        var field = new WaterField(window);

        field.Rasterize([Square(new(2f, 2f), 8f, 5f, 3f, 1f)], new FlatWaterGround(0f));

        var pyramid = new WaterFieldPyramid(window.Resolution);
        pyramid.Build(field);

        // A 64-metre square whose only wet part is the corner it starts in.
        Assert.True(pyramid.AnyCoverage(new(0f, 0f), new(64f, 64f)));
        Assert.False(pyramid.AnyCoverage(new(100f, 100f), new(164f, 164f)));
    }

    // --- The bound has to contain the crests --------------------------------

    /// <summary>
    ///     A node's box grows by the sea state's maximum amplitude, or a swell is culled away.
    /// </summary>
    /// <remarks>
    ///     ⚠ The symptom of getting this wrong is a strip of missing sea that appears only when the
    ///     wind rises, which is why the amplitude is a stated input to the selection rather than
    ///     something the renderer applies afterwards.
    /// </remarks>
    [Fact]
    public void TheSelectionSurvivesACameraJustAboveTheCrests() {
        var window = Window();
        var mesh = new WaterSurfaceMesh(window, TerrainLodRanges.Default with { NearRange = 32f });

        mesh.Update(Everywhere(window), 3f);

        var nodes = new List<TerrainLodNode>();

        // A frustum that only contains the band the crests reach into.
        var slab = Slab(10.5f, 14f);

        Assert.True(mesh.Select(new(128f, 12f, 128f), slab, nodes) > 0);

        // ⚠ The negative control, without which this test cannot fail. The same field and the same
        // frustum with a still sea selects nothing, because every node's box stops at the rest height
        // of 10 — so what the assertion above is measuring really is the amplitude.
        var still = new WaterSurfaceMesh(window, TerrainLodRanges.Default with { NearRange = 32f });
        still.Update(Everywhere(window), 0f);

        nodes.Clear();

        Assert.Equal(0, still.Select(new(128f, 12f, 128f), slab, nodes));
    }

    // --- The far mesh -------------------------------------------------------

    [Fact]
    public void TheFarMeshRingsTheWindowAndDoesNotCoverIt() {
        var window = Window();
        var mesh = new WaterSurfaceMesh(window, TerrainLodRanges.Default) { FarDistance = 1024f };

        mesh.Update(Everywhere(window), 0f);

        var nodes = new List<TerrainLodNode>();

        Assert.True(mesh.SelectFar(Everything(), 10f, nodes) > 0);
        Assert.DoesNotContain(nodes, node => node is { X: 0, Z: 0 });

        // Four rings of window-sized cells for a kilometre past a 256-metre window, minus the middle.
        Assert.Equal((9 * 9) - 1, nodes.Count);
    }

    /// <summary>
    ///     The waves are gone by the window's edge, so the near mesh and the far mesh meet flush.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one place the drawn surface is deliberately not the queried one</b> — see
    ///     <see cref="WaterSurfaceMesh.EdgeFade" /> for why this is the narrower of the two available
    ///     divergences. Without it there is a step the height of a crest along a straight line at the
    ///     horizon, in every frame.
    /// </remarks>
    [Fact]
    public void TheWavesFadeToNothingAtTheWindowsEdge() {
        var window = Window();
        var mesh = new WaterSurfaceMesh(window, TerrainLodRanges.Default) { EdgeFade = 32f };

        mesh.Update(Everywhere(window), 0f);

        Assert.Equal(0f, mesh.EdgeDamping(new(0f, 128f)), 6);
        Assert.Equal(0f, mesh.EdgeDamping(new(256f, 128f)), 6);
        Assert.Equal(0f, mesh.EdgeDamping(new(128f, 0f)), 6);
        Assert.Equal(1f, mesh.EdgeDamping(new(128f, 128f)), 6);

        // And monotone across the band, so the fade is a ramp rather than a second seam.
        var previous = -1f;

        for (var at = 0; at <= 64; at++) {
            var damping = mesh.EdgeDamping(new(at * 0.5f, 128f));

            Assert.True(damping >= previous, $"the fade went backwards at {at * 0.5f} m.");
            previous = damping;
        }
    }

    static BoundingFrustum Everything() =>
        Slab(-100000f, 100000f);

    /// <summary>A frustum that is a horizontal slab, so a test can aim at a height range.</summary>
    static BoundingFrustum Slab(float low, float high) {
        var wide = Matrix4x4.Orthographic(1_000_000f, high - low, 0.1f, 1_000_000f);
        var at = Matrix4x4.LookAt(
            new(0f, (low + high) * 0.5f, -500_000f),
            new(0f, (low + high) * 0.5f, 0f),
            Vector3.UnitY
        );

        return new(at * wide);
    }
}
