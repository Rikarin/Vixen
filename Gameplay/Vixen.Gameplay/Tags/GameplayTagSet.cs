// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;

namespace Vixen.Gameplay;

/// <summary>The tags something has right now, counted by how many sources granted each.</summary>
/// <remarks>
///     <para>
///         <b>Counted, and that is the entire reason this is a type rather than a
///         <c>HashSet&lt;GameplayTag&gt;</c>.</b> Two effects grant <c>State.Stunned</c>; one expires;
///         the target must still be stunned. A plain set loses that, and the bug it produces — a
///         crowd-control break that depends on which of two stuns landed first — is the kind that
///         reproduces once a week in a raid and never on a developer's machine. Every grant is
///         balanced by exactly one revoke, and a tag is present while its count is above zero.
///     </para>
///     <para>
///         <b>Sorted by index, so a prefix query is a binary search.</b> The pre-order numbering means
///         everything beneath <c>Damage.Fire</c> is contiguous, so
///         <see cref="ContainsAny(GameplayTagRange)" /> looks for the first index at or after the
///         range's start and asks whether it is still inside it — one search, no scan, no allocation.
///     </para>
/// </remarks>
public sealed class GameplayTagSet : IReadOnlyCollection<GameplayTag> {
    readonly List<Entry> entries = [];

    /// <summary>How many distinct tags are present.</summary>
    public int Count => entries.Count;

    /// <summary>Grants a tag, or increments the count of one already granted.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether this grant is what made it present.</returns>
    public bool Add(GameplayTag tag) {
        if (!tag.IsSome) {
            return false;
        }

        var slot = Search(tag.Index);

        if (slot >= 0) {
            entries[slot] = entries[slot] with { Count = entries[slot].Count + 1 };

            return false;
        }

        entries.Insert(~slot, new(tag.Index, 1));

        return true;
    }

    /// <summary>Grants several tags.</summary>
    /// <param name="tags">The tags.</param>
    public void AddRange(ReadOnlySpan<GameplayTag> tags) {
        foreach (var tag in tags) {
            Add(tag);
        }
    }

    /// <summary>Revokes one grant of a tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether that was the last grant, so the tag is now absent.</returns>
    /// <remarks>
    ///     Revoking a tag nobody granted is a no-op rather than an error: the calling shape is "this
    ///     effect ended, undo what it did", and an effect that was blocked by an immunity granted
    ///     nothing.
    /// </remarks>
    public bool Remove(GameplayTag tag) {
        var slot = tag.IsSome ? Search(tag.Index) : -1;

        if (slot < 0) {
            return false;
        }

        var count = entries[slot].Count - 1;

        if (count > 0) {
            entries[slot] = entries[slot] with { Count = count };

            return false;
        }

        entries.RemoveAt(slot);

        return true;
    }

    /// <summary>Revokes one grant of each of several tags.</summary>
    /// <param name="tags">The tags.</param>
    public void RemoveRange(ReadOnlySpan<GameplayTag> tags) {
        foreach (var tag in tags) {
            Remove(tag);
        }
    }

    /// <summary>Forgets every tag and every count.</summary>
    public void Clear() => entries.Clear();

    /// <summary>Whether a tag is present, exactly.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it is present.</returns>
    /// <remarks>
    ///     ⚠ <b>Exact, not hierarchical.</b> A target with <c>Damage.Fire.Burn</c> does not
    ///     <c>Contains(Damage.Fire)</c> — that question is <see cref="ContainsAny(GameplayTagRange)" />,
    ///     and keeping the two apart is what stops "has the stun" from quietly meaning "has anything
    ///     under control effects".
    /// </remarks>
    public bool Contains(GameplayTag tag) => tag.IsSome && Search(tag.Index) >= 0;

    /// <summary>How many sources granted a tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The count, or zero.</returns>
    public int CountOf(GameplayTag tag) {
        var slot = tag.IsSome ? Search(tag.Index) : -1;

        return slot < 0 ? 0 : entries[slot].Count;
    }

    /// <summary>Whether anything in the set falls under a prefix.</summary>
    /// <param name="range">The resolved prefix.</param>
    /// <returns>Whether anything matches.</returns>
    public bool ContainsAny(GameplayTagRange range) {
        if (!range.IsSome) {
            return false;
        }

        var slot = Search(range.Start);

        if (slot >= 0) {
            return true;
        }

        slot = ~slot;

        return slot < entries.Count && entries[slot].Index < range.End;
    }

    /// <summary>Whether anything in the set falls under any of several prefixes.</summary>
    /// <param name="ranges">The resolved prefixes.</param>
    /// <returns>Whether anything matches.</returns>
    public bool ContainsAny(ReadOnlySpan<GameplayTagRange> ranges) {
        foreach (var range in ranges) {
            if (ContainsAny(range)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether every one of several prefixes is matched by something in the set.</summary>
    /// <param name="ranges">The resolved prefixes.</param>
    /// <returns>Whether they all match. An empty list is vacuously true.</returns>
    public bool ContainsAll(ReadOnlySpan<GameplayTagRange> ranges) {
        foreach (var range in ranges) {
            if (!ContainsAny(range)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Walks the present tags in index order, which is the pre-order walk of the tag tree.</summary>
    /// <returns>The enumerator.</returns>
    public Enumerator GetEnumerator() => new(entries);

    IEnumerator<GameplayTag> IEnumerable<GameplayTag>.GetEnumerator() {
        foreach (var entry in entries) {
            yield return new(entry.Index);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<GameplayTag>)this).GetEnumerator();

    /// <summary>The index of a tag, or the bitwise complement of where it would go.</summary>
    int Search(uint index) {
        var low = 0;
        var high = entries.Count - 1;

        while (low <= high) {
            var middle = low + ((high - low) >> 1);
            var candidate = entries[middle].Index;

            if (candidate == index) {
                return middle;
            }

            if (candidate < index) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return ~low;
    }

    internal readonly record struct Entry(uint Index, int Count);

    /// <summary>Walks a set without allocating.</summary>
    /// <remarks>
    ///     A struct enumerator, because a tag set is read on the damage path and <c>foreach</c> over
    ///     an <see cref="IEnumerable{T}" /> would box one per hit.
    /// </remarks>
    public struct Enumerator {
        readonly List<Entry> entries;
        int position;

        internal Enumerator(List<Entry> entries) {
            this.entries = entries;
            position = -1;
        }

        /// <summary>The tag under the cursor.</summary>
        public readonly GameplayTag Current => new(entries[position].Index);

        /// <summary>Advances.</summary>
        /// <returns>Whether there is another tag.</returns>
        public bool MoveNext() => ++position < entries.Count;
    }
}
