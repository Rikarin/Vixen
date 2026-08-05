// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Foliage;
using Vixen.Terrain;

namespace Vixen.Editor.Assets.Terrain;

/// <summary>How one of the terrain toolset's assets is imported.</summary>
/// <remarks>
///     Empty, like <c>MaterialImportSettings</c> and for the same reason: a <c>.vxlayer</c> already
///     <em>is</em> engine data, so there is nothing about the conversion to decide.
/// </remarks>
[DataContract("TerrainAssetImporter")]
public sealed record TerrainAssetImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>
///     Imports the four assets the terrain toolset authors: a paint layer, a foliage type, a grass
///     type and a spline.
/// </summary>
/// <remarks>
///     <para>
///         <b>The owed item [docs/plan/31] carried through four phases.</b> T4 shipped
///         <c>TerrainLayerDescription</c> and said "this is the content and the editor's form, and
///         turning either into a file belongs with <c>Vixen.Editor.Assets</c>"; T5, T6 and T8 each
///         said the same about theirs, deliberately, so that this would be one job rather than four.
///         It is.
///     </para>
///     <para>
///         <b>Its real work is validation, not conversion.</b> These are already YAML in the engine's
///         own dialect — <see cref="NativeFormatImporter" /> would carry any of them forward
///         untouched. What that importer cannot do is <em>read</em> them: every one of these four
///         types has a <c>Validate()</c> that returns the sentence explaining why it cannot be used,
///         and running it here turns "the grass never grew" from a bug report into a message beside
///         the file that caused it.
///     </para>
///     <para>
///         ⚠ <b>A refusal is an error and a suspicion is a warning, and the split is deliberate.</b> A
///         spline with one control point cannot be built and is an error; a foliage type with no mesh
///         is legal — an author is part-way through — and is a warning. Failing the second would stop
///         a build over a file somebody is in the middle of.
///     </para>
///     <para>
///         ⚠ <b>Three of the four are written forward as the document; the grass type is written as
///         its serialized record, because it is the one with a runtime reader.</b>
///         <c>AssetTerrainSource</c> opens a <c>.vxgrass</c> chunk and hands it to the binary
///         serializer — a game does not carry the YAML dialect, which is the editor's format — so a
///         text chunk is ground that quietly never grows. The layer, foliage and spline documents
///         have no runtime consumer yet (a layer rides inside its <c>.vxterrain</c>), and they stay
///         text until one exists, on <c>MaterialImporter</c>'s precedent for the split.
///     </para>
/// </remarks>
[Importer(LayerExtension, FoliageExtension, GrassExtension, SplineExtension)]
public sealed class TerrainAssetImporter : AssetImporter<TerrainAssetImportSettings> {
    /// <summary>The vector forms these documents write — a wind direction, a spline point.</summary>
    /// <remarks>On <c>MaterialImporter</c>'s terms: registered from a static constructor rather
    ///     than a module initializer, for the reason <see cref="MathScalars" /> gives about a
    ///     process-wide table.</remarks>
    static TerrainAssetImporter() => MathScalars.Register();

    /// <summary>What a terrain paint layer is written as.</summary>
    public const string LayerExtension = ".vxlayer";

    /// <summary>What a foliage type is written as.</summary>
    public const string FoliageExtension = ".vxfoliage";

    /// <summary>What a grass type is written as.</summary>
    public const string GrassExtension = ".vxgrass";

    /// <summary>What a spline is written as.</summary>
    public const string SplineExtension = ".vxspline";

    /// <inheritdoc />
    public override int Version => 1;

    /// <summary>The <c>[DataContract]</c> alias each extension's document is recorded under.</summary>
    /// <param name="extension">The extension, with its dot, in any case.</param>
    /// <returns>The alias, or <see langword="null" /> if it is not one of these four.</returns>
    /// <remarks>
    ///     ⚠ <b>The alias of the type actually written, which is what a chunk's reader resolves.</b>
    ///     <c>MaterialImporter</c>'s own remarks record what the other spelling costs: the bytes of
    ///     one record handed to the reader of another, thrown from inside the asset manager about
    ///     content the build had just declared good.
    /// </remarks>
    public static string? AliasOf(string extension) =>
        extension.ToLowerInvariant() switch {
            LayerExtension => nameof(TerrainLayerDescription),
            FoliageExtension => nameof(FoliageType),
            GrassExtension => nameof(GrassType),
            SplineExtension => nameof(SplineAsset),
            _ => null
        };

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        TerrainAssetImportSettings settings,
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
            // Reported rather than thrown, so an author gets every broken file in one pass.
            context.Report(ImportSeverity.Error, $"It is not valid YAML: {failure.Message}");

