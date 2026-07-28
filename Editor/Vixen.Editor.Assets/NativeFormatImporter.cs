// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets;

/// <summary>Settings for the importer that takes Vixen's own YAML assets.</summary>
/// <remarks>
///     Empty, and that is the honest shape. Every other importer's settings answer "how should this
///     foreign file be turned into engine data" — a texture's compression, a model's scale. A
///     <c>.vxmat</c> <em>is</em> engine data, so there is nothing to decide and no knob to add.
/// </remarks>
[DataContract("NativeFormatImporter")]
public sealed record NativeFormatImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Imports an asset Vixen itself authored: a material, a scene, a prefab, a group.</summary>
/// <remarks>
///     <para>
///         <b>Its real job is the dependency graph, not the conversion.</b> There is nothing to
///         convert — the file is already in the engine's own format, which is the whole point of
///         [doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md)'s YAML dialect. What is
///         <em>not</em> already known is what the file points at, and that is what makes a material
///         re-import when the texture it names is replaced. So this walks the document, finds every
///         <c>vx:</c> scalar, and declares each one.
///     </para>
///     <para>
///         <b>A reference that does not parse fails the import.</b> A scalar beginning <c>vx:</c> was
///         meant to be a reference by whoever wrote it; if the GUID after it is malformed, the
///         alternatives are failing here — naming the file and the text — or shipping an asset whose
///         pointer resolves to nothing on a player's machine. Anything that does not begin with the
///         prefix is left alone, because a string field holding arbitrary text is ordinary.
///     </para>
///     <para>
///         <b>What it writes is the document, and that is a deliberate stopping point.</b> Doc 08
///         splits import from compile: import produces editor-domain objects, and the compiler turns
///         them into the runtime chunks a player loads — a <c>MaterialAsset</c> with named parameters
///         and asset references becomes a <c>Material</c> with a resolved pipeline and
///         <c>ObjectId</c>s. That compiler does not exist yet. Emitting a half-resolved binary here
///         would put the compiler's decisions inside the importer, where the artefact cache key
///         cannot see them; carrying the document forward keeps the seam where the plan puts it, and
///         is written down in the README as owed.
///     </para>
/// </remarks>
[Importer(".vxmat", ".vxscene", ".vxprefab", ".vxgroup", ".vxanim", ".vxvfx", ".vxinput", ".vxasset")]
public sealed class NativeFormatImporter : AssetImporter<NativeFormatImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        NativeFormatImportSettings settings,
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
                $"Its root is a {Describe(document)} rather than a mapping. Every Vixen asset is a mapping of "
                + "fields, whatever its extension."
            );

            return context.Finish();
        }

        // A warning rather than an error, and the file is still carried forward. The reader turns an
        // empty document into an empty mapping deliberately — a truncated .meta has to re-import
        // rather than stop the editor opening — so an empty asset arrives here looking exactly like a
        // valid one with nothing in it. Failing the build on it would punish an author mid-edit;
        // saying nothing would let a material that was never saved ship as one.
        if (root.Count == 0) {
            context.Report(
                ImportSeverity.Warning,
                "It has no fields. That is what a save which did not finish looks like, and it will load as an "
                + "asset with every default in place."
            );
        }

        var malformed = 0;

        foreach (var reference in References(root, context, ref malformed)) {
            context.DependsOn(reference);
        }

        if (malformed > 0) {
            return context.Finish();
        }

        // The type comes from the document's own tag when it has one, and from the extension when it
        // does not. A .vxgroup carries no tag — it is bound as one known type — and inventing one for
        // it here would put a second spelling of "AddressableGroup" in the artefact record.
        var type = root.Tag is { Length: > 0 } tag ? tag.TrimStart('!') : TypeOf(context.SourcePath.ToString());

        context.Write(SubAssetId.Main, type, System.Text.Encoding.UTF8.GetBytes(text));
        return context.Finish();
    }

    /// <summary>Every asset the document points at.</summary>
    /// <param name="root">The document.</param>
    /// <param name="context">Where to complain about a reference that will not parse.</param>
    /// <param name="malformed">How many did not parse.</param>
    /// <returns>The assets, deduplicated by the caller's set.</returns>
    /// <remarks>
    ///     <para>
    ///         A walk of the node tree rather than a regular expression over the text, because a GUID
    ///         inside a comment or a quoted description is not a reference and a text scan cannot tell
    ///         the difference. It would produce a dependency that never changes and never breaks
    ///         anything — which is exactly the kind of wrongness nobody finds.
    ///     </para>
    ///     <para>
    ///         Iterative rather than recursive. A scene is the deepest document the engine has and a
    ///         deeply nested prefab hierarchy is an ordinary thing to author; a stack overflow inside
    ///         an importer takes the whole process with it, which is the one failure the
    ///         one-bad-asset-does-not-stop-the-build promise cannot catch.
    ///     </para>
    /// </remarks>
    static List<AssetId> References(YamlMapping root, ImportContext context, ref int malformed) {
        var found = new List<AssetId>();
        var pending = new Stack<YamlNode>();
        pending.Push(root);

        while (pending.Count > 0) {
            switch (pending.Pop()) {
                case YamlMapping mapping:
                    foreach (var (_, value) in mapping.Entries) {
                        pending.Push(value);
                    }

                    break;

                case YamlSequence sequence:
                    foreach (var item in sequence.Items) {
                        pending.Push(item);
                    }

                    break;

                case YamlScalar scalar when scalar.Value.StartsWith(AssetReference.Prefix, StringComparison.Ordinal):
                    if (AssetReference.TryParse(scalar.Value, out var reference)) {
                        if (!reference.IsNull) {
                            found.Add(reference.Asset);
                        }
                    } else {
                        malformed++;

                        context.Report(
                            ImportSeverity.Error,
                            $"'{scalar.Value}' begins with '{AssetReference.Prefix}' but is not a reference. It "
                            + "should be 'vx:' and 32 hex digits, optionally '#' and a sub-asset id."
                        );
                    }

                    break;
            }
        }

        return found;
    }

    /// <summary>What kind of thing an extension holds, when the document does not say.</summary>
    static string TypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".vxmat" => "Material",
        ".vxscene" => "Scene",
        ".vxprefab" => "Prefab",
        ".vxgroup" => "AddressableGroup",
        ".vxanim" => "AnimationClip",
        ".vxvfx" => "VisualEffect",

        // A .vxinput is engine data in the same YAML dialect as the rest, so it needs no importer of
        // its own — what it needs is to be *in* this list, so that the reference scan runs over it and
        // an addressable label on an action asset resolves like any other.
        ".vxinput" => "InputActions",
        _ => "Asset"
    };

    static string Describe(YamlNode node) => node is YamlSequence ? "sequence" : "single value";
}
