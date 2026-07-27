// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Collections;

namespace Vixen.Ecs;

/// <summary>
///     Which entities a query is about, as three sets of component types and an optional change
///     filter.
/// </summary>
/// <remarks>
///     <para>
///         Matching is three <see cref="BitSet" /> tests against an archetype's mask — a superset
///         test, an intersection test and its negation — so the cost of deciding whether ten
///         thousand entities are relevant is proportional to the number of archetypes, not the
///         number of entities. The result is cached per world in a <see cref="Query" />.
///     </para>
///     <para>
///         A description is built once and reused. Building one per frame works and allocates three
///         bit sets and a list for nothing.
///     </para>
/// </remarks>
public sealed partial class QueryDescription {
    readonly List<ComponentTypeId> changed = [];

    internal BitSet All { get; } = new(64);

    internal BitSet Any { get; } = new(64);

    internal BitSet None { get; } = new(64);

    /// <summary>The component types whose change versions the filter looks at.</summary>
    public IReadOnlyList<ComponentTypeId> ChangedComponents => changed;

    /// <summary>Whether any change filter is set at all.</summary>
    public bool HasChangeFilter => changed.Count > 0;

    /// <summary>Requires every one of these components.</summary>
    /// <param name="componentTypes">The component type ids.</param>
    /// <returns>This description, for chaining.</returns>
    public QueryDescription RequireAll(ReadOnlySpan<ComponentTypeId> componentTypes) {
        foreach (var id in componentTypes) {
            All.Set(id.Value);
        }

        return this;
    }

    /// <summary>Requires at least one of these components. An empty set means "no such requirement".</summary>
    /// <param name="componentTypes">The component type ids.</param>
    /// <returns>This description, for chaining.</returns>
    public QueryDescription RequireAny(ReadOnlySpan<ComponentTypeId> componentTypes) {
        foreach (var id in componentTypes) {
            Any.Set(id.Value);
        }

        return this;
    }

    /// <summary>Excludes entities that have any of these components.</summary>
    /// <param name="componentTypes">The component type ids.</param>
    /// <returns>This description, for chaining.</returns>
    public QueryDescription Exclude(ReadOnlySpan<ComponentTypeId> componentTypes) {
        foreach (var id in componentTypes) {
            None.Set(id.Value);
        }

        return this;
    }

    /// <summary>
    ///     Requires these components, and narrows iteration to chunks in which at least one of them
    ///     has been written since the version the caller passes.
    /// </summary>
    /// <param name="componentTypes">The component type ids.</param>
    /// <returns>This description, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         The filter is "any of them", not "all of them": a transform system wants to run when
    ///         either the local transform or the parent moved, and a system that genuinely needs the
    ///         conjunction can ask for two queries and intersect, which nothing has yet wanted to do.
    ///     </para>
    ///     <para>
    ///         It also requires them, because filtering on a change to a component the entity does
    ///         not have has no meaning, and leaving that to the caller to remember produces a query
    ///         that silently matches everything.
    ///     </para>
    ///     <para>
    ///         The granularity is the chunk, not the entity. A chunk holding a few hundred entities
    ///         of which one moved is iterated whole. That is the trade every archetype ECS makes
    ///         here and it is the right one: the alternative is a per-entity dirty bit, which costs
    ///         a branch in the inner loop of every system to save work in the systems that skip.
    ///     </para>
    /// </remarks>
    public QueryDescription RequireChanged(ReadOnlySpan<ComponentTypeId> componentTypes) {
        foreach (var id in componentTypes) {
            All.Set(id.Value);

            if (!changed.Contains(id)) {
                changed.Add(id);
            }
        }

        return this;
    }

    /// <summary>Whether an archetype's component set satisfies the description.</summary>
    /// <param name="archetype">The archetype.</param>
    /// <returns>Whether its entities are in the query.</returns>
    public bool Matches(Archetype archetype) {
        ArgumentNullException.ThrowIfNull(archetype);

        return archetype.Mask.Contains(All)
            && (Any.IsEmpty() || archetype.Mask.Intersects(Any))
            && !archetype.Mask.Intersects(None);
    }

    /// <summary>Whether a chunk has been written since a version, per the change filter.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="since">The version the caller last saw.</param>
    /// <returns>Whether the chunk is worth iterating.</returns>
    public bool MatchesChange(Chunk chunk, uint since) {
        ArgumentNullException.ThrowIfNull(chunk);

        if (changed.Count == 0) {
            return true;
        }

        foreach (var id in changed) {
            var column = chunk.Archetype.ColumnOf(id);

            // A tag has no column and so no change version. It cannot change — it is either there
            // or it is not, and that is a structural change the archetype already expresses.
            if (column >= 0 && chunk.VersionOf(column) > since) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Renders the three sets, which is what a diagnostic wants.</summary>
    /// <returns>The description in text.</returns>
    public override string ToString() {
        var parts = new List<string>();

        Describe(parts, "all", All);
        Describe(parts, "any", Any);
        Describe(parts, "none", None);

        if (changed.Count > 0) {
            parts.Add($"changed({string.Join(", ", changed.Select(id => ComponentRegistry.Get(id).Type.Name))})");
        }

        return parts.Count == 0 ? "everything" : string.Join(" ", parts);
    }

    static void Describe(List<string> parts, string label, BitSet set) {
        if (set.IsEmpty()) {
            return;
        }

        var names = new List<string>();

        foreach (var bit in set) {
            names.Add(ComponentRegistry.Get(new(bit)).Type.Name);
        }

        parts.Add($"{label}({string.Join(", ", names)})");
    }
}
