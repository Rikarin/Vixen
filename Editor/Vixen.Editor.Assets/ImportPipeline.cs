// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.ExceptionServices;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;

namespace Vixen.Editor.Assets;

/// <summary>What happened to one asset.</summary>
/// <param name="Asset">Which asset.</param>
/// <param name="Importer">Which importer claimed it, or <see langword="null" /> if none did.</param>
/// <param name="WasCached">Whether the importer was skipped because nothing had changed.</param>
/// <param name="Succeeded">Whether it came out with no errors.</param>
/// <param name="Record">What it produced, or the previous record on a cache hit.</param>
/// <param name="Diagnostics">Everything the importer said. Empty on a cache hit.</param>
public sealed record ImportOutcome(
    AssetId Asset,
    string? Importer,
    bool WasCached,
    bool Succeeded,
    ImportRecord? Record,
    IReadOnlyList<ImportDiagnostic> Diagnostics
);

/// <summary>Turns source files into artefacts, and skips the ones that have not changed.</summary>
/// <remarks>
///     <para>
///         The order is fixed and each step exists for a reason: read the sidecar, decide which
///         importer claims the file, resolve the per-target overrides, hash the source and the
///         resolved settings, compute the key, and only then decide whether to run anything.
///     </para>
///     <para>
///         <b>Artefacts go into the content-addressed object database</b>, so two assets that import
///         to identical bytes are one chunk — a hundred prefabs sharing one material cost one
///         material — and an artefact's id is a checksum of itself.
///     </para>
///     <para>
///         <b>The sidecar is written back through the node tree.</b> An import records its
///         <c>sourceHash</c> and the sub-assets it found, and the byte-fidelity emitter means every
///         other line of the file — the settings, the addressable block, the comments — comes back
///         out exactly as it went in. That is what makes an import a diff of the two lines it
///         changed rather than of the whole file.
///     </para>
/// </remarks>
public sealed class ImportPipeline {
    /// <summary>
    ///     Settings keys that an import <i>writes</i> rather than reads, and which are therefore
    ///     excluded from the settings hash.
    /// </summary>
    /// <remarks>
    ///     Found by the tests rather than reasoned about: an import records <c>sourceHash</c> and
    ///     <c>version</c> into the sidecar when it finishes, so hashing them would mean every import
    ///     changed the thing it had just hashed and no second import would ever hit the cache. The
    ///     settings hash covers the author's settings; both of these are already first-class parts of
    ///     the key in their own right.
    /// </remarks>
    static readonly string[] RecordedByImport = ["version", "sourceHash"];

    readonly AssetDatabase database;
    readonly ImporterRegistry importers;
    readonly ObjectDatabase artifacts;
    readonly IFileProvider files;

    /// <summary>What the last import of each asset produced.</summary>
    public ImportCache Cache { get; }

    /// <summary>Which build target to import for — <c>Windows</c>, <c>Android/Vulkan</c>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Whether an importer that reads an undeclared file fails.</summary>
    public bool EnforceDeclaredReads { get; set; } = true;

    /// <summary>How many imports <see cref="ImportAllAsync" /> may run at once.</summary>
    /// <remarks>
    ///     <para>
    ///         Cores minus one by default, which is <c>WorkerHost.DefaultWorkerCount</c>'s number for
    ///         doc 08's reason — the coordinator is still doing work of its own, and an import is
    ///         mostly filesystem, so the last core is worth more as headroom than as a worker.
    ///     </para>
    ///     <para>
    ///         <b>One is not a special case, it is the sequential loop.</b> Setting this to one gives
    ///         exactly the run order this had before there was a scheduler, which is what makes it
    ///         usable as a control in a test rather than only as a throttle.
    ///     </para>
    /// </remarks>
    public int MaxConcurrency { get; set; } = DefaultConcurrency;

