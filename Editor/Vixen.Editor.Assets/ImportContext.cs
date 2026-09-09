// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets;

/// <summary>Where an import finds another asset's source, given its id.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the wall doc 47 § 2 chose a whole file format around, and it is one method.</b>
///         An importer was handed its own <c>Guid</c>, its own <c>SourcePath</c>, a provider over
///         <em>paths</em> and a <c>DependsOn(AssetId)</c> that declares an edge and returns nothing —
///         so a scene could name a prefab and no importer could open it. Doc 47 took model (C) over
///         Unity's sparse patch for that reason, and navigation's placement bake stopped at the same
///         wall from the other side.
///     </para>
///     <para>
///         <b>A separate interface rather than a method on the pipeline, because an import may not be
///         in this process.</b> <see cref="IImportExecutor" /> is a process boundary: an
///         <see cref="ImportJob" /> is five strings and a flag, and shipping a project's whole
///         guid-to-path index across it per job would cost more than the imports. So whoever builds
///         an executor supplies the lookup, and a worker builds its own from the project it was
///         pointed at.
///     </para>
/// </remarks>
public interface IAssetSources {
    /// <summary>Finds where an asset's source file is.</summary>
    /// <param name="asset">Its id.</param>
    /// <param name="path">Where its source is.</param>
    /// <returns>Whether this project has such an asset.</returns>
    bool TryGetPath(AssetId asset, out VirtualPath path);
}

/// <summary>Everything one import is allowed to read, and everything it has to declare.</summary>
/// <remarks>
///     <para>
///         <b>Dependency registration is mandatory, and the context is what makes it enforceable.</b>
///         <see cref="Files" /> is a wrapper that refuses a read of anything not declared, so an
///         importer that reaches for a sibling file without saying so fails at that moment instead of
///         producing an artefact that is stale for ever. See
///         <see cref="UnregisteredReadException" /> for why that trade is worth an exception.
///     </para>
///     <para>
///         The check is on by default rather than only in debug builds, which is a deviation from
///         [08](../../docs/plan/08-asset-pipeline-and-addressables.md). An import runs at most once
///         per asset per change and the check is a set lookup per file open, so it is not measurable;
///         and an incrementality bug that only manifests in the configuration nobody develops in is
///         the worst possible place for one.
///     </para>
/// </remarks>
public sealed class ImportContext {
    readonly IAssetSources? sources;
    readonly HashSet<VirtualPath> declaredFiles = [];
    readonly HashSet<AssetId> declaredAssets = [];
    readonly List<ImportDiagnostic> diagnostics = [];
    readonly List<SubAssetEntry> subAssets = [];
    readonly List<ImportedArtifact> artifacts = [];

    /// <summary>Which (kind, name) pairs are spoken for, so a second one can be given a suffix.</summary>
    readonly HashSet<(string Kind, string Name)> taken = [];

    /// <summary>The asset being imported.</summary>
    public AssetId Guid { get; }

    /// <summary>Where its source file is.</summary>
    public VirtualPath SourcePath { get; }

    /// <summary>Which build target this import is for — <c>Windows</c>, <c>Android/Vulkan</c>.</summary>
    public string Target { get; }

    /// <summary>How this asset is configured, with per-target overrides already applied.</summary>
    public IImportSettings Settings { get; }

    /// <summary>Which importer is running, for messages.</summary>
    public string Importer { get; }

    /// <summary>
    ///     The only way to read a file. Refuses anything that was not declared first, unless the
    ///     context was made without the check.
    /// </summary>
    public IFileProvider Files { get; }

    /// <summary>Every file this import has declared it depends on, including the source.</summary>
    public IReadOnlyCollection<VirtualPath> FileDependencies => declaredFiles;

    /// <summary>Every other asset this import has declared it depends on.</summary>
    public IReadOnlyCollection<AssetId> AssetDependencies => declaredAssets;

    /// <summary>Everything the importer has said so far.</summary>
    public IReadOnlyList<ImportDiagnostic> Diagnostics => diagnostics;

