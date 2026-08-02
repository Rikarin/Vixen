// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>
///     CDLOD node selection and the vertex morph — [docs/plan/31 § D3] and [§ T2].
/// </summary>
/// <remarks>
///     ⚠ <b>Written before the renderer, which [§ Part 4] asks for in as many words.</b> A crack
///     found by eye is found in a screenshot at one camera position, attributed to the wrong thing,
///     and worked around with a skirt — which is how terrain renderers acquire skirts they do not
///     need and then keep them for ever. The two properties that decide whether a skirt is needed are
///     functions of the morph, so they are unit tests and they exist first.
/// </remarks>
public sealed class TerrainLodTests {
    static TerrainDescription Shape(int tiles = 4) =>
        TerrainDescription.Default with {
            TileSamples = 128, TilesX = tiles, TilesZ = tiles,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static Terrain Flat(int tiles = 4) => new(Shape(tiles), height: 0f);

    static TerrainLodTree Tree(Terrain terrain, int gridQuads = 8) =>
        new(terrain.Description, TerrainLodRanges.Default with { NearRange = 32f }, gridQuads);

    /// <summary>A frustum that contains everything, so selection is not confounded by culling.</summary>
    static BoundingFrustum Everything() {
        var view = Matrix4x4.LookAt(new(0f, 5_000f, 0f), Vector3.Zero, new(0f, 0f, 1f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI * 0.9f, 1f, 0.1f, 100_000f);
        return new(view * projection);
    }

    static List<TerrainLodNode> Select(TerrainLodTree tree, Terrain terrain, Vector3 view) {
        var nodes = new List<TerrainLodNode>();
        tree.Select(view, Everything(), terrain, nodes);
        return nodes;
    }

    // --- The morph ----------------------------------------------------------

    /// <summary>
    ///     At full morph a patch has exactly its parent's resolution.
    /// </summary>
    /// <remarks>
    ///     The whole no-crack argument in one assertion. Every odd vertex has arrived at its even
    ///     neighbour, so the patch's distinct positions are the even ones — which are its parent's
    ///     vertices. A boundary between the two levels therefore has nothing to leak through.
    /// </remarks>
    [Fact]
    public void AtFullMorphEveryOddVertexHasArrivedAtItsEvenNeighbour() {
        for (var index = 0; index <= 32; index++) {
            var morphed = TerrainLodTree.MorphIndex(index, 1f);
            Assert.Equal(index - (index & 1), morphed, 5);
            Assert.Equal(0, (int)morphed % 2);
        }
    }

    [Fact]
    public void AtNoMorphNothingMoves() {
        for (var index = 0; index <= 32; index++) {
            Assert.Equal(index, TerrainLodTree.MorphIndex(index, 0f), 5);
        }
    }

    [Fact]
    public void TheMorphIsContinuousAndMonotonicInBetween() {
        for (var index = 0; index <= 16; index++) {
            var previous = (float)index;

            for (var step = 0; step <= 100; step++) {
                var value = TerrainLodTree.MorphIndex(index, step / 100f);

                Assert.True(value <= previous + 1e-5f, $"index {index} moved forwards at {step}.");
                Assert.True(previous - value < 0.02f, $"index {index} jumped at {step}.");
                previous = value;
            }

            Assert.Equal(index - (index & 1), previous, 5);
        }
    }

    /// <summary>
    ///     Two adjacent patches at different levels agree along the edge they share.
    /// </summary>
    /// <remarks>
    ///     <b>The test this whole class exists for.</b> The finer patch is at the far end of its band,
    ///     so it is fully morphed; the coarser one has just taken over, so it is not morphed at all.
    ///     Every vertex the finer patch puts on the shared edge must land exactly on a vertex the
    ///     coarser patch has — otherwise there is a gap, and a renderer that found it by eye would
    ///     grow a skirt to hide it.
    /// </remarks>
    [Fact]
    public void AFinePatchsSharedEdgeLandsExactlyOnItsCoarseNeighboursVertices() {
        var terrain = Flat();
        var tree = Tree(terrain, gridQuads: 8);

        // Level 1 spans 16 quads, level 2 spans 32. Put them side by side along X, sharing x = 16.
        var fine = new TerrainLodNode(0, 0, 16, 1, 1f);
        var coarse = new TerrainLodNode(16, 0, 32, 2, 0f);

        // Every position the coarse patch has along its near edge.
        var coarseEdge = new HashSet<float>();

        for (var grid = 0; grid <= tree.GridQuads; grid++) {
            coarseEdge.Add(MathF.Round(tree.SampleOf(coarse, 0, grid).Y, 4));
        }

        // Every position the fine patch puts on the same edge must be one of them.
        for (var grid = 0; grid <= tree.GridQuads; grid++) {
            var sample = tree.SampleOf(fine, tree.GridQuads, grid);

            Assert.Equal(16f, sample.X, 4);
            Assert.Contains(MathF.Round(sample.Y, 4), coarseEdge);
        }
    }

    [Fact]
    public void AFullyMorphedPatchHasHalfItsResolution() {
        var terrain = Flat();
        var tree = Tree(terrain, gridQuads: 8);

        var node = new TerrainLodNode(0, 0, 16, 1, 1f);
        var distinct = new HashSet<float>();

        for (var grid = 0; grid <= tree.GridQuads; grid++) {
            distinct.Add(MathF.Round(tree.SampleOf(node, grid, 0).X, 4));
        }

        // Nine vertices collapse to five distinct positions, which is the parent's grid.
        Assert.Equal((tree.GridQuads / 2) + 1, distinct.Count);
    }

    [Fact]
    public void APatchsVertexMovesContinuouslyAsTheMorphRuns() {
        var terrain = Flat();
        var tree = Tree(terrain, gridQuads: 8);

        // The worst case is an odd vertex of a coarse patch, which travels the furthest.
        var previous = tree.SampleOf(new(0, 0, 32, 2, 0f), 3, 3);

        for (var step = 1; step <= 200; step++) {
            var node = new TerrainLodNode(0, 0, 32, 2, step / 200f);
            var sample = tree.SampleOf(node, 3, 3);

            Assert.True(
                Vector2.Distance(previous, sample) < 0.2f,
                $"the vertex jumped at morph {step / 200f}."
            );

            previous = sample;
        }
    }

    // --- The ranges ---------------------------------------------------------

    /// <summary>
    ///     A level is fully morphed exactly where the next level takes over.
    /// </summary>
    /// <remarks>
    ///     The identity the no-crack property rests on. If a level's morph ended anywhere short of
    ///     its range, there would be a band of distances in which it is selected, adjacent to a
    ///     coarser node, and not degenerate — which is a crack.
    /// </remarks>
    [Fact]
    public void EachLevelIsFullyMorphedExactlyWhereTheNextOneBegins() {
        var ranges = TerrainLodRanges.Default;

        for (var level = 0; level < ranges.LevelCount - 1; level++) {
            Assert.Equal(ranges.RangeOf(level), ranges.MorphEndOf(level), 4);
            Assert.Equal(1f, ranges.MorphOf(level, ranges.RangeOf(level)), 5);
            Assert.Equal(ranges.RangeOf(level), ranges.BandStartOf(level + 1), 4);
        }
    }

    [Fact]
    public void AMorphStartsInsideItsOwnBandAndNotBefore() {
        var ranges = TerrainLodRanges.Default;

        for (var level = 0; level < ranges.LevelCount; level++) {
            var start = ranges.MorphStartOf(level);

            Assert.True(start >= ranges.BandStartOf(level), $"level {level} morphs before its band.");
            Assert.True(start < ranges.MorphEndOf(level), $"level {level} has no band to morph in.");

            Assert.Equal(0f, ranges.MorphOf(level, start), 5);
            Assert.Equal(0f, ranges.MorphOf(level, ranges.BandStartOf(level)), 5);
        }
    }

    [Fact]
    public void TheMorphIsMonotonicAcrossTheWholeBand() {
        var ranges = TerrainLodRanges.Default;
        var previous = -1f;

        for (var step = 0; step <= 400; step++) {
            var value = ranges.MorphOf(2, step / 400f * ranges.RangeOf(2) * 1.2f);

            Assert.InRange(value, 0f, 1f);
            Assert.True(value >= previous, $"the morph went backwards at {step}.");
            previous = value;
        }

        Assert.Equal(1f, previous, 5);
    }

    [Fact]
    public void RangesDoubleWithTheLevel() {
        var ranges = TerrainLodRanges.Default with { NearRange = 50f };

        Assert.Equal(50f, ranges.RangeOf(0));
        Assert.Equal(100f, ranges.RangeOf(1));
        Assert.Equal(800f, ranges.RangeOf(4));
    }

    [Theory]
    [InlineData(0f, 5, 0.5f)]
    [InlineData(-1f, 5, 0.5f)]
    [InlineData(64f, 0, 0.5f)]
    [InlineData(64f, 5, 1f)]
    [InlineData(64f, 5, 1.5f)]
    [InlineData(64f, 5, -0.1f)]
    public void RangesThatCannotWorkAreRefused(float near, int levels, float ratio) {
        var ranges = new TerrainLodRanges {
            NearRange = near, LevelCount = levels, MorphStartRatio = ratio
        };

        Assert.NotNull(ranges.Validate());
    }

    /// <summary>
    ///     A morph ratio of one is refused, because it is a crack at every transition.
    /// </summary>
    [Fact]
    public void AMorphRatioOfOneIsRefusedWithTheReasonSpelledOut() {
        var ranges = TerrainLodRanges.Default with { MorphStartRatio = 1f };
        var reason = ranges.Validate();

        Assert.NotNull(reason);
        Assert.Contains("crack", reason, StringComparison.Ordinal);
    }

    // --- Selection ----------------------------------------------------------

    /// <summary>
    ///     The chosen patches tile the terrain exactly once.
    /// </summary>
    /// <remarks>
    ///     A tiling with a gap is a hole in the ground; a tiling with an overlap is z-fighting. Both
    ///     are quiet, and both are a counting argument rather than a picture — so this counts quads.
    /// </remarks>
    [Fact]
    public void TheChosenPatchesTileTheTerrainExactlyOnce() {
        var terrain = Flat(tiles: 4);
        var tree = Tree(terrain, gridQuads: 8);

        foreach (var view in new[] {
            new Vector3(0f, 10f, 0f),
            new Vector3(254f, 10f, 254f),
            new Vector3(120f, 400f, 200f)
        }) {
            var nodes = Select(tree, terrain, view);
            var covered = new HashSet<(int, int)>();

            foreach (var node in nodes) {
                for (var z = node.Z; z < node.Z + node.Quads; z++) {
                    for (var x = node.X; x < node.X + node.Quads; x++) {
                        Assert.True(covered.Add((x, z)), $"quad ({x}, {z}) was covered twice.");
                    }
                }
            }

            // Every quad of the terrain, and nothing outside it.
            var quadsX = terrain.Description.SamplesX - 1;
            var quadsZ = terrain.Description.SamplesZ - 1;

            for (var z = 0; z < quadsZ; z++) {
                for (var x = 0; x < quadsX; x++) {
                    Assert.True(covered.Contains((x, z)), $"quad ({x}, {z}) was not covered.");
                }
            }
        }
    }

    [Fact]
    public void PatchesNearTheViewAreFinerThanPatchesFarFromIt() {
        var terrain = Flat(tiles: 4);
        var tree = Tree(terrain, gridQuads: 8);

        var view = new Vector3(0f, 2f, 0f);
        var nodes = Select(tree, terrain, view);

        Assert.NotEmpty(nodes);

        TerrainLodNode Nearest() {
            var best = nodes[0];
            var bestDistance = float.MaxValue;

            foreach (var node in nodes) {
                var distance = new Vector2(node.X, node.Z).Length();

                if (distance < bestDistance) {
                    bestDistance = distance;
                    best = node;
                }
            }

            return best;
        }

        var near = Nearest();
        var far = nodes.MaxBy(node => new Vector2(node.X, node.Z).Length());

        Assert.True(near.Level < far.Level, $"near was level {near.Level}, far was {far.Level}.");
        Assert.True(near.Quads < far.Quads);
    }

    [Fact]
    public void EveryChosenPatchIsInsideTheTerrain() {
        var terrain = Flat(tiles: 3);
        var tree = Tree(terrain, gridQuads: 8);

        // Three tiles of 127 quads is 381, which is not a power of two patches — so the root hangs
        // off the far edge and the nodes out there must be skipped rather than clamped.
        foreach (var node in Select(tree, terrain, new(100f, 10f, 100f))) {
            Assert.True(node.X < terrain.Description.SamplesX - 1);
            Assert.True(node.Z < terrain.Description.SamplesZ - 1);
            Assert.InRange(node.Level, 0, tree.DepthCount - 1);
            Assert.InRange(node.Morph, 0f, 1f);
        }
    }

    [Fact]
    public void SelectionIsDeterministic() {
        var terrain = Flat();
        var tree = Tree(terrain);
        var view = new Vector3(90f, 20f, 140f);

        Assert.Equal(Select(tree, terrain, view), Select(tree, terrain, view));
    }

    [Fact]
    public void ANarrowFrustumSelectsFewerPatchesThanAWideOne() {
        var terrain = Flat(tiles: 4);
        var tree = Tree(terrain, gridQuads: 8);
        var view = new Vector3(128f, 60f, 128f);

        var wide = Select(tree, terrain, view);

        var narrowView = Matrix4x4.LookAt(view, new(200f, 0f, 128f), new(0f, 1f, 0f));
        var narrowProjection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 12f, 1f, 0.1f, 400f);

        var narrow = new List<TerrainLodNode>();
        tree.Select(view, new(narrowView * narrowProjection), terrain, narrow);

        Assert.True(narrow.Count < wide.Count, $"narrow chose {narrow.Count}, wide chose {wide.Count}.");
        Assert.NotEmpty(narrow);
    }

    [Fact]
    public void ADeeperTreeThanTheTerrainNeedsIsNotAnError() {
        // A range list is a project setting shared by terrains of different sizes, so having more
        // levels than a small terrain can use is ordinary rather than a misconfiguration.
        var terrain = new Terrain(Shape(tiles: 1));
        var tree = new TerrainLodTree(
            terrain.Description,
            TerrainLodRanges.Default with { LevelCount = 20 },
            gridQuads: 32
        );

        Assert.True(tree.DepthCount <= 20);
        Assert.NotEmpty(Select(tree, terrain, new(10f, 10f, 10f)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void AGridPatchThatIsNotAPowerOfTwoIsRefused(int gridQuads) {
        var terrain = Flat();

        Assert.Throws<ArgumentException>(
            () => new TerrainLodTree(terrain.Description, TerrainLodRanges.Default, gridQuads)
        );
    }

    // --- Positions ----------------------------------------------------------

    [Fact]
    public void APatchsVertexReadsTheHeightUnderIt() {
        var terrain = new Terrain(Shape(tiles: 1), height: 12f);
        var tree = Tree(terrain, gridQuads: 8);

        var node = new TerrainLodNode(0, 0, 32, 1, 0f);
        var position = tree.PositionOf(node, 4, 4, terrain);

        Assert.Equal(16f, position.X, 3);
        Assert.Equal(16f, position.Z, 3);
        Assert.Equal(12f, position.Y, 2);
    }

    /// <summary>
    ///     A morphed vertex reads the heightmap bilinearly, so the surface does not step.
    /// </summary>
    /// <remarks>
    ///     Snapping a morphed vertex to the nearest sample would make the surface jump by up to one
    ///     sample's height as the camera moves — the pop the morph exists to remove, reintroduced by
    ///     the thing that reads it.
    /// </remarks>
    [Fact]
    public void AMorphingVertexsHeightMovesSmoothly() {
        var terrain = new Terrain(Shape(tiles: 1));
        var layer = terrain.AddLayer("Ridge");

        // A ramp along X, so a vertex sliding along X has a height that must slide with it.
        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                layer.SetDelta(x, z, (short)(x * 100));
            }
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        var tree = Tree(terrain, gridQuads: 8);
        var previous = tree.PositionOf(new(0, 0, 32, 1, 0f), 3, 3, terrain).Y;

        for (var step = 1; step <= 100; step++) {
            var height = tree.PositionOf(new(0, 0, 32, 1, step / 100f), 3, 3, terrain).Y;

            Assert.True(MathF.Abs(height - previous) < 0.5f, $"the height stepped at morph {step}.");
            previous = height;
        }
    }
}