            return context.Finish();
        }

        if (document is not YamlMapping root) {
            context.Report(
                ImportSeverity.Error,
                "Its root is not a mapping. Every Vixen asset is a mapping of fields, whatever its "
                + "extension."
            );

            return context.Finish();
        }

        var extension = Path.GetExtension(context.SourcePath.ToString());
        var alias = AliasOf(extension);

        if (alias is null) {
            context.Report(ImportSeverity.Error, $"'{extension}' is not one of the terrain asset kinds.");

            return context.Finish();
        }

        if (root.Count == 0) {
            context.Report(
                ImportSeverity.Warning,
                "It has no fields. That is what a save which did not finish looks like, and it will "
                + "load with every default in place."
            );
        }

        // The references first, so a layer naming a texture that has been deleted is reported even
        // when the layer itself is otherwise fine.
        if (AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        // The grass type is compiled to the record the runtime reads back — the class remarks say
        // why it alone is — and the other three are carried forward as their documents.
        if (extension.Equals(GrassExtension, StringComparison.OrdinalIgnoreCase)) {
            WriteGrass(root, text, context);
        } else {
            Inspect(extension, root, context);
            context.Write(SubAssetId.Main, alias, System.Text.Encoding.UTF8.GetBytes(text));
        }

        return context.Finish();
    }

    /// <summary>Writes a grass document as the serialized record <c>AssetTerrainSource</c> opens.</summary>
    /// <remarks>
    ///     ⚠ <b>A document this build cannot read is carried forward as text with a warning</b>, on
    ///     <see cref="Inspect" />'s terms: a field this build does not know is what an asset written
    ///     by a newer editor looks like, and refusing it would make the project unopenable. What
    ///     that chunk costs is honest — the runtime counts it failed where a compiled record would
    ///     have grown — and the warning is how somebody learns which build wrote it.
    /// </remarks>
    static void WriteGrass(YamlMapping root, string text, ImportContext context) {
        GrassType grass;

        try {
            grass = YamlSerializer.Deserialize<GrassType>(root);
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            context.Report(
                ImportSeverity.Warning,
                $"It could not be read as a {nameof(GrassType)}: {failure.Message}. The file is "
                + "still imported, because a field this build does not know is what an asset written "
                + "by a newer editor looks like."
            );

            context.Write(SubAssetId.Main, nameof(GrassType), System.Text.Encoding.UTF8.GetBytes(text));

            return;
        }

        if (grass.Validate() is { } problem) {
            // A warning rather than an error, for the reason the class remarks give: an author
            // part-way through a file is a legal state, and the runtime's own validation drops the
            // rule where a person can see the count.
            context.Report(ImportSeverity.Warning, problem);
        }

        context.Write(SubAssetId.Main, nameof(GrassType), Serializer.ToBytes(grass));
    }

    /// <summary>Reads the document as its own type and reports what the type says about itself.</summary>
    /// <remarks>
    ///     ⚠ <b>A deserialisation failure is a warning rather than an error, and that is the awkward
    ///     one.</b> The document is carried forward either way, because a field this build does not
    ///     know is exactly what an asset written by a newer editor looks like — and refusing to import
    ///     it would make a project unopenable rather than partly readable. What the warning buys is
    ///     that somebody sees it.
    /// </remarks>
    static void Inspect(string extension, YamlMapping root, ImportContext context) {
        try {
            var problem = extension.ToLowerInvariant() switch {
                LayerExtension => YamlSerializer.Deserialize<TerrainLayerDescription>(root).Validate(),
                FoliageExtension => YamlSerializer.Deserialize<FoliageType>(root).Validate(),
                SplineExtension => YamlSerializer.Deserialize<SplineAsset>(root).Validate(),
                _ => null
            };

            if (problem is not null) {
                context.Report(Severity(extension, problem), problem);
            }
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            context.Report(
                ImportSeverity.Warning,
                $"It could not be read as a {AliasOf(extension)}: {failure.Message}. The file is "
                + "still imported, because a field this build does not know is what an asset written "
                + "by a newer editor looks like."
            );
        }
    }

    /// <summary>Whether a validation message stops the build or only warns.</summary>
    /// <remarks>
    ///     A spline that cannot be built is an error; everything else is an author part-way through.
    ///     A foliage type with no mesh, a layer with no textures and a grass type with no name are all
    ///     legal states of a file somebody is editing, and failing a build over one is how a toolset
    ///     earns a reputation for getting in the way.
    /// </remarks>
    static ImportSeverity Severity(string extension, string problem) =>
        extension.Equals(SplineExtension, StringComparison.OrdinalIgnoreCase)
        && problem.Contains("control point", StringComparison.Ordinal)
            ? ImportSeverity.Error
            : ImportSeverity.Warning;
}