    /// <summary>Sets up an import.</summary>
    /// <param name="guid">The asset.</param>
    /// <param name="sourcePath">Where its source is.</param>
    /// <param name="settings">How it is configured, overrides already applied.</param>
    /// <param name="files">Where files come from.</param>
    /// <param name="importer">Which importer is running.</param>
    /// <param name="target">Which build target, or <see langword="null" /> for none in particular.</param>
    /// <param name="enforceDeclaredReads">Whether an undeclared read throws.</param>
    /// <param name="sources">
    ///     Where another asset's source is found, or <see langword="null" /> when this import cannot
    ///     reach one — see <see cref="TryResolve" />.
    /// </param>
    public ImportContext(
        AssetId guid,
        VirtualPath sourcePath,
        IImportSettings settings,
        IFileProvider files,
        string importer,
        string? target = null,
        bool enforceDeclaredReads = true,
        IAssetSources? sources = null
    ) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(importer);

        Guid = guid;
        SourcePath = sourcePath;
        Settings = settings;
        Importer = importer;
        Target = target ?? string.Empty;

        // The asset's own source is declared for it. Making an importer say it depends on the file
        // it exists to read would be ceremony, and forgetting it would be the one mistake nobody
        // would ever be caught making.
        declaredFiles.Add(sourcePath);

