// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Vixen.Core;

/// <summary>An interned name: four bytes that compare as fast as an integer, because they are one.</summary>
/// <param name="Id">The hash. Zero is the empty symbol and matches nothing.</param>
/// <remarks>
///     <para>
///         <b>A hash of the name rather than an index into a table, and the reason is determinism.</b>
///         An index assigned in first-seen order depends on what a process happened to load first, so
///         two machines running the same build would assign different numbers to the same word — and
///         a selection that breaks a tie on a number would then pick differently on each of them.
///         [16](../../../docs/plan/16-networking.md) makes that a desync rather than a curiosity. A
///         hash is the same everywhere, for ever, with no table to ship and no order to agree on.
///     </para>
///     <para>
///         ⚠ <b>32 bits collide, and the bake is where that is caught.</b> Two words in one
///         vocabulary hashing alike would silently make them the same word. Whatever composes a
///         vocabulary checks the whole of it for that and refuses — <c>MoveSet.Compose</c> for a
///         movement vocabulary, <c>BlackboardLayoutBuilder.Build</c> for a blackboard's keys — which
///         turns a one-in-fifty-thousand mystery into a build error naming both words. The
///         alternative — 64 bits — doubles the size of every facet, key and world state to avoid a
///         case the build can simply detect.
///     </para>
///     <para>
///         <b>Names are kept for diagnostics only.</b> <see cref="ToString" /> resolves through a
///         process-wide table filled as symbols are created, so a debugger and an error message can
///         say <c>gait</c> rather than <c>0x4f9a2b1c</c>. Nothing in a frame reads it, and a symbol
///         that arrived from a deserialised set with no name still compares correctly.
///     </para>
///     <para>
///         ⚠ <b>It lives here rather than beside its first caller.</b> Move sets needed an interned
///         name first, so this type spent its first release in <c>Vixen.Animation.Moves</c>; a
///         blackboard key, a gameplay-relevant tag and a GOAP world key each want exactly the same
///         four bytes, and two interned-name types in one engine is the duplication this repository
///         avoids everywhere else. docs/plan/37 § The rows this touches is where that was decided.
///     </para>
/// </remarks>
public readonly record struct Symbol(uint Id) {
    static readonly ConcurrentDictionary<uint, string> Names = new();
    static readonly ConcurrentDictionary<uint, string> Collisions = new();

    /// <summary>The symbol that is not a word. Matches nothing, including itself in a facet.</summary>
    public static Symbol None => default;

    /// <summary>Whether this is a real word.</summary>
    public bool IsSome => Id != 0;

    /// <summary>Interns a name.</summary>
    /// <param name="name">The word.</param>
    /// <returns>Its symbol, or <see cref="None" /> for an empty name.</returns>
    /// <remarks>
    ///     ⚠ <b>Case- and culture-insensitive would be wrong here.</b> A facet vocabulary is authored
    ///     content, and content that means two different things depending on how somebody typed it is
    ///     a bug the build should surface rather than paper over. The editor's vocabulary list is
    ///     where a project keeps the spellings honest.
    /// </remarks>
    public static Symbol Intern(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return None;
        }

        // FNV-1a, 32-bit. Chosen for being short, well known and trivially reimplementable in
        // whatever a pipeline is written in — an importer in another language has to be able to
        // produce the same numbers, which rules out anything that depends on a runtime's own
        // string hashing (randomised per process since .NET Core, which would be the same desync
        // this type exists to avoid).
        var hash = 2166136261u;

        foreach (var character in name) {
            hash ^= character;
            hash *= 16777619u;
        }

        // Zero is reserved for None, and a name that hashes to it would otherwise be indistinguishable
        // from absence. One is as good a substitute as any and the collision check will find it if it
        // ever matters.
        if (hash == 0) {
            hash = 1;
        }

        // ⚠ Recorded here and not left to the caller, because the name table alone cannot tell a
        // collision from a repeat: `TryAdd` fails identically for "already interned" and "a
        // different word hashed the same", so anything checking afterwards sees one name for both
        // words and concludes they agree. This is where the two spellings are still both in hand.
        if (!Names.TryAdd(hash, name)
            && Names.TryGetValue(hash, out var seen)
            && !string.Equals(seen, name, StringComparison.Ordinal)) {
            Collisions[hash] = name;
        }

        return new(hash);
    }

    /// <summary>Whether two different words this process has interned share this symbol.</summary>
    /// <param name="first">The spelling that got there first.</param>
    /// <param name="second">The one that collided with it.</param>
    /// <returns>Whether there is a collision.</returns>
    /// <remarks>
    ///     <b>What a vocabulary's builder asks before it accepts one.</b> A collision only matters
    ///     where the two words can meet, so the check belongs to whatever composes a vocabulary
    ///     rather than to interning — which happens in static initialisers all over an assembly and
    ///     is no place to throw from.
    /// </remarks>
    public bool TryGetCollision(out string first, out string second) {
        if (Collisions.TryGetValue(Id, out var other) && Names.TryGetValue(Id, out var original)) {
            first = original;
            second = other;

            return true;
        }

        first = string.Empty;
        second = string.Empty;

        return false;
    }

    /// <summary>The name this was interned from, if this process has seen it.</summary>
    /// <returns>The name, or the id in hexadecimal.</returns>
    public override string ToString() =>
        Id == 0 ? "<none>" : Names.TryGetValue(Id, out var name) ? name : $"0x{Id:x8}";
}
