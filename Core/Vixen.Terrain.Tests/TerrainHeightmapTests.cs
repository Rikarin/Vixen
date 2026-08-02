// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>Raw 16-bit heightmap import and export — [docs/plan/31 § T1].</summary>
public sealed class TerrainHeightmapTests {
    static TerrainDescription Shape(int tiles = 1) =>
        TerrainDescription.Default with {
            TileSamples = 8, TilesX = tiles, TilesZ = tiles,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    /// <summary>A heightmap whose value ramps with X, so a shear or a flip is visible.</summary>
    static byte[] Ramp(int width, int height, bool bigEndian = false) {
        var bytes = new byte[width * height * sizeof(ushort)];

        for (var z = 0; z < height; z++) {
            for (var x = 0; x < width; x++) {
                var value = (ushort)(x * 65_535 / Math.Max(1, width - 1));
                var slice = bytes.AsSpan(((z * width) + x) * sizeof(ushort), sizeof(ushort));

                if (bigEndian) {
                    BinaryPrimitives.WriteUInt16BigEndian(slice, value);
                } else {
                    BinaryPrimitives.WriteUInt16LittleEndian(slice, value);
                }
            }
        }

        return bytes;
    }

    [Fact]
    public void AHeightmapOfTheSameSizeImportsSampleForSample() {
        var terrain = new Terrain(Shape());
        var format = TerrainHeightmapFormat.Of(terrain.Description);
        var bytes = Ramp(format.Width, format.Height);

        TerrainHeightmap.Import(terrain, bytes, format);
        terrain.Resolve();

        for (var x = 0; x < format.Width; x++) {
            var expected = (ushort)(x * 65_535 / (format.Width - 1));
            Assert.Equal(expected, terrain.Composite[x, 3]);
        }
    }

    [Fact]
    public void ExportThenImportIsTheIdentity() {
        var terrain = new Terrain(Shape(tiles: 2));
        var layer = terrain.AddLayer("Sculpt");

        TerrainSculpt.Sculpt(
            terrain, layer,
            TerrainBrush.Default with { Radius = 4f, Strength = 1f },
            new(new(7f, 7f)), 30f
        );

        terrain.Resolve();
        var before = terrain.Composite.Span.ToArray();

        var bytes = new byte[TerrainHeightmap.ExportByteCount(terrain.Description)];
        Assert.Equal(bytes.Length, TerrainHeightmap.Export(terrain, bytes));

        var reloaded = new Terrain(Shape(tiles: 2));
        TerrainHeightmap.Import(reloaded, bytes, TerrainHeightmapFormat.Of(reloaded.Description));
        reloaded.Resolve();

        Assert.Equal(before, reloaded.Composite.Span.ToArray());
    }

    [Fact]
    public void EndiannessIsHonouredOnBothSides() {
        var terrain = new Terrain(Shape());
        var format = TerrainHeightmapFormat.Of(terrain.Description) with { BigEndian = true };

        TerrainHeightmap.Import(terrain, Ramp(format.Width, format.Height, bigEndian: true), format);
        terrain.Resolve();
        var expected = terrain.Composite.Span.ToArray();

        var bytes = new byte[TerrainHeightmap.ExportByteCount(terrain.Description)];
        TerrainHeightmap.Export(terrain, bytes, bigEndian: true);

        var reloaded = new Terrain(Shape());
        TerrainHeightmap.Import(reloaded, bytes, format);
        reloaded.Resolve();

        Assert.Equal(expected, reloaded.Composite.Span.ToArray());
    }

    [Fact]
    public void ReadingALittleEndianFileAsBigEndianIsVisiblyWrong() {
        // Not a test of correctness so much as of the setting mattering: a raw file says nothing
        // about itself, so the two readings must not happen to agree.
        var terrain = new Terrain(Shape());
        var format = TerrainHeightmapFormat.Of(terrain.Description);
        var bytes = Ramp(format.Width, format.Height);

        TerrainHeightmap.Import(terrain, bytes, format);
        terrain.Resolve();
        var little = terrain.Composite.Span.ToArray();

        var other = new Terrain(Shape());
        TerrainHeightmap.Import(other, bytes, format with { BigEndian = true });
        other.Resolve();

        Assert.NotEqual(little, other.Composite.Span.ToArray());
    }

    /// <summary>
    ///     A heightmap of a different size is resampled, and its edges land on the terrain's.
    /// </summary>
    /// <remarks>
    ///     Mapping by scale factor instead of edge-to-edge leaves a fractional strip at the far edge
    ///     reading whatever the clamp gives it — a flat lip along two sides of every imported
    ///     terrain, subtle enough to ship. A terrain of one 8-sample tile is 8 across; the source
    ///     here is 15, and neither is a multiple of the other.
    /// </remarks>
    [Fact]
    public void ADifferentlySizedHeightmapIsResampledEdgeToEdge() {
        var terrain = new Terrain(Shape());
        var format = new TerrainHeightmapFormat(15, 15);

        TerrainHeightmap.Import(terrain, Ramp(15, 15), format);
        terrain.Resolve();

        // The corners are pinned: source 0 to sample 0, source 14 to the last sample.
        Assert.Equal(0, terrain.Composite[0, 0]);
        Assert.Equal(65_535, terrain.Composite[terrain.Description.SamplesX - 1, 0]);

        // And it is monotonic in between rather than stepping or lipping.
        for (var x = 1; x < terrain.Description.SamplesX; x++) {
            Assert.True(terrain.Composite[x, 3] > terrain.Composite[x - 1, 3], $"not rising at {x}.");
        }
    }

    [Fact]
    public void ASmallerHeightmapUpsamplesWithoutABlockyEdge() {
        var terrain = new Terrain(Shape(tiles: 4));
        TerrainHeightmap.Import(terrain, Ramp(4, 4), new(4, 4));
        terrain.Resolve();

        Assert.Equal(0, terrain.Composite[0, 0]);
        Assert.Equal(65_535, terrain.Composite[terrain.Description.SamplesX - 1, 0]);
    }

    // --- Layers -------------------------------------------------------------

    /// <summary>
    ///     Importing into a layer leaves the base alone, so a sculpt underneath survives.
    /// </summary>
    /// <remarks>
    ///     The return on docs/plan/31 § D4: a terrain imported from World Machine can be sculpted on
    ///     top of and re-imported without losing the sculpt.
    /// </remarks>
    [Fact]
    public void ImportingIntoALayerLeavesTheBaseAndTheLayersBelowAlone() {
        var terrain = new Terrain(Shape());
        var sculpt = terrain.AddLayer("Sculpt");
        var imported = terrain.AddLayer("Imported");

        sculpt.SetDelta(2, 2, 5_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        var baseBefore = terrain.Base.Span.ToArray();
        var format = TerrainHeightmapFormat.Of(terrain.Description);

        TerrainHeightmap.Import(terrain, Ramp(format.Width, format.Height), format, imported);
        terrain.Resolve();

        Assert.Equal(baseBefore, terrain.Base.Span.ToArray());
        Assert.Equal(5_000, sculpt.DeltaAt(2, 2));

        // Hiding the import brings the sculpt back exactly.
        imported.IsVisible = false;
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(terrain.Base[2, 2] + 5_000, terrain.Composite[2, 2]);
    }

    [Fact]
    public void ReimportingALayerReplacesItRatherThanStackingIt() {
        var terrain = new Terrain(Shape());
        var imported = terrain.AddLayer("Imported");
        var format = TerrainHeightmapFormat.Of(terrain.Description);
        var bytes = Ramp(format.Width, format.Height);

        TerrainHeightmap.Import(terrain, bytes, format, imported);
        terrain.Resolve();
        var once = terrain.Composite.Span.ToArray();

        TerrainHeightmap.Import(terrain, bytes, format, imported);
        terrain.Resolve();

        Assert.Equal(once, terrain.Composite.Span.ToArray());
    }

    [Fact]
    public void ImportingIntoAReservedOrLockedLayerIsRefused() {
        var terrain = new Terrain(Shape());
        var splines = terrain.AddLayer("Splines", TerrainLayerKind.Splines);
        var format = TerrainHeightmapFormat.Of(terrain.Description);
        var bytes = Ramp(format.Width, format.Height);

        Assert.Throws<ArgumentException>(() => TerrainHeightmap.Import(terrain, bytes, format, splines));

        var locked = terrain.AddLayer("Locked");
        locked.IsLocked = true;

        Assert.Throws<ArgumentException>(() => TerrainHeightmap.Import(terrain, bytes, format, locked));
    }

    // --- Refusals -----------------------------------------------------------

    [Fact]
    public void TooFewBytesIsRefusedWithTheWidthNamedAsTheLikelyCause() {
        var terrain = new Terrain(Shape());
        var thrown = Assert.Throws<ArgumentException>(
            () => TerrainHeightmap.Import(terrain, new byte[10], new(64, 64))
        );

        Assert.Contains("width", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 8)]
    [InlineData(8, 1)]
    [InlineData(-4, 8)]
    public void ADegenerateFormatIsRefused(int width, int height) {
        var terrain = new Terrain(Shape());

        Assert.Throws<ArgumentException>(
            () => TerrainHeightmap.Import(terrain, new byte[4096], new(width, height))
        );
    }

    [Fact]
    public void ExportingIntoTooLittleRoomIsRefused() {
        var terrain = new Terrain(Shape());

        Assert.Throws<ArgumentException>(() => TerrainHeightmap.Export(terrain, new byte[4]));
    }

    [Fact]
    public void ExportWritesTheCompositeRatherThanTheBase() {
        // Exporting the base would hand somebody round-tripping through an external tool a terrain
        // missing every edit layer, silently.
        var terrain = new Terrain(Shape());
        var layer = terrain.AddLayer("Sculpt");

        layer.SetDelta(3, 3, 9_000);
        terrain.InvalidateAll();

        var bytes = new byte[TerrainHeightmap.ExportByteCount(terrain.Description)];
        TerrainHeightmap.Export(terrain, bytes);

        var offset = ((3 * terrain.Description.SamplesX) + 3) * sizeof(ushort);
        var written = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

        Assert.Equal(terrain.Composite[3, 3], written);
        Assert.NotEqual(terrain.Base[3, 3], written);
    }
}
