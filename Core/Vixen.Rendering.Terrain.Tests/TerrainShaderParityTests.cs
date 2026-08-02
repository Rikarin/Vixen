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
    static string Source() {
        var directory = AppContext.BaseDirectory;

        // Walk up to the repository root, which is the directory holding Raven/. The test binary
        // lives several levels below it and the depth differs between configurations.
        for (var at = new DirectoryInfo(directory); at is not null; at = at.Parent) {
            var candidate = Path.Combine(at.FullName, "Raven", "Library", "Terrain", "Terrain.rvn");

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/Terrain/Terrain.rvn was not found above {directory}.");
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
        Assert.Equal([("origin", "float2"), ("step", "float"), ("morph", "float")], fields);
        Assert.Equal(16, TerrainNodeRecord.SizeInBytes);
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
}
