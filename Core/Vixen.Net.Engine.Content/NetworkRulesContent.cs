// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Assets;
using Vixen.Core.Serialization;
using Vixen.Net.Rules;

namespace Vixen.Net.Engine.Content;

/// <summary>What filling a rules registry from a build's content produced.</summary>
/// <param name="Loaded">
///     The <see cref="NetworkRulesAsset.Name" /> of every policy that went in, in address order.
/// </param>
/// <param name="Problems">
///     Addresses that were labelled a policy and could not be one, in address order.
/// </param>
/// <remarks>
///     ⚠ <b>A problem here is a content mistake, not a load failure</b> — <c>NetworkPrefabLoad</c>'s
///     line, and the same three cases behind it: an address labelled a policy that holds something
///     else, a policy this build's importer would have refused, and two policies claiming one name. A
///     missing bundle or a corrupt chunk still throws, because that is the build being broken rather
///     than the content being wrong.
/// </remarks>
public readonly record struct NetworkRulesLoad(ImmutableArray<string> Loaded, ImmutableArray<string> Problems);

/// <summary>Fills a <see cref="NetworkRulesRegistry" /> out of <see cref="Vixen.Assets" />, by label.</summary>
/// <remarks>
///     <para>
///         <b>The half <see cref="NetworkRulesRegistry.Load" /> was written for and nothing supplied.</b>
///         A <c>.vxnetrules</c> is imported, written into a bundle and addressed — and then, until
///         this existed, the only way into the registry was a hand-written <c>Load(name, rules)</c>
///         per file on every peer. <c>NetworkPrefabContent</c> makes exactly this argument about
///         prefabs one question over: a list written twice is a list that drifts, and the drift is
///         silent.
///     </para>
///     <para>
///         ⚠ <b>Silent in a particular way here, and worse than for a prefab.</b> A prefab the client
///         never registered is a spawn it drops, which shows. A policy the <em>server</em> never
///         loaded leaves every node naming it on
///         <see cref="NetworkRulesRegistry.Default" /> — server-authoritative, so nothing unsafe
///         happens — and the symptom is a game rule that does not work: a weapon nobody can pick up,
///         with a policy file in the project that reads exactly right.
///         <c>NetworkSpawner.UnresolvedRules</c> is the counter that catches it after the fact; this
///         is what stops it happening.
///     </para>
///     <para>
///         <b>Nothing about the registry is sent, and it does not have to be.</b> A policy is keyed by
///         the name it calls itself and named by <see cref="NetworkRulesReference" /> on authored
///         content, so a server and a client that read the same build agree without a handshake —
///         the same property <c>NetworkPrefabContent</c> relies on, for the same reason. In practice
///         only a server asks the questions, but a listen server is both ends of one process and a
///         client that predicts ownership reads the same policy.
///     </para>
/// </remarks>
public static class NetworkRulesContent {
    /// <summary>The label a content build puts on the group its policies live in.</summary>
    /// <remarks>
    ///     A convention rather than a rule —
    ///     <see cref="LoadAsync(NetworkRulesRegistry, AssetManager, IEnumerable{string}, CancellationToken)" />
    ///     takes whichever labels a game used. It is written down here so that the template, the
    ///     sample and the guide all name the same string.
    ///     <para>
    ///         ⚠ <b>Unlike <c>NetworkPrefabContent.Label</c>, there is no reason to narrow this.</b> A
    ///         registered prefab costs a template world held for the process, so its label says "this
    ///         may arrive over the wire" rather than "this is a prefab". A policy is six enums: a game
    ///         that labels every one of its policy files pays for a dictionary entry each, and the
    ///         failure of leaving one out is the invisible one above.
    ///     </para>
    /// </remarks>
    public const string Label = "network-rules";

    /// <summary>Loads every policy under <see cref="Label" />.</summary>
    /// <param name="registry">What to fill. Whatever is already in it stays.</param>
    /// <param name="assets">Where the content is.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What was loaded, and anything that was labelled and is not a policy.</returns>
    public static ValueTask<NetworkRulesLoad> LoadAsync(
        NetworkRulesRegistry registry,
        AssetManager assets,
        CancellationToken cancellation = default
    ) =>
        LoadAsync(registry, assets, [Label], cancellation);

    /// <summary>Loads every policy under some labels.</summary>
    /// <param name="registry">What to fill. Whatever is already in it stays.</param>
    /// <param name="assets">Where the content is.</param>
    /// <param name="labels">Which groups hold policies. A label nothing carries contributes nothing.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What was loaded, and anything that was labelled and is not a policy.</returns>
    public static ValueTask<NetworkRulesLoad> LoadAsync(
        NetworkRulesRegistry registry,
        AssetManager assets,
        IEnumerable<string> labels,
        CancellationToken cancellation = default
    ) =>
        LoadFromAsync(registry, assets, Addresses(assets, labels), cancellation);

