// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Testing;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The import half of [08](../../docs/plan/08-asset-pipeline-and-addressables.md)'s scale row,
///     gated the only way it can honestly be gated: as work rather than as time.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 08 § Testing states the import budget as a clock — "incremental import of
///         one asset &lt; 1 s" — and that number cannot be asserted here.</b> (This said "§ Exit
///         criteria"; doc 08 has no such section, and the row is the <c>Scale</c> one in § Testing.)
///         This suite runs ten test
///         hosts against one disk, and an import is almost entirely filesystem: a wall-clock ceiling
///         tight enough to mean anything would be a reading of the machine, which is the defect two
///         sweeps spent a week removing from this repository. What it would catch — the import
///         forgetting its cache, or acquiring a cost that grows faster than the project — is a
///         property of the algorithm, is an integer, and is the same integer on a laptop and on a
///         runner with nine other jobs on it.
///     </para>
///     <para>
///         So the budget is translated rather than dropped. "Incremental import of one asset" means
///         <see cref="ImportSummary.Imported" /> is <b>one</b>; the clock was only ever a proxy for
///         that. The times are still measured and put in every message, passing or failing, so a
///         reader gets the number this cost on the machine that ran it — but nothing branches on
///         them.
///     </para>
///     <para>
///         ⚠ <b>Ten thousand by default and not a hundred thousand</b>, for the reason the scan's own
///         budget test gives one directory along: at ten thousand the fixture's file writes do not
///         dominate, and the assertions are exact counts, so the size is chosen to keep the test
///         affordable rather than to reach a threshold. Doc 08's own number is a hundred thousand,
///         and <see cref="ScaleVariable" /> is how it is asked for — every assertion here is derived
///         from <see cref="Files" />, so the larger fixture is the same test rather than a second
///         one.
///     </para>
/// </remarks>
public sealed class ImportBudgetTests : IDisposable {
    /// <summary>The variable that raises the fixture to the size doc 08 actually names.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 08 § Testing says a hundred thousand and this suite measures ten, which is an
    ///         order of magnitude — and "the interesting failures in an index rebuild are usually the
    ///         ones that appear between those two numbers" is exactly right.</b> A target nothing
    ///         measures is not a target.
    ///     </para>
    ///     <para>
    ///         <b>Why it is a variable rather than a raised constant.</b> Every assertion here is an
    ///         exact integer computed from <see cref="Files" />, so the size is a one-line change
    ///         functionally; what it costs is a hundred thousand <c>File.WriteAllText</c> calls and
    ///         three full imports of them, on a real filesystem, on a machine already running ten
    ///         other test hosts. At that point the fixture's own writes dominate the thing being
    ///         measured, which is the trade the "ten thousand and not a hundred thousand" note below
    ///         is about — so the default stays where a developer can run it and the full size is
    ///         asked for by name, the way <c>VIXEN_FUZZ_SECONDS</c> is.
    ///     </para>
    ///     <para>
    ///         <c>VIXEN_IMPORT_SCALE=100000 dotnet test Editor/Vixen.Editor.Assets.Tests --filter
    ///         "FullyQualifiedName~ImportBudgetTests"</c>, and read the times out of the message.
    ///     </para>
    /// </remarks>
    public const string ScaleVariable = "VIXEN_IMPORT_SCALE";

    /// <summary>How many source files the synthetic project has.</summary>
    /// <remarks>
    ///     ⚠ <b>Read once into a static, not per call.</b> The fixture is written from it and
    ///     <see cref="Edited" /> is derived from it, and both are read in three phases of one test;
    ///     a property re-reading the environment would let the fixture and the expectation disagree
    ///     if anything ever set the variable mid-run.
    /// </remarks>
    static readonly int Files =
        int.TryParse(
            Environment.GetEnvironmentVariable(ScaleVariable),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var asked
        ) && asked >= 100
            ? asked
            : 10_000;

    /// <summary>The index of the one asset the incremental phase edits.</summary>
    /// <remarks>
    ///     Derived rather than written down, because it used to be <c>asset4242.bin</c> — a name that
    ///     is only in the project while <see cref="Files" /> is ten thousand, and a file that does not
    ///     exist would be *created* by the edit rather than changing one, so the incremental phase
    ///     would still see exactly one import and still pass while measuring something else.
    /// </remarks>
    static readonly int Edited = Files / 2;

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-import-budget-" + Guid.NewGuid().ToString("N"));

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
    ///     A synthetic project imports every asset once, nothing at all the second time, and exactly
    ///     the one file that changed the third — at ten thousand assets by default, and at whatever
    ///     <see cref="ScaleVariable" /> asks for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The three phases are one test and not three because the middle one is what makes the
    ///         other two mean anything. A counter that reported zero imports would pass "only one
    ///         asset was imported" forever; a counter wired to the entry count would pass the cold
    ///         phase forever. Asked for all three readings in one run, the same counter has to say
    ///         the fixture's own entry count — 10 101 at the default size — then 0, then 1, so it is
    ///         shown to move, in both directions, before it is believed. That is this test's own
    ///         anti-vacuity control and it is why the phases are not separable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A new workspace per phase.</b> Reusing one keeps the import cache in memory and
    ///         would gate a data structure rather than a build: what a second <c>vixen import</c> in
    ///         CI actually does is read <c>Library/ImportCache</c> back off disk, and an import cache
    ///         that is never persisted looks identical from inside one process.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task EveryAssetImportsOnceThenNothingThenOnlyTheOneThatChanged() {
        var project = Project();
        var paths = new ProjectPaths(project.Root);

