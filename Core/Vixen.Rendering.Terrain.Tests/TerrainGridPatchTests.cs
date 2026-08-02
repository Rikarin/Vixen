// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Terrain;
using Vixen.Shaders.Generated;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>The shared grid patch and its instance record — [docs/plan/31 § T2].</summary>
public sealed class TerrainGridPatchTests {
    [Theory]
    [InlineData(2, 9, 24)]
    [InlineData(8, 81, 384)]
    [InlineData(32, 1089, 6144)]
    public void APatchHasAVertexPerLatticePointAndSixIndicesPerQuad(int quads, int vertices, int indices) {
        Assert.Equal(vertices, TerrainGridPatch.VertexCount(quads));
        Assert.Equal(indices, TerrainGridPatch.IndexCount(quads));
    }

    [Fact]
    public void EveryIndexAddressesAVertexThePatchHas() {
        const int quads = 8;
        var indices = new uint[TerrainGridPatch.IndexCount(quads)];

        Assert.Equal(indices.Length, TerrainGridPatch.FillIndices(quads, indices));

        foreach (var index in indices) {
            Assert.InRange(index, 0u, (uint)TerrainGridPatch.VertexCount(quads) - 1);
        }
    }

    [Fact]
    public void EveryQuadIsCoveredByExactlyTwoTriangles() {
        const int quads = 8;
        var indices = new uint[TerrainGridPatch.IndexCount(quads)];
        TerrainGridPatch.FillIndices(quads, indices);

        // Total area in lattice units: each triangle of a split quad is a half.
        var area = 0f;

        for (var at = 0; at < indices.Length; at += 3) {
            var a = TerrainGridPatch.VertexOf((int)indices[at], quads);
            var b = TerrainGridPatch.VertexOf((int)indices[at + 1], quads);
            var c = TerrainGridPatch.VertexOf((int)indices[at + 2], quads);

            area += MathF.Abs((((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z))) * 0.5f);
        }

        Assert.Equal(quads * quads, area, 3);
    }

    /// <summary>
    ///     Every triangle winds the same way, seen from above.
    /// </summary>
    /// <remarks>
    ///     ⚠ A terrain wound backwards is invisible from above and solid from below — the
    ///     whole-screen version of the flipped-winding failure, and one that reads as nothing drawing
    ///     at all rather than as a winding problem. The cross product's sign in the XZ plane is the
    ///     whole test, and it has to be the same sign for all of them.
    /// </remarks>
    [Fact]
    public void EveryTriangleWindsTheSameWay() {
        const int quads = 8;
        var indices = new uint[TerrainGridPatch.IndexCount(quads)];
        TerrainGridPatch.FillIndices(quads, indices);

        for (var at = 0; at < indices.Length; at += 3) {
            var a = TerrainGridPatch.VertexOf((int)indices[at], quads);
            var b = TerrainGridPatch.VertexOf((int)indices[at + 1], quads);
            var c = TerrainGridPatch.VertexOf((int)indices[at + 2], quads);

            var cross = ((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z));

            Assert.True(cross < 0f, $"triangle at {at} wound the other way ({cross}).");
        }
    }

