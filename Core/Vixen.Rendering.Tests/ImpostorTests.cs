// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The far field — [docs/plan/31 § T7].</summary>
public sealed class ImpostorTests {
    static ImpostorGrid Grid => new(9);

    [Fact]
    public void TheFoldRoundTripsOverTheHemisphere() {
        for (var i = 0; i < 64; i++) {
            for (var j = 0; j < 64; j++) {
                var azimuth = i / 64f * MathF.Tau;
                var elevation = j / 63f * (MathF.PI / 2f);

                var direction = new Vector3(
                    MathF.Cos(azimuth) * MathF.Cos(elevation),
                    MathF.Sin(elevation),
                    MathF.Sin(azimuth) * MathF.Cos(elevation)
                );

                var back = ImpostorGrid.Decode(ImpostorGrid.Encode(direction));

                Assert.True(
                    Vector3.Distance(direction, back) < 1e-3f,
                    $"{direction} folded to {back}."
                );
            }
        }
    }

    /// <summary>Straight up is the centre of the square, and an odd grid has a cell there.</summary>
    /// <remarks>
    ///     ⚠ <b>Straight down is where a top-down view spends its whole time</b>, and an even grid
    ///     puts a seam exactly there — four cells blended for the one direction that ought to be a
    ///     single photograph.
    /// </remarks>
    [Fact]
    public void ACellSitsExactlyOverhead() {
        var square = ImpostorGrid.Encode(Vector3.UnitY);

        Assert.Equal(0.5f, square.X, 5);
        Assert.Equal(0.5f, square.Y, 5);

        var cell = Grid.NearestTo(Vector3.UnitY);

        Assert.Equal(new ImpostorCell(4, 4), cell);
        Assert.True(Vector3.Distance(Vector3.UnitY, Grid.DirectionOf(cell)) < 1e-5f);
    }

    /// <summary>A direction from below is folded onto its mirror, not pinned to the horizon.</summary>
    [Fact]
    public void LookingFromBelowKeepsTheViewItHad() {
        var above = ImpostorGrid.Encode(Vector3.Normalize(new(1f, 0.2f, 0.4f)));
        var below = ImpostorGrid.Encode(Vector3.Normalize(new(1f, -0.2f, 0.4f)));

        Assert.Equal(above.X, below.X, 4);
        Assert.Equal(above.Y, below.Y, 4);
    }

    /// <summary>The three blended views always sum to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Continuity is the whole feature.</b> Weights that did not sum to one would make an
    ///     impostor brighten or fade as the camera crossed a cell boundary, which for a forest is
    ///     every tree flickering on a different frame.
    /// </remarks>
    [Fact]
    public void TheThreeWeightsSumToOneEverywhere() {
        var grid = Grid;
        Span<ImpostorSample> samples = stackalloc ImpostorSample[3];

        for (var i = 0; i < 128; i++) {
            for (var j = 0; j < 32; j++) {
                var azimuth = i / 128f * MathF.Tau;
                var elevation = j / 31f * (MathF.PI / 2f);

                var direction = new Vector3(
                    MathF.Cos(azimuth) * MathF.Cos(elevation),
                    MathF.Sin(elevation),
                    MathF.Sin(azimuth) * MathF.Cos(elevation)
                );

                grid.Blend(direction, samples);

                var total = samples[0].Weight + samples[1].Weight + samples[2].Weight;

                Assert.Equal(1f, total, 4);
                Assert.All(
                    samples.ToArray(),
                    sample => {
                        Assert.InRange(sample.Weight, -1e-4f, 1.0001f);
                        Assert.InRange(sample.Cell.X, 0, grid.Side - 1);
                        Assert.InRange(sample.Cell.Z, 0, grid.Side - 1);
                    }
                );
            }
        }
    }

    /// <summary>A direction that is a cell takes that cell and nothing else.</summary>
    [Fact]
    public void ACellsOwnDirectionBlendsToItself() {
        var grid = Grid;
        Span<ImpostorSample> samples = stackalloc ImpostorSample[3];

        var cell = new ImpostorCell(2, 6);

        grid.Blend(grid.DirectionOf(cell), samples);

        var dominant = samples.ToArray().MaxBy(sample => sample.Weight);

        Assert.Equal(cell, dominant.Cell);
        Assert.True(dominant.Weight > 0.99f, $"the cell's own direction gave it {dominant.Weight}.");
    }

    /// <summary>The blend moves continuously as the camera does.</summary>
    [Fact]
    public void CrossingACellBoundaryIsContinuous() {
        var grid = Grid;
        Span<ImpostorSample> here = stackalloc ImpostorSample[3];
        Span<ImpostorSample> there = stackalloc ImpostorSample[3];

        var previous = default(Vector3?);

        for (var step = 0; step <= 400; step++) {
            var azimuth = step / 400f * MathF.Tau;
            var direction = Vector3.Normalize(new(MathF.Cos(azimuth), 0.6f, MathF.Sin(azimuth)));

            grid.Blend(direction, here);

            // The blended direction: what the impostor is actually showing.
            var shown = Vector3.Zero;

            foreach (var sample in here) {
                shown += grid.DirectionOf(sample.Cell) * sample.Weight;
            }

            if (previous is { } last) {
                Assert.True(
                    Vector3.Distance(last, shown) < 0.1f,
                    $"the shown direction jumped from {last} to {shown} at step {step}."
                );
            }

            previous = shown;
            there.Clear();
        }
    }

