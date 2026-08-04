// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Terrain;
using Xunit;

using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     That <c>GrassScatter.rvn</c> still scatters the field <c>GrassScatter</c> defines.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T6]'s third exit criterion, and it is the seam test [§ Part 4] asks
///         for.</b> Grass is derived, so there is no file to compare a device run against: if the
///         dispatch and the reference disagree, the field simply differs between a machine with a
///         compute queue and one without, and nobody can point at a wrong number. The hash is what
///         both halves are built on, so it is what this holds them to.
///     </para>
///     <para>
///         ⚠ <b>A transliteration and a source assertion, not an execution.</b> The transliteration
///         says the arithmetic is <em>right</em> — it computes what the shader computes, in C#, and
///         compares it to the kernel over thousands of candidates at zero drift. The source assertion
///         says the arithmetic is still <em>there</em>, which is the failure that actually happens:
///         somebody edits the shader's mixing constants and every cell scatters a different field.
///         What neither catches is a subtly different but similar-looking expression, and the golden
///         screenshot in [§ Part 4] is what catches that — it needs a GPU.
///     </para>
/// </remarks>
public sealed class GrassScatterParityTests {
    static string Source(string file) {
        var directory = AppContext.BaseDirectory;

        for (var at = new DirectoryInfo(directory); at is not null; at = at.Parent) {
            var candidate = Path.Combine(at.FullName, "Raven", "Library", "Terrain", file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/Terrain/{file} was not found above {directory}.");
    }

    /// <summary>The argument phase still clamps a cell's count to the run it may write.</summary>
    /// <remarks>
    ///     ⚠ <b>Not belt and braces.</b> <c>atomicAdd</c> hands a slot to every candidate that passed
    ///     the density test, including the ones that then returned because the run was full — so on a
    ///     cell whose weight is painted to one everywhere, <c>counts</c> is the number of
    ///     <em>candidates</em>, which is larger than the run. An unclamped instance count draws off the
    ///     end of the cell's blades and into the next cell's, which is a field of grass appearing
    ///     inside another one and moving every frame.
    /// </remarks>
    [Fact]
    public void TheArgumentWriteStillClampsToTheRun() {
        var source = Source("GrassScatter.rvn");

        Assert.Matches(new Regex(@"if \(drawn > cell\.capacity\) \{"), source);
        Assert.Matches(new Regex(@"command\.instanceCount = drawn"), source);
        Assert.Matches(new Regex(@"command\.firstInstance = cell\.first"), source);
    }

    /// <summary>Three places have to agree about where the ground stops, and now they do.</summary>
    /// <remarks>
    ///     ⚠ <b>The reference has always rejected holes and the scatter did not.</b>
    ///     <c>TerrainSurface</c> answers a miss over a missing quad, so a CPU-scattered field stopped
    ///     at a cave mouth and a device-scattered one grew a blade standing in it — a disagreement no
    ///     seam test over the hash could ever have seen, because the hash is the same either way.
    /// </remarks>
    [Fact]
    public void TheScatterAndTheDrawRejectHolesAtTheSameThreshold() {
        var scatter = Source("GrassScatter.rvn");
        var draw = Source("Terrain.rvn");

        Assert.Matches(new Regex(@"holeMap\.SampleLevel\(.*\)\.r > 0\.5f"), scatter);
        Assert.Matches(new Regex(@"holeMap\.SampleLevel\(.*\)\.r > 0\.5f"), draw);

        // And the scatter rejects before it samples the ground, because a candidate over a hole has
        // no surface to stand on whatever its slope and whatever is painted there.
        var hole = scatter.IndexOf("if (Hole(local))", StringComparison.Ordinal);
        var slope = scatter.IndexOf("if (slope > maxSlope)", StringComparison.Ordinal);

        Assert.True(hole >= 0, "the scatter no longer rejects holes.");
        Assert.True(hole < slope, "the scatter tests the slope before it tests for ground.");
    }

    /// <summary>The shader's <c>GrassMath.Hash</c>, written in C#.</summary>
    static uint ShaderHash(uint seed, int index) {
        var hash = seed ^ (uint)index;

        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;

        return hash;
    }

    /// <summary>And its <c>GrassMath.CellHash</c>.</summary>
    static uint ShaderCellHash(int cellX, int cellZ, int index) =>
        ShaderHash(((uint)cellX * 0x9E3779B1u) ^ ((uint)cellZ * 0x85EBCA77u), index);

    /// <summary>And its <c>GrassMath.Unit</c>, divisor included.</summary>
    static float ShaderUnit(uint hash, int channel) =>
        ShaderHash(hash ^ ((uint)channel * 0x27D4EB2u), channel) / 4294967296f;

    /// <summary>And its <c>GrassMath.DensityAt</c>.</summary>
    static float ShaderDensityAt(float weight, float low, float high) {
        var top = MathF.Max(high, low + 0.0001f);
        var t = Math.Clamp((weight - low) / (top - low), 0f, 1f);

        return t * t * (3f - (2f * t));
    }

    [Fact]
    public void TheTransliteratedShaderHashEqualsTheKernels() {
        for (var x = -4; x <= 4; x++) {
            for (var z = -4; z <= 4; z++) {
                for (var index = 0; index < 64; index++) {
                    Assert.Equal(GrassScatter.Hash(new(x, z), index), ShaderCellHash(x, z, index));
                }
            }
        }
    }

    /// <summary>Every stream both halves draw from, at zero drift.</summary>
    /// <remarks>
    ///     ⚠ <b>The divisor is the interesting one.</b> The host writes
    ///     <c>mixed / (float)uint.MaxValue</c>, and that cast is not 4294967295 — a <c>float</c> has
    ///     twenty-four bits of mantissa and rounds it up to 4294967296. A shader written with the
    ///     true maximum would agree to six decimal places and disagree in the last bits of every
    ///     draw, which is a field that is <em>almost</em> the same.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void TheTransliteratedShaderDrawsEqualTheKernels(int stream) {
        for (var index = 0; index < 512; index++) {
            var hash = GrassScatter.Hash(new(7, -3), index);

            Assert.Equal(FoliageScatter.Unit(hash, stream), ShaderUnit(hash, stream));
        }
    }

    [Fact]
    public void TheTransliteratedShaderDensityCurveEqualsTheKernels() {
        var type = GrassType.Of("Meadow") with { Layer = "Grass", MinWeight = 0.2f, MaxWeight = 0.8f };

        for (var step = 0; step <= 100; step++) {
            var weight = step / 100f;

            Assert.Equal(type.DensityAt(weight), ShaderDensityAt(weight, type.MinWeight, type.MaxWeight), 6);
        }
    }

    /// <summary>A collapsed weight range does not divide by zero on either side.</summary>
    /// <remarks>
    ///     An artist dragging both ends of a range onto the same number is an ordinary thing to do,
    ///     and GLSL leaves <c>smoothstep(e, e, x)</c> undefined — which is why the shader writes the
    ///     polynomial out with the same guard the host has rather than calling it.
    /// </remarks>
    [Fact]
    public void ACollapsedWeightRangeIsAStepOnBothSides() {
        var type = GrassType.Of("Cliff") with { Layer = "Rock", MinWeight = 0.5f, MaxWeight = 0.5f };

        Assert.Equal(0f, type.DensityAt(0.49f));
        Assert.Equal(1f, type.DensityAt(0.6f));
        Assert.Equal(type.DensityAt(0.6f), ShaderDensityAt(0.6f, 0.5f, 0.5f), 6);
    }

    /// <summary>The shader's candidate position is the kernel's, jitter included.</summary>
    [Fact]
    public void TheTransliteratedShaderCandidateEqualsTheKernels() {
        var type = GrassType.Of("Meadow") with { Density = 4f, Jitter = 0.8f };
        var grid = new FoliageCellGrid(32f);

        var side = type.GridOf(grid.CellSize);
        var step = grid.CellSize / side;
        var reach = type.Jitter * step * 0.5f;

        foreach (var cell in new FoliageCellKey[] { new(0, 0), new(-2, 5), new(11, -7) }) {
            for (var index = 0; index < side * side; index++) {
                var hash = ShaderCellHash(cell.X, cell.Z, index);

                var slotX = index % side;
                var slotZ = index / side;

                // What GrassScatter.rvn's Main computes.
                var x = (cell.X * grid.CellSize)
                    + ((slotX + 0.5f) * step)
                    + (((ShaderUnit(hash, 6) * 2f) - 1f) * reach);

                var z = (cell.Z * grid.CellSize)
                    + ((slotZ + 0.5f) * step)
                    + (((ShaderUnit(hash, 7) * 2f) - 1f) * reach);

                var kernel = GrassScatter.CandidateAt(type, cell, grid, index);

                Assert.Equal(kernel.X, x, 4);
                Assert.Equal(kernel.Y, z, 4);
            }
        }
    }

    [Fact]
    public void TheShaderStillHoldsTheMixingConstants() {
        var source = Source("GrassScatter.rvn");

        Assert.Contains("0x7FEB352Du", source, StringComparison.Ordinal);
        Assert.Contains("0x846CA68Bu", source, StringComparison.Ordinal);
        Assert.Contains("0x27D4EB2u", source, StringComparison.Ordinal);
        Assert.Contains($"const val CellSeedX = 0x{GrassScatter.CellSeedX:X8}u", source, StringComparison.Ordinal);
        Assert.Contains($"const val CellSeedZ = 0x{GrassScatter.CellSeedZ:X8}u", source, StringComparison.Ordinal);
        Assert.Contains("const val UnitScale = 4294967296f", source, StringComparison.Ordinal);
    }

    /// <summary>The shader draws each stream from the number the kernel names.</summary>
    [Fact]
    public void TheShadersStreamsAreTheKernelsStreams() {
        var source = Source("GrassScatter.rvn");

        Assert.Contains($"const val JitterXStream = {GrassScatter.JitterXStream}", source, StringComparison.Ordinal);
        Assert.Contains($"const val JitterZStream = {GrassScatter.JitterZStream}", source, StringComparison.Ordinal);
        Assert.Contains($"const val DensityStream = {GrassScatter.DensityStream}", source, StringComparison.Ordinal);
        Assert.Contains($"const val TintStream = {GrassScatter.TintStream}", source, StringComparison.Ordinal);
        Assert.Contains($"const val PhaseStream = {GrassScatter.PhaseStream}", source, StringComparison.Ordinal);
    }

    /// <summary>The scatter still hashes the cell rather than counting.</summary>
    /// <remarks>
    ///     The failure this catches is somebody replacing the hash with the invocation's linear index
    ///     — which produces a plausible-looking field, and a different one every time the dispatch
    ///     covers a different set of cells.
    /// </remarks>
    [Fact]
    public void TheShaderStillHashesTheCellCoordinate() {
        var source = Source("GrassScatter.rvn");

        var cellHash = new Regex(
            @"Hash\(\s*\(\s*uint\(\s*cellX\s*\)\s*\*\s*CellSeedX\s*\)\s*\^\s*\(\s*uint\(\s*cellZ\s*\)\s*\*\s*CellSeedZ\s*\)\s*,\s*index\s*\)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        Assert.True(
            cellHash.IsMatch(source),
            "GrassScatter.rvn no longer seeds its hash from the cell coordinate. If it was "
            + "refactored, check that a cell's field still depends on the cell and the slot and on "
            + "nothing else — GrassScatterTests.TheHashDependsOnTheCellAndTheSlotAndNothingElse is "
            + "the property it has to preserve."
        );
    }

    /// <summary>The shader's <c>AtlasUv</c>, written in C# over metres.</summary>
    /// <remarks>What <c>GrassScatter.rvn</c>'s <c>Height</c>, <c>Hole</c> and <c>Weight</c> compute.</remarks>
    static Vector2 ShaderAtlasUv(Vector2 local, in TerrainDescription description, in TerrainAtlas atlas) {
        var sample = local / description.MetresPerQuad;
        var tileX = Math.Clamp(MathF.Floor(sample.X / description.TileQuads), 0f, description.TilesX - 1f);
        var tileZ = Math.Clamp(MathF.Floor(sample.Y / description.TileQuads), 0f, description.TilesZ - 1f);

        return new(
            ((tileX * description.TileSamples) + (sample.X - (tileX * description.TileQuads)) + 0.5f) / atlas.Width,
            ((tileZ * description.TileSamples) + (sample.Y - (tileZ * description.TileQuads)) + 0.5f) / atlas.Height
        );
    }

    /// <summary>A GPU-style bilinear tap over an array of texels, clamped at the edges.</summary>
    static float Bilinear(float[] texels, int width, int height, Vector2 uv) {
        var x = (uv.X * width) - 0.5f;
        var z = (uv.Y * height) - 0.5f;

        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var fx = x - x0;
        var fz = z - z0;

        var cx0 = Math.Clamp(x0, 0, width - 1);
        var cx1 = Math.Clamp(x0 + 1, 0, width - 1);
        var cz0 = Math.Clamp(z0, 0, height - 1);
        var cz1 = Math.Clamp(z0 + 1, 0, height - 1);

        var top = float.Lerp(texels[(cz0 * width) + cx0], texels[(cz0 * width) + cx1], fx);
        var bottom = float.Lerp(texels[(cz1 * width) + cx0], texels[(cz1 * width) + cx1], fx);

        return float.Lerp(top, bottom, fz);
    }

    /// <summary>The atlas's level-0 texels, packed the way the terrain renderer uploads them.</summary>
    static float[] PackAtlas(TerrainMap terrain, in TerrainAtlas atlas) {
        var description = terrain.Description;
        var packed = new float[atlas.Width * atlas.Height];

        for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < description.TilesX; tileX++) {
                var rect = description.SamplesOf(tileX, tileZ);
                var block = atlas.BlockOf(tileX, tileZ);

                for (var z = 0; z < description.TileSamples; z++) {
                    for (var x = 0; x < description.TileSamples; x++) {
                        packed[((block.Z + z) * atlas.Width) + block.X + x] =
                            terrain.CompositeAt(rect.X + x, rect.Z + z) / (float)TerrainSamples.MaxHeight;
                    }
                }
            }
        }

