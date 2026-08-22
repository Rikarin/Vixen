// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Ui.Styling;

/// <summary>Everything the cascade decided about one element, frozen.</summary>
/// <remarks>
///     <para>
///         Immutable, interned, and compared by reference. The three go together: because it never
///         changes it is safe to share, because it is shared two elements that look alike hold the
///         same object, and because of that <c>ReferenceEquals</c> is a complete answer to "did this
///         element's style change" — one pointer comparison instead of walking a property table.
///     </para>
///     <para>
///         [Doc 09](../../docs/plan/09-ui-framework.md) makes that the load-bearing property of the
///         whole styling system: layout marks itself dirty only when the reference changed
///         <i>and</i> a layout-affecting property differs, and a <c>DataGrid</c> with ten thousand
///         identical cells computes one of these. It is the reason browsers can render large tables
///         and the reason Vixen can.
///     </para>
///     <para>
///         Properties are kept sorted by id in parallel arrays. Lookup is a binary search over
///         integers with no indirection, and — the part that matters more — equality is a memcmp of
///         two <see cref="int" /> spans, which is what makes interning cheap enough to do on every
///         element of every restyle.
///     </para>
/// </remarks>
public sealed class ComputedStyle : IEquatable<ComputedStyle> {
    /// <summary>The style of an element no rule matched and which inherits nothing.</summary>
    public static readonly ComputedStyle Empty = new([], [], null);

    readonly int[] properties;
    readonly int[] values;
    readonly int hash;

    ComputedStyle(int[] properties, int[] values, ComputedStyle? inheritedFrom) {
        this.properties = properties;
        this.values = values;
        Parent = inheritedFrom;

        var accumulated = new HashCode();
        accumulated.AddBytes(MemoryMarshal.AsBytes(properties.AsSpan()));
        accumulated.AddBytes(MemoryMarshal.AsBytes(values.AsSpan()));
        hash = accumulated.ToHashCode();
    }

    /// <summary>The style this one inherited from, if any.</summary>
    /// <remarks>
    ///     Kept for diagnostics and for the sharing key, not for lookup: inherited values are
    ///     resolved <i>into</i> this style when it is built, so reading a property never walks up.
    ///     A grid ten levels deep would otherwise pay ten dictionary probes for every text colour.
    /// </remarks>
    public ComputedStyle? Parent { get; }

    /// <summary>How many properties it holds.</summary>
    public int Count => properties.Length;

    /// <summary>The property ids it holds, ascending.</summary>
    public ReadOnlySpan<int> Properties => properties;

    /// <summary>The value ids it holds, in the same order.</summary>
    public ReadOnlySpan<int> Values => values;

    /// <summary>Looks a property up.</summary>
    /// <param name="property">The interned property name.</param>
    /// <param name="value">Receives the interned value.</param>
    /// <returns>Whether the property is set.</returns>
    public bool TryGet(int property, out int value) {
        var index = properties.AsSpan().BinarySearch(property);
        if (index < 0) {
            value = NameTable.None;
            return false;
        }

        value = values[index];
        return true;
    }

    /// <summary>Builds a style from property/value pairs, which need not be sorted.</summary>
    /// <param name="pairs">The pairs.</param>
    /// <param name="inheritedFrom">The parent style, for the sharing key.</param>
    /// <returns>The style.</returns>
    /// <remarks>
    ///     Not interned — <see cref="ComputedStyleCache" /> does that. Kept apart so that a style can
    ///     be built and compared without being retained, which is what the sharing oracle needs.
    /// </remarks>
    public static ComputedStyle Create(List<KeyValuePair<int, int>> pairs, ComputedStyle? inheritedFrom = null) {
        ArgumentNullException.ThrowIfNull(pairs);

        // Sorted in place. The caller's list is scratch that the resolver reuses across elements,
        // and a `pairs.OrderBy(…)` here would allocate two arrays per element of every restyle for
        // the privilege of not touching it.
        pairs.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        var properties = new int[pairs.Count];
        var values = new int[pairs.Count];

        for (var i = 0; i < pairs.Count; i++) {
            properties[i] = pairs[i].Key;
            values[i] = pairs[i].Value;
        }

        return new ComputedStyle(properties, values, inheritedFrom);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Value equality over the property table, which is what <see cref="ComputedStyleCache" />
    ///     needs to decide two elements can share. The <see cref="Parent" /> is deliberately
    ///     <i>not</i> compared: inherited values have already been resolved into the table, so two
    ///     styles with identical tables are interchangeable however they got there.
    /// </remarks>
    public bool Equals(ComputedStyle? other) {
        if (ReferenceEquals(this, other)) {
            return true;
        }

        return other is not null
            && hash == other.hash
            && properties.AsSpan().SequenceEqual(other.properties)
            && values.AsSpan().SequenceEqual(other.values);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ComputedStyle);

    /// <inheritdoc />
    public override int GetHashCode() => hash;

    /// <inheritdoc />
    public override string ToString() => $"{properties.Length} properties";
}

/// <summary>Makes equal computed styles be the same object.</summary>
/// <remarks>
///     <para>
///         A dictionary keyed by the style itself. Interning is what turns "these two elements
///         resolved to the same thing" from a claim into a pointer, and it is the precondition for
///         every cheap thing downstream: reference-compared invalidation, a sharing cache that can
///         key on a parent style, and a layout pass that can skip a subtree by comparing one word.
///     </para>
///     <para>
///         Nothing is ever evicted. The set of distinct computed styles in a UI is bounded by the
///         stylesheet rather than by the element count — that is the entire premise — so growth is
///         bounded by the thing that would have to be reloaded anyway. <see cref="Clear" /> is what
///         a stylesheet reload calls.
///     </para>
/// </remarks>
public sealed class ComputedStyleCache {
    readonly Dictionary<ComputedStyle, ComputedStyle> interned = [];

