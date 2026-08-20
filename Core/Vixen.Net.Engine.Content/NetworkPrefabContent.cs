// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Assets;
using Vixen.Core.Serialization;
using Vixen.Engine.Scenes;

namespace Vixen.Net.Engine.Content;

/// <summary>What filling a registry from a build's content produced.</summary>
/// <param name="Registered">What went in, in address order.</param>
/// <param name="Problems">Addresses that were labelled a networked prefab and are not, in address order.</param>
/// <remarks>
///     ⚠ <b>A problem here is a content mistake, not a load failure.</b> An address that is labelled a
///     networked prefab and holds a texture is somebody's <c>.vxgroup</c> being too broad, and the
///     honest answer is to register the rest and say which one. A missing bundle, a corrupt chunk or
///     content from a newer build still throws, because that is not content being wrong, it is the
///     build being broken.
/// </remarks>
public readonly record struct NetworkPrefabLoad(
    ImmutableArray<NetworkPrefab> Registered,
    ImmutableArray<string> Problems
);

/// <summary>Fills a <see cref="NetworkPrefabRegistry" /> out of <see cref="Vixen.Assets" />, by label.</summary>
/// <remarks>
///     <para>
///         <b>What makes "networked prefab" something an asset <em>is</em> rather than something a
///         start-up path remembers to say.</b> <see cref="NetworkPrefabRegistry.Register" /> is a
///         call, and a call has to be written once per prefab on every peer that could be told to
///         spawn it — so the failure mode is a client that was never told about the crate, receives
///         the spawn, and drops it. A label is a property of the content, so both ends read the same
///         list out of the same build and neither maintains it.
///     </para>
///     <para>
///         ⚠ <b>Both peers still build the registry independently, and that is the design rather
///         than an accident.</b> A prefab's id is the hash of its address
///         ([08](../../../docs/plan/08-asset-pipeline-and-addressables.md)), so a server and a client
///         that register the same addresses agree without a handshake. What this changes is only
///         <em>where the list of addresses comes from</em>; what it does not change is that nothing
///         about the registry is sent.
///     </para>
///     <para>
///         ⚠ <b>A bad label is a problem and a bad address is an exception.</b> A
///         <c>.vxgroup</c> broad enough to sweep a texture in is a content mistake, so the rest
///         registers and the problem is named. An address that is not in this build's content catalog
///         at all is the <em>caller</em> being wrong — a typo in a hand-written list — and swallowing
///         it would turn that typo into a prefab that silently can never be spawned.
///     </para>
///     <para>
///         <b>The templates are held and the assets are not.</b> A <see cref="Prefab" /> is a
///         template world, built once per prefab and stamped out for the life of the build — so this
///         releases its <see cref="AssetHandle{T}" /> as soon as the template is captured. What a
///         prefab's components point at — a mesh, a material, a sound — is an
///         <c>AssetReference</c> inside the component and is loaded on the ordinary handle path by
///         whoever draws it, which is the same division <c>DefinitionContent</c> makes.
///     </para>
///     <para>
///         ⚠ <b>A prefab that arrives this way has exactly one networked node today, whatever its
///         author marked.</b> <see cref="NetworkPrefabRegistry" /> reads the marker off the template
///         as <c>NetworkId</c>, and a compiled scene may only name a component that is
///         <c>[Component]</c> <b>and</b> <c>[DataContract]</c> — which <c>NetworkId</c> is not, so
///         <c>SceneContent.Capture</c> drops it without a word. It is asserted in this assembly's
///         tests rather than left to be found, because the symptom is a turret whose barrel simply
///         never replicates. See the README.
///     </para>
/// </remarks>
public static class NetworkPrefabContent {
    /// <summary>The label a content build puts on the group its networked prefabs live in.</summary>
    /// <remarks>
    ///     A convention rather than a rule —
    ///     <see cref="LoadAsync(NetworkPrefabRegistry, AssetManager, IEnumerable{string}, CancellationToken)" />
    ///     takes whichever labels a game used. It is written down here so that the template, the
    ///     sample and the guide all name the same string.
    ///     <para>
    ///         ⚠ <b>Not every prefab, deliberately.</b> A game has far more prefabs than it replicates,
    ///         and every registered one costs a template world held for the process — so the label
    ///         says "this may arrive over the wire" rather than "this is a prefab".
    ///     </para>
    /// </remarks>
    public const string Label = "networked-prefabs";

    /// <summary>Registers everything under <see cref="Label" />.</summary>
    /// <param name="registry">What to fill. Whatever is already in it stays.</param>
    /// <param name="assets">Where the content is.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What was registered, and anything that was labelled and is not a prefab.</returns>
    public static ValueTask<NetworkPrefabLoad> LoadAsync(
        NetworkPrefabRegistry registry,
        AssetManager assets,
        CancellationToken cancellation = default
    ) =>
        LoadAsync(registry, assets, [Label], cancellation);

