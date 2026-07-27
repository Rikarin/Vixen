// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling;

/// <summary>An element's index in the style store.</summary>
/// <param name="Index">The slot.</param>
public readonly record struct StyleNodeId(int Index) {
    /// <summary>The id no element has.</summary>
    public static readonly StyleNodeId Invalid = new(-1);

    /// <summary>Whether this refers to a slot at all.</summary>
    public bool IsValid => Index >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        IsValid ? "element " + Index.ToString(CultureInfo.InvariantCulture) : "no element";
}

/// <summary>Everything about an element that a selector can ask about.</summary>
/// <remarks>
///     <para>
///         Not the element tree — that is <c>Vixen.Ui</c>'s, and it does not exist yet. This is the
///         same arrangement <c>Vixen.Ui.Layout</c> uses: the subsystem owns a dense store keyed by an
///         <see cref="int" />, and the element that arrives later holds one id per store. It is what
///         lets the matcher be built and judged on its own, which
///         [doc 02](../../docs/plan/02-repository-layout.md) gives as the reason these projects are
///         split this finely in the first place.
///     </para>
///     <para>
///         Plain managed arrays rather than <c>NativeArray</c>, unlike the layout store, and the
///         reason is the shape of the data rather than taste: a class list is variable-length, the
///         matcher's inner loop is a dictionary probe rather than a linear sweep, and doc 09 puts the
///         element count at 10⁴ rather than the layout store's 10⁵. The struct-of-arrays discipline
///         is worth its complexity where the loops are, and this is not one of those places.
///     </para>
/// </remarks>
public sealed class StyleTree {
    const int NoParent = -1;

    readonly NameTable names;
    readonly List<int> classArena = [];
    readonly List<int> childArena = [];
    readonly List<AttributeEntry> attributeArena = [];

    int[] tags = new int[64];
    int[] identifiers = new int[64];
    ElementState[] states = new ElementState[64];
    ClassRange[] classes = new ClassRange[64];
    AttributeRange[] attributes = new AttributeRange[64];
    ElementLinks[] links = new ElementLinks[64];
    AncestorBloom[] blooms = new AncestorBloom[64];
    int count;

    /// <summary>Creates a store.</summary>
    /// <param name="names">The table tag, id, class and attribute names are interned in.</param>
    public StyleTree(NameTable names) {
        ArgumentNullException.ThrowIfNull(names);
        this.names = names;
    }

    /// <summary>The table this store's names live in.</summary>
    public NameTable Names => names;

    /// <summary>How many elements there are.</summary>
    public int Count => count;

    /// <summary>Adds an element.</summary>
    /// <param name="tag">Its tag name.</param>
    /// <param name="parent">Its parent, or <see langword="null" /> for a root.</param>
    /// <param name="id">Its id attribute, if it has one.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The new element's id.</returns>
    /// <remarks>
    ///     The parent is nullable rather than defaulting to <see cref="StyleNodeId.Invalid" />,
    ///     which would read better and be a trap: <c>default(StyleNodeId)</c> is index <i>zero</i>,
    ///     a perfectly valid element, so every root created without an explicit parent would have
    ///     silently become a child of the first element ever made. That is not hypothetical — it is
    ///     what this signature did until four matching tests disagreed with each other about a tree
    ///     nobody had built.
    /// </remarks>
    public StyleNodeId CreateElement(
        string tag,
        StyleNodeId? parent = null,
        string? id = null,
        params ReadOnlySpan<string> classNames
    ) {
        ArgumentNullException.ThrowIfNull(tag);

        if (count == tags.Length) {
            Grow();
        }

        var index = count++;
        tags[index] = names.Intern(tag);
        identifiers[index] = id is null ? NameTable.None : names.Intern(id);
        states[index] = ElementState.None;
        attributes[index] = default;

        var classStart = classArena.Count;
        foreach (var className in classNames) {
            classArena.Add(names.Intern(className));
        }

        classes[index] = new ClassRange(classStart, classNames.Length);

        var parentIndex = parent is { IsValid: true } owner ? owner.Index : NoParent;
        links[index] = new ElementLinks {
            Parent = parentIndex,
            ChildOffset = -1,
            IndexInParent = parentIndex >= 0 ? links[parentIndex].ChildCount : 0
        };

        if (parentIndex >= 0) {
            AppendChild(parentIndex, index);
        }

        blooms[index] = BuildBloom(index);
        return new StyleNodeId(index);
    }

    /// <summary>Sets an attribute.</summary>
    /// <param name="element">The element.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">Its value.</param>
    /// <remarks>
    ///     Attributes are appended and never removed, and the range is re-pointed when one is added,
    ///     which leaks the old range. That is deliberate for now: attribute selectors are rare, the
    ///     arena is compacted when the tree is rebuilt, and a free list here would be complexity
    ///     bought for a case no measurement has produced.
    /// </remarks>
    public void SetAttribute(StyleNodeId element, string name, string value) {
        var index = Validate(element);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        var nameId = names.Intern(name);
        var valueId = names.Intern(value);
        var range = attributes[index];

        for (var i = 0; i < range.Count; i++) {
            if (attributeArena[range.Start + i].Name != nameId) {
                continue;
            }

            attributeArena[range.Start + i] = new AttributeEntry(nameId, valueId);
            return;
        }

        var start = attributeArena.Count;
        for (var i = 0; i < range.Count; i++) {
            attributeArena.Add(attributeArena[range.Start + i]);
        }

        attributeArena.Add(new AttributeEntry(nameId, valueId));
        attributes[index] = new AttributeRange(start, range.Count + 1);
    }

