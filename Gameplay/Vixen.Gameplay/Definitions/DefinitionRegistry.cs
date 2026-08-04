// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>Where a rule resolves a <see cref="DefId" />, and what a live content update swaps.</summary>
/// <remarks>
///     An interface rather than the class, because the realm, the editor's preview and a test each
///     hand systems a different one, and because a game that wants its own resolution — an overlay,
///     a per-shard override, a recording proxy — should not have to subclass the shipped one.
/// </remarks>
public interface IDefinitionRegistry {
    /// <summary>The definitions in force right now.</summary>
    /// <remarks>
    ///     <b>Take it once and use it for the whole of a piece of work.</b> A reload replaces this
    ///     reference between reads, so code that resolves five ids through the property rather than
    ///     through one local can see two catalogs inside one ability — which is the class of bug a
    ///     live reload exists to not have.
    /// </remarks>
    DefinitionCatalog Catalog { get; }

    /// <summary>The tag table those definitions are numbered against.</summary>
    GameplayTagTable Tags { get; }

    /// <summary>Finds a definition and checks it is the kind the caller expected.</summary>
    /// <typeparam name="TDefinition">The kind.</typeparam>
    /// <param name="id">The id.</param>
    /// <param name="definition">The definition, or null.</param>
    /// <returns>Whether it is there and is that kind.</returns>
    bool TryGet<TDefinition>(DefId id, out TDefinition? definition) where TDefinition : Definition;

    /// <summary>Finds a definition, and refuses to carry on without it.</summary>
    /// <typeparam name="TDefinition">The kind.</typeparam>
    /// <param name="id">The id.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="DefinitionNotFoundException">There is no such definition, or it is another kind.</exception>
    TDefinition Get<TDefinition>(DefId id) where TDefinition : Definition;
}

/// <summary>A definition an id named and the catalog does not have.</summary>
/// <remarks>
///     Its own type rather than <see cref="KeyNotFoundException" /> because it is nearly always one of
///     two specific things — content that was not built, or a peer running a different build — and a
///     handler that wants to answer differently for those needs to be able to catch it apart from
///     everything else a dictionary throws.
/// </remarks>
public sealed class DefinitionNotFoundException : Exception {
    /// <summary>Makes one.</summary>
    public DefinitionNotFoundException() : base("No such definition.") { }

    /// <summary>Makes one.</summary>
    /// <param name="message">What went wrong.</param>
    public DefinitionNotFoundException(string message) : base(message) { }

    /// <summary>Makes one.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public DefinitionNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>The shipped registry: one catalog, swappable while the frame runs.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28's step 6 — "realms reload their definition registry live; no restart, no
///         drain".</b> A catalog is immutable, so a reload is a reference assignment and a reader
///         either sees all of the old one or all of the new one. There is no lock on the read path.
///     </para>
///     <para>
///         ⚠ <b>Not every content change can be applied live, and the two that cannot are checked
///         here.</b> This is where doc 28's walk turned out to be optimistic:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>A new tag renumbers the table</b>, and every <see cref="GameplayTag" /> already
///                 sitting in a component, an effect or a packet in flight is an index into the old
///                 numbering. Adding an item that uses existing tags is live; adding an item that
///                 introduces a tag is a rolling update
///                 ([27](../../../docs/plan/27-mmo-framework.md) § Upgrades' <em>build</em> kind).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>A removed address is never additive.</b> Doc 28 says so and this is where it is
///                 enforced: a stack in a bank naming a definition the catalog no longer has is
///                 unresolvable, and there is no reading of that which is not data loss. Deprecate,
///                 drain, then delete.
///             </description>
///         </item>
///     </list>
///     <para>
///         Everything else — a new address, a changed value, a retuned loot table — applies.
///     </para>
/// </remarks>
public sealed class DefinitionRegistry : IDefinitionRegistry {
    volatile DefinitionCatalog catalog;

    /// <summary>Makes a registry over a catalog.</summary>
    /// <param name="catalog">The definitions in force, or null for none.</param>
    public DefinitionRegistry(DefinitionCatalog? catalog = null) => this.catalog = catalog ?? DefinitionCatalog.Empty;

    /// <inheritdoc />
    public DefinitionCatalog Catalog => catalog;

    /// <inheritdoc />
    public GameplayTagTable Tags => catalog.Tags;

    /// <summary>How many times a reload has been applied. What a diagnostic reports and a test asserts.</summary>
    public int Generation { get; private set; }

    /// <inheritdoc />
    public bool TryGet<TDefinition>(DefId id, out TDefinition? definition) where TDefinition : Definition =>
        catalog.TryGet(id, out definition);

    /// <inheritdoc />
    public TDefinition Get<TDefinition>(DefId id) where TDefinition : Definition {
        var current = catalog;

        if (current.TryGet<TDefinition>(id, out var definition)) {
            return definition!;
        }

        var found = current.Find(id);

        throw new DefinitionNotFoundException(
            found is null
                ? $"{id} is in nothing this build loaded. Either the content was not built, or the peer "
                + "that sent it is running a different one."
                : $"{id} is '{found.Address}', which is a {found.TypeName} and not a {typeof(TDefinition).Name}."
        );
    }

    /// <summary>Swaps in a new catalog, if the change is one that can be applied live.</summary>
    /// <param name="next">The catalog the content update produced.</param>
    /// <exception cref="InvalidOperationException">The change is not additive. The message says which rule it broke.</exception>
    public void Reload(DefinitionCatalog next) {
        if (!TryReload(next, out var reason)) {
            throw new InvalidOperationException(reason);
        }
    }

    /// <summary>Swaps in a new catalog, and says why not when it will not.</summary>
    /// <param name="next">The catalog the content update produced.</param>
    /// <param name="reason">Why it was refused, or the empty string.</param>
    /// <returns>Whether it was applied.</returns>
    public bool TryReload(DefinitionCatalog next, out string reason) {
        ArgumentNullException.ThrowIfNull(next);

        var current = catalog;

        if (ReferenceEquals(current, next)) {
            reason = string.Empty;

            return true;
        }

        if (current.Tags.BuildHash != next.Tags.BuildHash && current.Tags.Count > 0) {
            reason =
                "The tag table changed, so every tag index already in a component or in flight would "
                + "mean something else. A content update that adds or removes a tag is a rolling "
                + "build update, not a live reload — see docs/plan/27 § Upgrades.";

            return false;
        }

        foreach (var definition in current.All) {
            if (!next.Contains(definition.Id)) {
                reason =
                    $"'{definition.Address}' is in the running catalog and not in the new one. Removing "
                    + "content is never additive: deprecate, drain, then delete.";

                return false;
            }
        }

        catalog = next;
        Generation++;
        reason = string.Empty;

        return true;
    }
}
