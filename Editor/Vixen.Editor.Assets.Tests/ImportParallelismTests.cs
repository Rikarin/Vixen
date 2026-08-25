// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     N imports at once produce what one at a time produced — every artefact key, every decision to
///     re-import, and the order the outcomes come back in.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a determinism gate, and determinism gates fail intermittently or not at
///         all.</b> A parallel import that got the ordering wrong would still produce a correct-looking
///         build most of the time — the wrong bytes need two particular assets to finish in a
///         particular order — so the assertion is repeated, the fixture importer sleeps for a random
///         few milliseconds so that no two runs interleave the same way, and the corpus is built to
///         put dependencies on both sides of their dependants in path order.
///     </para>
///     <para>
///         ⚠ <b>The anti-vacuity control is the concurrency high-water mark, and it is not
///         optional.</b> Every assertion below would pass for ever against a pipeline that quietly ran
///         everything sequentially — which is precisely the state this task started from. So the
///         fixture importer counts how many imports were in flight at once, and the test requires the
///         sequential leg to peak at exactly one and the parallel leg to peak at more than one. A
///         parallelism that stopped happening fails here rather than passing everywhere.
///     </para>
///     <para>
///         <b>No wall clock appears in any assertion.</b> The two legs are compared on what they
///         produced and on how much overlapped, both of which are integers and neither of which
///         changes when the machine is busy. What a parallel import <i>saves</i> is not a claim this
///         suite is in a position to make.
///     </para>
/// </remarks>
public sealed class ImportParallelismTests : IDisposable {
    /// <summary>How many assets with nothing to wait for. These are what actually overlap.</summary>
    const int Bulk = 40;

    /// <summary>How many times the parallel leg is run against the same expectation.</summary>
    /// <remarks>
    ///     One run of a scheduler proves nothing about the next one. Eight is chosen so that a
    ///     one-in-four race is missed about once in six thousand runs rather than once in eight.
    /// </remarks>
    const int Attempts = 8;

    /// <summary>What the parallel leg is allowed to run at once, regardless of the machine's cores.</summary>
    /// <remarks>
    ///     Pinned rather than defaulted, because <see cref="ImportPipeline.DefaultConcurrency" /> is
    ///     one on a single-core runner and this test would then assert nothing on exactly the machine
    ///     where it is cheapest to run.
    /// </remarks>
    const int Wide = 16;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-import-parallel-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>
    ///     Sixteen imports at once agree with one at a time about every key, every re-import and the
    ///     order of the answer — eight times over, on two projects at different paths.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two legs are separate projects rather than two passes over one, so the comparison
    ///         also covers the thing a cross-machine byte gate covers: a key that had picked up a path
    ///         or a GUID would differ here before it differed in CI.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The warm half is the one that catches an ordering bug in the <i>decision</i>
    ///         rather than in the key.</b> One asset's dependency is edited on each side and one
    ///         dependant sits before its dependency in path order — so the correct answer is that it
    ///         is <i>not</i> re-imported in this run, and converges in the next. A scheduler that
    ///         waited for dependencies rather than for earlier ones would re-import it, produce a
    ///         perfectly reasonable build, and disagree with every other machine.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task SixteenImportsAtOnceProduceWhatOneAtATimeProduced() {
        var sequential = await TrialAsync("one-at-a-time", concurrency: 1);

        // The control for the control: at a concurrency of one the pipeline is the loop this
        // replaced, so anything above one here would mean the probe is counting something else.
        Assert.Equal(1, sequential.Peak);

        // And a first import of a project imports all of it, so a counter that had come loose from
        // the run would be caught here rather than agreeing with itself on both legs.
        Assert.Equal(sequential.ColdOrder.Count, sequential.ColdImported);
        Assert.True(sequential.ColdImported > Bulk, "the corpus is the bulk plus both ends of every pair");

