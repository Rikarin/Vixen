// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core.Scenes;

namespace Vixen.Editor.Assets.Scenes;

/// <summary>How a scene or a prefab is compiled.</summary>
/// <remarks>
///     <para>
///         Almost nothing, and that is the honest shape: a <c>.vxscene</c> is already engine data, so
///         there is no conversion to configure. What is left is what a <i>build</i> may drop, which is
///         a per-target decision — a phone build strips the names a desktop debug build keeps — and
///         the <c>.meta</c> format's per-target overrides already express exactly that.
///     </para>
///     <para>
///         Every value is a cache key input, because the settings hash is part of the artefact key.
///         Turning the names off recompiles; changing nothing does not.
///     </para>
/// </remarks>
[DataContract("SceneImporter")]
public sealed record SceneImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>Whether the compiled scene carries what each entity is called.</summary>
    /// <remarks>
    ///     ⚠ <b>On by default, and it is a real cost.</b> A name is a string per entity in the chunk
    ///     — nothing per frame and nothing in a chunk of world memory, because a compiled name is a
    ///     table on the asset and never a component. What it buys is a level that can still be
    ///     debugged, searched and opened in an editor after it has been compiled, which is worth more
    ///     than the bytes until somebody measures that it is not.
    /// </remarks>
    public bool KeepEntityNames { get; init; } = true;
}

/// <summary>Compiles a <c>.vxscene</c> or <c>.vxprefab</c> into the asset a player loads.</summary>
/// <remarks>
///     <para>
///         <b>The build step doc 08 calls <c>SceneCompiler</c>, wired to the pipeline that already
///         exists.</b> The compiler is a pure function from a scene file to a
///         <c>SceneAsset</c>; this is what reads the file, declares what it points at, and writes the
///         result as the chunk an address resolves to. The split is deliberate — the interesting
///         decisions are testable without a project on disk.
///     </para>
///     <para>
///         <b>An importer rather than a stage of its own, and that is a deviation worth stating.</b>
///         Doc 08 describes compilers as a second pass over what import produced, with its own build
///         graph. This engine's importers already produce chunks in the object database, already have
///         a content-addressed cache keyed on the importer's version and the settings, and already
///         run out of process in parallel — everything the compile pass was specified to add. A
///         second graph would buy one thing this does not have: sharing work between two assets that
///         compile to the same intermediate. Scenes do not, so the seam is not paid for here. The one
///         that will need it is the material/effect pair, where a permutation is genuinely shared.
///     </para>
///     <para>
///         <b>What it points at is declared before it is compiled.</b> A scene naming a mesh or a
///         material is what makes replacing that mesh recompile the level, and the scan that finds
///         them is the one <c>NativeFormatImporter</c> uses — the same answer for the same question,
///         in one place.
///     </para>
/// </remarks>
[Importer(SceneFile.Extension, SceneFile.PrefabExtension)]
public sealed class SceneImporter : AssetImporter<SceneImportSettings> {
    /// <summary>The type a compiled scene's chunk is recorded as.</summary>
    public const string SceneType = "SceneAsset";

    /// <summary>The type a compiled prefab's chunk is recorded as.</summary>
    public const string PrefabType = "PrefabAsset";

    /// <inheritdoc />
    /// <remarks>
    ///     Tied to the compiler's own version, so that a change to the compiled layout invalidates
    ///     every scene artefact in every project — which is the whole mechanism for "the format
    ///     moved, recompile everything".
    /// </remarks>
    public override int Version => SceneCompiler.Version;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        SceneImportSettings settings,
        CancellationToken cancellationToken
    ) {
        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        YamlNode document;

        try {
            document = YamlReader.Read(text);
        } catch (YamlParseException failure) {
            // Reported rather than thrown, so the run continues and the author gets every broken file
            // in one pass instead of one per build.
            context.Report(ImportSeverity.Error, $"It is not valid YAML: {failure.Message}");
            return context.Finish();
        }

        if (document is not YamlMapping root) {
            context.Report(
                ImportSeverity.Error,
                "Its root is not a mapping. A scene is a mapping with a `roots` field, whatever its extension."
            );

            return context.Finish();
        }

        if (AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        SceneFile file;

        try {
            file = SceneFile.FromYaml(text);
        } catch (Exception failure) when (failure is YamlParseException or YamlBindingException or NotSupportedException) {
            // ⚠ Three failures with one answer. The document is not a scene, it names a component
            // this build cannot construct, or it is a scene from a newer editor — and all three are
            // "somebody has to look at this file", which is a reported error rather than a thrown one.
            context.Report(ImportSeverity.Error, failure.Message);
            return context.Finish();
        }

        var prefab = context.SourcePath.ToString()
            .EndsWith(SceneFile.PrefabExtension, StringComparison.OrdinalIgnoreCase);

        void Report(ImportSeverity severity, string message) => context.Report(severity, message);

        if (prefab) {
            var asset = SceneCompiler.CompilePrefab(file, Report, settings.KeepEntityNames);

            if (asset is not null) {
                Written(context, asset.Content.Count, "prefab");
                context.Write(SubAssetId.Main, PrefabType, Serializer.ToBytes(asset));
            }
        } else {
            var asset = SceneCompiler.CompileScene(file, Report, settings.KeepEntityNames);

            if (asset is not null) {
                Written(context, asset.Content.Count, "scene");
                context.Write(SubAssetId.Main, SceneType, Serializer.ToBytes(asset));
            }
        }

        return context.Finish();
    }

    /// <summary>
    ///     Says what was compiled, because a level is the asset whose size somebody is most likely to
    ///     be surprised by and the block count is what its load costs.
    /// </summary>
    static void Written(ImportContext context, int entities, string what) =>
        context.Report(ImportSeverity.Information, $"Compiled {entities} entities into a {what}.");
}
