// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Assets;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Audio;

/// <summary>How a <c>.vxmixer</c> is compiled. There is nothing to configure.</summary>
/// <remarks>
///     The settings exist because an importer has them, and the version is what invalidates every
///     mixer artefact when the document format moves.
/// </remarks>
[DataContract("MixerImporter")]
public sealed record MixerImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a project's audio mixer into the chunk a game loads by address.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The two halves existed and did not meet.</b>
///         <c>AudioEngine.LoadMixer(MixerAsset)</c> has been there and tested since the mixing layer
///         was written, and <c>AudioMixerDocument</c> authors and saves the YAML — but no
///         <c>[Importer]</c> claimed <c>.vxmixer</c>, so the file fell to <see cref="RawImporter" />
///         and shipped as a chunk called <c>Blob</c>. A game could not load a mix a sound designer
///         had made, and nothing said so
///         ([#473](https://github.com/Rikarin/Vixen/issues/473)).
///     </para>
///     <para>
///         <b>Compiled rather than carried, which is <see cref="Compositors.CompositorImporter" />'s
///         argument restated.</b> A host reads this with
///         <c>AssetManager.LoadAsync&lt;MixerAsset&gt;</c>, which goes through the binary serializer,
///         so shipping the authored YAML would be shipping a document no runtime links a parser for.
///         <see cref="NativeFormatImporter" /> carries text only for the two formats whose runtime
///         reader takes text; this is not one of them.
///     </para>
///     <para>
///         ⚠ <b>The document is bound but not <i>built</i>, and that is deliberate.</b>
///         <c>MixerBuilder.Build</c> needs a live <c>AudioMixer</c> and returns the problems a game
///         would hit at load; running it here would need an audio device in a content build and would
///         move a decision the runtime has to make anyway — a send naming a bus that is not there is
///         reported by <c>LoadMixer</c>, and reported again identically by the editor's own
///         <c>AudioMixerDocument.Validate</c>, which is the panel that can do something about it.
///         What the import owes is that the bytes are a <c>MixerAsset</c>, and a document that does
///         not bind is where that stops being true.
///     </para>
/// </remarks>
[Importer(".vxmixer")]
public sealed class MixerImporter : AssetImporter<MixerImportSettings> {
    /// <summary>The type a compiled mixer's chunk is recorded as.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>[DataContract]</c> alias, not a friendly name.</b>
    ///     <c>ImportPipeline.TypeIdOf</c> resolves this through the type registry and falls back to
    ///     <c>ImportedArtifact</c> when it cannot — which stamps the chunk with an editor type and
    ///     produces "nothing registered in this process claims it" at load, in a game, about content
    ///     the build declared good.
    /// </remarks>
    public const string MixerType = "MixerAsset";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        MixerImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

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
                "Its root is not a mapping. A mixer is a mapping with a `buses` sequence in it."
            );

            return context.Finish();
        }

        // A mixer names its effects and its snapshots by string rather than by reference, so this
        // ordinarily finds nothing. It runs anyway, because "the scan runs over every document" is a
        // rule worth having no exceptions to — and a mixer that grows a reference to an impulse
        // response is exactly the case that would otherwise not re-import when the file was replaced.
        if (AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        MixerAsset asset;

        try {
            asset = context.BindYaml<MixerAsset>(text, "a mixer");
        } catch (YamlBindingException failure) {
            context.Report(ImportSeverity.Error, $"It is not a mixer: {failure.Message}");
            return context.Finish();
        }

        context.Report(
            ImportSeverity.Information,
            $"{asset.Buses.Length} bus(es), {asset.Snapshots.Length} snapshot(s), "
            + $"{asset.Parameters.Length} parameter(s)."
        );

        context.Write(SubAssetId.Main, MixerType, Serializer.ToBytes(asset));
        return context.Finish();
    }
}