    /// <summary>How many imports run at once when nobody says.</summary>
    public static int DefaultConcurrency => Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Where importers actually run.</summary>
    /// <remarks>
    ///     In this process by default. <c>Tools/Vixen.AssetCompiler</c> supplies one that runs them in
    ///     worker processes, which buys the thing an exception handler cannot: surviving an importer
    ///     that takes its process down. Everything else about an import is the same either way, which
    ///     is the whole point of the seam being here and not further out.
    /// </remarks>
    public IImportExecutor Executor { get; set; }

    /// <summary>Sets up a pipeline over a project.</summary>
    /// <param name="database">The project's assets.</param>
    /// <param name="importers">Which importers this build has.</param>
    /// <param name="artifacts">Where artefacts are stored.</param>
    /// <param name="files">Where source files are read from, rooted at the project.</param>
    /// <param name="cache">What previous imports produced, or <see langword="null" /> for a fresh one.</param>
    public ImportPipeline(
        AssetDatabase database,
        ImporterRegistry importers,
        ObjectDatabase artifacts,
        IFileProvider files,
        ImportCache? cache = null
    ) {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(files);

        this.database = database;
        this.importers = importers;
        this.artifacts = artifacts;
        this.files = files;
        Cache = cache ?? new ImportCache();
        Executor = new InProcessImportExecutor(importers, files);
    }

    /// <summary>Imports everything in the project that needs it.</summary>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What happened to each asset, in path order.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Deciding is parallel; importing is parallel; the answer is the sequential one.</b>
    ///         Working out whether an asset needs anything done is a read of its sidecar, a parse, a
    ///         hash of its source and a lookup in the cache — no writes, anywhere — and in a project
    ///         where nothing has changed it is the whole cost of the command. Measured on a
    ///         ten-thousand-asset project it was the difference between the phase's one-second budget
    ///         being met and being missed by half. The imports then run
    ///         <see cref="MaxConcurrency" />-at-a-time over the same list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What made the imports sequential was one thing, and it is preserved rather than
    ///         given up.</b> A dependency's artefact ids are part of a dependent's key, so what an
    ///         asset's key <i>is</i> depends on which of its dependencies have already re-imported —
    ///         and in a path-ordered loop the answer is exactly "the ones before it in path order".
    ///         Nine importers declare asset dependencies through <c>AssetReferenceScan</c>, so this is
    ///         the ordinary case and not an exotic one, and dispatching without it would make a
    ///         build's bytes depend on how many cores the machine has.
    ///     </para>
    ///     <para>
    ///         <b>So an asset waits for its dependencies and for nothing else.</b> Before asset
    ///         <i>i</i> reads another asset's record — to ask whether a cached decision still stands,
    ///         or to price a fresh key — it waits for that asset if and only if that asset comes
    ///         earlier in path order, and reads the record as it was before the run if it comes later.
    ///         That is the sequential loop's view of the cache, reproduced exactly, so
    ///         <see cref="MaxConcurrency" /> changes when work happens and never what it produces.
    ///     </para>
    ///     <para>
    ///         <b>Waiting cannot deadlock, by construction.</b> An asset only ever waits on a
    ///         <i>lower</i> index, and indices are handed to workers in increasing order — so
    ///         whatever an asset is waiting for has already been given to some worker, and the lowest
    ///         index still running is waiting for nothing.
    ///     </para>
    ///     <para>
    ///         Running importers in separate <i>processes</i> is a different axis and still the
    ///         out-of-process worker's, in
    ///         [08](../../docs/plan/08-asset-pipeline-and-addressables.md): that one buys crash
    ///         isolation, and it composes with this one, because a
    ///         <see cref="IImportExecutor" /> now has several jobs in flight instead of one.
    ///     </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<ImportOutcome>> ImportAllAsync(CancellationToken cancellationToken = default) {
        var entries = database.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var prepared = new Prepared[entries.Length];

        Parallel.For(
            0,
            entries.Length,
            new ParallelOptions { CancellationToken = cancellationToken },
            index => prepared[index] = Prepare(entries[index], default)
        );

