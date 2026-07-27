// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ecs;

/// <summary>
///     A <see cref="QueryDescription" /> bound to a world, with the archetypes it matched remembered.
/// </summary>
/// <remarks>
///     The mask tests happen once per description per world and are redone only when the world grows
///     a new archetype — which happens when an entity first reaches a component set nobody has used
///     before, and then never again. Everything after that is a walk of a list.
/// </remarks>
public sealed class Query {
    readonly List<Archetype> matched = [];

    int structuralVersionSeen = -1;

    /// <summary>The world the archetypes belong to.</summary>
    public World World { get; }

    /// <summary>What the query asks for.</summary>
    public QueryDescription Description { get; }

    internal Query(World world, QueryDescription description) {
        World = world;
        Description = description;
    }

    /// <summary>The archetypes whose entities are in the query.</summary>
    public IReadOnlyList<Archetype> Archetypes {
        get {
            Refresh();
            return matched;
        }
    }

    /// <summary>How many entities the query matches, ignoring any change filter.</summary>
    public int EntityCount {
        get {
            Refresh();
            var total = 0;

            foreach (var archetype in matched) {
                total += archetype.EntityCount;
            }

            return total;
        }
    }

    /// <summary>The chunks to iterate.</summary>
    /// <param name="since">
    ///     The version the caller last processed. With a change filter, only chunks written
    ///     <em>after</em> it are handed back. Zero — the default — precedes every write, so it means
    ///     "everything".
    /// </param>
    /// <returns>Something to <c>foreach</c> over.</returns>
    /// <remarks>
    ///     Strictly after, not at-or-after, and the difference is the whole contract: a system
    ///     remembers <see cref="Ecs.World.Version" /> when it finishes, the scheduler advances the
    ///     version at the sync point, and the next run sees writes made since — but never its own
    ///     from last time, which at-or-after would hand back for ever.
    /// </remarks>
    public ChunkSequence Chunks(uint since = 0) {
        Refresh();
        return new(matched, Description, since);
    }

    void Refresh() {
        if (structuralVersionSeen == World.StructuralVersion) {
            return;
        }

        // Rebuilt rather than appended to. Appending would be faster and would rely on archetypes
        // only ever being added, which is true today and is exactly the kind of assumption that
        // stops being true in a world that can unload a scene.
        matched.Clear();

        foreach (var archetype in World.Archetypes) {
            if (Description.Matches(archetype)) {
                matched.Add(archetype);
            }
        }

        structuralVersionSeen = World.StructuralVersion;
    }

    /// <summary>Renders the description and how many entities it currently matches.</summary>
    /// <returns>The query in text.</returns>
    public override string ToString() => $"{Description} → {EntityCount} entities";
}

/// <summary>The chunks a query matched, ready to be iterated.</summary>
/// <remarks>
///     A struct with a struct enumerator, so <c>foreach</c> over it allocates nothing and the JIT can
///     see through the whole loop.
/// </remarks>
public readonly struct ChunkSequence {
    readonly List<Archetype> archetypes;
    readonly QueryDescription description;
    readonly uint since;

    internal ChunkSequence(List<Archetype> archetypes, QueryDescription description, uint since) {
        this.archetypes = archetypes;
        this.description = description;
        this.since = since;
    }

    /// <summary>Starts iterating.</summary>
    /// <returns>The enumerator.</returns>
    public Enumerator GetEnumerator() => new(archetypes, description, since);

    /// <summary>Walks the matched archetypes' chunks, skipping the empty ones and the unchanged ones.</summary>
    public struct Enumerator {
        readonly List<Archetype> archetypes;
        readonly QueryDescription description;
        readonly uint since;
        readonly bool filtered;

        int archetypeIndex;
        int chunkIndex = -1;

        internal Enumerator(List<Archetype> archetypes, QueryDescription description, uint since) {
            this.archetypes = archetypes;
            this.description = description;
            this.since = since;
            filtered = description.HasChangeFilter;
            Current = null!;
        }

        /// <summary>The chunk being iterated.</summary>
        public Chunk Current { get; private set; }

        /// <summary>Moves to the next non-empty chunk the filter admits.</summary>
        /// <returns>Whether there was one.</returns>
        public bool MoveNext() {
            while (archetypeIndex < archetypes.Count) {
                var chunks = archetypes[archetypeIndex].Chunks;

                while (++chunkIndex < chunks.Count) {
                    var chunk = chunks[chunkIndex];

                    // An archetype keeps one chunk after its last entity leaves, so an empty chunk
                    // is normal rather than a sign of anything, and a system that saw one would have
                    // to guard every span access against a zero length.
                    if (chunk.Count > 0 && (!filtered || description.MatchesChange(chunk, since))) {
                        Current = chunk;
                        return true;
                    }
                }

                archetypeIndex++;
                chunkIndex = -1;
            }

            Current = null!;
            return false;
        }
    }
}
