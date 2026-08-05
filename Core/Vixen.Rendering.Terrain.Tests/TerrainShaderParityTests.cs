// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     That <c>Terrain.rvn</c> still contains the arithmetic <c>TerrainLodTree</c> defines.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap <see cref="Vixen.Rendering.GpuCulling" />'s remarks name, closed the same
///         way.</b> A CPU mirror says whether the arithmetic the device will run is the arithmetic
///         the engine means; what it cannot say is whether the shader <em>still contains</em> that
///         arithmetic. `GpuVisibilityGroupTests` answers that with a source assertion and so does
///         this — because the alternative is a device, and the property being checked is one the
///         no-crack tests in `Vixen.Terrain` have already established as arithmetic.
///     </para>
///     <para>
///         ⚠ <b>A source assertion is weaker than an execution and is chosen knowing that.</b> It
///         catches the failure that actually happens — somebody edits the shader's morph, or deletes
///         it, and every level boundary opens — and it does not catch a subtly different but
///         similar-looking expression. The golden image in [docs/plan/31 § Part 4] is what catches
///         that, and it needs a GPU.
///     </para>
/// </remarks>
public sealed class TerrainShaderParityTests {
    static string Source() => Source(Path.Combine("Terrain", "Terrain.rvn"));

    static string Source(string relative) {
        var directory = AppContext.BaseDirectory;

        // Walk up to the repository root, which is the directory holding Raven/. The test binary
        // lives several levels below it and the depth differs between configurations.
        for (var at = new DirectoryInfo(directory); at is not null; at = at.Parent) {
            var candidate = Path.Combine(at.FullName, "Raven", "Library", relative);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{relative} was not found above {directory}.");
    }

    [Fact]
    public void TheShaderStillMorphsOddIndicesOntoTheirEvenNeighbour() {
        var source = Source();

        // `float(gridX) - float(gridX % 2) * node.morph`, whitespace-insensitively — the expression
        // TerrainLodTree.MorphIndex is, written in the shader's types.
        var morph = new Regex(
            @"float\(\s*grid([XZ])\s*\)\s*-\s*float\(\s*grid\1\s*%\s*2\s*\)\s*\*\s*node\.morph",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        var matches = morph.Matches(source);

        Assert.True(
            matches.Count == 2,
            $"expected the morph on both axes; found {matches.Count}. If the shader was refactored, "
            + "check it still degenerates a patch onto its parent's grid and update this pattern — "
            + "the no-crack property in Vixen.Terrain's TerrainLodTests is what it has to preserve."
        );
    }

    [Fact]
    public void TheShaderReadsTheHeightmapWithAnExplicitLevel() {
        // A vertex stage has no derivatives, so `Sample` outside a fragment stage never meant what it
        // looked like — SPIR-V was quietly substituting level zero. docs/plan/07 records this as the
        // reason SampleLevel landed, and a terrain is the shader that motivated it.
        var source = Source();

        Assert.Contains("heightMap.SampleLevel(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("heightMap.Sample(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShadersNodeStructIsTheOneTheHostPacks() {
        var source = Source();
        var start = source.IndexOf("struct TerrainNode", StringComparison.Ordinal);

        Assert.True(start >= 0, "the shader has no TerrainNode struct.");

        var body = source[start..source.IndexOf('}', start)];
        var fields = new Regex(@"var\s+(\w+)\s*:\s*(\w+)", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Matches(body)
            .Select(match => (Name: match.Groups[1].Value, Type: match.Groups[2].Value))
            .ToArray();

        // Declaration order is the byte order, and the host writes these bytes.
        Assert.Equal(
            [
                ("origin", "float2"),
                ("step", "float"),
                ("morph", "float"),
                ("level", "float"),
                ("padding0", "float")
            ],
            fields
        );

        Assert.Equal(24, TerrainNodeRecord.SizeInBytes);
    }

    /// <summary>The shader still reads the atlas the host packs.</summary>
    /// <remarks>
    ///     ⚠ <b>An atlas coordinate that is off by a block draws a terrain made of the wrong
    ///     tiles</b>, which reads as a corrupt heightmap rather than as an arithmetic error — and the
    ///     two expressions have to be one expression written twice.
    /// </remarks>
    [Fact]
    public void TheShaderStillReadsTheAtlasTheHostPacks() {
        var source = Source();

        // The tile is floor(sample / tileQuads), clamped — `TerrainAtlas.Locate`'s division.
        Assert.Matches(new Regex(@"floor\(sample\s*/\s*tileQuads\)"), source);

        // And the texel is that tile's block plus the local offset, at the texel's centre.
        Assert.Matches(new Regex(@"tile\s*\*\s*tileSamples\s*\+\s*local"), source);
        Assert.Matches(new Regex(@"float2\(0\.5f,\s*0\.5f\)\)\s*/\s*heightMapSize"), source);
    }

    /// <summary>A patch reads its own level rather than level zero.</summary>
    /// <remarks>
    ///     ⚠ <b>Reading level 0 on a coarse patch gives it a height nothing between its own vertices
    ///     ever had</b>, so the surface swims as the camera moves — worst on the patches furthest
    ///     away, where it is hardest to attribute.
    /// </remarks>
    [Fact]
    public void TheShaderStillSamplesTheNodesOwnLevel() {
        var source = Source();

        Assert.Matches(new Regex(@"Height\(sample,\s*node\.level\)"), source);
        Assert.Matches(new Regex(@"SampleLevel\(heightSampler,\s*AtlasUv\(sample\),\s*level\)"), source);
    }

    /// <summary>And the weights are taken with the packed coordinate's derivatives.</summary>
    /// <remarks>
    ///     ⚠ <b>An atlas coordinate jumps by a whole block at every tile boundary</b>, so the
    ///     hardware's own derivative there is enormous and it picks the coarsest level it has — which
    ///     draws as a dark line one pixel wide along every tile edge, on every terrain, and reads as a
    ///     crack in the mesh.
    /// </remarks>
    [Fact]
    public void TheShaderStillTakesItsWeightDerivativesFromThePackedCoordinate() {
        var source = Source();

        Assert.Matches(new Regex(@"SampleGrad\("), source);
        Assert.Matches(new Regex(@"ddx\(sampleCoord\)"), source);
        Assert.Matches(new Regex(@"ddy\(sampleCoord\)"), source);
    }

    /// <summary>
    ///     The grid patch's side is a permutation, so the morph's divisions fold at compile time.
    /// </summary>
    /// <remarks>
    ///     A vertex stage that read the patch size from a uniform would do two integer divisions per
    ///     vertex on a value that is the same for the whole frame. As a permutation constant they
    ///     fold, which for a power of two is a shift and a mask.
    /// </remarks>
    [Fact]
    public void TheGridSizeIsAPermutationRatherThanAUniform() {
        var source = Source();

        Assert.Contains("[Permutation] val GridQuads: int", source, StringComparison.Ordinal);
        Assert.Equal(
            TerrainLodTree.DefaultGridQuads,
            int.Parse(
                new Regex(@"\[Permutation\] val GridQuads: int = (\d+)", RegexOptions.None, TimeSpan.FromSeconds(5))
                    .Match(source)
                    .Groups[1]
                    .Value
            )
        );
    }

    /// <summary>
    ///     The shader's morph, transliterated, agrees with the kernel's over every index and morph.
    /// </summary>
    /// <remarks>
    ///     The other half of the bargain, and the one a source assertion cannot make: the pattern
    ///     above says the expression is there, and this says the expression is <em>right</em>. It is a
    ///     transliteration rather than an execution, which is exactly what
    ///     <see cref="Vixen.Rendering.GpuCulling.IsVisible" /> is for its own shader.
    /// </remarks>
    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    public void TheTransliteratedShaderMorphEqualsTheKernels(int gridQuads) {
        for (var vertex = 0; vertex < TerrainGridPatch.VertexCount(gridQuads); vertex++) {
            var (gridX, gridZ) = TerrainGridPatch.VertexOf(vertex, gridQuads);

            for (var step = 0; step <= 20; step++) {
                var morph = step / 20f;

                // What Terrain.rvn computes, written in C#.
                var shaderX = gridX - ((gridX % 2) * morph);
                var shaderZ = gridZ - ((gridZ % 2) * morph);

                Assert.Equal(TerrainLodTree.MorphIndex(gridX, morph), shaderX, 5);
                Assert.Equal(TerrainLodTree.MorphIndex(gridZ, morph), shaderZ, 5);
            }
        }
    }

    // ------------------------------------------------------------------ the lit path

    /// <summary>The shadow bias is added, which under reverse-Z is toward the light.</summary>
    /// <remarks>
    ///     ⚠ <b>Subtracting it is the conventional-Z habit, and it answers backwards here</b>: near
    ///     maps to 1 and far to 0, so moving a receiver toward the light moves it numerically up.
    ///     The compare itself — <c>depth &gt;= stored</c> — lives in <c>Lighting.ShadowTap</c>,
    ///     which the lit ground calls rather than copies; the bias line is the half it owns.
    /// </remarks>
    [Fact]
    public void TheLitShadowBiasIsAddedNotSubtracted() {
        var shared = Source(Path.Combine("Terrain", "FrameLit.rvn"));

        Assert.Matches(new Regex(@"ndc\.z\s*\+\s*bias"), shared);
        Assert.DoesNotMatch(new Regex(@"ndc\.z\s*-\s*bias"), shared);

        // And the tap it hands that depth to is still the reverse-Z compare.
        var lighting = Source(Path.Combine("Shading", "Lighting.rvn"));

        Assert.Matches(new Regex(@"depth\s*>=\s*stored"), lighting);
    }

    /// <summary>The split normal plane takes the raw signed world normal, never an encode.</summary>
    /// <remarks>
    ///     ⚠ <b><c>SceneNormals</c> is raw signed in <c>Rgba16Float</c>, where zero is the sky.</b>
    ///     A <c>*0.5+0.5</c> encode is every downstream reader — the probe gather, both occlusion
    ///     passes, the combine — fed normals folded into one hemisphere, which draws as 16-pixel
    ///     squares on the floor rather than as anything traceable to a normal.
    /// </remarks>
    [Fact]
    public void TheSplitNormalIsRawAndSigned() {
        var terrain = Source();
        var grass = Source(Path.Combine("Terrain", "Grass.rvn"));

        Assert.Matches(new Regex(@"targets\.normal\s*=\s*float4\(n,"), terrain);
        Assert.Matches(new Regex(@"targets\.normal\s*=\s*float4\(n,"), grass);
        Assert.DoesNotContain("0.5f + 0.5f", terrain, StringComparison.Ordinal);
        Assert.DoesNotContain("0.5f + 0.5f", grass, StringComparison.Ordinal);
    }

    /// <summary>The transliterated froxel grid still has the culler's shape.</summary>
    /// <remarks>
    ///     <c>FrameClusters</c> is <c>ClusterGrid</c> written twice — the Terrain package cannot
    ///     import the pipeline package without dragging its unbound compose slots into the editor's
    ///     standalone compilation — and a fragment that derives its cluster differently reads the
    ///     list that was culled for somewhere else.
    /// </remarks>
    [Fact]
    public void TheTransliteratedClusterGridEqualsTheCullers() {
        var shared = Source(Path.Combine("Terrain", "FrameLit.rvn"));

        Assert.Contains($"const val TilesX = {Vixen.Rendering.ClusterGrid.TilesX}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val TilesY = {Vixen.Rendering.ClusterGrid.TilesY}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val Slices = {Vixen.Rendering.ClusterGrid.Slices}", shared, StringComparison.Ordinal);
        Assert.Contains($"const val Capacity = {Vixen.Rendering.ClusterGrid.Capacity}", shared, StringComparison.Ordinal);
        Assert.Contains($"indices: uint[{Vixen.Rendering.ClusterGrid.Capacity}]", shared, StringComparison.Ordinal);
    }

    /// <summary>The lit terrain returns to world space before it asks the frame anything.</summary>
    /// <remarks>
    ///     The placement rides <c>viewProjection</c>, so <c>positionWS</c> is terrain-local — and a
    ///     cascade matrix given a local position shadows the terrain with a copy of the world
    ///     standing at the origin, which reads as shadows sliding off by exactly the terrain's
    ///     placement.
    /// </remarks>
    [Fact]
    public void TheLitTerrainRestoresItsWorldPlacement() {
        Assert.Matches(new Regex(@"positionWS\s*\+\s*originWS"), Source());
    }
}