    /// <summary>How many distinct styles exist.</summary>
    public int Count => interned.Count;

    /// <summary>How many times interning found an existing style.</summary>
    /// <remarks>
    ///     The measurement doc 09's claim rests on. Ten thousand identical cells should produce ten
    ///     thousand hits and one entry, and if that ever stops being true this is where it shows.
    /// </remarks>
    public int Hits { get; private set; }

    /// <summary>Returns the canonical instance equal to <paramref name="style" />.</summary>
    /// <param name="style">A freshly built style.</param>
    /// <returns>The canonical instance, which may be a previously built one.</returns>
    public ComputedStyle Intern(ComputedStyle style) {
        ArgumentNullException.ThrowIfNull(style);

        if (interned.TryGetValue(style, out var existing)) {
            Hits++;
            return existing;
        }

        interned[style] = style;
        return style;
    }

    /// <summary>Forgets everything, as a stylesheet reload must.</summary>
    public void Clear() {
        interned.Clear();
        Hits = 0;
    }
}

/// <summary>What makes two elements able to share one <see cref="ComputedStyle" />.</summary>
/// <param name="Parent">The parent <i>element</i>. See the remarks — this is not what doc 09 says.</param>
/// <param name="Tag">The element's interned tag name.</param>
/// <param name="Id">Its interned id, or <see cref="NameTable.None" />.</param>
/// <param name="ClassHash">An order-independent hash of its class list.</param>
/// <param name="ClassCount">How many classes it has, so a hash collision needs a second coincidence.</param>
/// <param name="State">Its pseudo state.</param>
/// <param name="Inline">A handle on its inline declarations.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The parent is the parent element, not the parent's computed style.</b>
///         [Doc 09](../../docs/plan/09-ui-framework.md) specifies the key as
///         <c>(tag, class set, inline style, parent computed style, pseudo state)</c> and that is not
///         sound. Two parents can hold the <i>same</i> computed style and still be told apart by a
///         selector: given <c>.a { color: red }</c> and <c>.b { color: red }</c>, an <c>.a</c> and a
///         <c>.b</c> intern to one identical style — and then <c>.a .row { color: blue }</c> matches
///         one of their children and not the other's. Keyed on the parent style, both children share,
///         and one of them is wrong.
///     </para>
///     <para>
///         Keyed on the parent <i>element</i>, sharing happens between siblings, and every
///         descendant and child combinator is sound for free because the two elements have literally
///         the same ancestor chain. This is what Gecko does and for this reason.
///     </para>
///     <para>
///         It is a narrower key, and the thing worth being clear about is what that does and does
///         not cost. <b>Interning</b> is what gives every identical grid cell the same
///         <see cref="ComputedStyle" /> reference, and it is unaffected: ten thousand cells across a
///         thousand rows still hold one object, so the reference-compared invalidation doc 09 is
///         really after still works. <b>Sharing</b> is what lets the cascade be <i>skipped</i>, and
///         that now happens per row rather than per grid. Cheaper than being wrong.
///     </para>
///     <para>
///         What the key still cannot carry is where an element sits among its siblings, or what its
///         attributes are — which is what <see cref="StyleRuleSet.SharingIsSound" /> is for.
///     </para>
///     <para>
///         ⚠ <b>The media scope is in the key even though the parent very nearly makes it
///         redundant, and the exception is exactly the case per-surface media exists for.</b> Two
///         elements with the same parent are in the same surface by construction, so for every
///         ordinary element the scope adds nothing. Surface <i>roots</i> are the elements that
///         break it: <c>UiDocument.CreateSurface</c> hangs them all off one owner, and two torn-off
///         windows are then the same tag with no id and no classes under the same parent — the
///         perfect sharing key, for two elements whose whole difference is which window's
///         breakpoints they answer. One integer, and the case it covers is the headline feature.
///     </para>
/// </remarks>
/// <param name="Scope">Which surface's <c>@media</c> answers it reads.</param>
/// <param name="Container">
///     Which container chain's <c>@container</c> answers it reads.
///     <para>
///         ⚠ <b>Not redundant with the parent, and the case is the ordinary one rather than an edge
///         case.</b> Two siblings are in the same container by construction — but a scope is interned
///         on the chain's <i>boxes</i> (see <see cref="ContainerScopes" />), so two rows of a list
///         that happen to be the same width get the same id and share, and the moment one of them
///         holds a container that has resized they do not. Leaving it out would let a row keep the
///         style it computed at the other width.
///     </para>
/// </param>
public readonly record struct StyleSharingKey(
    StyleNodeId Parent,
    int Tag,
    int Id,
    int ClassHash,
    int ClassCount,
    ElementState State,
    InlineStyleId? Inline,
    int Scope,
    int Container
);
