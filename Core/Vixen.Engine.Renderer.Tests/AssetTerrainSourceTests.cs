// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Engine.Renderer;
using Vixen.Foliage;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Tests;

/// <summary>
///     A terrain component's references become a heightfield and a grass rule, through the content
///     manager a game has.
/// </summary>
/// <remarks>
///     <para>
///         <b>Over a real <see cref="AssetManager" /> and a real bundle</b>, on
///         <c>AssetMaterialSourceTests</c>' terms: the interesting part is the join. The chunks are
///         written the way the build writes them — a <c>.vxterrain</c> as
///         <see cref="TerrainStore" />'s raw bytes under a tool-payload stamp, a <c>.vxgrass</c> as
///         the serialized <see cref="GrassType" /> under its own type id — so a change to either
///         content format breaks this rather than being discovered in a game.
///     </para>
///     <para>
///         ⚠ <b>The grass half exists because the first wiring of this seam never loaded
///         anything.</b> It asked for the rule as <c>Load&lt;object&gt;</c>, and the database
///         refuses a chunk read as a type that did not write it — every field in every level would
///         have counted as failed, quietly. The rule is a struct, so no typed load can be the
///         reader either; the fix is the source opening the payload itself, and this is the test
///         that holds it.
///     </para>
/// </remarks>
public sealed class AssetTerrainSourceTests {
    const string TerrainAddress = "Assets/Terrain/Meadow.vxterrain";
    const string GrassAddress = "Assets/Terrain/Meadow.vxgrass";

    /// <summary>A multi-tile terrain round-trips: heights, a painted layer, and a hole.</summary>
    [Fact]
    public void ATerrainReferenceBecomesTheTerrainTheBuildWrote() {
        var authored = Map();
        var source = new AssetTerrainSource(Content(authored, null));

        Assert.True(Settles(() => source.Terrain(TerrainAddress) is not null));

        var loaded = source.Terrain(TerrainAddress)!;

        Assert.Equal(authored.Description, loaded.Description);

        // Composited on the way in — the renderer's first upload copies ground, not the zeroed base.
        Assert.Equal(authored.Base[9, 9], loaded.CompositeAt(9, 9));
        Assert.True(loaded.Holes.IsHole(4, 4));
        Assert.Equal(authored.Weights.LayerCount, loaded.Weights.LayerCount);
        Assert.Equal(0, source.Failed);
    }

    /// <summary>The join this file exists for: a grass reference in, the authored rule out.</summary>
    [Fact]
    public void AGrassReferenceBecomesTheRuleTheBuildWrote() {
        var meadow = GrassType.Of("Meadow") with { Layer = "Grass", Density = 24f };
        var source = new AssetTerrainSource(Content(Map(), meadow));

        Assert.True(Settles(() => source.Grass(GrassAddress) is not null));

        var loaded = source.Grass(GrassAddress)!.Value;

        Assert.Equal("Meadow", loaded.Name);
        Assert.Equal("Grass", loaded.Layer);
        Assert.Equal(24f, loaded.Density);
        Assert.Equal(0, source.Failed);
    }

    /// <summary>A reference this build shipped nothing for is counted, not thrown for.</summary>
    [Fact]
    public void AReferenceNothingShippedIsCountedAsFailed() {
        var source = new AssetTerrainSource(Content(Map(), null));

        // The reads start on the first ask and their failures land with the tasks, so keep asking —
        // which is what a frame does — until both are settled rather than asserting a race.
        Assert.True(
            Settles(
                () => source.Terrain("Assets/Terrain/Gone.vxterrain") is null
                    && source.Grass("Assets/Terrain/Gone.vxgrass") is null
                    && source.Failed == 2
            )
        );
    }

    /// <summary>Asks until the load lands, which is what extraction does by asking next frame.</summary>
    static bool Settles(Func<bool> landed) {
        for (var attempt = 0; attempt < 200; attempt++) {
            if (landed()) {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    /// <summary>A small terrain that still exercises every stored section: 2×2 tiles, one painted
    ///     layer, one hole.</summary>
    static TerrainMap Map() {
        var map = new TerrainMap(
            new() {
                TileSamples = 8, TilesX = 2, TilesZ = 2,
                MetresPerQuad = 1f, MinHeight = -10f, MaxHeight = 10f
            }
        );

        for (var z = 0; z < map.Base.Height; z++) {
            for (var x = 0; x < map.Base.Width; x++) {
                map.Base[x, z] = (ushort)(30_000 + (x * 100) + z);
            }
        }

        map.Weights.AddLayer("Grass");
        map.Weights.SetWeight(0, 3, 3, 200);
        map.Holes.SetHole(4, 4, true);

        return map;
    }

    /// <summary>A content manager holding the two chunks, written the way the build writes them.</summary>
    static AssetManager Content(TerrainMap terrain, GrassType? grass) {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var backend = new FileOdbBackend(files, new("/store/odb"));
        var database = new ObjectDatabase(backend);

        // The raw importer's stamp is a tool-payload type nothing registers; the source opens the
        // bytes and never reads the stamp, so any id that is not GrassType's stands in for it here.
        var terrainId = database.WriteRaw(ContentHash.TypeId(typeof(TerrainStore)), [], TerrainStore.Write(terrain));

        var entries = new List<CatalogEntry> {
            new(TerrainAddress, terrainId, "Main", ContentProvider.Local, [], [], 0)
        };

        if (grass is { } rule) {
            // TerrainAssetImporter's exact spelling: the record's own type id over its serialized
            // bytes, which is what lets the source hand the payload to the serializer.
            var grassId = database.WriteRaw(ContentHash.TypeId(typeof(GrassType)), [], Serializer.ToBytes(rule));

            entries.Add(new(GrassAddress, grassId, "Main", ContentProvider.Local, [], [], 0));
        }

        var bundle = new BundleWriter();

        bundle.AddAll(backend);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [.. entries],
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new(catalog, new LocalBundleSource(files, new("/bundles")));
    }
}
