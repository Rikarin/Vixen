// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The incrementality matrix: everything that must re-run an import, and the one thing that must
///     not. Each row is a way for a build system to be wrong, and being wrong here means an artist
///     changing a file, rebuilding, and getting the old result.
/// </summary>
public sealed class ImportPipelineTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));
    readonly ProjectPaths paths;

    public ImportPipelineTests() {
        paths = new(root);
        Directory.CreateDirectory(paths.Assets);
    }

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    [Fact]
    public async Task AFirstImportRunsAndPutsItsArtefactInTheObjectDatabase() {
        Write("Assets/hero.pal", "palette bytes");
        var (pipeline, artifacts, database) = Pipeline();

        var outcome = await pipeline.ImportAsync(Entry(database, "Assets/hero.pal"), TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.WasCached);
        Assert.Equal("PaletteImporter", outcome.Importer);
        var artifact = Assert.Single(outcome.Record!.Artifacts);
        Assert.True(artifact.SubAsset.IsMain);
        Assert.True(artifacts.Exists(artifact.Id));
    }

    [Fact]
    public async Task ASecondImportOfAnUnchangedAssetIsSkipped() {
        Write("Assets/hero.pal", "palette bytes");
        var (pipeline, _, database) = Pipeline();
        var entry = Entry(database, "Assets/hero.pal");

        await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken);
        var second = await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken);

        Assert.True(second.WasCached);
    }

    [Fact]
    public async Task ChangingTheSourceRunsItAgain() {
        Write("Assets/hero.pal", "palette bytes");
        var (pipeline, _, database) = Pipeline();
        var entry = Entry(database, "Assets/hero.pal");

        await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken);
        Write("Assets/hero.pal", "different bytes");

        Assert.False((await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    [Fact]
    public async Task ChangingASettingRunsItAgain() {
        Write("Assets/hero.pal", "palette bytes");
        var (pipeline, _, database) = Pipeline();
        var entry = Entry(database, "Assets/hero.pal");

        await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken);
        EditMeta("Assets/hero.pal", root => ((YamlMapping)root["importer"]!).Set("maxColours", new YamlScalar("16", YamlScalarStyle.Plain)));

        Assert.False((await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    /// <summary>"I fixed the mip filter, re-import everything" is a version bump and nothing else.</summary>
    [Fact]
    public async Task BumpingTheImporterVersionRunsEverythingAgain() {
        Write("Assets/hero.pal", "palette bytes");
        var database = Database();
        var cache = new ImportCache();
        var artifacts = Artifacts();
        var entry = Entry(database, "Assets/hero.pal");

        var first = new ImportPipeline(database, Registry(new PaletteImporter()), artifacts, Files(), cache);
        await first.ImportAsync(entry, TestContext.Current.CancellationToken);

        var second = new ImportPipeline(
            database,
            Registry(new PaletteImporter { VersionOverride = 2 }),
            artifacts,
            Files(),
            cache
        );

        Assert.False((await second.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    /// <summary>
    ///     A declared dependency changing re-runs the import. This is what makes a material rebuild
    ///     when the palette it reads is edited, and it is the row every naive build system gets
    ///     wrong.
    /// </summary>
    [Fact]
    public async Task ChangingADeclaredDependencyRunsItAgain() {
        Write("Assets/hero.pal", "palette bytes");
        Write("Assets/shared.pal", "shared palette");
        var (pipeline, _, database) = Pipeline();
        var entry = Entry(database, "Assets/hero.pal");

        await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken);
        Assert.True((await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);

        Write("Assets/shared.pal", "the shared palette, edited");

        Assert.False((await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    /// <summary>
    ///     And a file nobody declared does <b>not</b> re-run it — which is precisely why the
    ///     declared-reads check exists. Without that check this row would be a silent, permanent
    ///     staleness bug rather than a documented property.
    /// </summary>
    [Fact]
    public async Task ChangingAFileNobodyDeclaredDoesNotRunItAgain() {
        Write("Assets/hero.pal", "palette bytes");
        Write("Assets/unrelated.txt", "nothing to do with it");
        var (pipeline, _, database) = Pipeline();
        var entry = Entry(database, "Assets/hero.pal");

        await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken);
        Write("Assets/unrelated.txt", "still nothing to do with it");

        Assert.True((await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    /// <summary>
    ///     The same asset for two targets is two cache entries. A texture is BC7 on a desktop and
    ///     ASTC on a phone, and sharing one entry would ship the wrong one.
    /// </summary>
    [Fact]
    public async Task TwoTargetsDoNotShareACacheEntry() {
        Write("Assets/hero.pal", "palette bytes");
        var (pipeline, _, database) = Pipeline();
        var entry = Entry(database, "Assets/hero.pal");

        pipeline.Target = "Windows";
        await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken);

        pipeline.Target = "Android";

        Assert.False((await pipeline.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    /// <summary>An import records what it learned, and the byte-fidelity emitter leaves the rest alone.</summary>
    [Fact]
    public async Task TheSidecarGainsWhatTheImportLearnedAndKeepsEverythingElse() {
        Write("Assets/hero.pal", "palette bytes");

        // Scanned first, because that is what creates the sidecar this then rewrites by hand.
        var database = Database();

        EditMeta(
            "Assets/hero.pal",
            _ => { },
            "# an artist's note\nguid: {guid}\nmetaVersion: 1\nimporter: !PaletteImporter\n  maxColours: 8\n"
        );

        var pipeline = new ImportPipeline(database, Registry(new PaletteImporter()), Artifacts(), Files());
        await pipeline.ImportAsync(Entry(database, "Assets/hero.pal"), TestContext.Current.CancellationToken);

        var written = File.ReadAllText(Path.Combine(root, "Assets/hero.pal.meta"));

        Assert.StartsWith("# an artist's note\n", written, StringComparison.Ordinal);
        Assert.Contains("maxColours: 8\n", written, StringComparison.Ordinal);
        Assert.Contains("sourceHash: ", written, StringComparison.Ordinal);
        Assert.Contains("version: 1\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedImportIsNotCachedSoTheNextAttemptRunsAgain() {
        Write("Assets/hero.pal", "palette bytes");
        var database = Database();
        var pipeline = new ImportPipeline(
            database,
            Registry(new PaletteImporter { Complaint = "the palette has 257 colours" }),
            Artifacts(),
            Files()
        );

        var outcome = await pipeline.ImportAsync(Entry(database, "Assets/hero.pal"), TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, pipeline.Cache.Count);
    }

    /// <summary>
    ///     And a failure does not throw away a record that was already there. A record is a true
    ///     statement about the input it was made from, so a failure on a <i>different</i> input does
    ///     not falsify it — an author who breaks a file and reverts it should not pay for a
    ///     re-import.
    /// </summary>
    [Fact]
    public async Task AFailureDoesNotDiscardAnEarlierGoodRecord() {
        Write("Assets/hero.pal", "palette bytes");
        var database = Database();
        var artifacts = Artifacts();
        var cache = new ImportCache();
        var entry = Entry(database, "Assets/hero.pal");

        var working = new ImportPipeline(database, Registry(new PaletteImporter()), artifacts, Files(), cache);
        await working.ImportAsync(entry, TestContext.Current.CancellationToken);

        // The source changes, and the import of the new content fails.
        Write("Assets/hero.pal", "corrupt");
        var broken = new ImportPipeline(
            database,
            Registry(new PaletteImporter { Complaint = "unreadable" }),
            artifacts,
            Files(),
            cache
        );

        Assert.False((await broken.ImportAsync(entry, TestContext.Current.CancellationToken)).Succeeded);

        // Reverting puts the input back where the good record describes it, so nothing has to run.
        Write("Assets/hero.pal", "palette bytes");

        Assert.True((await working.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    /// <summary>
    ///     One bad file must not stop the import of everything else — the difference between "one bad
    ///     asset" and "the editor won't open".
    /// </summary>
    [Fact]
    public async Task AnImporterThatThrowsFailsThatAssetAndNotTheRun() {
        Write("Assets/bad.pal", "palette bytes");
        Write("Assets/good.txt", "fine");
        var database = Database();

        var pipeline = new ImportPipeline(
            database,
            new ImporterRegistry().Add(new PaletteImporter { Explode = true }).AddFallback(new RawImporter()),
            Artifacts(),
            Files()
        );

        var outcomes = await pipeline.ImportAllAsync(TestContext.Current.CancellationToken);

        Assert.Contains(outcomes, outcome => !outcome.Succeeded);
        Assert.Contains(outcomes, outcome => outcome is { Succeeded: true, Importer: "RawImporter" });
    }

    /// <summary>
    ///     A record keeps the sub-asset each chunk holds, not just the ids. Without that the build
    ///     has four chunks out of a model and nothing to say which is the mesh, so it can address
    ///     none of them.
    /// </summary>
    [Fact]
    public async Task ARecordSaysWhichSubAssetEachChunkHolds() {
        Write("Assets/hero.pal", "palette bytes");
        var database = Database();
        var artifacts = Artifacts();

        var pipeline = new ImportPipeline(
            database,
            Registry(new PaletteImporter { SubAssetName = "swatch" }),
            artifacts,
            Files()
        );

        var outcome = await pipeline.ImportAsync(Entry(database, "Assets/hero.pal"), TestContext.Current.CancellationToken);
        var written = outcome.Record!.Artifacts;

        Assert.Equal(2, written.Count);
        Assert.Contains(written, artifact => artifact.SubAsset.IsMain);

        Assert.Contains(
            written,
            artifact => artifact.SubAsset == SubAssets.Derive("PaletteImporter", "Palette", "swatch")
        );

        Assert.All(written, artifact => Assert.True(artifacts.Exists(artifact.Id)));
    }

    [Fact]
    public async Task TheCacheSurvivesBeingWrittenAndReadBack() {
        Write("Assets/hero.pal", "palette bytes");
        Write("Assets/shared.pal", "shared palette");
        var database = Database();

        // With a sub-asset, so the round trip covers the pair the file stores and not just an id.
        var pipeline = new ImportPipeline(
            database,
            Registry(new PaletteImporter { SubAssetName = "swatch" }),
            Artifacts(),
            Files()
        );

        await pipeline.ImportAsync(Entry(database, "Assets/hero.pal"), TestContext.Current.CancellationToken);
        var path = Path.Combine(paths.Library, "ImportCache");
        pipeline.Cache.Save(path);

        var reloaded = new ImportCache();

        Assert.True(reloaded.TryLoad(path));
        Assert.Equal(pipeline.Cache.Count, reloaded.Count);

        var original = pipeline.Cache.Records.Single();
        Assert.True(reloaded.TryGet(original.Asset, out var restored));
        Assert.Equal(original.Key, restored!.Key);
        Assert.Equal(original.Artifacts, restored.Artifacts);
        Assert.Equal(original.FileDependencies, restored.FileDependencies);
    }

    /// <summary>A reloaded cache still hits, which is what makes a cold editor start fast.</summary>
    [Fact]
    public async Task AReloadedCacheStillSkipsAnUnchangedAsset() {
        Write("Assets/hero.pal", "palette bytes");
        var database = Database();
        var artifacts = Artifacts();
        var first = new ImportPipeline(database, Registry(new PaletteImporter()), artifacts, Files());
        var entry = Entry(database, "Assets/hero.pal");

        await first.ImportAsync(entry, TestContext.Current.CancellationToken);
        var path = Path.Combine(paths.Library, "ImportCache");
        first.Cache.Save(path);

        var reloaded = new ImportCache();
        reloaded.TryLoad(path);
        var second = new ImportPipeline(database, Registry(new PaletteImporter()), artifacts, Files(), reloaded);

        Assert.True((await second.ImportAsync(entry, TestContext.Current.CancellationToken)).WasCached);
    }

    /// <summary>
    ///     Content addressing means two assets that import to identical bytes are one chunk. A
    ///     hundred prefabs sharing one material cost one material.
    /// </summary>
    [Fact]
    public async Task TwoAssetsThatImportToTheSameBytesAreOneChunk() {
        Write("Assets/a.txt", "identical");
        Write("Assets/b.txt", "identical");
        var database = Database();

        var pipeline = new ImportPipeline(
            database,
            new ImporterRegistry().AddFallback(new RawImporter()),
            Artifacts(),
            Files()
        );

        var outcomes = await pipeline.ImportAllAsync(TestContext.Current.CancellationToken);
        var produced = outcomes.Where(outcome => outcome.Record is not null).Select(outcome => outcome.Record!).ToList();

        Assert.Equal(2, produced.Count);
        Assert.Equal(produced[0].Artifacts.Single(), produced[1].Artifacts.Single());
    }

    (ImportPipeline Pipeline, ObjectDatabase Artifacts, AssetDatabase Database) Pipeline() {
        var database = Database();
        var artifacts = Artifacts();
        return (new(database, Registry(new PaletteImporter()), artifacts, Files()), artifacts, database);
    }

    AssetDatabase Database() {
        var database = new AssetDatabase(paths);
        database.Scan();
        return database;
    }

    static ObjectDatabase Artifacts() {
        var files = new VirtualFileSystem();
        files.Mount(new VirtualPath("/"), new MemoryFileProvider());
        return new(new FileOdbBackend(files, new VirtualPath("/artifacts")));
    }

    IFileProvider Files() => new PhysicalFileProvider(root);

    static ImporterRegistry Registry(IAssetImporter importer) =>
        new ImporterRegistry().Add(importer).Add(new FolderImporter()).AddFallback(new RawImporter());

    static AssetEntry Entry(AssetDatabase database, string path) {
        Assert.True(database.TryGetByPath(path, out var entry));
        return entry;
    }

    void Write(string relativePath, string content) {
        var absolute = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    void EditMeta(string relativePath, Action<YamlMapping> edit, string? replacement = null) {
        var metaPath = Path.Combine(root, relativePath + ".meta");

        if (replacement is not null) {
            var guid = ((YamlScalar)((YamlMapping)YamlReader.Read(File.ReadAllText(metaPath)))["guid"]!).Value;
            File.WriteAllText(metaPath, replacement.Replace("{guid}", guid, StringComparison.Ordinal));
            return;
        }

        var document = (YamlMapping)YamlReader.Read(File.ReadAllText(metaPath));
        edit(document);
        File.WriteAllText(metaPath, YamlWriter.Write(document));
    }
}