        var run = new SequentialView(entries, Cache);
        var outcomes = new ImportOutcome[entries.Length];
        var dispensed = -1;
        ExceptionDispatchInfo? failure = null;

        async Task WorkAsync() {
            while (true) {
                // Increasing order, which is what makes waiting on a lower index safe: anything an
                // asset can wait for has already been given to a worker.
                var index = Interlocked.Increment(ref dispensed);

                if (index >= entries.Length) {
                    return;
                }

                try {
                    if (Volatile.Read(ref failure) is not null) {
                        // Still answered, and answered for every remaining index. A run that stopped
                        // replying would leave whatever was waiting on this asset waiting for ever,
                        // which turns a failed import into a hung command.
                        run.Abandon(index);
                        continue;
                    }

                    outcomes[index] = await ImportOneAsync(run, index, entries[index], prepared[index], cancellationToken)
                        .ConfigureAwait(false);

                    run.Finish(index, outcomes[index] is { WasCached: false, Record: not null });
                } catch (Exception thrown) {
                    // The first one is the one that gets reported; the ones after it are the
                    // cancellations this one caused.
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(thrown), null);
                    run.Abandon(index);
                }
            }
        }

        var workers = new Task[Math.Clamp(MaxConcurrency, 1, Math.Max(entries.Length, 1))];

        for (var worker = 0; worker < workers.Length; worker++) {
            // ⚠ Not given the cancellation token. A worker cancelled before it started would leave
            // its share of the list undispensed, and every asset waiting on one of those would wait
            // for ever. Cancellation arrives through the imports themselves, which is where it can
            // be answered.
            workers[worker] = Task.Run(WorkAsync, CancellationToken.None);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        failure?.Throw();

        return outcomes;
    }

    /// <summary>Imports one asset, unless nothing about it has changed.</summary>
    /// <param name="entry">The asset.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What happened.</returns>
    public ValueTask<ImportOutcome> ImportAsync(AssetEntry entry, CancellationToken cancellationToken = default) =>
        ImportAsync(entry, default, cancellationToken);

    /// <summary>
    ///     Acts on a whole-project decision: takes the cached answer if it still stands, and imports
    ///     otherwise — after waiting for whatever the sequential loop would have run first.
    /// </summary>
    /// <remarks>
    ///     Both waits are the same set when the decision was reusable, and the second returns at
    ///     once. The first is not redundant, because the importing branch reads exactly those records
    ///     too: <see cref="Prepare" /> prices the previous import's dependencies to work out whether
    ///     its key still holds.
    /// </remarks>
    async ValueTask<ImportOutcome> ImportOneAsync(
        SequentialView run,
        int index,
        AssetEntry entry,
        Prepared decision,
        CancellationToken cancellationToken
    ) {
        var view = new View(run, index);

        await view.WaitForEarlierAsync(run.RecordFor(entry.Guid, index)?.AssetDependencies ?? [])
            .ConfigureAwait(false);

        if (decision.Reusable is { } record
            && !await view.WaitForEarlierAsync(record.AssetDependencies).ConfigureAwait(false)) {
            return new(entry.Guid, decision.Importer!.Name, true, true, record, []);
        }

        return await ImportAsync(entry, view, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<ImportOutcome> ImportAsync(AssetEntry entry, View view, CancellationToken cancellationToken) {
        var decision = Prepare(entry, view);

        if (decision.Refusal is { } refusal) {
            return Refused(entry, refusal);
        }

        if (decision.Importer is not { } importer) {
            return new(entry.Guid, null, false, true, null, []);
        }

        if (decision.Reusable is { } reusable) {
            return new(entry.Guid, importer.Name, true, true, reusable, []);
        }

        var metaPath = decision.MetaPath;
        var root = decision.Root!;
        var resolved = decision.Resolved!;
        var source = decision.Source;
        var sourceHash = decision.SourceHash;
        var settingsHash = decision.SettingsHash;

        // Where the importer actually runs, which may not be here. Everything around it — the
        // decision, the key, the sidecar, the cache — stays in one copy whichever executor is in
        // force; what crosses the boundary is one asset's worth of work. See IImportExecutor.
        var result = await Executor
            .ExecuteAsync(
                new(entry.Guid, importer.Name, source, YamlWriter.Write(resolved), Target, EnforceDeclaredReads),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!result.Succeeded) {
            // Nothing is written, and — deliberately — nothing already there is thrown away. A
            // record is a true statement about the input it was made from; a failure on a *different*
            // input does not falsify it. Discarding it would only mean that an author who broke a
            // file and then reverted it paid for a re-import they did not need. This started out as
            // a Cache.Forget call, and survived a sabotage that removed it, which is how it came to
            // be examined at all.
            return new(entry.Guid, importer.Name, false, false, null, result.Diagnostics);
        }

        // Each chunk keeps the sub-asset it holds, because the id alone cannot be addressed: two
        // meshes out of one model are two chunks and the build has to know which is which.
        var stored = result.Artifacts
            .Select(artifact => new StoredArtifact(
                    artifact.SubAsset,
                    artifacts.WriteRaw(TypeIdOf(artifact.Type), [], artifact.Content.Span)
                )
            )
            .ToArray();

        var fileDependencies = result.FileDependencies.Order(StringComparer.Ordinal).ToArray();
        var assetDependencies = result.AssetDependencies.Order().ToArray();

        // ⚠ What an import declares is only known once it has run, so this is the second wait and
        // not a repeat of the first: an asset can announce a dependency the previous import did not
        // have — a scene that gained a reference to a material — and that dependency's artefacts are
        // about to go into this asset's key. Nothing has been written yet, so waiting here costs
        // ordering and not work.
        await view.WaitForEarlierAsync(assetDependencies).ConfigureAwait(false);

        // The key stored is the one describing what the import *actually* depended on, not the
        // speculative one it was tested against. Storing the speculative key would mean the first
        // import of every asset was followed by a second one that also ran — the first computed its
        // key knowing no dependencies, and the second would compute a different one knowing them.
        var fresh = new ImportRecord(
            entry.Guid,
            importer.Name,
            importer.Version,
            Key(importer, sourceHash, settingsHash, fileDependencies, assetDependencies, view),
            stored,
            fileDependencies,
            assetDependencies
        );

        Cache.Set(fresh);
        WriteBack(metaPath, root, importer, sourceHash, result.SubAssets);
        return new(entry.Guid, importer.Name, false, true, fresh, result.Diagnostics);
    }

    /// <summary>
    ///     Everything that can be worked out about an asset without running anything, and the answer
    ///     to whether anything needs to run.
    /// </summary>
    /// <param name="MetaPath">Where its sidecar is.</param>
    /// <param name="Root">The parsed sidecar.</param>
    /// <param name="Importer">Which importer claims it, or <see langword="null" /> if none does.</param>
    /// <param name="Resolved">Its settings node with per-target overrides applied.</param>
    /// <param name="Source">Its source file.</param>
    /// <param name="SourceHash">That file's content hash.</param>
    /// <param name="SettingsHash">The hash of the settings the author wrote.</param>
    /// <param name="Reusable">The previous record, if this import can be skipped entirely.</param>
    /// <param name="Refusal">Why nothing can be decided, if that is the case.</param>
    readonly record struct Prepared(
        string MetaPath,
        YamlMapping? Root,
        IAssetImporter? Importer,
        YamlMapping? Resolved,
        VirtualPath Source,
        ObjectId SourceHash,
        ObjectId SettingsHash,
        ImportRecord? Reusable,
        string? Refusal
    );

    /// <summary>
    ///     Reads the sidecar, resolves the overrides, hashes what the key is made of, and decides
    ///     whether the previous import still stands.
    /// </summary>
    /// <remarks>
    ///     <b>It writes nothing and it throws nothing</b>, which is what lets a whole project's worth
    ///     of these run at once. Everything it touches — the sidecar on disk, the source file, the
    ///     importer registry, the cache — is read-only for the duration.
    /// </remarks>
    Prepared Prepare(AssetEntry entry, View view) {
        var metaPath = AssetMetaFile.PathFor(database.Paths.Absolute(entry.Path));

        try {
            if (YamlReader.Read(File.ReadAllText(metaPath)) is not YamlMapping root) {
                return Refusal(metaPath, "Its .meta is not a mapping.");
            }

            if (!TryChooseImporter(entry, root, out var importer)) {
                return new(metaPath, root, null, null, default, default, default, null, null);
            }

            // ⚠ The recorded settings belong to the importer that recorded them. When the chosen
            // importer is not that one — a format that used to fall through to RawImporter and now
            // has a compiler of its own — binding the old block against the new settings type fails
            // with "its import settings do not fit", about a file nobody has touched. Starting from
            // an empty block is the right answer: the settings of an importer that no longer runs are
            // not settings, and every one of them has a default.
            var recorded = root["importer"] as YamlMapping;

            var settingsNode = recorded is not null && recorded.Tag == importer.Name
                ? recorded
                : new YamlMapping { Tag = importer.Name };
            var resolved = Target.Length == 0 ? settingsNode : TargetOverrides.Resolve(settingsNode, Target);
            var forHashing = new YamlMapping { Tag = resolved.Tag };

            foreach (var (settingKey, value) in resolved.Entries) {
                if (!RecordedByImport.Contains(settingKey, StringComparer.Ordinal)) {
                    forHashing.Set(settingKey, value);
                }
            }

            var source = new VirtualPath("/" + entry.Path);
            var sourceHash = entry.IsFolder ? ObjectId.Empty : HashOfSource(entry);
            var settingsHash = ArtifactKey.HashOf(YamlWriter.Write(forHashing));
            var previous = view.RecordOf(Cache, entry.Guid);

            // Computed from what the *previous* import declared, because what this one will declare
            // is not known until it has run. If nothing it depended on has moved, this matches the
            // key that import stored and there is nothing to do.
            var reusable = previous is not null
                && previous.Key == Key(
                    importer,
                    sourceHash,
                    settingsHash,
                    previous.FileDependencies,
                    previous.AssetDependencies,
                    view,
                    entry.IsFolder ? null : (source.ToString(), sourceHash)
                )
                && previous.Artifacts.All(artifact => artifacts.Exists(artifact.Id))
                    ? previous
                    : null;

            return new(metaPath, root, importer, resolved, source, sourceHash, settingsHash, reusable, null);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                              or YamlParseException) {
            // A sidecar that has gone or will not parse fails that asset rather than the run, the
            // same way an importer that throws does. It is one file, and the message names it.
            return Refusal(metaPath, $"Its .meta could not be read: {failure.Message}");
        }
    }

    static Prepared Refusal(string metaPath, string why) =>
        new(metaPath, null, null, null, default, default, default, null, why);

    ArtifactKey Key(
        IAssetImporter importer,
        ObjectId sourceHash,
        ObjectId settingsHash,
        IReadOnlyList<string> fileDependencies,
        IReadOnlyList<AssetId> assetDependencies,
        View view,
        (string Path, ObjectId Hash)? alreadyHashed = null
    ) =>
        ArtifactKey.Compute(
            importer.Name,
            importer.Version,
            sourceHash,
            settingsHash,
            Target,
            DependencyHashes(fileDependencies, assetDependencies, view, alreadyHashed)
        );

    /// <summary>
    ///     The hashes of everything an import depended on, which is what puts a dependency's change
    ///     into this asset's key.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A file dependency contributes its content hash; an asset dependency contributes its
    ///         artefacts' ids, which is stronger — a change to the other asset's <i>settings</i>
    ///         moves its artefacts and so moves this key too.
    ///     </para>
    ///     <para>
    ///         A file that has been deleted contributes nothing, which is correct: its absence is a
    ///         change, and the absence of its hash is what expresses that.
    ///     </para>
    ///     <para>
    ///         <b>An asset's own source is in this list and has already been hashed</b> — it is
    ///         declared for the importer, and it is <c>sourceHash</c> in its own right. Reading it
    ///         again would be the single largest cost of deciding that a project needs nothing done:
    ///         one extra open and one extra read per asset, on every asset, on every run.
    ///         <paramref name="alreadyHashed" /> hands the value over instead, which keeps the key
    ///         bit-for-bit what it was rather than dropping a contributor and invalidating every
    ///         cache in existence.
    ///     </para>
    /// </remarks>
    IEnumerable<ObjectId> DependencyHashes(
        IReadOnlyList<string> fileDependencies,
        IReadOnlyList<AssetId> assetDependencies,
        View view,
        (string Path, ObjectId Hash)? alreadyHashed = null
    ) {
        foreach (var path in fileDependencies) {
            if (alreadyHashed is { } known && string.Equals(path, known.Path, StringComparison.Ordinal)) {
                yield return known.Hash;
                continue;
            }

            var absolute = database.Paths.Absolute(path.TrimStart('/'));

            if (File.Exists(absolute)) {
                using var stream = File.OpenRead(absolute);
                yield return ArtifactKey.HashOf(stream);
            }
        }

        foreach (var asset in assetDependencies) {
            // ⚠ Through the view, which is the whole of what makes a parallel run produce the
            // sequential run's bytes. A dependency that comes earlier in path order has finished and
            // contributes the artefacts it just wrote; one that comes later has not run yet as far
            // as this asset is concerned, and contributes what it had before the run — whatever
            // another thread may have done to it in the meantime.
            if (view.RecordOf(Cache, asset) is { } record) {
                foreach (var artifact in record.Artifacts) {
                    yield return artifact.Id;
                }
            }
        }
    }

    /// <summary>
    ///     Decides which importer claims a file: what the sidecar says, then what the extension says.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The sidecar wins because it is the record of what actually imported this asset last
    ///         time, and changing an asset's importer is a decision somebody made rather than
    ///         something an extension table should silently revisit. A sidecar naming an importer this
    ///         build does not have falls back to the extension, and says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Except the fallback, which pins nothing.</b> A sidecar saying
    ///         <c>!RawImporter</c> is not a decision — it is the record of a build in which nothing
    ///         claimed the extension, and <see cref="ImporterRegistry.AddFallback" /> is what put it
    ///         there. Treating it as a choice means that the moment a real importer for that format
    ///         ships, every file imported before it stays a byte blob for ever, in every checkout that
    ///         has the sidecar — a format that works in new projects and silently does not in the ones
    ///         that most need it. This was found by adding <c>CompositorImporter</c>: the frame kept
    ///         shipping as a <c>Blob</c> and the host kept quietly drawing its own built-in one.
    ///     </para>
    ///     <para>
    ///         <b>The trade, stated.</b> Somebody who genuinely wanted a file shipped as bytes despite
    ///         an importer existing for it no longer gets that by writing <c>!RawImporter</c> in the
    ///         sidecar. That was never what the fallback was for — <c>AddressableInfo.Excluded</c> is
    ///         how a project says what it does and does not ship — and the alternative is a sidecar in
    ///         which "nobody chose" and "I chose this" are written identically.
    ///     </para>
    /// </remarks>
    bool TryChooseImporter(AssetEntry entry, YamlMapping root, out IAssetImporter importer) {
        if (entry.IsFolder) {
            return importers.TryGetByName("FolderImporter", out importer!);
        }

        var found = importers.TryGetForFile(entry.Path, out var byExtension);

        // Naming a *specific* importer is still a decision and still wins — including one that is not
        // the extension's, which is how somebody imports a .png as a cube map. Only the fallback is
        // disregarded, and only because it is the one entry nobody chose.
        if (root["importer"]?.Tag is { } tag
            && importers.TryGetByName(tag, out var named)
            && !ReferenceEquals(named, importers.Fallback)) {
            importer = named;
            return true;
        }

        importer = byExtension!;
        return found;
    }

    /// <summary>The chunk type id an artefact is stored under.</summary>
    /// <param name="type">What the importer called it — a <c>[DataContract]</c> alias, or a name of
    /// its own for a chunk that is not a serialised object.</param>
    /// <returns>The type id for the chunk's header.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The declared type, and every artefact used to be written as
    ///         <see cref="ImportedArtifact" /> instead.</b> <c>ObjectDatabase.Read&lt;T&gt;</c> checks
    ///         the header against the type being asked for, so a chunk stamped with the wrapper's name
    ///         could not be read as the thing inside it: <c>assets.Load&lt;SceneAsset&gt;</c>,
    ///         <c>Load&lt;GraphicsCompositorAsset&gt;</c> and every other typed load out of a shipped
    ///         build threw "was written by type … and is being read as …". Nothing caught it because
    ///         every test that loads a chunk either wrote it with <c>ObjectDatabase.Write</c> — which
    ///         stamps the real type — or read the artefact straight out of the <c>ImportResult</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unregistered name keeps the old stamp rather than failing.</b> Not every
    ///         artefact is a serialised object: a virtual-geometry page blob and a streamed audio
    ///         payload are bytes an importer named for its own loader, read through
    ///         <c>assets.Open</c> and never through <c>Read&lt;T&gt;</c>. Those have no contract to
    ///         resolve and nothing to gain from one.
    ///     </para>
    /// </remarks>
    static ulong TypeIdOf(string type) =>
        TypeRegistry.TryGetByAlias(type, out var descriptor)
            ? ContentHash.TypeId(descriptor.Type)
            : ContentHash.TypeId(typeof(ImportedArtifact));

    ObjectId HashOfSource(AssetEntry entry) {
        using var stream = File.OpenRead(database.Paths.Absolute(entry.Path));
        return ArtifactKey.HashOf(stream);
    }

    static ImportOutcome Refused(AssetEntry entry, string message) =>
        new(entry.Guid, null, false, false, null, [new(ImportSeverity.Error, message)]);

    /// <summary>
    ///     One asset's entitlement to the rest of the cache: which records it may see, and what it
    ///     has to wait for before it sees them.
    /// </summary>
    /// <param name="Run">The run this asset is part of, or <see langword="null" /> for an import of
    /// one asset on its own, which has nothing to be ordered against.</param>
    /// <param name="Index">Where this asset comes in path order.</param>
    /// <remarks>
    ///     A struct with a null <see cref="Run" /> is the whole of "there is no run", which is what
    ///     <see cref="ImportPipeline.ImportAsync(AssetEntry, CancellationToken)" /> passes: it reads
    ///     the cache as it stands and waits for nothing, because nothing else is running.
    /// </remarks>
    readonly record struct View(SequentialView? Run, int Index) {
        /// <summary>What this asset may see of another asset's last import.</summary>
        internal ImportRecord? RecordOf(ImportCache cache, AssetId asset) =>
            Run is { } run
                ? run.RecordFor(asset, Index)
                : cache.TryGet(asset, out var record) ? record : null;

        /// <summary>
        ///     Waits for every one of these that comes earlier in path order, and says whether any of
        ///     them re-imported.
        /// </summary>
        internal ValueTask<bool> WaitForEarlierAsync(IReadOnlyList<AssetId> dependencies) =>
            Run is { } run ? run.EarlierReimportedAsync(dependencies, Index) : new(false);
    }

    /// <summary>
    ///     The state that lets N concurrent imports agree on the answer one sequential, path-ordered
    ///     loop would have produced.
    /// </summary>
    /// <remarks>
    ///     Three things, and each of them is one half of a sequential loop's implicit knowledge: where
    ///     every asset comes in path order, whether the asset at each index has finished and whether
    ///     it re-imported, and what the cache held before any of it started.
    /// </remarks>
    sealed class SequentialView {
        readonly Dictionary<AssetId, int> indexOf;
        readonly Dictionary<AssetId, ImportRecord> before;
        readonly TaskCompletionSource<bool>[] finished;
        readonly ImportCache cache;

        internal SequentialView(AssetEntry[] entries, ImportCache cache) {
            this.cache = cache;
            indexOf = new(entries.Length);
            finished = new TaskCompletionSource<bool>[entries.Length];

            for (var index = 0; index < entries.Length; index++) {
                indexOf[entries[index].Guid] = index;

                // Asynchronously, so that completing an asset does not run the continuations of
                // everything waiting on it on the worker that happened to finish it.
                finished[index] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            before = new(cache.Count);

            foreach (var record in cache.Records) {
                before[record.Asset] = record;
            }
        }

        /// <summary>Says an asset is done, and whether an importer ran for it.</summary>
        internal void Finish(int index, bool reimported) => finished[index].TrySetResult(reimported);

        /// <summary>Says an asset will never be done, so nothing waits on it for ever.</summary>
        internal void Abandon(int index) => finished[index].TrySetCanceled();

        /// <summary>
        ///     Waits for every dependency that comes earlier in path order and reports whether any of
        ///     them re-imported.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>It does not stop at the first <see langword="true" />, deliberately.</b> The
        ///     answer is only half of what the caller wants; the other half is that by the time this
        ///     returns, every earlier dependency's record is final and can be priced into a key.
        ///     Short-circuiting would return the right boolean and leave the caller reading a record
        ///     another thread was still writing.
        /// </remarks>
        internal async ValueTask<bool> EarlierReimportedAsync(IReadOnlyList<AssetId> dependencies, int index) {
            var moved = false;

            foreach (var dependency in dependencies) {
                if (indexOf.TryGetValue(dependency, out var other) && other < index) {
                    moved |= await finished[other].Task.ConfigureAwait(false);
                }
            }

            return moved;
        }

        /// <summary>What the asset at <paramref name="index" /> is entitled to see of another's record.</summary>
        /// <remarks>
        ///     An asset that comes earlier has run, so the live cache is what the sequential loop
        ///     would have held. Anything else — a later asset, this asset itself, an id that is not in
        ///     this project — is read from the snapshot, because in path order it has not been touched
        ///     yet at the moment this asset's key is computed.
        /// </remarks>
        internal ImportRecord? RecordFor(AssetId asset, int index) =>
            indexOf.TryGetValue(asset, out var other) && other < index
                ? cache.TryGet(asset, out var current) ? current : null
                : before.GetValueOrDefault(asset);
    }

    /// <summary>Records what the import learned, and nothing else.</summary>
    static void WriteBack(
        string metaPath,
        YamlMapping root,
        IAssetImporter importer,
        ObjectId sourceHash,
        IReadOnlyList<SubAssetEntry> subAssets
    ) {
        var settings = root["importer"] as YamlMapping ?? new YamlMapping();
        settings.Tag = importer.Name;
        settings.Set("version", new YamlScalar(importer.Version.ToString(CultureInfo.InvariantCulture), YamlScalarStyle.Plain));

        if (!sourceHash.IsEmpty) {
            settings.Set("sourceHash", new YamlScalar(sourceHash.ToString()));
        }

        root.Set("importer", settings);

        if (subAssets.Count > 0) {
            var declared = new YamlSequence();

            foreach (var subAsset in subAssets) {
                declared.Add(
                    new YamlMapping()
                        .Set("id", new YamlScalar(subAsset.Id.ToString()))
                        .Set("name", new YamlScalar(subAsset.Name))
                        .Set("type", new YamlScalar(subAsset.Type))
                );
            }

            root.Set("subAssets", declared);
        }

        File.WriteAllText(metaPath, YamlWriter.Write(root));
    }
}
