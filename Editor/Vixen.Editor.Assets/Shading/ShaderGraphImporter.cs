// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Shading;

/// <summary>How a shader graph is compiled.</summary>
/// <remarks>
///     Empty, on <c>VfxImportSettings</c>'s terms: a <c>.vxshadergraph</c> is a graph and there is
///     nothing about the conversion to decide. What a build might one day vary — which permutations
///     of the generated shader are baked — belongs to the effect manifest, which is a project's
///     decision and not this file's.
/// </remarks>
[DataContract("ShaderGraphImporter")]
public sealed record ShaderGraphImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Checks a <c>.vxshadergraph</c>, and writes nothing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One of the five extensions the editor's own Create menu writes, opens and saves and
///         no importer claimed</b> — <c>docs/overview.md</c> records the set, and
///         <c>CreateAssetMenuAttribute</c>'s remarks state the contract it broke. Without this the
///         fallback took a graph as a <c>Blob</c> no typed reader resolves.
///     </para>
///     <para>
///         ⚠ <b>It writes no artefact, and that is the one place it departs from every other
///         importer in this assembly.</b> What a shader graph produces is Raven <em>source</em>, and
///         source is not content: it is an input to a shader compilation, which
///         <see cref="ShaderGraphSources" /> assembles for the editor and for
///         <c>ShaderBuildRunner</c> at the moment one is needed. Writing the text into the artefact
///         store would put a second copy of it behind an address nothing resolves, and would make the
///         compilation depend on an import having run — which is exactly the coupling that stops an
///         editor opening a project it has not built.
///     </para>
///     <para>
///         <b>So what this is for is the diagnostics.</b> It compiles the graph to find out whether
///         it is one, on <c>MaterialImporter</c>'s terms — "this graph has two masters", "this
///         surface reads a world position" — and reports them beside the file that caused them
///         rather than as a shader that mysteriously fails to appear. A graph that does not compile
///         is an <em>error</em> here even though nothing is written, because a material naming it
///         resolves to an effect miss, which is a draw that does not happen with nothing in the log
///         about a material.
///     </para>
///     <para>
///         <b>A standalone graph is imported without complaint.</b> It is a legitimate thing to have
///         — a preview thumbnail is one, and so is a shader an author hands to <c>raven compile</c> —
///         and only a surface graph reaches a material.
///     </para>
/// </remarks>
[Importer(ShaderGraphSources.Extension)]
public sealed class ShaderGraphImporter : AssetImporter<ShaderGraphImportSettings> {
    /// <inheritdoc />
    /// <remarks>
    ///     Bumped when what the graph compiler emits changes, so that every graph in every project is
    ///     re-checked — the same reason <c>MaterialImporter</c> ties its version to the content
    ///     format's.
    /// </remarks>
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        ShaderGraphImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (text.Trim().Length == 0) {
            // What "create shader graph" leaves behind before anybody opens it, and the editor fills
            // it on first open. A warning rather than an error, on VfxImporter's terms: an empty
            // graph is unfinished work rather than broken work.
            context.Report(
                ImportSeverity.Warning,
                "This shader graph is empty. Open it in the editor and add a master node."
            );

            return context.Finish();
        }

        // The reference scan, on the material's terms. A shader graph points at nothing today — its
        // properties are names, and the textures behind them are the material's — but a node that
        // named an asset would be found by this rather than by somebody remembering to add it.
        if (YamlReader.Read(text) is YamlMapping root && AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        // ⚠ The text, not the path. `SourcePath` is a *virtual* path into the project's VFS —
        // `/Assets/Thing.vxshadergraph` — so opening it as a file is a "could not find part of the
        // path" reported against an asset that is plainly there.
        var compiled = ShaderGraphSources.From(context.SourcePath.ToString(), text);

        foreach (var diagnostic in compiled.Diagnostics) {
            context.Report(ImportSeverity.Error, diagnostic);
        }

        return context.Finish();
    }
}
