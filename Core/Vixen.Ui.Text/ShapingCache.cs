// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>Shaped paragraphs, kept until something has to go.</summary>
/// <remarks>
///     <para>
///         Shaping is the expensive part of drawing text and almost all of it is repeated: a label
///         that has not changed is reshaped every frame unless something remembers. This is that,
///         with least-recently-used eviction so a long session cannot grow without bound.
///     </para>
///     <para>
///         <b>The size is not part of the key, and that is the point.</b> The font is held at
///         design-unit scale and HarfBuzz's OpenType path has no hinting, so a string shapes
///         identically at 12pt and at 48pt — the caller scales the result. A cache keyed on size
///         would hold one entry per string per DPI scale per animation frame of a growing label, and
///         would miss on the frame that mattered. See <see cref="FontFace" />.
///     </para>
///     <para>
///         ⚠ <b>Whole paragraphs, not runs</b>, and the reason is a consequence of a decision made
///         two files away. A run is shaped with the text around it as context, because an Arabic
///         letter's form depends on whether its neighbour joins — so a run's glyphs are not a
///         function of the run alone. Keying on runs would either be unsound or need the context in
///         the key, at which point it is a paragraph cache with extra steps. Reuse across paragraphs
///         that share a word is given up deliberately.
///     </para>
///     <para>
///         Not thread-safe, and neither is a <see cref="FontFace" />: parallel layout gets a cache
///         and a face per worker.
///     </para>
/// </remarks>
public sealed class ShapingCache {
    readonly int capacity;
    readonly Dictionary<Key, LinkedListNode<Entry>> entries;
    readonly LinkedList<Entry> order = new();

    /// <summary>Creates a cache.</summary>
    /// <param name="capacity">How many shaped paragraphs to keep.</param>
    /// <remarks>
    ///     Counted in paragraphs rather than in bytes, which is the cruder measure and the honest
    ///     one — the cost of an entry is its glyph array, and a paragraph of Kannada is not a
    ///     paragraph of Latin. If this ever needs to be a memory budget it should be measured
    ///     first rather than estimated.
    /// </remarks>
    public ShapingCache(int capacity = 512) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        this.capacity = capacity;
        entries = new Dictionary<Key, LinkedListNode<Entry>>(capacity);
    }

    /// <summary>How many paragraphs are held.</summary>
    public int Count => entries.Count;

    /// <summary>How many lookups were answered from the cache.</summary>
    public long Hits { get; private set; }

    /// <summary>How many lookups had to shape.</summary>
    public long Misses { get; private set; }

    /// <summary>Shapes a paragraph, or returns the one shaped earlier.</summary>
    /// <param name="font">The font to shape with.</param>
    /// <param name="text">The paragraph.</param>
    /// <param name="direction">Its base direction.</param>
    /// <returns>
    ///     The shaped paragraph. Shared with every other caller that asked for the same thing, so it
    ///     must be treated as immutable.
    /// </returns>
    public ShapedText Shape(FontFace font, string text, ParagraphDirection direction = ParagraphDirection.Auto) {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        var key = new Key(font, text, direction);

        if (entries.TryGetValue(key, out var node)) {
            Hits++;
            order.Remove(node);
            order.AddFirst(node);
            return node.Value.Shaped;
        }

        Misses++;
        var shaped = TextShaper.Shape(font, text, direction);

        // Evict before inserting, so the cache never holds capacity + 1 even momentarily.
        if (entries.Count >= capacity) {
            var oldest = order.Last!;
            order.RemoveLast();
            entries.Remove(oldest.Value.Key);
        }

        entries[key] = order.AddFirst(new Entry(key, shaped));
        return shaped;
    }

    /// <summary>Forgets everything.</summary>
    /// <remarks>
    ///     Call this when a face is disposed. An entry outliving its <see cref="FontFace" /> holds a
    ///     glyph array whose ids mean nothing, which is worse than a miss because it is silent.
    /// </remarks>
    public void Clear() {
        entries.Clear();
        order.Clear();
    }

    readonly record struct Key(FontFace Font, string Text, ParagraphDirection Direction) {
        public bool Equals(Key other) =>
            ReferenceEquals(Font, other.Font)
            && Direction == other.Direction
            && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override int GetHashCode() =>
            HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Font),
                StringComparer.Ordinal.GetHashCode(Text),
                Direction
            );
    }

    sealed record Entry(Key Key, ShapedText Shaped);
}