    /// <summary>Sets the transient state a selector can ask about.</summary>
    /// <param name="element">The element.</param>
    /// <param name="state">The new state.</param>
    public void SetState(StyleNodeId element, ElementState state) => states[Validate(element)] = state;

    /// <summary>The transient state of an element.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its state.</returns>
    public ElementState GetState(StyleNodeId element) => states[Validate(element)];

    /// <summary>An element's parent, or <see cref="StyleNodeId.Invalid" />.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The parent.</returns>
    public StyleNodeId GetParent(StyleNodeId element) => new(links[Validate(element)].Parent);

    /// <summary>How many children an element has.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The count.</returns>
    public int GetChildCount(StyleNodeId element) => links[Validate(element)].ChildCount;

    /// <summary>One child of an element.</summary>
    /// <param name="element">The element.</param>
    /// <param name="index">Which child.</param>
    /// <returns>The child.</returns>
    public StyleNodeId GetChild(StyleNodeId element, int index) {
        var parent = Validate(element);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, links[parent].ChildCount);
        return new StyleNodeId(childArena[links[parent].ChildOffset + index]);
    }

    internal int Validate(StyleNodeId element) {
        if ((uint) element.Index >= (uint) count) {
            throw new ArgumentOutOfRangeException(nameof(element), $"{element} is not in this tree.");
        }

        return element.Index;
    }

    internal int TagOf(int index) => tags[index];

    internal int IdOf(int index) => identifiers[index];

    internal ElementState StateOf(int index) => states[index];

    internal int ParentOf(int index) => links[index].Parent;

    internal int IndexInParentOf(int index) => links[index].IndexInParent;

    internal int SiblingCountOf(int index) {
        var parent = links[index].Parent;
        return parent < 0 ? 1 : links[parent].ChildCount;
    }

    internal int PreviousSiblingOf(int index) {
        var parent = links[index].Parent;
        var position = links[index].IndexInParent;
        return parent < 0 || position == 0 ? NoParent : childArena[links[parent].ChildOffset + position - 1];
    }

    internal AncestorBloom BloomOf(int index) => blooms[index];

    internal bool HasClass(int index, int classId) {
        var range = classes[index];
        for (var i = 0; i < range.Count; i++) {
            if (classArena[range.Start + i] == classId) {
                return true;
            }
        }

        return false;
    }

    internal bool TryGetAttribute(int index, int nameId, out int valueId) {
        var range = attributes[index];
        for (var i = 0; i < range.Count; i++) {
            var entry = attributeArena[range.Start + i];
            if (entry.Name != nameId) {
                continue;
            }

            valueId = entry.Value;
            return true;
        }

        valueId = NameTable.None;
        return false;
    }

    internal void ForEachIdentifier(int index, Action<int> visit) {
        visit(tags[index]);
        if (identifiers[index] != NameTable.None) {
            visit(identifiers[index]);
        }

        var range = classes[index];
        for (var i = 0; i < range.Count; i++) {
            visit(classArena[range.Start + i]);
        }
    }

    AncestorBloom BuildBloom(int index) {
        var parent = links[index].Parent;
        if (parent < 0) {
            return default;
        }

        // The element's own identifiers are *not* in its bloom — the bloom answers "could an
        // ancestor have been this", and a descendant combinator never matches the element itself.
        var bloom = blooms[parent];
        bloom.Add(tags[parent]);

        if (identifiers[parent] != NameTable.None) {
            bloom.Add(identifiers[parent]);
        }

        var range = classes[parent];
        for (var i = 0; i < range.Count; i++) {
            bloom.Add(classArena[range.Start + i]);
        }

        return bloom;
    }

    void AppendChild(int parent, int child) {
        ref var parentLinks = ref links[parent];
        if (parentLinks.ChildOffset < 0) {
            parentLinks.ChildOffset = childArena.Count;
            parentLinks.ChildCapacity = 0;
        }

        if (parentLinks.ChildOffset + parentLinks.ChildCount != childArena.Count) {
            // Another element has grown into the space after this one's run, so the run moves to the
            // end. Elements are built parent-then-children in practice, which is why this is the
            // uncommon path rather than the usual one.
            var moved = childArena.Count;
            for (var i = 0; i < parentLinks.ChildCount; i++) {
                childArena.Add(childArena[parentLinks.ChildOffset + i]);
            }

            parentLinks.ChildOffset = moved;
        }

        childArena.Add(child);
        parentLinks.ChildCount++;
    }

    void Grow() {
        var next = tags.Length * 2;
        Array.Resize(ref tags, next);
        Array.Resize(ref identifiers, next);
        Array.Resize(ref states, next);
        Array.Resize(ref classes, next);
        Array.Resize(ref attributes, next);
        Array.Resize(ref links, next);
        Array.Resize(ref blooms, next);
    }

    readonly record struct ClassRange(int Start, int Count);

    readonly record struct AttributeRange(int Start, int Count);

    readonly record struct AttributeEntry(int Name, int Value);

    struct ElementLinks {
        public int Parent;
        public int ChildOffset;
        public int ChildCount;
        public int ChildCapacity;
        public int IndexInParent;
    }
}
