// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.VfxGraph;
using Vixen.Rendering.Vfx;

namespace Vixen.Editor.Assets.Vfx;

/// <summary>How a visual effect is compiled.</summary>
/// <remarks>
///     Empty, on <c>MaterialImportSettings</c>'s terms: a <c>.vxvfx</c> is already engine data and
///     there is nothing about the conversion to decide. What a build might one day vary — whether the
///     GPU kernels are emitted and compiled for the target as well as the CPU graph — is doc 06's
///     dispatch path and not this file's.
/// </remarks>
[DataContract("VfxImporter")]
public sealed record VfxImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxvfx</c> into the effect asset a player loads.</summary>
/// <remarks>
///     <para>
///         <b>The compiler <c>NativeFormatImporter</c> named as owed.</b> That importer carried the
///         graph forward verbatim and said so — its own remarks list <c>.vxvfx</c> among "the ones
///         whose compiler is still owed", and <c>docs/overview.md</c> recorded the consequence: a game
///         could not load one by address, so an effect had to be written in C#. This is the step that
///         closes it, written on <see cref="Materials.MaterialImporter" />'s terms.
///     </para>
///     <para>
///         <b>What is written is the <em>compiled</em> graph, not the document.</b> That is the one
///         place this departs from the material's shape, and the reason is what the two files hold: a
///         <c>.vxmat</c> is a description a runtime can bind directly, while a <c>.vxvfx</c> is nodes,
///         edges and canvas positions — none of which a simulation has any use for, and all of which
///         needs a node library and a topological sort to make sense of. So the artefact is
///         <see cref="VfxEffectContent" />: the flat instruction list both backends run, with nothing
///         of the editor left in it.
///     </para>
///     <para>
///         ⚠ <b>Which means an effect's diagnostics are build errors rather than runtime surprises.</b>
///         A graph with no spawner produces no particles; one whose updater reads an attribute nothing
///         writes runs over zeroed memory and looks like an effect that does nothing. Both come out of
///         <see cref="VfxGraphCompiler" /> as diagnostics, and both are reported here against the file
///         that caused them.
///     </para>
///     <para>
///         <b>The shader is compiled and thrown away</b>, exactly as
///         <see cref="Materials.MaterialImporter" /> compiles a material to find out whether it is one.
///         <see cref="VfxGraphCompiler" /> emits the Raven source alongside the graph from one call —
///         so asking for it costs nothing and proves the GPU half of the same effect exists. What a
///         device would need is a compiled module, which is the effect system's business and not an
///         importer's.
///     </para>
/// </remarks>
[Importer(Extension)]
public sealed class VfxImporter : AssetImporter<VfxImportSettings> {
    /// <summary>What a visual effect is written as.</summary>
    public const string Extension = ".vxvfx";

    /// <summary>The type a compiled effect's chunk is recorded as.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>[DataContract]</c> alias of the type actually written</b>, which is
    ///     <see cref="VfxEffectContent" /> — not <c>"VisualEffect"</c>, which is what the old
    ///     text-carrying importer called these bytes. A chunk's type string is resolved through the
    ///     type registry at load, so the wrong alias hands one record's bytes to another's reader.
    ///     <see cref="Materials.MaterialImporter.MaterialType" /> records the same mistake being made
    ///     twice before, with <c>"Material"</c> and with <c>"Mesh"</c>, and why it survived: no project
    ///     had ever loaded one at run time, so nothing resolved the string.
    /// </remarks>
    public const string EffectType = "VfxEffectContent";

    /// <inheritdoc />
    /// <remarks>
    ///     Tied to the content format's version, so a change to what a compiled effect holds
    ///     re-imports every effect in every project.
    /// </remarks>
    public override int Version => VfxEffectContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        VfxImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (text.Trim().Length == 0) {
            // An empty file is what `vixen new` and the editor's "create effect" both leave behind
            // before anybody opens it, and the editor fills it with a spawner and an output on first
            // open. Reported rather than written, because an effect with no graph would compile to a
            // system that spawns nothing — which is a working asset that does nothing.
            context.Report(
                ImportSeverity.Warning,
                "This effect is empty. Open it in the editor, which starts a new graph with a spawner "
                + "and an output in it, and save."
            );

            return context.Finish();
        }

        NodeGraphAsset stored;

        try {
            stored = YamlSerializer.Parse<NodeGraphAsset>(text);
        } catch (Exception failure) when (failure is YamlParseException or YamlBindingException or FormatException) {
            // Reported rather than thrown, so the run continues and the author gets every broken file
            // in one pass instead of one per build.
            context.Report(ImportSeverity.Error, $"It is not a readable effect graph: {failure.Message}");
            return context.Finish();
        }

        // The reference scan, on the material's terms. A VFX graph points at nothing today — its
        // parameters are numbers — but a node that named a texture or a mesh would be found by this
        // rather than by somebody remembering to add it, which is what makes replacing that asset
        // re-import the effects that use it.
        if (YamlReader.Read(text) is YamlMapping root && AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        NodeGraphModel graph;

        try {
            graph = NodeGraphDocument.Load(stored, out var repairs);

            foreach (var repair in repairs) {
                // Repairs and refusals both arrive here. A graph from a later version is refused
                // outright and the message says so; a node this build has dropped is repaired away,
                // which is a real change to the effect and worth a line in the build.
                context.Report(ImportSeverity.Warning, $"{repair.Id}: {repair.Message}");
            }
        } catch (Exception failure) when (failure is YamlBindingException or NotSupportedException) {
            context.Report(ImportSeverity.Error, failure.Message);
            return context.Finish();
        }

        var result = new VfxGraphCompiler(VfxNodeLibrary.Create()) {
            DefaultName = Path.GetFileNameWithoutExtension(context.SourcePath.ToString())
        }.Compile(graph);

        foreach (var diagnostic in result.Diagnostics) {
            context.Report(ImportSeverity.Error, $"{diagnostic.Id}: {diagnostic.Message}");
        }

        if (result.Artefact is not { } artefact) {
            // The compiler reported why — no spawner, a cycle, an updater reading what nothing writes
            // — so there is nothing to add and nothing to write.
            return context.Finish();
        }

        context.Write(SubAssetId.Main, EffectType, Serializer.ToBytes(VfxEffectContent.From(artefact.Graph)));
        return context.Finish();
    }
}