        this.sources = sources;
        Files = enforceDeclaredReads ? new DeclaredReadsOnlyProvider(files, this) : files;
    }

    /// <summary>Declares that this import's result depends on another asset.</summary>
    /// <param name="asset">The asset.</param>
    /// <remarks>
    ///     Its artefact id goes into the cache key, so a change to it re-runs this import — which is
    ///     what makes a material re-import when the texture it points at is replaced.
    /// </remarks>
    public void DependsOn(AssetId asset) {
        if (!asset.IsEmpty) {
            declaredAssets.Add(asset);
        }
    }

    /// <summary>Declares that this import's result depends on a file.</summary>
    /// <param name="path">The file.</param>
    public void DependsOnFile(VirtualPath path) => declaredFiles.Add(path);

    /// <summary>Finds another asset's source file, and declares both edges to it in the same call.</summary>
    /// <param name="asset">Which asset.</param>
    /// <param name="path">Where its source is.</param>
    /// <returns>Whether it was found. A <see langword="false" /> is also returned when this import
    ///     has no way to look, which is what <see cref="CanResolve" /> distinguishes.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Resolving and declaring are one call because coming apart is the failure that
    ///         cannot be seen.</b> A <c>Resolve</c> that handed back a path without registering the
    ///         edge would let an importer read a template and never re-run when the template changed
    ///         — a compiled artefact that is silently stale, which is strictly worse than the refusal
    ///         it replaces. So this calls <see cref="DependsOn" /> for the cache key <em>and</em>
    ///         <see cref="DependsOnFile" /> for the read, and there is no way to get the path without
    ///         both. <c>DeclaredReadsOnlyProvider</c> is the same discipline over paths.
    ///     </para>
    ///     <para>
    ///         <b>Nothing is declared when the asset is not found</b>, so a dangling reference does
    ///         not put a phantom into the key. That is the one asymmetry: an asset that appears later
    ///         re-imports this one through the ordinary source-hash path rather than through an edge
    ///         to a file that did not exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <see langword="false" /> from a context with no <see cref="IAssetSources" /> is
    ///         not the same statement as a false from one that looked</b>, and an importer that
    ///         reports "there is no such asset" for the first is lying about somebody's project.
    ///         Check <see cref="CanResolve" /> before turning a miss into a diagnostic.
    ///     </para>
    /// </remarks>
    public bool TryResolve(AssetId asset, out VirtualPath path) {
        path = default;

        if (asset.IsEmpty || sources is null || !sources.TryGetPath(asset, out path)) {
            path = default;
            return false;
        }

        DependsOn(asset);
        DependsOnFile(path);
        return true;
    }

    /// <summary>Whether this import can look an asset id up at all.</summary>
    /// <remarks>
    ///     False in a context built without an <see cref="IAssetSources" /> — a unit test that
    ///     constructs one by hand, or an executor whose host did not supply the lookup. An importer
    ///     that needs to read another asset should say <em>that</em> rather than reporting the asset
    ///     missing, because the two have opposite fixes.
    /// </remarks>
    public bool CanResolve => sources is not null;

    /// <summary>Opens the asset's own source file.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A readable stream the caller owns.</returns>
    public ValueTask<Stream> OpenSourceAsync(CancellationToken cancellationToken = default) =>
        Files.OpenReadAsync(SourcePath, cancellationToken);

    /// <summary>Says something about the asset.</summary>
    /// <param name="severity">How much attention it needs.</param>
    /// <param name="message">What to say.</param>
    public void Report(ImportSeverity severity, string message) {
        ArgumentException.ThrowIfNullOrEmpty(message);
        diagnostics.Add(new(severity, message));
    }

    /// <summary>Binds a document to the type an importer reads it as, saying so when a key named nothing.</summary>
    /// <typeparam name="T">What the document is.</typeparam>
    /// <param name="yaml">The document's text.</param>
    /// <param name="what">What to call the thing in the warning — <c>a material</c>, <c>a compositor</c>.</param>
    /// <returns>The bound value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="yaml" /> is null.</exception>
    /// <exception cref="YamlBindingException">It is YAML that is not a <typeparamref name="T" />.</exception>
    /// <exception cref="YamlParseException">It is not YAML.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists because <see cref="YamlSerializer.Parse{T}(string, YamlSerializerOptions)" />
    ///         drops an unknown key unless the caller asks, and eleven importers did not ask.</b>
    ///         <see cref="YamlSerializerOptions.OnUnknownKey" /> documents that default as deliberate —
    ///         a project opened in an older editor has to load — and says in its own words that
    ///         dropping silently is the other failure. For an <em>asset</em> there is no forward
    ///         compatibility argument to weigh against it: a <c>.vxmat</c> is read by the same build
    ///         that wrote it, so a key nothing read is a typo and the value the author meant to set is
    ///         on its default with no diagnostic anywhere.
    ///     </para>
    ///     <para>
    ///         <b>One method rather than one fix per importer, because the defect was one line
    ///         repeated.</b> Every call site that reads its source as text and binds it goes through
    ///         here, so an importer added tomorrow is warned about by construction rather than by
    ///         someone remembering.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A key that names a member the binder cannot write reaches the same callback</b>,
    ///         which is not a false positive: a get-only member is a key nothing read, which is the
    ///         thing the warning is about.
    ///     </para>
    /// </remarks>
    public T BindYaml<T>(string yaml, string what) {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrEmpty(what);

        return YamlSerializer.Parse<T>(yaml, YamlSerializerOptions.Default with { OnUnknownKey = Unknown });

        void Unknown(string key) =>
            Report(
                ImportSeverity.Warning,
                $"'{key}' is not a field of {what}, so nothing read it and whatever it was meant to set is "
                + "on its default. Check the spelling against the guide for this asset kind."
            );
    }

    /// <summary>Declares a sub-asset and derives its stable id.</summary>
    /// <param name="kind">What kind of thing it is — <c>Mesh</c>, <c>AnimationClip</c>.</param>
    /// <param name="name">What the source file calls it.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     <para>
    ///         Derived here rather than by the importer, so that every importer gets the same rule and
    ///         no importer is tempted to number its sub-assets by position — which is the mistake that
    ///         breaks every reference to a mesh when an artist re-exports an FBX.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The author's rename is applied first, and it is applied to the name the source
    ///         gave.</b> <see cref="IImportSettings.SubAssetNames" /> is keyed by what is in the file
    ///         rather than by what the last import ended up calling it, so a re-export that adds a
    ///         third <c>Cube</c> does not shuffle which rename lands on which mesh.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A name already taken is suffixed rather than refused, and this is a change of
    ///         mind.</b> Two meshes called <c>Cube</c> in one <c>.glb</c> derive one id, and that used
    ///         to throw <c>SubAssetCollisionException</c> and fail the whole asset with "rename one of
    ///         them" — advice nobody could act on, because the names are in the file and the editor
    ///         could not edit them. The second one becomes <c>Cube_1</c>, the author is warned, and
    ///         the rename map is there for anybody who wants better names than that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A generated suffix is stable only for as long as the order in the file is.</b>
    ///         That is the property a derived id exists to protect and the one case where it cannot
    ///         be: nothing distinguishes two things with one name except which came first. Renaming
    ///         them — in the DCC tool, or here — is what makes their ids survive a re-export, which is
    ///         what the warning says.
    ///     </para>
    /// </remarks>
    public SubAssetId DeclareSubAsset(string kind, string name) {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);

        var chosen = Rename(name);

        if (!taken.Add((kind, chosen))) {
            var unique = chosen;

            // Bumped until it is free rather than once, because the file is allowed to contain a
            // real `Cube_1` beside the two `Cube`s — and a suffix that collided with a genuine name
            // would put the collision back where it started.
            for (var index = 1; !taken.Add((kind, unique = $"{chosen}_{index}")); index++) { }

            Report(
                ImportSeverity.Warning,
                $"Two {kind} sub-assets are both called '{chosen}', so the second is '{unique}'. That name — and "
                + "every reference to it — depends on the order they appear in the file, so give them distinct "
                + "names in the source or rename one under Sub-assets to make it survive a re-export."
            );

            chosen = unique;
        }

        var id = Vixen.Core.Yaml.Meta.SubAssets.Derive(Importer, kind, chosen);
        subAssets.Add(new() { Id = id, Name = chosen, Type = kind, Source = chosen == name ? string.Empty : name });
        return id;
    }

    /// <summary>What the author asked this source name to be called, or the name itself.</summary>
    /// <remarks>
    ///     First match wins. Two rows naming one source is a list somebody edited twice rather than a
    ///     question with two answers, and refusing the import over it would be a settings panel that
    ///     can put a project into a state only a text editor can get it out of.
    /// </remarks>
    string Rename(string source) {
        foreach (var rename in Settings.SubAssetNames) {
            if (string.Equals(rename.Source, source, StringComparison.Ordinal) && rename.Name.Length > 0) {
                return rename.Name;
            }
        }

        return source;
    }

    /// <summary>Records an artefact the import produced.</summary>
    /// <param name="subAsset">Which sub-asset it is, or <see cref="SubAssetId.Main" />.</param>
    /// <param name="type">What kind of thing it is.</param>
    /// <param name="content">Its bytes.</param>
    public void Write(SubAssetId subAsset, string type, ReadOnlyMemory<byte> content) {
        ArgumentException.ThrowIfNullOrEmpty(type);
        artifacts.Add(new(subAsset, type, content));
    }

    /// <summary>Collects what the import produced.</summary>
    /// <returns>The result.</returns>
    /// <exception cref="SubAssetCollisionException">Two sub-assets derived the same id.</exception>
    public ImportResult Finish() {
        Vixen.Core.Yaml.Meta.SubAssets.EnsureDistinct(subAssets);
        return new([.. artifacts], [.. subAssets], [.. diagnostics]);
    }

    internal bool IsDeclared(VirtualPath path) => declaredFiles.Contains(path);

    /// <summary>A provider that refuses to read anything the import has not declared.</summary>
    sealed class DeclaredReadsOnlyProvider(IFileProvider inner, ImportContext context) : IFileProvider {
        public bool IsReadOnly => inner.IsReadOnly;

        // Existence and metadata are not reads of content, and an importer legitimately probes for a
        // sibling before deciding whether it depends on it. Declaring a file that turned out not to
        // be there is the caller's business; being unable to look is not.
        public bool Exists(VirtualPath path) => inner.Exists(path);

        public bool TryGetEntry(VirtualPath path, out FileEntry entry) => inner.TryGetEntry(path, out entry);

        public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) =>
            inner.Enumerate(directory, recursive);

        public ValueTask<Stream> OpenReadAsync(VirtualPath path, CancellationToken cancellationToken = default) {
            Check(path);
            return inner.OpenReadAsync(path, cancellationToken);
        }

        public ValueTask<Stream> OpenWriteAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
            inner.OpenWriteAsync(path, cancellationToken);

        public bool Delete(VirtualPath path) => inner.Delete(path);

        public void CreateDirectory(VirtualPath path) => inner.CreateDirectory(path);

        public bool TryMap(VirtualPath path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMappedFile? mapped) {
            Check(path);
            return inner.TryMap(path, out mapped);
        }

        void Check(VirtualPath path) {
            if (!context.IsDeclared(path)) {
                throw new UnregisteredReadException(path, context.Importer);
            }
        }
    }
}