    /// <summary>Registers everything under some labels.</summary>
    /// <param name="registry">What to fill. Whatever is already in it stays.</param>
    /// <param name="assets">Where the content is.</param>
    /// <param name="labels">Which groups hold networked prefabs. A label nothing carries contributes nothing.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What was registered, and anything that was labelled and is not a prefab.</returns>
    /// <remarks>
    ///     Several labels rather than one, because a game that bundles its vehicles separately from
    ///     its creatures has two groups and one registry — and unlike a definition catalog there is
    ///     no second-best answer here, since two registries would answer
    ///     <see cref="NetworkPrefabRegistry.TryGet(Vixen.Net.Replication.NetworkPrefabId, out NetworkPrefab?)" />
    ///     differently and a spawn carries the id and nothing else.
    /// </remarks>
    public static ValueTask<NetworkPrefabLoad> LoadAsync(
        NetworkPrefabRegistry registry,
        AssetManager assets,
        IEnumerable<string> labels,
        CancellationToken cancellation = default
    ) =>
        LoadFromAsync(registry, assets, Addresses(assets, labels), cancellation);

    /// <summary>Registers a named set of addresses.</summary>
    /// <param name="registry">What to fill. Whatever is already in it stays.</param>
    /// <param name="assets">Where the content is.</param>
    /// <param name="addresses">What to register. Duplicates are read once.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What was registered, and anything that is not a prefab.</returns>
    /// <exception cref="AddressNotFoundException">An address is not in this build's content catalog.</exception>
    /// <remarks>
    ///     The escape hatch for a game whose networked prefabs are not a group — a list in code, or
    ///     one derived from something else the game knows. It is the caller's list, so a name that is
    ///     not in the catalog throws rather than being reported.
    /// </remarks>
    public static ValueTask<NetworkPrefabLoad> LoadFromAsync(
        NetworkPrefabRegistry registry,
        AssetManager assets,
        IEnumerable<string> addresses,
        CancellationToken cancellation = default
    ) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(addresses);

        return RegisterAsync(registry, assets, addresses, cancellation);
    }

    static async ValueTask<NetworkPrefabLoad> RegisterAsync(
        NetworkPrefabRegistry registry,
        AssetManager assets,
        IEnumerable<string> addresses,
        CancellationToken cancellation
    ) {
        var registered = new List<NetworkPrefab>();
        var problems = new List<string>();

        // Address order. It changes no artefact — what it makes stable is which of two clashing
        // addresses is the one reported, and in what order, so a build that fails twice fails the
        // same way.
        foreach (var address in addresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            cancellation.ThrowIfCancellationRequested();

            // A second load of the same address would capture a second template and Register would
            // refuse it — rightly, because two templates under one address make the same spawn build
            // different things on two peers. Nothing has changed under an address that is already
            // registered, so there is nothing to do and nothing to report.
            if (registry.TryGet(address, out var already)) {
                registered.Add(already);

                continue;
            }

            var asset = await ReadAsync(assets, address, cancellation).ConfigureAwait(false);

            if (asset is null) {
                // Labelled a networked prefab and is not one: a texture, a definition, a scene, or a
                // prefab compiled by a build this one has no components for.
                problems.Add($"'{address}' is labelled a networked prefab and did not read as one.");

                continue;
            }

            Prefab prefab;

            try {
                prefab = asset.ToPrefab();
            } catch (InvalidOperationException exception) {
                // Not one root, which SceneCompiler already refuses at build time — so reaching here
                // means content built by something else.
                problems.Add($"'{address}': {exception.Message}");

                continue;
            } catch (SceneComponentException exception) {
                problems.Add($"'{address}': {exception.Message}");

                continue;
            }

            try {
                registry.Register(address, prefab);
                registered.Add(registry.Require(address));
            } catch (InvalidOperationException exception) {
                // Two addresses that hash to one NetworkPrefabId — the registry's own refusal, which
                // is about the set rather than about the file, and which it is the only thing holding
                // both names to be able to make.
                prefab.Dispose();
                problems.Add(exception.Message);
            }
        }

        return new([.. registered], [.. problems]);
    }

    static List<string> Addresses(AssetManager assets, IEnumerable<string> labels) {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(labels);

        var addresses = new List<string>();

        foreach (var label in labels) {
            addresses.AddRange(assets.Catalog.ByLabel(label));
        }

        return addresses;
    }

    /// <summary>Reads one address as a prefab asset, or null if it is not one.</summary>
    /// <remarks>
    ///     ⚠ <b>The handle is released as soon as the template is captured, and on the failure path
    ///     it has already released itself.</b> <c>AssetManager.LoadRootAsync</c> gives back everything
    ///     a failed load claimed before it throws, so releasing again here would take somebody else's
    ///     claim on a shared dependency.
    /// </remarks>
    static async ValueTask<PrefabAsset?> ReadAsync(
        AssetManager assets,
        string address,
        CancellationToken cancellation
    ) {
        // ⚠ Not inside the try. This throws AddressNotFoundException synchronously, and that one is
        // the caller being wrong rather than the content being wrong — see the type's remarks.
        var handle = assets.LoadAsync<PrefabAsset>(address, cancellation);

        try {
            var asset = await handle.Completion.ConfigureAwait(false);

            handle.Release();

            return asset;
        } catch (InvalidOperationException) {
            // The chunk is something else — the address is right and the type is not.
            return null;
        } catch (SerializationException) {
            // Not a serialized object at all, or one naming a contract this build does not have.
            return null;
        }
    }
}
