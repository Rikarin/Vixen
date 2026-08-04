// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>Every definition a build knows, and the tag table baked out of them.</summary>
/// <remarks>
///     <para>
///         Immutable. A content update produces a <em>new</em> catalog and
///         <see cref="DefinitionRegistry.Reload" /> swaps it in, which is what makes a reload safe
///         while a frame is running: a reader that took the old one keeps reading a consistent old
///         one rather than watching a dictionary mutate underneath it.
///     </para>
///     <para>
///         The tag table is here rather than beside it because the two are one artefact — a
///         definition's tags are indices into <em>this</em> table, and a catalog paired with the wrong
///         table is a set of rules about the wrong tags. <see cref="BuildHash" /> covers both.
///     </para>
/// </remarks>
public sealed class DefinitionCatalog {
    readonly Dictionary<uint, Definition> byId;

    internal DefinitionCatalog(Dictionary<uint, Definition> byId, GameplayTagTable tags, uint buildHash) {
        this.byId = byId;
        Tags = tags;
        BuildHash = buildHash;
    }

    /// <summary>A catalog with nothing in it. What a host that has loaded no content has.</summary>
    public static DefinitionCatalog Empty { get; } = new DefinitionCatalogBuilder().Build();

    /// <summary>The tag table every tag in every definition here is numbered against.</summary>
    public GameplayTagTable Tags { get; }

    /// <summary>How many definitions it holds.</summary>
    public int Count => byId.Count;

    /// <summary>
    ///     A hash over every address and the tag table, so two hosts can establish they agree before
    ///     either dispatches anything that carries a <see cref="DefId" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Addresses and tags, not values.</b> Two builds whose sword does 251 power and 252
    ///     power have the same hash, and that is deliberate: what has to match for the wire to be
    ///     safe is the <em>vocabulary</em>, and making a balance change a handshake failure would make
    ///     every balance change a fleet-wide drain. Whether the two ends hold identical bytes is the
    ///     content catalog's question ([08](../../../docs/plan/08-asset-pipeline-and-addressables.md)),
    ///     asked once, over everything.
    /// </remarks>
    public uint BuildHash { get; }

    /// <summary>Every definition, in address order.</summary>
    public IEnumerable<Definition> All => byId.Values.OrderBy(definition => definition.Address, StringComparer.Ordinal);

    /// <summary>Whether the catalog has a definition with this id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it does.</returns>
    public bool Contains(DefId id) => byId.ContainsKey(id.Value);

    /// <summary>Finds a definition and checks it is the kind the caller expected.</summary>
    /// <typeparam name="TDefinition">The kind.</typeparam>
    /// <param name="id">The id.</param>
    /// <param name="definition">The definition, or null.</param>
    /// <returns>Whether it is there and is that kind.</returns>
    public bool TryGet<TDefinition>(DefId id, out TDefinition? definition) where TDefinition : Definition {
        if (byId.TryGetValue(id.Value, out var found) && found is TDefinition typed) {
            definition = typed;

            return true;
        }

        definition = null;

        return false;
    }

    /// <summary>Finds a definition, whatever kind it is.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The definition, or null.</returns>
    public Definition? Find(DefId id) => byId.GetValueOrDefault(id.Value);

    /// <summary>Every definition of one kind.</summary>
    /// <typeparam name="TDefinition">The kind.</typeparam>
    /// <returns>Them, in address order.</returns>
    /// <remarks>
    ///     An <c>is</c> test over the whole catalog rather than a per-type index. Callers are
    ///     start-up code — an editor's balance table, a vendor filling its stock, a test — and a
    ///     per-type index would be a second structure to keep correct across a reload for a question
    ///     nothing asks in a frame.
    /// </remarks>
    public IEnumerable<TDefinition> OfType<TDefinition>() where TDefinition : Definition =>
        All.OfType<TDefinition>();
}

/// <summary>Composes a <see cref="DefinitionCatalog" /> out of what the content build imported.</summary>
/// <remarks>
///     Where the two failures that cannot be detected later are detected: an address registered twice,
///     and two addresses whose <see cref="DefId" /> collides. Both produce a game in which one piece
///     of content silently becomes another.
/// </remarks>
public sealed class DefinitionCatalogBuilder {
    readonly Dictionary<uint, Definition> byId = [];
    readonly GameplayTagTableBuilder tags = new();
    readonly List<string> declared = [];

    /// <summary>How many definitions have been added.</summary>
    public int Count => byId.Count;

    /// <summary>Adds a definition at an address, stamping its address and id onto it.</summary>
    /// <param name="address">Where the content build found it — <c>items/flamebrand</c>.</param>
    /// <param name="definition">The definition as it was authored.</param>
    /// <returns>The builder, so declarations chain.</returns>
    /// <exception cref="ArgumentException">The address is empty.</exception>
    /// <exception cref="InvalidOperationException">The address is already taken, or its id collides with another address'.</exception>
    public DefinitionCatalogBuilder Add(string address, Definition definition) {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(definition);

        var id = DefId.From(address);

        if (byId.TryGetValue(id.Value, out var existing)) {
            throw new InvalidOperationException(
                string.Equals(existing.Address, address, StringComparison.Ordinal)
                    ? $"'{address}' is in the catalog twice."
                    : $"'{address}' and '{existing.Address}' hash to the same DefId. Rename one — two "
                    + "addresses the wire cannot tell apart are two pieces of content that are the "
                    + "same piece of content to every peer."
            );
        }

        byId.Add(id.Value, definition with { Address = address, Id = id });
        definition.CollectTags(declared);

        foreach (var tag in declared) {
            tags.Add(tag);
        }

        declared.Clear();

        return this;
    }

    /// <summary>Adds a tag that no definition mentions but a game's code does.</summary>
    /// <param name="name">The dotted name.</param>
    /// <returns>The builder, so declarations chain.</returns>
    /// <remarks>
    ///     The escape hatch for a tag granted by code rather than by content — <c>State.InCombat</c>,
    ///     which nothing authors and everything asks about. Without it such a tag is absent from the
    ///     table, so every rule mentioning it resolves to an empty range and quietly matches nothing.
    /// </remarks>
    public DefinitionCatalogBuilder AddTag(string name) {
        tags.Add(name);

        return this;
    }

    /// <summary>Bakes the tag table and produces the catalog.</summary>
    /// <returns>The catalog.</returns>
    public DefinitionCatalog Build() {
        var table = tags.Build();
        var hash = table.BuildHash;

        // Address order, so the hash is a property of the content rather than of the order an
        // importer happened to walk a directory in.
        foreach (var definition in byId.Values.OrderBy(entry => entry.Address, StringComparer.Ordinal)) {
            foreach (var character in definition.Address) {
                hash ^= character;
                hash *= 16777619u;
            }

            hash ^= '\n';
            hash *= 16777619u;
        }

        return new(new(byId), table, hash);
    }
}
