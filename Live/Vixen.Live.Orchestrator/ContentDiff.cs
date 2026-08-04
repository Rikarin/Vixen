// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Live.Orchestration;

/// <summary>What a catalog entry is, as far as deciding whether it can change under a live realm.</summary>
/// <param name="Address">The addressable address. The identity.</param>
/// <param name="Hash">What it currently is.</param>
/// <param name="Kind">What sort of thing — <c>definition</c>, <c>prefab</c>, <c>scene</c>, <c>bundle</c>.</param>
/// <param name="Schema">
///     The shape of the thing at that address, when it has one — a definition's field list, a
///     replicated component's layout. Changing it is the whole reason this field exists.
/// </param>
public readonly record struct ContentEntry(string Address, ulong Hash, string Kind, string Schema = "") {
    /// <summary>The address. Null only on <c>default</c>.</summary>
    public string Address { get; init; } = Address ?? "";

    /// <summary>The kind. Null only on <c>default</c>.</summary>
    public string Kind { get; init; } = Kind ?? "";

    /// <summary>The schema. Null only on <c>default</c>. <b>Empty means unknown, not unchanging.</b></summary>
    public string Schema { get; init; } = Schema ?? "";

    /// <summary>Whether the shape of this entry is known at all.</summary>
    /// <remarks>
    ///     ⚠ <b>A content catalog does not carry one today</b>, and that is the gap this property
    ///     exists to make visible rather than to paper over. <c>CatalogEntry</c> has an address, a
    ///     content id, a bundle and a size — nothing that says whether a definition gained a field.
    ///     So anything projected from a catalog has an unknown shape, and
    ///     <see cref="ContentDiff" /> refuses to call a change to it additive.
    /// </remarks>
    public bool ShapeIsKnown => !string.IsNullOrEmpty(Schema);

    /// <summary>Whether this names anything.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Address);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Address} ({Kind}) {Hash:x16}");
}

/// <summary>What happened to one address between two catalogs.</summary>
public enum ContentChange : byte {
    /// <summary>It is new. Nothing live refers to it, because nothing could.</summary>
    Added = 0,

    /// <summary>Its content changed and its shape did not.</summary>
    Modified = 1,

    /// <summary>Its shape changed. Anything holding one of these now holds the wrong thing.</summary>
    Reshaped = 2,

    /// <summary>It is gone.</summary>
    Removed = 3
}

/// <summary>One address's change, and whether a running fleet can take it.</summary>
/// <param name="Address">Which address.</param>
/// <param name="Change">What happened.</param>
/// <param name="Kind">What sort of thing it is.</param>
/// <param name="Additive">Whether this change alone could be applied to a running realm.</param>
/// <param name="Reason">Why not, when it is not. Empty when it is.</param>
public readonly record struct ContentDelta(
    string Address,
    ContentChange Change,
    string Kind,
    bool Additive,
    string Reason
) {
    /// <inheritdoc />
    public override string ToString() =>
        Additive
            ? string.Create(CultureInfo.InvariantCulture, $"{Change} {Address}")
            : string.Create(CultureInfo.InvariantCulture, $"{Change} {Address}: {Reason}");
}

/// <summary>
///     Compares two catalogs and says whether the difference can be applied to a running fleet.
/// </summary>
/// <remarks>
///     <para>
///         Doc 27 § Upgrades: <i>"'Additive' is proven by the build, not asserted by a human."</i>
///         That sentence is this class. The gate is <c>vixen live upgrade --content</c> refusing to
///         apply a non-additive diff live, <b>with the reason</b>, rather than applying it and
///         finding out.
///     </para>
///     <para>
///         ⚠ <b>The classifier is deliberately pessimistic, and the asymmetry is the whole safety
///         argument.</b> Calling a non-additive change additive means a live reload that corrupts a
///         running world; calling an additive change non-additive means a drain nobody needed. The
///         first is unrecoverable and the second costs an evening, so every case this cannot decide
///         is <em>not</em> additive.
///     </para>
///     <para>
///         ⚠ <b>A removal is never additive, even of something nothing is using.</b> Whether an
///         address is in use is a question about every entity in every world in the fleet, and this
///         compares two files. A classifier that guessed would be guessing about the one case that
///         deletes a player's sword.
///     </para>
/// </remarks>
public static class ContentDiff {
    /// <summary>Kinds whose content a running realm can reload.</summary>
    /// <remarks>
    ///     ⚠ <b>The list is short because the list is the risk.</b> A definition table is data a
    ///     realm reads through <c>IDefinitionRegistry</c> and can be told to read again; a prefab is
    ///     baked into entities that already exist, and a scene is the map a realm is currently
    ///     simulating. Adding a kind here is a decision to let it change under a running world, and
    ///     it should be made with the same care as a schema change.
    /// </remarks>
    public static ImmutableArray<string> LiveReloadable { get; } = ["definition", "table", "text", "localisation"];

