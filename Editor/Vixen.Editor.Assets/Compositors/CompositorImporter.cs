// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.Assets.Compositors;

/// <summary>How a <c>.vxcompositor</c> is compiled. There is nothing to configure.</summary>
/// <remarks>
///     The settings exist because an importer has them, and the version is what invalidates every
///     compositor artefact when the document format moves.
/// </remarks>
[DataContract("CompositorImporter")]
public sealed record CompositorImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a project's frame into the chunk the host loads by address.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What this replaces is a silent wrong answer.</b> A <c>.vxcompositor</c> matched no
///         importer, so it fell through to <see cref="RawImporter" /> and was published as a
///         <c>Blob</c> — bytes with the wrong type stamped on them. <c>AppGraphics</c> then loaded
///         <c>GraphicsOptions.Compositor</c>, got a type mismatch, logged it at warning level and
///         quietly used its own built-in one-pass frame instead. The game started, drew a picture,
///         and rendered a frame nobody had asked for; every post-effect and every field node in the
///         project's own document did nothing, and the only trace was one line in the log.
///     </para>
///     <para>
///         <b>Compiled rather than carried, unlike the formats <see cref="NativeFormatImporter" />
///         still handles.</b> A host reads this with <c>AssetManager.Load&lt;GraphicsCompositorAsset&gt;</c>,
///         which goes through the binary serializer — so shipping the authored YAML would be shipping
///         a document nothing at run time can read. <c>SceneImporter</c> made the same move for the
///         same reason, and its remarks explain why the compile lives in the importer rather than in a
///         pass of its own.
///     </para>
/// </remarks>
[Importer(".vxcompositor")]
public sealed class CompositorImporter : AssetImporter<CompositorImportSettings> {
    /// <summary>The type a compiled frame's chunk is recorded as.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>[DataContract]</c> alias, not a friendly name.</b>
    ///     <c>ImportPipeline.TypeIdOf</c> resolves this through the type registry and falls back to
    ///     <c>ImportedArtifact</c> when it cannot — which stamps the chunk with an editor type and
    ///     produces "nothing registered in this process claims it" at load, in a game, about content
    ///     the build declared good.
    /// </remarks>
    public const string CompositorType = "GraphicsCompositor";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        CompositorImportSettings settings,
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
            // Reported rather than thrown, so one build reports every broken document instead of the
            // first one.
            context.Report(ImportSeverity.Error, $"It is not valid YAML: {failure.Message}");
            return context.Finish();
        }

        if (document is not YamlMapping root) {
            context.Report(
                ImportSeverity.Error,
                "Its root is not a mapping. A compositor is a mapping with a `game` node in it."
            );

            return context.Finish();
        }

        // A frame names effects and shaders by string rather than by reference, so this ordinarily
        // finds nothing — it is here because a document that does carry one has to declare it like
        // any other, and because "the scan runs over every document" is a rule worth having no
        // exceptions to.
        if (AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        GraphicsCompositorAsset asset;

        try {
            asset = YamlSerializer.Parse<GraphicsCompositorAsset>(text);
        } catch (YamlBindingException failure) {
            context.Report(ImportSeverity.Error, $"It is not a compositor: {failure.Message}");
            return context.Finish();
        }

        if (asset.Version > CompositorBuilder.SupportedVersion) {
            context.Report(
                ImportSeverity.Error,
                $"It is version {asset.Version} and this build reads {CompositorBuilder.SupportedVersion}. "
                + "Building it would ship a frame half of which this engine does not understand."
            );

            return context.Finish();
        }

        context.Report(
            ImportSeverity.Information,
            $"Frame '{asset.Game?.Name ?? "(none)"}', {asset.Resources.Length} resource(s), "
            + $"{asset.Stages.Length} stage(s)."
        );

        context.Write(SubAssetId.Main, CompositorType, Serializer.ToBytes(asset));
        return context.Finish();
    }
}
