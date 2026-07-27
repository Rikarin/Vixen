// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.IO;
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
    }

    /// <summary>Imports everything in the project that needs it.</summary>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What happened to each asset, in path order.</returns>
    public async ValueTask<IReadOnlyList<ImportOutcome>> ImportAllAsync(CancellationToken cancellationToken = default) {
        var outcomes = new List<ImportOutcome>();

        foreach (var entry in database.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)) {
            outcomes.Add(await ImportAsync(entry, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>Imports one asset, unless nothing about it has changed.</summary>
    /// <param name="entry">The asset.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What happened.</returns>
    public async ValueTask<ImportOutcome> ImportAsync(AssetEntry entry, CancellationToken cancellationToken = default) {
        var metaPath = AssetMetaFile.PathFor(database.Paths.Absolute(entry.Path));

        if (YamlReader.Read(File.ReadAllText(metaPath)) is not YamlMapping root) {
            return Refused(entry, "Its .meta is not a mapping.");
        }

        if (!TryChooseImporter(entry, root, out var importer)) {
            return new(entry.Guid, null, false, true, null, []);
        }

        var settingsNode = root["importer"] as YamlMapping ?? new YamlMapping { Tag = importer.Name };
        var resolved = Target.Length == 0 ? settingsNode : TargetOverrides.Resolve(settingsNode, Target);

        var forHashing = new YamlMapping { Tag = resolved.Tag };

        foreach (var (settingKey, value) in resolved.Entries) {
            if (!RecordedByImport.Contains(settingKey, StringComparer.Ordinal)) {
                forHashing.Set(settingKey, value);
            }
        }

        IImportSettings settings;

        try {
            settings = (IImportSettings)YamlSerializer.Deserialize(resolved, importer.SettingsType)!;
        } catch (YamlBindingException failure) {
            return Refused(entry, $"Its import settings do not fit {importer.Name}: {failure.Message}");
        }

        var sourceHash = entry.IsFolder ? ObjectId.Empty : HashOfSource(entry);
        var settingsHash = ArtifactKey.HashOf(YamlWriter.Write(forHashing));
        var previous = Cache.TryGet(entry.Guid, out var record) ? record : null;

        // Computed from what the *previous* import declared, because what this one will declare is
        // not known until it has run. If nothing it depended on has moved, this matches the key that
        // import stored and there is nothing to do.
        if (previous is not null
            && previous.Key == Key(importer, sourceHash, settingsHash, previous.FileDependencies, previous.AssetDependencies)
            && previous.Artifacts.All(artifact => artifacts.Exists(artifact.Id))) {
            return new(entry.Guid, importer.Name, true, true, previous, []);
        }

        var context = new ImportContext(
            entry.Guid,
            new VirtualPath("/" + entry.Path),
            settings,
            files,
            importer.Name,
            Target,
            EnforceDeclaredReads
        );

        ImportResult result;

        try {
            result = await importer.ImportAsync(context, cancellationToken).ConfigureAwait(false);
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            // One bad file must not stop the import of everything else — the difference, as doc 08
            // puts it, between "one bad asset" and "the editor won't open". The out-of-process
            // worker takes this further by surviving a crash rather than an exception; this is the
            // in-process half of the same promise.
            return Refused(entry, $"{importer.Name} threw: {failure.Message}");
        }

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
                    artifacts.WriteRaw(ContentHash.TypeId(typeof(ImportedArtifact)), [], artifact.Content.Span)
                )
            )
            .ToArray();

        var fileDependencies = context.FileDependencies
            .Select(path => path.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();

        var assetDependencies = context.AssetDependencies.Order().ToArray();

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
        WriteBack(metaPath, root, importer, sourceHash, result);
        return new(entry.Guid, importer.Name, false, true, fresh, result.Diagnostics);
    }

    ArtifactKey Key(
        IAssetImporter importer,
        ObjectId sourceHash,
        ObjectId settingsHash,
        IReadOnlyList<string> fileDependencies,
        IReadOnlyList<AssetId> assetDependencies
    ) =>
        ArtifactKey.Compute(
            importer.Name,
            importer.Version,
            sourceHash,
            settingsHash,
            Target,
            DependencyHashes(fileDependencies, assetDependencies)
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
    /// </remarks>
    IEnumerable<ObjectId> DependencyHashes(
        IReadOnlyList<string> fileDependencies,
        IReadOnlyList<AssetId> assetDependencies
    ) {
        foreach (var path in fileDependencies) {
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
    ///     The sidecar wins because it is the record of what actually imported this asset last time,
    ///     and changing an asset's importer is a decision somebody made rather than something an
    ///     extension table should silently revisit. A sidecar naming an importer this build does not
    ///     have falls back to the extension, and says so.
    /// </remarks>
    bool TryChooseImporter(AssetEntry entry, YamlMapping root, out IAssetImporter importer) {
        if (entry.IsFolder) {
            return importers.TryGetByName("FolderImporter", out importer!);
        }

        if (root["importer"]?.Tag is { } tag && importers.TryGetByName(tag, out var named)) {
            importer = named;
            return true;
        }

        return importers.TryGetForFile(entry.Path, out importer!);
    }

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
        ImportResult result
    ) {
        var settings = root["importer"] as YamlMapping ?? new YamlMapping();
        settings.Tag = importer.Name;
        settings.Set("version", new YamlScalar(importer.Version.ToString(CultureInfo.InvariantCulture), YamlScalarStyle.Plain));

        if (!sourceHash.IsEmpty) {
            settings.Set("sourceHash", new YamlScalar(sourceHash.ToString()));
        }

        root.Set("importer", settings);

        if (result.SubAssets.Count > 0) {
            var declared = new YamlSequence();

            foreach (var subAsset in result.SubAssets) {
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