    /// <summary>Loads a named set of addresses.</summary>
    /// <param name="registry">What to fill. Whatever is already in it stays.</param>
    /// <param name="assets">Where the content is.</param>
    /// <param name="addresses">What to load. Duplicates are read once.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What was loaded, and anything that is not a policy.</returns>
    /// <exception cref="AddressNotFoundException">An address is not in this build's content catalog.</exception>
    /// <remarks>
    ///     The escape hatch for a game whose policies are not a group. It is the caller's list, so a
    ///     name that is not in the catalog throws rather than being reported — a typo swallowed here
    ///     is a policy that silently never applies, which is the whole failure this type exists to
    ///     stop.
    /// </remarks>
    public static ValueTask<NetworkRulesLoad> LoadFromAsync(
        NetworkRulesRegistry registry,
        AssetManager assets,
        IEnumerable<string> addresses,
        CancellationToken cancellation = default
    ) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(addresses);

        return LoadCoreAsync(registry, assets, addresses, cancellation);
    }

    static async ValueTask<NetworkRulesLoad> LoadCoreAsync(
        NetworkRulesRegistry registry,
        AssetManager assets,
        IEnumerable<string> addresses,
        CancellationToken cancellation
    ) {
        var loaded = new List<string>();
        var problems = new List<string>();

        // Which name came from which address, so the second file to claim a name can say what the
        // first one was. The registry is keyed by name and holds no addresses, and a game may load
        // twice from two label sets, so this covers this call rather than the registry's whole life.
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);

        // Address order, for NetworkPrefabContent's reason: it changes no artefact, and it makes
        // which of two clashing files is the one reported stable, so a build that fails twice fails
        // the same way.
        foreach (var address in addresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            cancellation.ThrowIfCancellationRequested();

            var policy = await ReadAsync(assets, address, cancellation).ConfigureAwait(false);

            if (policy is null) {
                // Labelled a policy and is not one: a texture, a prefab, a definition, or a record
                // written by a build this one has no contract for.
                problems.Add($"'{address}' is labelled a network policy and did not read as one.");

                continue;
            }

            // The importer fills an empty name from the file name, so an empty one here is content
            // from somewhere else. Reported rather than thrown, because it is one file being wrong.
            if (policy.Name.Length == 0) {
                problems.Add(
                    $"'{address}' is a network policy with no name, so nothing could refer to it. "
                    + "NetworkRulesReference names a policy by its name and never by its address."
                );

                continue;
            }

            // NetworkRulesImporter already refuses this, so reaching here means content built by
            // something else — NetworkPrefabContent catches SceneCompiler's refusals for the same
            // reason. Loading it anyway would put a policy that decides nothing into the registry
            // under a name a prefab is relying on.
            if (policy.Validate() is { } invalid) {
                problems.Add($"'{address}': {invalid}");

                continue;
            }

            // ⚠ Caught here or not at all. NetworkRulesRegistry.Load is a dictionary assignment: two
            // files claiming one name would leave whichever came last, silently, and the symptom is
            // a rule that works or does not depending on address order. This is the one place with
            // both addresses in hand, so it says both — NetworkPrefabRegistry.Register's argument
            // about two prefabs that hash alike, one layer up.
            if (claimed.TryGetValue(policy.Name, out var first)) {
                if (!registry.TryGetNamed(policy.Name, out var already) || already != policy.Rules) {
                    problems.Add(
                        $"'{address}' and '{first}' are both called '{policy.Name}' and say different things, "
                        + "so which one governs would depend on load order. Rename one of the two policies."
                    );
                }

                continue;
            }

            claimed[policy.Name] = address;
            registry.Load(policy.Name, policy.Rules);
            loaded.Add(policy.Name);
        }

        return new([.. loaded], [.. problems]);
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

    /// <summary>Reads one address as a policy, or null if it is not one.</summary>
    /// <remarks>
    ///     ⚠ <b>The handle is released once the record is read, and on the failure path it has already
    ///     released itself.</b> <c>AssetManager</c> gives back everything a failed load claimed before
    ///     it throws, so releasing again here would take somebody else's claim on a shared dependency.
    ///     Unlike a prefab there is nothing to capture — the record <i>is</i> the asset — so what the
    ///     registry keeps is a <c>NetworkRules</c> and not a handle.
    /// </remarks>
    static async ValueTask<NetworkRulesAsset?> ReadAsync(
        AssetManager assets,
        string address,
        CancellationToken cancellation
    ) {
        // ⚠ Not inside the try. This throws AddressNotFoundException synchronously, and that one is
        // the caller being wrong rather than the content being wrong — see LoadFromAsync's remarks.
        var handle = assets.LoadAsync<NetworkRulesAsset>(address, cancellation);

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
