// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Rendering.Materials;

namespace Vixen.Editor.Assets.Materials;

/// <summary>How a material is compiled.</summary>
/// <remarks>
///     Empty, like <c>SceneImportSettings</c> nearly is and for the same reason: a <c>.vxmat</c> is
///     already engine data, so there is nothing about the conversion to decide. What a build may vary
///     — which shader variants are baked for which target — is the effect system's decision and not
///     this file's.
/// </remarks>
[DataContract("MaterialImporter")]
public sealed record MaterialImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxmat</c> into the material asset a player loads.</summary>
/// <remarks>
///     <para>
///         <b>The step <c>NativeFormatImporter</c> predicted and stopped short of.</b> That importer
///         carried the document forward verbatim and said so: import produces editor-domain objects and
///         a compiler turns them into the runtime chunk, "and those compilers do not exist yet". This
///         is the material's, written on <see cref="Scenes.SceneImporter" />'s terms — read the file,
///         declare what it points at, write the result as the chunk an address resolves to.
///     </para>
///     <para>
///         <b>Why the runtime cannot read the <c>.vxmat</c> itself.</b> <c>Vixen.Rendering</c>
///         deliberately links no YAML parser — the note is in its project file, beside the compositor
///         asset that made the same choice — so a shipping build reads a baked record graph and never
///         sees the text. The parse happens here, once, at build time, on a machine that has a parser.
///     </para>
///     <para>
///         <b>It compiles the material to find out whether it is one.</b> The artefact is the document,
///         not the compilation, because <see cref="MaterialCompiler" /> is a pure function the runtime
///         also has — but running it here is what turns "this material has two normal maps" from a
///         wrong image in a level into a message beside the file that caused it. Nothing of the result
///         is kept.
///     </para>
/// </remarks>
[Importer(Extension)]
public sealed class MaterialImporter : AssetImporter<MaterialImportSettings> {
    /// <summary>What a material is written as.</summary>
    public const string Extension = ".vxmat";

    /// <summary>The type a compiled material's chunk is recorded as.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>[DataContract]</c> alias of the type actually written, which is
    ///         <see cref="MaterialContent" /> — not <c>"Material"</c>, which is
    ///         <see cref="MaterialDescriptor" />'s.</b> A chunk's type string is resolved through the
    ///         type registry at load, so the wrong alias hands the bytes of one record to the reader of
    ///         another: <c>MaterialDescriptor data holds 5 members and this build knows 3</c>, thrown
    ///         from inside <c>AssetManager.Load</c> about content the build had just declared good.
    ///     </para>
    ///     <para>
    ///         It said <c>"Material"</c> because that is the word the old text-carrying importer used,
    ///         and the argument was continuity of the address. The address is the catalog's and has
    ///         nothing to do with this; this names the <em>type of the bytes</em>. Exactly the mistake
    ///         <c>ModelImporter</c> made with <c>"Mesh"</c> against <c>MeshData</c>, and it survived for
    ///         the same reason — no project had loaded a <c>.vxmat</c> at run time, so nothing ever
    ///         resolved the string.
    ///     </para>
    /// </remarks>
    public const string MaterialType = "MaterialContent";

    /// <summary>
    ///     Teaches the binder how a vector reads before anything asks it to read a feature.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A feature is where a material's vectors are</b> — a base colour, a specular tint — and
    ///     without this they are "Vector3 has no descriptor" from inside the bind. Registered from a
    ///     static constructor rather than a module initializer, for the reason
    ///     <see cref="MathScalars" /> gives about a process-wide table.
    /// </remarks>
    static MaterialImporter() => MathScalars.Register();

    /// <inheritdoc />
    /// <remarks>
    ///     Tied to the content format's version, so that a change to what a compiled material holds
    ///     re-imports every material in every project.
    /// </remarks>
    public override int Version => MaterialContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        MaterialImportSettings settings,
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
            // Reported rather than thrown, so the run continues and the author gets every broken file
            // in one pass instead of one per build.
            context.Report(ImportSeverity.Error, $"It is not valid YAML: {failure.Message}");
            return context.Finish();
        }

        if (document is not YamlMapping root) {
            context.Report(
                ImportSeverity.Error,
                "Its root is not a mapping. A material is a mapping of fields, whatever its extension."
            );

            return context.Finish();
        }

        // Before the bind, and this is what makes replacing a texture re-import the material that
        // samples it. The scan finds every `vx:` scalar in the document, which is a superset of the
        // ones the content type binds — a parameter the editor wrote and this does not read still
        // counts as something the material points at.
        if (AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        MaterialContent content;

        try {
            content = YamlSerializer.Parse<MaterialContent>(text);
        } catch (Exception failure) when (failure is YamlBindingException or FormatException) {
            // A feature tag this build has never heard of lands here, which is the honest message: the
            // material names something the engine does not have, and no artefact should be written.
            context.Report(ImportSeverity.Error, failure.Message);
            return context.Finish();
        }

        if (content.Version > MaterialContent.Current) {
            context.Report(
                ImportSeverity.Error,
                $"This material is version {content.Version} and this build reads {MaterialContent.Current}. "
                + "Importing it would bind the parts it recognises and drop the rest."
            );

            return context.Finish();
        }

        if (!MaterialShading.TryResolve(content.Shading, out var shading)) {
            context.Report(
                ImportSeverity.Error,
                $"'{content.Shading}' is not a shading model this build has. It knows "
                + $"{string.Join(", ", MaterialShading.All.Keys)}."
            );

            return context.Finish();
        }

        if (!Compiles(context, content, shading)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, MaterialType, Serializer.ToBytes(content));
        return context.Finish();
    }

    /// <summary>Compiles the material to find out whether it is one, and says what it found.</summary>
    /// <returns>Whether an artefact should be written.</returns>
    /// <remarks>
    ///     Warnings are reported and do not stop the build — "this material has no features, so it is a
    ///     white dielectric" is a true thing to say about a block-out and a wrong thing to fail on.
    /// </remarks>
    static bool Compiles(ImportContext context, MaterialContent content, IMaterialShading shading) {
        var compilation = MaterialCompiler.Compile(content.ToDescriptor(shading));

        foreach (var diagnostic in compilation.Diagnostics) {
            context.Report(
                diagnostic.IsError ? ImportSeverity.Error : ImportSeverity.Warning,
                diagnostic.Message
            );
        }

        return !compilation.Failed;
    }
}
