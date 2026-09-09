// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     An <c>ImportCache</c> written by one checkout produces cache hits in another, which is the
///     property a shared artefact cache is made of.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this decides.</b> Doc 08 says a shared artefact database makes CI content builds
///         cacheable across machines, and it is half true today: a shared <c>IOdbBackend</c> hands
///         every leg the artefact <em>chunks</em>, and no leg gets a <em>hit</em>, because
///         <see cref="ImportPipeline.Prepare" /> decides to reuse an import from an
///         <see cref="ImportRecord" /> and those live in a file under <c>Library/</c> that nothing
///         publishes. A leg with a warm chunk store and a cold cache re-runs every importer and
///         re-writes chunks it already had.
///     </para>
///     <para>
///         ⚠ <b>The two ways out are sized very differently, and this is the measurement that says
///         so.</b> "Teach the pipeline to reconstruct a hit from the key alone" runs into the
///         chicken-and-egg the key already documents — the key contains
///         <c>sorted(dependencyArtefactIds)</c>, and what <em>this</em> import will declare is not
///         known until it has run. "Ship the records too" needs the record file to mean the same
///         thing in another checkout, and nothing had ever checked that it does. It does: what is
///         stored is guids, hashes, artefact ids and project-relative paths, so a cache saved at one
///         root is a cache at any other. So option 1 is publish-and-restore plumbing rather than a
///         format, which is the finding.
///     </para>
///     <para>
///         ⚠ <b>The second project is a copy at a different root, not the same directory reopened.</b>
///         Reopening would pass against a cache full of absolute paths, machine-local ids or anything
///         else that happens to be true here — which is precisely the failure this exists to catch.
///     </para>
/// </remarks>
public sealed class SharedImportCacheTests : IDisposable {
    readonly string first = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));
    readonly string second = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));
    readonly string cacheFile =
        Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N") + ".cache");

    public SharedImportCacheTests() {
        Directory.CreateDirectory(Path.Combine(first, "Assets"));
    }

    public void Dispose() {
        foreach (var path in new[] { first, second }) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            } catch (IOException) {
                // A temporary directory that would not go is not a test failure.
            }
        }

        try {
            File.Delete(cacheFile);
        } catch (IOException) {
            // Likewise.
        }
    }

    [Fact]
    public async Task ACacheSavedAtOneRootIsAHitAtAnother() {
        File.WriteAllText(Path.Combine(first, "Assets", "shared.pal"), "the shared palette");
        File.WriteAllText(Path.Combine(first, "Assets", "hero.pal"), "palette bytes");

        // One artefact store, mounted by both, standing in for the shared backend the whole design
        // is about. The chunks are warm in the second checkout because they are the same object.
        var artifacts = Artifacts();

        var (before, database) = Pipeline(first, artifacts, new());
        var outcomes = await before.ImportAllAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(outcomes);
        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));

        // ⚠ Not a filtered count: every asset in the project has to have run, or the assertion below
        // is about whichever ones happened to.
        Assert.Equal(database.Count, outcomes.Count);
        Assert.DoesNotContain(outcomes, outcome => outcome.WasCached);

        before.Cache.Save(cacheFile);

        Copy(first, second);

        var restored = new ImportCache();

        Assert.True(restored.TryLoad(cacheFile));
        Assert.Equal(before.Cache.Count, restored.Count);

        var (after, elsewhere) = Pipeline(second, artifacts, restored);
        var again = await after.ImportAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(elsewhere.Count, again.Count);
        Assert.All(again, outcome => Assert.True(outcome.WasCached, outcome.Importer));
    }

    /// <summary>And the control: without the records, the same warm chunk store hits nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the half that makes the test above mean something</b>, and it is also the
    ///     measurement the issue was filed about. If a shared chunk store were enough on its own,
    ///     both runs would be cached and the first test would be asserting the artefact database
    ///     rather than the records.
    /// </remarks>
    [Fact]
    public async Task WithoutTheRecordsTheSameWarmChunkStoreHitsNothing() {
        File.WriteAllText(Path.Combine(first, "Assets", "shared.pal"), "the shared palette");
        File.WriteAllText(Path.Combine(first, "Assets", "hero.pal"), "palette bytes");

        var artifacts = Artifacts();
        var (before, _) = Pipeline(first, artifacts, new());

        await before.ImportAllAsync(TestContext.Current.CancellationToken);

        Copy(first, second);

        var (after, _) = Pipeline(second, artifacts, new());
        var again = await after.ImportAllAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(again);
        Assert.DoesNotContain(again, outcome => outcome.WasCached);
    }

    static ObjectDatabase Artifacts() {
        var files = new VirtualFileSystem();

        files.Mount(new VirtualPath("/"), new MemoryFileProvider());

        return new(new FileOdbBackend(files, new VirtualPath("/artifacts")));
    }

    static (ImportPipeline Pipeline, AssetDatabase Database) Pipeline(
        string root,
        ObjectDatabase artifacts,
        ImportCache cache
    ) {
        var database = new AssetDatabase(new ProjectPaths(root));

        database.Scan();

        var registry = new ImporterRegistry()
            .Add(new PaletteImporter())
            .Add(new FolderImporter())
            .AddFallback(new RawImporter());

        return (new(database, registry, artifacts, new PhysicalFileProvider(root), cache), database);
    }

    /// <summary>Copies the project, sidecars and all, the way a second checkout would have it.</summary>
    static void Copy(string from, string to) {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) {
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