    [Fact]
    public void TheAtlasPacksEveryCellWithoutOverlapping() {
        var atlas = new ImpostorAtlas(Grid, cellSize: 64, padding: 4);
        var covered = new HashSet<(int X, int Y)>();

        for (var z = 0; z < atlas.Grid.Side; z++) {
            for (var x = 0; x < atlas.Grid.Side; x++) {
                var (rx, ry, width, height) = atlas.RectOf(new(x, z));

                Assert.Equal(56, width);
                Assert.Equal(56, height);
                Assert.InRange(rx, 0, atlas.Resolution - width);
                Assert.InRange(ry, 0, atlas.Resolution - height);

                for (var py = ry; py < ry + height; py++) {
                    for (var px = rx; px < rx + width; px++) {
                        Assert.True(covered.Add((px, py)), $"texel {px},{py} is in two cells.");
                    }
                }
            }
        }
    }

    /// <summary>Every cell's drawable area is separated by at least the gutter.</summary>
    [Fact]
    public void TheGutterSeparatesNeighbouringCells() {
        var atlas = new ImpostorAtlas(Grid, cellSize: 64, padding: 4);

        var (leftX, _, leftWidth, _) = atlas.RectOf(new(0, 0));
        var (rightX, _, _, _) = atlas.RectOf(new(1, 0));

        Assert.Equal(atlas.Padding * 2, rightX - (leftX + leftWidth));
    }

    [Fact]
    public void ACellTooSmallForItsGutterIsRefused() {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new ImpostorAtlas(Grid, 8, 4));

        Assert.Contains("nothing left to draw", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A mip chain stops before two cells share a texel.</summary>
    /// <remarks>
    ///     ⚠ <b>The bleed the padding exists to stop, arriving through another door.</b> A mip that
    ///     mixed two cells would put one view's silhouette into its neighbour's at a distance, which
    ///     is exactly where the impostor is the only thing being drawn.
    /// </remarks>
    [Fact]
    public void TheMipChainStopsAtTheCellSize() {
        var atlas = new ImpostorAtlas(Grid, cellSize: 128, padding: 4);

        Assert.Equal(1152, atlas.Resolution);
        Assert.Equal(8, atlas.MipLevels);

        // The atlas's own size would allow eleven, which is what a caller building them naively gets.
        Assert.True(atlas.MipLevels < 11);
    }

    /// <summary>A bake camera is orthographic and looks from the cell's own direction.</summary>
    /// <remarks>
    ///     ⚠ <b>A perspective bake fixes the distance into the texture</b>, so an impostor drawn
    ///     nearer or further shows the wrong parallax. Orthographic is direction-only, which is what a
    ///     billboard replays.
    /// </remarks>
    [Fact]
    public void ABakeCameraIsOrthographic() {
        var grid = Grid;
        var view = ImpostorView.For(grid, new(3, 5), new(0f, 4f, 0f), 6f);

        Assert.Equal(grid.DirectionOf(new(3, 5)), view.Direction);
        Assert.Equal(6f, view.Radius);

        // An orthographic projection's bottom row is (0, 0, *, 1): no perspective divide.
        Assert.Equal(0f, view.Projection.M14, 5);
        Assert.Equal(0f, view.Projection.M24, 5);
        Assert.Equal(1f, view.Projection.M44, 5);
    }

    /// <summary>The overhead cell's camera has a defined up vector.</summary>
    [Fact]
    public void LookingStraightDownDoesNotProduceANaN() {
        var view = ImpostorView.For(Grid, new(4, 4), Vector3.Zero, 5f);

        Assert.Equal(Vector3.UnitY, view.Direction);
        Assert.False(float.IsNaN(view.View.M11), "the overhead view matrix is not a number.");
        Assert.False(float.IsNaN(view.View.M22));
    }

    /// <summary>The shader still folds the way the kernel does.</summary>
    /// <remarks>
    ///     ⚠ <b>An impostor that samples the wrong cell looks like a tree facing slightly the wrong
    ///     way</b> — which nobody reports and everybody notices. A source assertion catches the
    ///     failure that actually happens: somebody edits one of the two folds.
    /// </remarks>
    [Fact]
    public void TheShaderStillHoldsTheSameFold() {
        var source = Source("Impostor.rvn");

        var encode = new Regex(
            @"float2\(\s*\(\s*n\.x\s*\+\s*n\.z\s*\)\s*\*\s*0\.5f\s*\+\s*0\.5f\s*,\s*\(\s*n\.x\s*-\s*n\.z\s*\)\s*\*\s*0\.5f\s*\+\s*0\.5f\s*\)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        Assert.True(
            encode.IsMatch(source),
            "Impostor.rvn no longer folds a direction the way ImpostorGrid.Encode does."
        );

        Assert.Contains("1f - abs(x) - abs(z)", source, StringComparison.Ordinal);
        Assert.Contains("f.x + f.y <= 1f", source, StringComparison.Ordinal);
    }

    /// <summary>And the transliterated fold agrees with the kernel's, at zero drift.</summary>
    [Fact]
    public void TheTransliteratedShaderFoldEqualsTheKernels() {
        for (var i = 0; i < 200; i++) {
            var azimuth = i / 200f * MathF.Tau;
            var direction = Vector3.Normalize(new(MathF.Cos(azimuth), 0.35f, MathF.Sin(azimuth)));

            // What Impostor.rvn's ImpostorMath.Encode computes.
            var scale = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
            var n = direction / scale;
            var shader = new Vector2(((n.X + n.Z) * 0.5f) + 0.5f, ((n.X - n.Z) * 0.5f) + 0.5f);

            var kernel = ImpostorGrid.Encode(direction);

            Assert.Equal(kernel.X, shader.X, 6);
            Assert.Equal(kernel.Y, shader.Y, 6);
        }
    }

    static string Source(string file) {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent) {
            var candidate = Path.Combine(at.FullName, "Raven", "Library", "Terrain", file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/Terrain/{file} was not found.");
    }
}
