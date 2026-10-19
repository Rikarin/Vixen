// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets;

/// <summary>Settings for the importer that claims an editor-only document and ships nothing.</summary>
[DataContract("EditorOnlyImporter")]
public sealed record EditorOnlyImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>
///     Claims a document the editor authors and nothing at run time reads, writes no artefact, and says
///     what ships instead (#1491).
/// </summary>
/// <remarks>
///     <para>
///         <b>A texture graph and a layer stack are recipes, not assets.</b> Nothing under <c>Core/</c>
///         opens a <c>.vxtexgraph</c> or a <c>.vxlayers</c>: the Texturing panel and
///         <c>vixen texture bake</c> evaluate one, and what a game loads is what the bake writes — a
///         folder of maps and the <c>.vxmat</c> beside them, which <c>TextureImporter</c> and
///         <c>MaterialImporter</c> already claim. Before this, the fallback took both and shipped the
///         recipe as a chunk called <c>Blob</c> that no reader resolves, in every build of every project
///         that had ever pressed Create ▸.
///     </para>
///     <para>
///         ⚠ <b>Here in the pipeline, and not beside the features that own the formats, because neither
///         feature can be linked by every host.</b> <c>Vixen.Editor.TextureGraph</c>'s csproj refuses
///         <c>Vixen.Editor.Assets</c> on a closure measurement, and <c>Vixen.Cli</c>'s refuses
///         <c>Vixen.Editor.Texturing</c> as an editor shell inside a command-line tool. An answer only the
///         editor gave would be the editor and <c>vixen</c> disagreeing about what a project ships — the
///         one thing <c>HostImporterMap</c> exists to prevent — so the answer lives where all three hosts
///         read it.
///     </para>
///     <para>
///         ⚠ <b>No artefact, so it is in no bundle</b>, which is <c>BuildPlanner</c>'s ordinary handling of
///         an importer that produces nothing — and an author who marked the recipe addressable on purpose
///         gets that planner's error, "its importer produced nothing", rather than a blob.
///     </para>
///     <para>
///         ⚠ <b>Never honoured by name over the extension's own importer.</b> <c>ImportPipeline</c>
///         disregards a sidecar naming this the way it disregards one naming <see cref="RawImporter" />:
///         the day one of these formats grows a real importer, every <c>.meta</c> written today says
///         <c>!EditorOnlyImporter</c>, and honouring it would keep those files unimported for ever — the
///         trap <see cref="UnimportedFormats" /> describes for a placeholder importer.
///     </para>
/// </remarks>
[Importer(".vxtexgraph", ".vxlayers")]
public sealed class EditorOnlyImporter : AssetImporter<EditorOnlyImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        EditorOnlyImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var extension = Path.GetExtension(context.SourcePath.ToString());

        context.Report(
            ImportSeverity.Information,
            $"A {extension} is a recipe the editor evaluates, not an asset a game loads: nothing at run time "
            + "reads one, so nothing is imported and it ships in no bundle. What ships is what the bake "
            + "writes — the maps and the .vxmat beside them — so make those addressable, not this file."
        );

        return ValueTask.FromResult(context.Finish());
    }
}
