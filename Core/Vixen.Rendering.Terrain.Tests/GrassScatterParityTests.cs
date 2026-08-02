// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Xunit;

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
