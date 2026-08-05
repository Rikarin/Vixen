// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Gameplay;

namespace Vixen.Editor.Assets.Gameplay;

/// <summary>Settings for the one importer every gameplay definition goes through.</summary>
[DataContract("DefinitionImporter")]
public sealed record DefinitionImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxdef</c> — an item, a quest, an effect, a loot table, a game's own kind.</summary>
/// <remarks>
///     <para>
///         <b>One importer, and the type tag is the discriminator</b> —
///         <a href="../../../docs/plan/28-gameplay-framework.md">doc 28</a> G-Q1, settled in favour of
///         one. <c>.vxitem</c>, <c>.vxquest</c> and <c>.vxdef</c> all route here; what a file
///         <em>is</em> comes from its <c>!ItemDefinition</c> tag, resolved through the generated type
///         registry, so a game adding <c>!MyCustomDefinition</c> adds no importer and edits no list.
///         The extensions are cosmetic and exist so an editor can associate an icon and a template
///         with them.
///     </para>
///     <para>
///         ⚠ <b>The artefact is self-describing, which no other artefact in the engine is.</b> A
///         catalog reads a whole directory of these without knowing any of their types, so the bytes
///         lead with the type's alias — see <see cref="DefinitionSerialization" />, which is where the
///         reason is written down at length.
///     </para>
///     <para>
///         <b>What this adds over copying the file is the checking</b>, and there is deliberately not
///         much of it: a definition whose tag names nothing, whose YAML does not bind, or whose type
///         is not a <see cref="Definition" /> fails the build. Whether the <em>tags</em> it mentions
///         exist is not checkable here — a tag exists because some definition mentions it, so the
///         question only has an answer once every definition has been imported, which is the
///         catalog's job and not one file's.
///     </para>
/// </remarks>
[Importer(".vxdef", ".vxitem", ".vxquest", ".vxeffect", ".vxloot", ".vxrecipe")]
public sealed class DefinitionImporter : AssetImporter<DefinitionImportSettings> {
    /// <summary>What the artefact's type is called, whatever kind of definition it holds.</summary>
    /// <remarks>
    ///     One type string for every definition, because the payload's own leading alias is the
    ///     discriminator and two places saying which type this is would be two places that can
    ///     disagree.
    /// </remarks>
    public const string ArtefactType = "GameplayDefinition";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        DefinitionImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);

            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        Definition definition;

        try {
            definition = YamlSerializer.Parse<Definition>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            context.Report(ImportSeverity.Error, exception.Message);

            return context.Finish();
        }

        try {
            context.Write(SubAssetId.Main, ArtefactType, DefinitionSerialization.ToBytes(definition));
        } catch (SerializationException exception) {
            context.Report(ImportSeverity.Error, exception.Message);
        }

        return context.Finish();
    }
}