        var cold = await ImportAsync(paths);

        // Nothing has been imported before, so everything is imported now — and the fact that this
        // number is the entry count rather than zero is what proves the counter is connected at all.
        Assert.Equal(project.Entries, cold.Imported);
        Assert.Equal(0, cold.Cached);
        Assert.Equal(0, cold.Failed);

        var warm = await ImportAsync(paths);

        // ⚠ The row every build system gets wrong, and the one an unwatched clock would have let
        // through: a second import of an untouched project runs no importer at all. If the artefact
        // key stops covering something, or the cache stops being written, this is 10 101 again.
        Assert.Equal(0, warm.Imported);
        Assert.Equal(project.Entries, warm.Cached);

        var edited = project.Blob(Edited);

        // ⚠ Asserted to exist before it is written. A path that had gone stale would be created by
        // the write, which is still exactly one import — so this phase would pass while measuring an
        // addition rather than a change, which is a different property with the same number.
        Assert.True(File.Exists(edited), $"{edited} is not in the fixture, so editing it adds an asset instead.");

        File.WriteAllText(edited, "edited", Encoding.UTF8);

        var incremental = await ImportAsync(paths);

        // Doc 08's "incremental import of one asset", said as work. One asset changed, so one
        // importer ran; the other ten thousand one hundred were skipped. This is the assertion the
        // one-second budget was standing in for, and unlike the second it is the same number on
        // every machine.
        Assert.Equal(1, incremental.Imported);
        Assert.Equal(project.Entries - 1, incremental.Cached);
        Assert.Equal(0, incremental.Failed);

