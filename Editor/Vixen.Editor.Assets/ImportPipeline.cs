// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
    ///         <b>Deciding is parallel; importing is not.</b> Working out whether an asset needs
    ///         anything done is a read of its sidecar, a parse, a hash of its source and a lookup in
    ///         the cache — no writes, anywhere — and in a project where nothing has changed it is the
    ///         whole cost of the command. Measured on a ten-thousand-asset project it was the
    ///         difference between the phase's one-second budget being met and being missed by half.
    ///     </para>
    ///     <para>
    ///         The imports themselves stay sequential, which keeps every semantic exactly as it was:
    ///         one importer at a time writing chunks, sidecars and cache records, in path order.
    ///         Running <i>importers</i> in parallel is what the out-of-process worker
    ///         [08](../../docs/plan/08-asset-pipeline-and-addressables.md) specifies is for, and it
    ///         buys crash isolation with it.
    ///     </para>
    ///     <para>
    ///         <b>A decision is discarded if something it depends on re-imported first.</b> A
    ///         dependency's artefact ids are part of a dependent's key, so a decision taken before
    ///         that dependency ran was taken against ids that no longer exist. That reproduces the
    ///         sequential loop's behaviour exactly — in path order, a dependent sees its dependency's
    ///         new artefacts if and only if the dependency came first.
    ///     </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<ImportOutcome>> ImportAllAsync(CancellationToken cancellationToken = default) {
        var entries = database.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var prepared = new Prepared[entries.Length];

        Parallel.For(
            0,
            entries.Length,
            new ParallelOptions { CancellationToken = cancellationToken },
            index => prepared[index] = Prepare(entries[index])
        );

        var outcomes = new List<ImportOutcome>(entries.Length);
        var reimported = new HashSet<AssetId>();

        for (var index = 0; index < entries.Length; index++) {
            var entry = entries[index];
            var decision = prepared[index];

            if (decision.Reusable is { } record && !record.AssetDependencies.Any(reimported.Contains)) {
                outcomes.Add(new(entry.Guid, decision.Importer!.Name, true, true, record, []));
                continue;
            }

            var outcome = await ImportAsync(entry, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);

            if (outcome is { WasCached: false, Record: not null }) {
                reimported.Add(entry.Guid);
            }
        }

        return outcomes;
    }

    /// <summary>Imports one asset, unless nothing about it has changed.</summary>
    /// <param name="entry">The asset.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What happened.</returns>
    public async ValueTask<ImportOutcome> ImportAsync(AssetEntry entry, CancellationToken cancellationToken = default) {
        var decision = Prepare(entry);

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

        // The key stored is the one describing what the import *actually* depended on, not the
        // speculative one it was tested against. Storing the speculative key would mean the first
        // import of every asset was followed by a second one that also ran — the first computed its
        // key knowing no dependencies, and the second would compute a different one knowing them.
        var fresh = new ImportRecord(
            entry.Guid,
            importer.Name,
            importer.Version,
            Key(importer, sourceHash, settingsHash, fileDependencies, assetDependencies),
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
    Prepared Prepare(AssetEntry entry) {
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
            var previous = Cache.TryGet(entry.Guid, out var record) ? record : null;

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
        (string Path, ObjectId Hash)? alreadyHashed = null
    ) =>
        ArtifactKey.Compute(
            importer.Name,
            importer.Version,
            sourceHash,
            settingsHash,
            Target,
            DependencyHashes(fileDependencies, assetDependencies, alreadyHashed)
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
            if (Cache.TryGet(asset, out var record) && record is not null) {
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
