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
    const string FoliageAddress = "Assets/Terrain/Pine.vxfoliage";
    const string VolumeAddress = "Assets/Terrain/Meadow.vxfol";

    /// <summary>A multi-tile terrain round-trips: heights, a painted layer, and a hole.</summary>
    [Fact]
    public void ATerrainReferenceBecomesTheTerrainTheBuildWrote() {
        var authored = Map();
        var source = new AssetTerrainSource(Content(authored, null));

        Settles(source, () => source.Terrain(TerrainAddress) is not null, "the terrain never arrived");

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

        Settles(source, () => source.Grass(GrassAddress) is not null, "the grass rule never arrived");

        var loaded = source.Grass(GrassAddress)!.Value;

        Assert.Equal("Meadow", loaded.Name);
        Assert.Equal("Grass", loaded.Layer);
        Assert.Equal(24f, loaded.Density);
        Assert.Equal(0, source.Failed);
    }

    /// <summary>The foliage join: a .vxfoliage reference in, the authored type out.</summary>
    /// <remarks>
    ///     ⚠ <b>The same trap the grass half closed, one asset kind over.</b> The importer wrote
    ///     <c>.vxfoliage</c> chunks as YAML text until the runtime path existed, and text handed to
    ///     the binary serializer is a forest that quietly never stands — this holds the serialized
    ///     spelling end to end.
    /// </remarks>
    [Fact]
    public void AFoliageReferenceBecomesTheTypeTheBuildWrote() {
        var pine = FoliageType.Of("Pine") with { Mesh = "vx:9e8a44c9930c64e388ca034c5fe4c426", Radius = 3f };
        var source = new AssetTerrainSource(Content(Map(), null, pine));

        Settles(source, () => source.Foliage(FoliageAddress) is not null, "the foliage type never arrived");

        var loaded = source.Foliage(FoliageAddress)!.Value;

        Assert.Equal("Pine", loaded.Name);
        Assert.Equal(3f, loaded.Radius);
        Assert.True(loaded.CastShadows);
        Assert.Equal(0, source.Failed);
    }

    /// <summary>A volume's bytes become instances, dressed in the palette the caller resolved.</summary>
    [Fact]
    public void AVolumeReferenceBecomesItsInstances() {
        var pine = FoliageType.Of("Pine") with { Radius = 2f };
        var authored = new FoliageVolume(new(32f));
        var type = authored.AddType(pine);

        for (var index = 0; index < 5; index++) {
            authored.Add(type, new(new(index * 3f, 0f, 8f), Vixen.Core.Mathematics.Quaternion.Identity, 1f));
        }

        var bytes = new byte[FoliageStore.ByteCount(authored)];

        FoliageStore.Write(authored, bytes);

        var source = new AssetTerrainSource(Content(Map(), null, volume: bytes));
        var palette = new List<FoliageType> { pine };

        Settles(source, () => source.Volume(VolumeAddress, palette) is not null, "the foliage volume never arrived");

        var loaded = source.Volume(VolumeAddress, palette)!;

        Assert.Equal(5, loaded.InstanceCount);
        Assert.Equal("Pine", loaded.Palette[0].Name);

        // The same object next frame — the compositor node keys its device state by it.
        Assert.Same(loaded, source.Volume(VolumeAddress, palette));
        Assert.Equal(0, source.Failed);
    }

    /// <summary>A reference this build shipped nothing for is counted, not thrown for.</summary>
    [Fact]
    public void AReferenceNothingShippedIsCountedAsFailed() {
        var source = new AssetTerrainSource(Content(Map(), null));

        // The reads start on the first ask and their failures land with the tasks, so keep asking —
        // which is what a frame does — until both are settled rather than asserting a race.
        Settles(
            source,
            () => source.Terrain("Assets/Terrain/Gone.vxterrain") is null
                && source.Grass("Assets/Terrain/Gone.vxgrass") is null
                && source.Failed == 2,
            "the two missing references were never both counted as failed"
        );
    }

    /// <summary>Asks until the load lands, or until nothing is left that could make it land.</summary>
    /// <param name="source">The source being asked, whose outstanding reads decide when to give up.</param>
    /// <param name="landed">What is being waited for. Called once per attempt, and it is the ask.</param>
    /// <param name="never">What to say when the source runs out of things to do first.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No deadline, for the reason <c>AssetWaterSourceTests.Settles</c> now gives at
    ///         length.</b> Every read here is <see cref="Task.Run{TResult}(Func{Task{TResult}})" />,
    ///         <c>build.sh Test</c> runs every test project at once, and a work item queued into a
    ///         saturated pool waits on .NET's thread injection — about two threads a second. The
    ///         delay is therefore a property of how many workers the whole host has blocked and not
    ///         of the read, which is a memcpy out of a mounted bundle. Four CI legs of this file have
    ///         gone red on the thirty seconds this replaces, and raising the number is the remedy
    ///         that already failed once.
    ///     </para>
    ///     <para>
    ///         So the giving-up condition is a fact about the source:
    ///         <see cref="AssetTerrainSource.Reading" /> zero at both ends of eight consecutive
    ///         attempts means nothing is on its way and no number of further attempts can change the
    ///         answer. Consecutive, because a read that finishes part way through an attempt is taken
    ///         up by the next attempt's ask and a single idle observation would call that source
    ///         stuck.
    ///     </para>
    /// </remarks>
    static void Settles(AssetTerrainSource source, Func<bool> landed, string never) {
        var reached = false;

        // The ask and the answer are one call here — Terrain both starts the read and reports it — so
        // the attempt is the predicate and the result is remembered for the line after it.
        Settling.Until(() => reached = landed(), () => reached, () => source.Reading > 0, never);
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

    /// <summary>A content manager holding the chunks, written the way the build writes them.</summary>
    static AssetManager Content(TerrainMap terrain, GrassType? grass, FoliageType? foliage = null, byte[]? volume = null) {
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

        if (foliage is { } kind) {
            // The importer's foliage spelling, on the grass rule's terms exactly.
            var foliageId = database.WriteRaw(ContentHash.TypeId(typeof(FoliageType)), [], Serializer.ToBytes(kind));

            entries.Add(new(FoliageAddress, foliageId, "Main", ContentProvider.Local, [], [], 0));
        }

        if (volume is not null) {
            // A .vxfol is carried forward as its bytes — FoliageStore's own format, no serializer
            // in between — so the stamp is a tool-payload id the source never reads.
            var volumeId = database.WriteRaw(ContentHash.TypeId(typeof(FoliageStore)), [], volume);

            entries.Add(new(VolumeAddress, volumeId, "Main", ContentProvider.Local, [], [], 0));
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