        // ⚠ Reported, never compared. Three Stopwatch readings dominated by one shared disk are not
        // a claim about this code, and PerceptionCostTests settled the same question the same way —
        // but the number a reader wants is "what did this cost here", and it should come out of the
        // run rather than out of a document that will go stale. The condition is the weakest true
        // thing that is still a claim: a clock that never advanced is a clock that is not running.
        Assert.True(incremental.Elapsed > TimeSpan.Zero, Report(cold, warm, incremental));
    }

    /// <summary>
    ///     What an import costs per asset does not grow with the number of assets — the accidental
    ///     O(n²) that a wall-clock budget is really there to catch, counted instead of timed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An import is filesystem-bound, so "how much work" is "how many files were opened", and
    ///         that is a number the pipeline's own <see cref="IFileProvider" /> seam can be asked for
    ///         exactly. A pipeline whose per-asset cost is constant opens twice as many files for
    ///         twice as many assets; one that has acquired a walk over its peers opens four times as
    ///         many. Doubling and comparing the two ratios needs no threshold calibrated on a
    ///         machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The control is the second half of this test and it is not optional.</b> A ratio
    ///         test over a counter nobody has seen fail is worth nothing, and three instruments were
    ///         found in this repository last week that reported success without ever having run. So
    ///         the same measurement is taken again against an importer that deliberately reads every
    ///         peer — the exact regression this exists to catch — and the run is required to breach
    ///         the ceiling the honest one passed. If the counter dies, both halves read zero, the
    ///         control's assertion fails, and the test goes red rather than quietly green.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ImportingTwiceAsManyAssetsOpensTwiceAsManyFilesAndNotFourTimes() {
        // Small, because the control is quadratic by construction and the honest half only has to be
        // big enough for a doubling to be a doubling. The claim is a ratio, not a size — measured at
        // 150 it read ×2.00 against ×3.99, and the only thing raising it buys is a longer run.
        const int Small = 100;

        var linear = await Opens(Small, peerReading: false);
        var doubled = await Opens(Small * 2, peerReading: false);

        var quadraticSmall = await Opens(Small, peerReading: true);
        var quadraticDoubled = await Opens(Small * 2, peerReading: true);

        var honest = (double)doubled / linear;
        var broken = (double)quadraticDoubled / quadraticSmall;

        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"per-asset cost: {linear} opens at {Small} assets and {doubled} at {Small * 2} (×{honest:0.00}); "
            + $"an importer that reads its peers: {quadraticSmall} then {quadraticDoubled} (×{broken:0.00})."
        );

        // Twice the assets, twice the opens. The margin is for the fixed cost an import pays whatever
        // the project's size — the cache file, the settings — which shrinks the ratio rather than
        // growing it, so the ceiling only has to be above two.
        Assert.True(honest < 2.5, report);

        // ⚠ The control, and the reason the line above is evidence. This is the same measurement over
        // an importer with the defect in it, and it has to fail the ceiling the honest one passed —
        // a doubling of a quadratic is a quadrupling. A counter stuck at zero divides to NaN, which
        // is not greater than 2.5, so a dead instrument fails here instead of passing everywhere.
        Assert.True(broken > 2.5, report);
    }

    /// <summary>Writes the synthetic project and says how big it turned out.</summary>
    /// <remarks>
    ///     <para>
    ///         Sources only. The sidecars are the scan's to mint — that is what a project written by
    ///         hand or checked out for the first time looks like, and it is the case a CI content
    ///         build runs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The counts come back from the fixture rather than being computed a second time
    ///         here</b>, which is what this suite got out of <see cref="FixtureProject" />: the entry
    ///         count used to be <c>Files + (Files / 100) + 1</c>, written beside the loop that made
    ///         the folders it is counting, and the two would have drifted the first time either
    ///         moved. <see cref="FixtureProject.Written.Entries" /> is read off the disk the fixture
    ///         just wrote.
    ///     </para>
    /// </remarks>
    FixtureProject.Written Project() => new FixtureProject { Root = root, Blobs = Files }.Write();

    /// <summary>Imports the project through the call the CLI and the editor both make.</summary>
    static async Task<ImportSummary> ImportAsync(ProjectPaths paths) {
        var summary = await ContentPipeline.ImportAsync(
            new ProjectWorkspace(paths),
            "Windows",
            diagnostic => Assert.NotEqual(ImportSeverity.Error, diagnostic.Severity),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(0, summary.Failed);
        return summary;
    }

    /// <summary>Cold-imports a project of the given size and says how many files were opened.</summary>
    /// <param name="assets">How many source files to make.</param>
    /// <param name="peerReading">Whether the importer walks every other asset — the defect, for the control.</param>
    async Task<int> Opens(int assets, bool peerReading) {
        var directory = Path.Combine(root, "ratio", (peerReading ? "peers-" : "linear-") + assets);

        // Flat and .pal, because this half is about PaletteImporter rather than about a project: the
        // ratio is over the opens those files cause, so a folder that also imports would be a
        // constant added to both readings and to neither's meaning.
        new FixtureProject { Root = directory, Blobs = assets, BlobExtension = ".pal", BlobsPerFolder = 0 }.Write();

        var paths = new ProjectPaths(directory);
        var database = new AssetDatabase(paths);

        database.Scan();

        var files = new CountingFileProvider(new PhysicalFileProvider(directory));
        var artifacts = Artifacts();

        var pipeline = new ImportPipeline(
            database,
            new ImporterRegistry().Add(new PaletteImporter { ReadsEveryPeer = peerReading }).AddFallback(new RawImporter()),
            artifacts,
            files
        ) {
            // The peer-reading importer does not declare what it reads, which is the point: an
            // importer acquiring an undeclared walk over the project is the shape of the regression.
            EnforceDeclaredReads = false
        };

        await pipeline.ImportAllAsync(TestContext.Current.CancellationToken);

        return files.Reads;
    }

    static ObjectDatabase Artifacts() {
        var files = new VirtualFileSystem();
        files.Mount(new VirtualPath("/"), new MemoryFileProvider());
        return new(new FileOdbBackend(files, new VirtualPath("/artifacts")));
    }

    static string Report(ImportSummary cold, ImportSummary warm, ImportSummary incremental) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Files} files: cold {cold.Elapsed.TotalMilliseconds:0} ms for {cold.Imported}; "
            + $"unchanged {warm.Elapsed.TotalMilliseconds:0} ms for {warm.Cached} cached; "
            + $"one file changed {incremental.Elapsed.TotalMilliseconds:0} ms for {incremental.Imported}."
        );

    /// <summary>An <see cref="IFileProvider" /> that says how many times a file was opened for reading.</summary>
    sealed class CountingFileProvider(IFileProvider inner) : IFileProvider {
        int reads;

        /// <summary>How many files have been opened for reading through this provider.</summary>
        public int Reads => Volatile.Read(ref reads);

        public bool IsReadOnly => inner.IsReadOnly;

        public bool Exists(VirtualPath path) => inner.Exists(path);

        public bool TryGetEntry(VirtualPath path, out FileEntry entry) => inner.TryGetEntry(path, out entry);

        public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) =>
            inner.Enumerate(directory, recursive);

        public ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
            Interlocked.Increment(ref reads);
            return inner.OpenReadAsync(path, cancellationToken);
        }

        public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
            inner.OpenWriteAsync(path, cancellationToken);

        public bool Delete(VirtualPath path) => inner.Delete(path);

        public void CreateDirectory(VirtualPath path) => inner.CreateDirectory(path);
    }
}