        for (var attempt = 0; attempt < Attempts; attempt++) {
            var parallel = await TrialAsync(
                string.Create(CultureInfo.InvariantCulture, $"sixteen-at-once-{attempt}"),
                Wide
            );

            var report = string.Create(
                CultureInfo.InvariantCulture,
                $"attempt {attempt}: peaked at {parallel.Peak} imports in flight against the sequential leg's "
                + $"{sequential.Peak}."
            );

            // ⚠ Without this the three assertions below are satisfied by doing nothing in parallel.
            Assert.True(parallel.Peak > 1, report);

            Assert.Equal(sequential.ColdKeys, parallel.ColdKeys);
            Assert.Equal(sequential.ColdOrder, parallel.ColdOrder);
            Assert.Equal(sequential.WarmReimported, parallel.WarmReimported);
            Assert.Equal(sequential.ColdImported, parallel.ColdImported);
            Assert.Equal(sequential.WarmImported, parallel.WarmImported);
        }
    }

    /// <summary>
    ///     The dependencies the corpus is built around, as (dependant, dependency) file names.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both directions, on purpose, and adjacent on purpose.</b> A dependency that comes
    ///         <i>later</i> in path order is the case a sequential loop answers with "it has not run
    ///         yet, so its old artefacts are what my key is made of" — the one a naive parallel run
    ///         gets wrong by seeing the new ones. A dependency that comes <i>earlier</i> is the
    ///         opposite: its new artefacts are what the key is made of, and a run that did not wait
    ///         would price the old ones. Adjacent, because two assets a thousand apart are never in
    ///         flight together and the race would never be attempted.
    ///     </para>
    ///     <para>
    ///         Eight of each rather than one of each, because each is a race that has to be lost to be
    ///         noticed and sixteen chances a run is a great deal better than two.
    ///     </para>
    /// </remarks>
    static IReadOnlyList<(string Dependant, string Dependency)> Pairs { get; } = [
        .. Enumerable.Range(0, 8).SelectMany(slot => new[] {
                // The dependant sorts before its dependency: 'a' then 'b' inside the same bulk slot.
                (Named(slot * 5, "a"), Named(slot * 5, "b")),

                // And after it: 'd' depends on 'c'.
                (Named((slot * 5) + 2, "d"), Named((slot * 5) + 2, "c"))
            }
        )
    ];

    /// <summary>What the whole of one leg produced, as things that are equal or are not.</summary>
    /// <param name="ColdKeys">Every asset's artefact key after a first import, by project-relative path.</param>
    /// <param name="ColdOrder">The order the outcomes came back in, as paths.</param>
    /// <param name="ColdImported">How many ran an importer.</param>
    /// <param name="WarmReimported">Which assets ran an importer after one dependency was edited.</param>
    /// <param name="WarmImported">How many did.</param>
    /// <param name="Peak">The most imports that were ever in flight at one moment.</param>
    sealed record Leg(
        IReadOnlyList<string> ColdKeys,
        IReadOnlyList<string> ColdOrder,
        int ColdImported,
        IReadOnlyList<string> WarmReimported,
        int WarmImported,
        int Peak
    );

    /// <summary>Builds a project of its own, imports it cold, edits one dependency and imports again.</summary>
    async Task<Leg> TrialAsync(string name, int concurrency) {
        var paths = new ProjectPaths(Path.Combine(root, name));

        Directory.CreateDirectory(paths.Assets);

        foreach (var file in Sources()) {
            File.WriteAllText(Path.Combine(paths.Assets, file), "content of " + file, Encoding.UTF8);
        }

        var database = new AssetDatabase(paths);

        database.Scan();

        // ⚠ After the scan, because the GUIDs are what it mints. An importer declaring a dependency
        // has to name one, and a project's GUIDs are not knowable before its sidecars exist.
        var dependencies = new Dictionary<string, AssetId[]>(StringComparer.Ordinal);

        foreach (var (dependant, dependency) in Pairs) {
            Assert.True(database.TryGetByPath("Assets/" + dependency, out var target));
            dependencies["/Assets/" + dependant] = [target.Guid];
        }

        var probe = new ConcurrencyProbe();
        var artifacts = Artifacts();

        var pipeline = new ImportPipeline(
            database,
            new ImporterRegistry().Add(new ChainImporter { Dependencies = dependencies, Probe = probe })
                .Add(new FolderImporter())
                .AddFallback(new RawImporter()),
            artifacts,
            new PhysicalFileProvider(paths.Root)
        ) {
            MaxConcurrency = concurrency
        };

        var cold = await pipeline.ImportAllAsync(TestContext.Current.CancellationToken);

        Assert.All(cold, outcome => Assert.True(outcome.Succeeded));

        var pathOf = database.Entries.ToDictionary(entry => entry.Guid, entry => entry.Path);
        var keys = cold.Where(outcome => outcome.Record is not null)
            .Select(outcome => $"{pathOf[outcome.Asset]} {outcome.Record!.Key.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // One asset that something later depends on, and one that something *earlier* depends on.
        // The second is the interesting one: its dependant is decided before it runs, so the correct
        // answer is that the dependant does not re-import in this pass.
        Edit(paths, Pairs[0].Dependency);
        Edit(paths, Pairs[1].Dependency);

        var warm = await pipeline.ImportAllAsync(TestContext.Current.CancellationToken);

        Assert.All(warm, outcome => Assert.True(outcome.Succeeded));

        return new(
            keys,
            [.. cold.Select(outcome => pathOf[outcome.Asset])],
            cold.Count(outcome => outcome is { WasCached: false, Importer: not null }),
            [
                .. warm.Where(outcome => outcome is { WasCached: false, Importer: not null })
                    .Select(outcome => pathOf[outcome.Asset])
                    .Order(StringComparer.Ordinal)
            ],
            warm.Count(outcome => outcome is { WasCached: false, Importer: not null }),
            probe.Peak
        );
    }

    static void Edit(ProjectPaths paths, string file) =>
        File.WriteAllText(Path.Combine(paths.Assets, file), "edited content of " + file, Encoding.UTF8);

    /// <summary>Every source file the corpus has, which is the bulk plus both ends of every pair.</summary>
    static IEnumerable<string> Sources() =>
        Enumerable.Range(0, Bulk)
            .Select(index => Named(index, string.Empty))
            .Concat(Pairs.SelectMany(pair => new[] { pair.Dependant, pair.Dependency }));

    /// <summary>
    ///     A file name that sorts where it is wanted: zero-padded so that ordinal order is numeric
    ///     order, with a suffix that lands the pair between two bulk assets rather than at the end.
    /// </summary>
    static string Named(int index, string suffix) =>
        string.Create(CultureInfo.InvariantCulture, $"bulk-{index:D3}{suffix}.chain");

    static ObjectDatabase Artifacts() {
        var files = new VirtualFileSystem();
        files.Mount(new VirtualPath("/"), new MemoryFileProvider());
        return new(new FileOdbBackend(files, new VirtualPath("/artifacts")));
    }
}