        return packed;
    }

    /// <summary>On a multi-tile terrain, the shader reads the atlas where the CPU reads the ground.</summary>
    /// <remarks>
    ///     ⚠ <b>The atlas packs each tile as its own <c>TileSamples</c>-wide block with the boundary
    ///     samples duplicated, and every single-tile terrain hides that.</b> A flat
    ///     <c>sample / heightMapSize</c> map agrees with the packed grid over tile zero exactly, so a
    ///     parity suite whose terrains are all one tile passes while every read past tile zero is
    ///     shifted by <c>tileIndex × (TileSamples − TileQuads)</c> texels — grass floating above
    ///     valleys and buried under ridges, worse the further from the origin.
    /// </remarks>
    [Fact]
    public void TheScatterSamplesThePackedAtlasWhereTheReferenceSamplesTheGround() {
        var description = new TerrainDescription {
            TileSamples = 8,
            TilesX = 2,
            TilesZ = 2,
            MetresPerQuad = 1f,
            MinHeight = 0f,
            MaxHeight = 100f
        };

        var terrain = new TerrainMap(description);

        // Distinctive ground in every tile, so a block read out of the wrong block cannot agree.
        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                terrain.Base[x, z] = description.StoreHeight(((x * 7) + (z * 13)) % 40);
            }
        }

        var atlas = new TerrainAtlas(description);
        var packed = PackAtlas(terrain, atlas);

        // Positions in all four tiles, on both sides of the tile boundary at 7 m, and taps whose
        // bilinear window crosses it — the duplicated column is what keeps those continuous.
        float[] stations = [0.4f, 3.7f, 6.6f, 6.9f, 7.0f, 7.2f, 8.6f, 11.3f, 13.9f];

        foreach (var z in stations) {
            foreach (var x in stations) {
                var uv = ShaderAtlasUv(new(x, z), description, atlas);
                var stored = Bilinear(packed, atlas.Width, atlas.Height, uv);
                var shader = description.MinHeight + (stored * description.HeightRange);

                Assert.Equal(TerrainPick.HeightAt(terrain, x, z), shader, 1e-3f);
            }
        }
    }

    /// <summary>The edge rejection uses the tiled width, which the atlas width overstates.</summary>
    /// <remarks>
    ///     ⚠ <b>The atlas is wider than the terrain by one duplicated boundary column per tile.</b>
    ///     An extent derived from <c>heightMapSize − 1</c> is therefore a phantom margin past the
    ///     real edge — candidates standing on ground that does not exist, passed by the bounds test
    ///     and answered by the sampler's clamp.
    /// </remarks>
    [Fact]
    public void TheExtentTestUsesTheTiledWidthNotTheAtlasWidth() {
        var description = new TerrainDescription {
            TileSamples = 8,
            TilesX = 2,
            TilesZ = 2,
            MetresPerQuad = 2f,
            MinHeight = 0f,
            MaxHeight = 100f
        };

        var atlas = new TerrainAtlas(description);

        // What the shader now computes: atlasTiles · tileQuads · metresPerQuad.
        var extent = description.TilesX * description.TileQuads * description.MetresPerQuad;

        Assert.Equal(description.WidthX, extent);

        // And what it computed before — the atlas width — overstates it by a margin per tile.
        var phantom = (atlas.Width - 1) * description.MetresPerQuad;

        Assert.True(
            phantom > extent,
            "the atlas width no longer overstates the terrain's extent; if the packing changed, "
            + "revisit whether GrassScatter.rvn's bounds test still needs the tiled width."
        );
    }

    /// <summary>The three ground samplers route through the atlas transform.</summary>
    /// <remarks>
    ///     The failure this catches is somebody reverting a sampler to the flat map — which passes
    ///     every single-tile test and shifts every read past tile zero of a real terrain.
    /// </remarks>
    [Fact]
    public void TheShaderSamplesTheGroundThroughAtlasUv() {
        var source = Source("GrassScatter.rvn");

        Assert.Contains("func AtlasUv(", source, StringComparison.Ordinal);
        Assert.Contains("heightMap.SampleLevel(heightSampler, AtlasUv(local), 0f)", source, StringComparison.Ordinal);
        Assert.Contains("holeMap.SampleLevel(holeSampler, AtlasUv(local), 0f)", source, StringComparison.Ordinal);
        Assert.Contains("weightMap.SampleLevel(weightSampler, AtlasUv(local), 0f)", source, StringComparison.Ordinal);

        // And the extent is the tiled width, not the atlas width.
        Assert.Contains("val extent = atlasTiles * tileQuads * metresPerQuad", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(heightMapSize.x - 1f) * metresPerQuad", source, StringComparison.Ordinal);
    }

    /// <summary>The wind reaches the blade through the one implementation of it.</summary>
    /// <remarks>
    ///     ⚠ <b>[docs/plan/31 § T6] asks for wind <em>through</em> <c>Displacement.Wind</c>.</b> A
    ///     second copy of two sines in the grass shader is how the grass and the trees in one scene
    ///     come to move at different speeds in the same gust — and it is the kind of duplication that
    ///     is invisible until somebody tunes one of them.
    /// </remarks>
    [Fact]
    public void TheGrassShaderSwaysThroughDisplacement() {
        var source = Source("Grass.rvn");

        Assert.Contains("Displacement.WindPhased(", source, StringComparison.Ordinal);
        Assert.Contains("blade.windPhase", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sin(phase", source, StringComparison.Ordinal);
    }

    /// <summary>And the phase is per instance, which is what makes a clump not one object.</summary>
    [Fact]
    public void TheWindTakesAPerInstancePhase() {
        var displacement = Path.Combine("..", "Geometry", "Displacement.rvn");
        var source = Source(displacement);

        Assert.Contains("static func WindPhased(", source, StringComparison.Ordinal);
        Assert.Contains("+ phase", source, StringComparison.Ordinal);

        // Added to the phase and not to the offset: displacing a blade that is not moving detaches
        // it from its root.
        Assert.DoesNotContain("offset * stiffness + phase", source, StringComparison.Ordinal);
    }
}
