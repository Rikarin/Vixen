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
///         ⚠ <b>The document is written forward, not a compiled record.</b> [doc 08] splits import
///         from compile, and none of these four has a compiler yet — the runtime reads the
///         <c>[DataContract]</c> graph. Emitting a binary here would put the compiler's decisions
///         inside the importer where the artefact cache key cannot see them.
///     </para>
/// </remarks>
[Importer(LayerExtension, FoliageExtension, GrassExtension, SplineExtension)]
public sealed class TerrainAssetImporter : AssetImporter<TerrainAssetImportSettings> {
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

        Inspect(extension, root, context);

        context.Write(SubAssetId.Main, alias, System.Text.Encoding.UTF8.GetBytes(text));

        return context.Finish();
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
                GrassExtension => YamlSerializer.Deserialize<GrassType>(root).Validate(),
                SplineExtension => SplineProblem(root),
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

    /// <summary>What is wrong with a spline, read from the document rather than from the type.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="SplineAsset" /> has no descriptor, and giving it one is a build-graph
    ///     decision rather than an importer's.</b> The YAML binder reads types the reflection
    ///     generator described, and that generator does not run over
    ///     <c>Vixen.Core.Mathematics</c> — running it there would make the assembly that holds
    ///     <c>Vector3</c> depend on <c>Vixen.Core.Reflection</c>, which every consumer of a vector
    ///     would then carry. So the one error case is read off the document directly: a path needs two
    ///     control points, and counting a sequence needs no descriptor at all.
    /// </remarks>
    static string? SplineProblem(YamlMapping root) {
        var points = root["points"] is YamlSequence sequence ? sequence.Count : 0;

        return points >= 2
            ? null
            : $"It has {points} control point(s); a curve needs two.";
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