/// <summary>How many imports were in flight at once, and the most there ever were.</summary>
/// <remarks>
///     Shared by every import in one run and written from all of them, so both numbers move under
///     <see cref="Interlocked" />. It is the instrument that says whether a "parallel" import was
///     parallel, so a probe that is itself racy would be a gate reporting on itself.
/// </remarks>
public sealed class ConcurrencyProbe {
    int inFlight;
    int peak;

    /// <summary>The most imports that were ever running at one moment.</summary>
    public int Peak => Volatile.Read(ref peak);

    /// <summary>Counts one import in, and remembers the mark if it is a new one.</summary>
    public void Enter() {
        var now = Interlocked.Increment(ref inFlight);
        var seen = Volatile.Read(ref peak);

        while (now > seen) {
            var was = Interlocked.CompareExchange(ref peak, now, seen);

            if (was == seen) {
                return;
            }

            seen = was;
        }
    }

    /// <summary>Counts one import out.</summary>
    public void Leave() => Interlocked.Decrement(ref inFlight);
}

/// <summary>Settings for the fixture importer this suite imports with.</summary>
[DataContract("ChainImporter")]
public sealed record ChainImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>
///     An importer that declares a dependency chosen per file, takes a random few milliseconds, and
///     says how many copies of itself were running.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The delay is the point, not an inconvenience.</b> Two imports that always take the
///         same time interleave the same way every run, and a determinism test over one interleaving
///         is a test of one case. A few random milliseconds is what makes eight attempts eight
///         different orderings.
///     </para>
///     <para>
///         Everything it is configured with is <c>init</c>-only, because one instance is registered
///         once and re-entered by every worker — which is the contract every built-in importer keeps
///         and the reason a shared registry is safe to run in parallel at all.
///     </para>
/// </remarks>
[Importer(".chain")]
public sealed class ChainImporter : AssetImporter<ChainImportSettings> {
    /// <summary>Which assets each source declares a dependency on, by virtual source path.</summary>
    public IReadOnlyDictionary<string, AssetId[]> Dependencies { get; init; } =
        new Dictionary<string, AssetId[]>(StringComparer.Ordinal);

    /// <summary>Where to report that an import started and finished.</summary>
    public ConcurrencyProbe? Probe { get; init; }

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        ChainImportSettings settings,
        CancellationToken cancellationToken
    ) {
        Probe?.Enter();

        try {
            foreach (var dependency in Dependencies.GetValueOrDefault(context.SourcePath.ToString(), [])) {
                context.DependsOn(dependency);
            }

            await Task.Delay(Random.Shared.Next(0, 4), cancellationToken).ConfigureAwait(false);

            // The source's own bytes, so that editing a file moves its artefact's id — which is what
            // a dependant's key is made of, and what makes a dependency's change reach it.
            await using var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false);
            using var content = new MemoryStream();

            await source.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
            context.Write(SubAssetId.Main, "Chain", content.ToArray());

            return context.Finish();
        } finally {
            Probe?.Leave();
        }
    }
}