    /// <summary>Compares two catalogs.</summary>
    /// <param name="before">What the fleet is running.</param>
    /// <param name="after">What was published.</param>
    /// <returns>Every address that changed, with its verdict.</returns>
    /// <exception cref="ArgumentNullException">Either catalog is null.</exception>
    public static ImmutableArray<ContentDelta> Compare(
        IReadOnlyCollection<ContentEntry> before,
        IReadOnlyCollection<ContentEntry> after
    ) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var old = ToMap(before);
        var current = ToMap(after);
        var deltas = ImmutableArray.CreateBuilder<ContentDelta>();

        foreach (var (address, entry) in current) {
            if (!old.TryGetValue(address, out var previous)) {
                // Nothing live can refer to an address that did not exist, so a new one is the one
                // change that is additive without qualification. This is doc 28's entire premise.
                deltas.Add(new(address, ContentChange.Added, entry.Kind, true, ""));

                continue;
            }

            if (previous.Hash == entry.Hash && string.Equals(previous.Schema, entry.Schema, StringComparison.Ordinal)) {
                continue;
            }

            deltas.Add(Classify(previous, entry));
        }

        foreach (var (address, entry) in old) {
            if (!current.ContainsKey(address)) {
                deltas.Add(
                    new(
                        address,
                        ContentChange.Removed,
                        entry.Kind,
                        false,
                        "a removed address may still be referenced by a live entity, and this compares files rather than worlds"
                    )
                );
            }
        }

        return [.. deltas.OrderBy(delta => delta.Address, StringComparer.Ordinal)];
    }

    /// <summary>Whether a whole diff can be applied to a running fleet.</summary>
    /// <param name="deltas">What <see cref="Compare" /> produced.</param>
    /// <returns>Whether every change in it is additive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deltas" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>One non-additive change makes the whole update non-additive.</b> There is no partial
    ///     apply: a catalog is one <c>BuildHash</c> and a realm is on it or it is not, so applying
    ///     the additive half would leave the fleet on a content version that never existed.
    /// </remarks>
    public static bool IsAdditive(IReadOnlyCollection<ContentDelta> deltas) {
        ArgumentNullException.ThrowIfNull(deltas);

        return deltas.All(delta => delta.Additive);
    }

    /// <summary>Why an update cannot be applied live, as sentences for an operator.</summary>
    /// <param name="deltas">What <see cref="Compare" /> produced.</param>
    /// <returns>One line per blocking change, empty when there are none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deltas" /> is null.</exception>
    /// <remarks>
    ///     Doc 27 puts <i>"with the reason"</i> in the sentence describing the gate, and it is the
    ///     part that gets left out: a tool that says "this needs a drain" and not "because
    ///     <c>items/greatsword</c> changed shape" makes the operator diff two catalogs by hand at
    ///     three in the morning.
    /// </remarks>
    public static ImmutableArray<string> Blockers(IReadOnlyCollection<ContentDelta> deltas) {
        ArgumentNullException.ThrowIfNull(deltas);

        return [.. deltas.Where(delta => !delta.Additive).Select(delta => delta.ToString())];
    }

    static ContentDelta Classify(ContentEntry previous, ContentEntry entry) {
        if (!string.Equals(previous.Kind, entry.Kind, StringComparison.Ordinal)) {
            // One address that was a prefab and is now a scene is not a modification, it is two
            // different things wearing one name — and every reference to it now means something else.
            return new(
                entry.Address,
                ContentChange.Reshaped,
                entry.Kind,
                false,
                $"it was a `{previous.Kind}` and is now a `{entry.Kind}`"
            );
        }

        if (!string.Equals(previous.Schema, entry.Schema, StringComparison.Ordinal)) {
            return new(
                entry.Address,
                ContentChange.Reshaped,
                entry.Kind,
                false,
                "its shape changed, so anything already holding one of these holds the wrong thing"
            );
        }

        // ⚠ An unknown shape is not an unchanged one. Without a schema there is no way to tell a
        // rebalance from a definition that gained a field, and the second is exactly the change that
        // corrupts a running world — so the pessimistic answer is the only safe one.
        if (!entry.ShapeIsKnown || !previous.ShapeIsKnown) {
            return new(
                entry.Address,
                ContentChange.Modified,
                entry.Kind,
                false,
                "its shape is not recorded, so a rebalance cannot be told from a change of layout"
            );
        }

        var reloadable = LiveReloadable.Contains(entry.Kind, StringComparer.OrdinalIgnoreCase);

        return new(
            entry.Address,
            ContentChange.Modified,
            entry.Kind,
            reloadable,
            reloadable ? "" : $"a `{entry.Kind}` is baked into things that already exist and cannot be reloaded under them"
        );
    }

    static Dictionary<string, ContentEntry> ToMap(IReadOnlyCollection<ContentEntry> entries) {
        var map = new Dictionary<string, ContentEntry>(entries.Count, StringComparer.Ordinal);

        foreach (var entry in entries) {
            if (entry.IsValid) {
                map[entry.Address] = entry;
            }
        }

        return map;
    }
}
