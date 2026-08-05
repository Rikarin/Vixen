// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Assets;
using Vixen.Core.Serialization;

namespace Vixen.Gameplay.Content;

/// <summary>What loading a build's definitions produced.</summary>
/// <param name="Catalog">The rules, with their tag table baked.</param>
/// <param name="Problems">Addresses that were labelled as definitions and are not, in address order.</param>
/// <remarks>
///     ⚠ <b>A problem here is a content mistake, not a load failure.</b> An address that is labelled a
///     definition and holds a texture is somebody's <c>.vxgroup</c> being too broad, and the honest
///     answer is to load the rest and say which one — the same posture every <c>*Library.Compile</c>
///     in doc 28 takes. A missing bundle or a corrupt chunk still throws, because that is not content
///     being wrong, it is the build being broken.
/// </remarks>
public readonly record struct DefinitionLoad(DefinitionCatalog Catalog, ImmutableArray<string> Problems);

/// <summary>Reads a build's definitions out of <see cref="Vixen.Assets" /> and bakes them into a catalog.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 § Definitions' third consequence, and the half that was owed from G0.</b> The
///         importer writes one self-describing artefact per <c>.vxdef</c> and
///         <see cref="DefinitionSerialization" /> reads one back; what was missing is the step that
///         finds them all. A game's definitions are found by <em>label</em>, because that is the
///         mechanism the content build already has for "everything of this kind" and inventing a
///         second one would be a second thing to keep in step.
///     </para>
///     <para>
///         ⚠ <b>A definition is copied out of its bundle rather than held by a handle, and doc 28's
///         sketch is wrong about this.</b> § Definitions says a definition is <em>"resolved through
///         <c>Vixen.Assets</c>, ref-counted"</em>. Ref-counting the definitions themselves would be
///         the wrong shape twice over: it puts a load call on the damage path, and it admits a state
///         in which a sword sitting in somebody's bag names a definition that has been unloaded — and
///         a <see cref="DefId" /> that sometimes resolves is worse than one that never does. The
///         catalog is loaded whole, at boot, and held for the life of the build; a live content update
///         replaces it wholesale through <see cref="DefinitionRegistry.Reload" />.
///     </para>
///     <para>
///         <b>What <em>is</em> ref-counted is what a definition points at</b> — the sword's mesh, its
///         icon, its sound. Those are <c>AssetReference</c>s inside the definition and they are loaded
///         by whoever draws them, on the ordinary handle path, long after this has run.
///     </para>
///     <para>
///         ⚠ <b><see cref="DefinitionCatalog.BuildHash" /> and <c>ContentCatalog.BuildHash</c> are
///         different numbers and neither substitutes for the other.</b> The content catalog's covers
///         every byte a build shipped and is what doc 27's placement filters on; this one covers the
///         addresses and the tag table, which is what two peers have to agree on before a tag index
///         means the same thing at both ends.
///     </para>
/// </remarks>
public static class DefinitionContent {
    /// <summary>The label a content build puts on the group its definitions live in.</summary>
    /// <remarks>
    ///     A convention rather than a rule — <see cref="LoadAsync(AssetManager, IEnumerable{string}, CancellationToken)" />
    ///     takes whichever labels a game used. It is written down here so that the template, the
    ///     sample and the importer's documentation all name the same string.
    /// </remarks>
    public const string Label = "definitions";

    /// <summary>Loads everything under <see cref="Label" />.</summary>
    /// <param name="assets">Where the content is.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The catalog, and anything that was labelled and is not a definition.</returns>
    public static ValueTask<DefinitionLoad> LoadAsync(
        AssetManager assets,
        CancellationToken cancellation = default
    ) =>
        LoadAsync(assets, [Label], cancellation);

    /// <summary>Loads everything under some labels.</summary>
    /// <param name="assets">Where the content is.</param>
    /// <param name="labels">Which groups hold definitions. A label nothing carries contributes nothing.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The catalog, and anything that was labelled and is not a definition.</returns>
    /// <remarks>
    ///     Several labels rather than one, because a game that bundles its items separately from its
    ///     quests has two groups and should not have to load them twice into two catalogs that then
    ///     disagree about tag numbering.
    /// </remarks>
    public static ValueTask<DefinitionLoad> LoadAsync(
        AssetManager assets,
        IEnumerable<string> labels,
        CancellationToken cancellation = default
    ) {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(labels);

        var addresses = new List<string>();

        foreach (var label in labels) {
            addresses.AddRange(assets.Catalog.ByLabel(label));
        }

        return LoadFromAsync(assets, addresses, cancellation);
    }

    /// <summary>Loads a named set of addresses.</summary>
    /// <param name="assets">Where the content is.</param>
    /// <param name="addresses">What to read. Duplicates are read once.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The catalog, and anything that is not a definition.</returns>
    /// <exception cref="AddressNotFoundException">An address is not in this build's content catalog.</exception>
    public static async ValueTask<DefinitionLoad> LoadFromAsync(
        AssetManager assets,
        IEnumerable<string> addresses,
        CancellationToken cancellation = default
    ) {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(addresses);

        var builder = new DefinitionCatalogBuilder();
        var problems = new List<string>();

        // Address order. The catalog's own hash already sorts, so this changes no artefact — what it
        // makes stable is which of two clashing addresses is reported, and in what order, so a build
        // that fails twice fails the same way.
        foreach (var address in addresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            cancellation.ThrowIfCancellationRequested();

            var bytes = await ReadAsync(assets, address, cancellation).ConfigureAwait(false);

            try {
                DefinitionSerialization.Add(builder, address, bytes);
            } catch (SerializationException exception) {
                // Labelled as a definition and is not one, or is one this build has no type for.
                problems.Add($"'{address}' is labelled a definition and did not read as one: {exception.Message}");
            } catch (InvalidOperationException exception) {
                // Two addresses in one catalog, or two that hash to one DefId — the builder's own
                // refusals, which are about the set rather than about the file.
                problems.Add(exception.Message);
            }
        }

        return new(builder.Build(), [.. problems]);
    }

    static async ValueTask<byte[]> ReadAsync(AssetManager assets, string address, CancellationToken cancellation) {
        var stream = await assets.OpenAsync(address, cancellation).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false)) {
            if (stream is MemoryStream memory) {
                // What OpenAsync actually hands back today, and copying it again would double the
                // peak footprint of loading ten thousand definitions for nothing.
                return memory.ToArray();
            }

            using var copy = new MemoryStream();

            await stream.CopyToAsync(copy, cancellation).ConfigureAwait(false);

            return copy.ToArray();
        }
    }
}