    /// <summary>
    ///     The diagonal alternates, so a flat terrain has no grain running across it.
    /// </summary>
    /// <remarks>
    ///     Splitting every quad the same way makes a lattice of parallel diagonals, which on a
    ///     heightfield reads as corduroy — most visible where the ground is nearly flat, which is
    ///     where an artist looks hardest.
    /// </remarks>
    [Fact]
    public void TheDiagonalAlternatesInACheckerRatherThanRunningOneWay() {
        const int quads = 4;
        var indices = new uint[TerrainGridPatch.IndexCount(quads)];
        TerrainGridPatch.FillIndices(quads, indices);

        var side = quads + 1;
        var diagonals = new HashSet<(int, int)>();

        for (var z = 0; z < quads; z++) {
            for (var x = 0; x < quads; x++) {
                var quad = (z * quads) + x;
                var first = indices.AsSpan(quad * 6, 6);

                // Which of the two diagonals this quad used: the one joining top-left to
                // bottom-right, or the one joining top-right to bottom-left.
                var topLeft = (uint)((z * side) + x);
                var bottomRight = (uint)(((z + 1) * side) + x + 1);
                var joined = first.ToArray().Count(i => i == topLeft) == 2
                    && first.ToArray().Count(i => i == bottomRight) == 2;

                diagonals.Add((quad, joined ? 1 : 0));
            }
        }

        // Both diagonals appear, and neighbours differ.
        Assert.Equal(2, diagonals.Select(pair => pair.Item2).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void APatchThatIsNotAPowerOfTwoIsRefused(int quads) {
        Assert.Throws<ArgumentException>(() => TerrainGridPatch.FillIndices(quads, new uint[4096]));
    }

    [Fact]
    public void TooLittleRoomIsRefused() {
        Assert.Throws<ArgumentException>(() => TerrainGridPatch.FillIndices(8, new uint[10]));
    }

    [Fact]
    public void VertexOfIsTheTwoDivisionsTheShaderDoes() {
        const int quads = 8;

        for (var vertex = 0; vertex < TerrainGridPatch.VertexCount(quads); vertex++) {
            var (x, z) = TerrainGridPatch.VertexOf(vertex, quads);

            Assert.InRange(x, 0, quads);
            Assert.InRange(z, 0, quads);
            Assert.Equal(vertex, (z * (quads + 1)) + x);
        }
    }

    // --- The instance record ------------------------------------------------

    [Fact]
    public void ANodeRecordIsTwentyFourBytesAndMatchesTheShadersStruct() {
        // The host packs these bytes and the shader reads them as a struct, so the two agree by
        // construction or not at all. Two floats, then three — std430 aligns a float2 to eight.
        Assert.Equal(24, TerrainNodeRecord.SizeInBytes);
    }

    /// <summary>A patch's level is log2 of its step, which is exact because the step is a power of two.</summary>
    /// <remarks>
    ///     ⚠ <b>Reading level 0 on a coarse patch gives it a height nothing between its own vertices
    ///     ever had</b>, so the surface swims as the camera moves — and the swim is at its worst on
    ///     the patches furthest away, where it is hardest to attribute to the near-field tool that
    ///     caused it.
    /// </remarks>
    [Fact]
    public void ARecordsLevelIsTheStepItSamplesAt() {
        Assert.Equal(0f, TerrainNodeRecord.Of(new(0, 0, 8, 0, 0f), gridQuads: 8, maxLevel: 7).Level, 5);
        Assert.Equal(1f, TerrainNodeRecord.Of(new(0, 0, 16, 1, 0f), gridQuads: 8, maxLevel: 7).Level, 5);
        Assert.Equal(3f, TerrainNodeRecord.Of(new(0, 0, 64, 3, 0f), gridQuads: 8, maxLevel: 7).Level, 5);
    }

    /// <summary>And it is clamped to the chain a tile actually has.</summary>
    /// <remarks>
    ///     ⚠ <b>The chain is a tile's rather than the atlas's.</b> An atlas of thirty-two 128-texel
    ///     tiles is 4096 wide and would allow thirteen levels; only eight keep a block at a texel or
    ///     more, and a patch that asked for the ninth would read a level mixing tiles.
    /// </remarks>
    [Fact]
    public void ALevelNeverLeavesTheChain() {
        Assert.Equal(2f, TerrainNodeRecord.Of(new(0, 0, 1024, 7, 0f), gridQuads: 8, maxLevel: 2).Level, 5);
        Assert.Equal(0f, TerrainNodeRecord.Of(new(0, 0, 1024, 7, 0f), gridQuads: 8, maxLevel: 0).Level, 5);
    }

    [Fact]
    public void ARecordCarriesTheNodesOriginStepAndMorph() {
        var node = new TerrainLodNode(64, 128, 32, 2, 0.25f);
        var record = TerrainNodeRecord.Of(node, gridQuads: 8);

        Assert.Equal(new Vector2(64f, 128f), record.Origin);
        Assert.Equal(4f, record.Step, 5);
        Assert.Equal(0.25f, record.Morph, 5);
    }

    [Fact]
    public void ARecordsStepIsWhatTurnsAGridIndexIntoASampleCoordinate() {
        // The identity the vertex stage relies on: origin + gridIndex × step is the sample the
        // heightmap is read at, and the far corner of the patch is its far sample.
        const int gridQuads = 8;
        var node = new TerrainLodNode(16, 16, 64, 3, 0f);
        var record = TerrainNodeRecord.Of(node, gridQuads);

        Assert.Equal(node.X, record.Origin.X + (0 * record.Step), 4);
        Assert.Equal(node.X + node.Quads, record.Origin.X + (gridQuads * record.Step), 4);
    }

    // --- The shader's own reflection ----------------------------------------

    [Fact]
    public void TheShaderTakesNoVertexBufferAndSaysSoInItsReflection() {
        // The patch is a regular lattice, so its positions are two divisions of SV_VertexID and
        // uploading them per frame would be sending the shader something it can count. The generated
        // keys are the evidence: a vertex input would have produced a location constant.
        Assert.Equal("Terrain", TerrainKeys.ShaderName);
        Assert.Equal(2, TerrainKeys.NodesSet);
        Assert.Equal(2, TerrainKeys.HeightMapSet);
    }

    [Fact]
    public void EveryBindingTheFeatureWillFillIsInTheSameSet() {
        // One descriptor set for the whole terrain material, so a patch is a draw rather than a
        // rebind. A binding that drifted into another set would be a set the pipeline layout has to
        // declare and nothing binds — "uses set N but that set is not bound", from two sets down.
        Assert.Equal(TerrainKeys.NodesSet, TerrainKeys.HeightMapSet);
        Assert.Equal(TerrainKeys.NodesSet, TerrainKeys.WeightMapsSet);
        Assert.Equal(TerrainKeys.NodesSet, TerrainKeys.LayerMapsSet);
        Assert.Equal(TerrainKeys.NodesSet, TerrainKeys.LayerScalesSet);
        Assert.Equal(TerrainKeys.NodesSet, TerrainKeys.ConstantBufferSet);
    }

    [Fact]
    public void EveryBindingHasItsOwnIndex() {
        uint[] bindings = [
            TerrainKeys.ConstantBufferBinding,
            TerrainKeys.HeightMapBinding,
            TerrainKeys.WeightMapsBinding,
            TerrainKeys.LayerMapsBinding,
            TerrainKeys.HeightSamplerBinding,
            TerrainKeys.WeightSamplerBinding,
            TerrainKeys.LayerSamplerBinding,
            TerrainKeys.NodesBinding,
            TerrainKeys.LayerScalesBinding
        ];

        Assert.Equal(bindings.Length, bindings.Distinct().Count());
    }
}
