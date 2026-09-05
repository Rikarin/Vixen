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
    const int NoInline = -1;

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

    /// <summary>Whether each element draws text of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>The only thing in this store that is not a name, a state or a link</b>, and it is here
    ///     for exactly one selector. CSS's <c>:empty</c> means "no child <i>nodes</i>", and in the DOM
    ///     text is a node — so a paragraph with words in it is not empty. Vixen puts text on the
    ///     element instead of in a node of its own (<c>UiElement.Text</c>), so a store that only
    ///     counted children would call every label empty, and <c>:empty</c> would hide precisely the
    ///     elements it was written to keep. A bit is the whole of what the tree needs to know:
    ///     <i>which</i> text is <c>Vixen.Ui</c>'s business and no selector can ask.
    /// </remarks>
    bool[] hasText = new bool[64];

    /// <summary>Each element's inline block, or -1 for none.</summary>
    /// <remarks>
    ///     ⚠ <b>A raw index rather than an <see cref="InlineStyleId" />?</b>, so that "none" is a
    ///     sentinel in the same array rather than a nullable struct per element. Ten thousand
    ///     elements, nearly all of which have no inline style, would otherwise pay eight bytes each
    ///     to say so.
    /// </remarks>
    int[] inlines = new int[64];

    /// <summary>Which surface's <c>@media</c> answers each element reads.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Inherited from the parent at creation, and that is the whole of the propagation
    ///         story.</b> A document's surfaces share one rule set, so a rule's conditional group is
    ///         answered per <i>scope</i> rather than per rule set — and the scope an element is in is
    ///         the scope of the surface it is shown in. Every element under a surface root is created
    ///         after it, and <c>UiDocument.Reparent</c> rebuilds a moved subtree's slots in pre-order
    ///         under its new parent rather than moving them, so a panel dragged into another window
    ///         picks that window's breakpoints up without anything walking the subtree to tell it.
    ///     </para>
    ///     <para>
    ///         <see cref="MediaScopes.Document" /> for a root, which is the answer for every element
    ///         of a document that has never opened a second window.
    ///     </para>
    /// </remarks>
    int[] scopes = new int[64];

    /// <summary>Which container chain each element's <c>@container</c> answers are about.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two arrays and not one, because a query container is not inside itself.</b> CSS
    ///         Containment 3 § 5.1 scopes a container query to the elements <i>within</i> the
    ///         container, so an element with <c>container-type: inline-size</c> still answers
    ///         <c>@container</c> against whatever is above <i>it</i>. This array is what an element
    ///         asks; <see cref="providedContainers" /> is what it offers its children, and for
    ///         everything that is not a container the two are equal.
    ///     </para>
    ///     <para>
    ///         Collapsing them to one array is the obvious saving and it is wrong in the direction
    ///         that hides: a container answering its own query matches slightly too often rather than
    ///         never, so every test written against the common case passes.
    ///     </para>
    ///     <para>
    ///         Inherited at creation exactly as <see cref="scopes" /> is, and
    ///         <see cref="ContainerScopes.Root" /> for a root — which is every element of a document
    ///         that has no <c>container-type</c> anywhere in it.
    ///     </para>
    /// </remarks>
    int[] containers = new int[64];
    int[] providedContainers = new int[64];
    bool[] alive = new bool[64];
    int count;
    int dead;

    /// <summary>Creates a store.</summary>
    /// <param name="names">The table tag, id, class and attribute names are interned in.</param>
    public StyleTree(NameTable names) {
        ArgumentNullException.ThrowIfNull(names);
        this.names = names;
    }

    /// <summary>The table this store's names live in.</summary>
    public NameTable Names => names;

    /// <summary>How many slots have ever been used, live and removed alike.</summary>
    /// <remarks>
    ///     The bound for anything walking the store by index, which is why it counts removed slots
    ///     too. <see cref="LiveCount" /> is how many elements there actually are.
    /// </remarks>
    public int Count => count;

    /// <summary>How many elements are still in the tree.</summary>
    public int LiveCount => count - dead;

    /// <summary>How many slots removal has left behind.</summary>
    /// <remarks>
    ///     ⚠ <b>Exposed because nothing reclaims it on its own.</b> A removed slot is tombstoned and
    ///     never reused, so a document that builds and tears down a list every frame grows without
    ///     bound until something calls <see cref="Compact" />. This is the number that decides when.
    ///     See <see cref="Remove" /> for why reuse is not the obvious fix it looks like.
    /// </remarks>
    public int DeadCount => dead;

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
        alive[index] = true;
        tags[index] = names.Intern(tag);
        identifiers[index] = id is null ? NameTable.None : names.Intern(id);
        states[index] = ElementState.None;
        attributes[index] = default;
        inlines[index] = NoInline;
        hasText[index] = false;

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

        // ⚠ From the parent rather than from a default, and before the element is handed back. This
        // is the only propagation `@media` scoping has: a subtree is only ever built downwards, so
        // inheriting here means a window's contents are in the window's scope by construction. See
        // the remarks on `scopes`.
        scopes[index] = parentIndex >= 0 ? scopes[parentIndex] : MediaScopes.Document;

        // ⚠ The *provided* scope of the parent, not its own. A child of a container is inside that
        // container; the container is not. See the remarks on `containers`.
        containers[index] = parentIndex >= 0 ? providedContainers[parentIndex] : ContainerScopes.Root;
        providedContainers[index] = containers[index];

        if (parentIndex >= 0) {
            AppendChild(parentIndex, index);
        }

        blooms[index] = BuildBloom(index);
        return new StyleNodeId(index);
    }

    /// <summary>Whether an element is still in the tree.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Whether it has not been removed.</returns>
    public bool IsAlive(StyleNodeId element) =>
        (uint) element.Index < (uint) count && alive[element.Index];

    /// <summary>Removes an element and everything under it.</summary>
    /// <param name="element">The element.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Tombstoned, never reused</b>, and that is the whole design decision. The obvious
    ///         implementation is a free list handing removed slots back out, and it would quietly
    ///         break three separate things that all rest on one unwritten invariant: <i>a parent's
    ///         index is lower than its children's</i>. <c>StyleUpdater.ResolveAll</c> walks slots in
    ///         ascending order because that is parents-before-children and inheritance needs it;
    ///         its incremental pass uses the index as a priority for the same reason; and
    ///         <see cref="AddToDescendantBlooms" /> sweeps forward from an element and gives up the
    ///         moment a climb passes below the ancestor's index. Fill a hole with a new child of a
    ///         later parent and the first two resolve a child before its parent, and the third
    ///         answers "not a descendant" about something that is — a descendant selector that
    ///         silently stops matching.
    ///     </para>
    ///     <para>
    ///         So slots leak until something calls <see cref="Compact" />, and <see cref="DeadCount" />
    ///         says by how much. Compaction rather than reuse, because rebuilding the arrays without
    ///         the dead slots <i>preserves relative order</i> — so all three invariants survive it,
    ///         where reuse is exactly what does not.
    ///     </para>
    ///     <para>
    ///         The subtree goes with it. A child of a removed element is not an orphan to be
    ///         reparented or a root to be left standing — it is gone, and leaving it in the store
    ///         would leave it matching selectors and being resolved every pass.
    ///     </para>
    /// </remarks>
    public void Remove(StyleNodeId element) {
        var index = Validate(element);
        if (!alive[index]) {
            throw new InvalidOperationException($"{element} has already been removed.");
        }

        Detach(index);
        Kill(index);
    }

    /// <summary>Rebuilds the store without the slots removal left behind.</summary>
    /// <param name="remap">
    ///     Filled with each old slot's new index, or <c>-1</c> where the slot was dead. Must be at
    ///     least <see cref="Count" /> long.
    /// </param>
    /// <returns>How many slots the store now has, which is what <see cref="LiveCount" /> was.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every <see cref="StyleNodeId" /> outside this store becomes wrong</b>, which is
    ///         why this hands back the mapping rather than quietly doing the right thing. A slot is an
    ///         index; moving one moves everything that names it — <c>StyleUpdater</c>'s resolved
    ///         styles, the <c>Animator</c>'s running transitions, and whatever element tree holds the
    ///         id. None of those can be reached from here, and a compaction that did not say so would
    ///         be a silent corruption rather than a leak.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Relative order is preserved, and that is the entire reason this exists rather
    ///         than a free list.</b> Live slots come out in the order they went in, so a parent's
    ///         index stays below its children's — the unwritten invariant three separate passes rest
    ///         on, spelled out in <see cref="Remove" />. Handing a hole back out is what breaks it.
    ///     </para>
    ///     <para>
    ///         The three arenas are rebuilt too, so this also reclaims the class runs of removed
    ///         elements and the child runs <see cref="AppendChild" /> abandoned when it relocated one.
    ///         Rebuilt into fresh lists rather than swept in place: a child run can sit <i>later</i> in
    ///         the arena than a run belonging to a lower-numbered element, precisely because
    ///         <see cref="AppendChild" /> moves a run to the end when something grows into the space
    ///         behind it, so an in-place forward sweep would overwrite a run it had not read yet.
    ///     </para>
    /// </remarks>
    public int Compact(Span<int> remap) {
        if (remap.Length < count) {
            throw new ArgumentException(
                $"The remap needs room for {count} slots and has room for {remap.Length}.",
                nameof(remap)
            );
        }

        var live = 0;

        for (var i = 0; i < count; i++) {
            remap[i] = alive[i] ? live++ : -1;
        }

        if (live == count) {
            // Nothing was removed, so nothing moves. The mapping is still filled in, because a caller
            // that has to remap only sometimes is a caller that will get it wrong.
            return count;
        }

        var newClasses = new List<int>(classArena.Count);
        var newAttributes = new List<AttributeEntry>(attributeArena.Count);
        var newChildren = new List<int>(childArena.Count);

        for (var i = 0; i < count; i++) {
            if (!alive[i]) {
                continue;
            }

            // ⚠ Read into locals before writing, because the destination is the *same* array. It is
            // never ahead of the source — a live slot's new index is at most its old one — so an
            // in-place move is safe in that direction and only in that direction.
            var to = remap[i];
            var classRange = classes[i];
            var attributeRange = attributes[i];
            var link = links[i];

            tags[to] = tags[i];
            identifiers[to] = identifiers[i];
            states[to] = states[i];
            blooms[to] = blooms[i];
            inlines[to] = inlines[i];
            scopes[to] = scopes[i];
            containers[to] = containers[i];
            providedContainers[to] = providedContainers[i];
            hasText[to] = hasText[i];
            alive[to] = true;

            var classStart = newClasses.Count;

            for (var c = 0; c < classRange.Count; c++) {
                newClasses.Add(classArena[classRange.Start + c]);
            }

            classes[to] = new ClassRange(classStart, classRange.Count);

            var attributeStart = newAttributes.Count;

            for (var a = 0; a < attributeRange.Count; a++) {
                newAttributes.Add(attributeArena[attributeRange.Start + a]);
            }

            attributes[to] = new AttributeRange(attributeStart, attributeRange.Count);

            var childStart = newChildren.Count;

            for (var c = 0; c < link.ChildCount; c++) {
                // ⚠ Every child of a live element is live. `Remove` detaches the element it was given
                // from its parent's run, and `Kill` empties the run of everything below it — so a run
                // holding a dead index would mean one of those two had missed a case, and the
                // exception says which rather than writing a -1 into the tree.
                var child = remap[childArena[link.ChildOffset + c]];

                if (child < 0) {
                    throw new InvalidOperationException(
                        $"Element {i} lists a removed child, so removal left the tree inconsistent."
                    );
                }

                newChildren.Add(child);
            }

            links[to] = new ElementLinks {
                Parent = link.Parent < 0 ? NoParent : remap[link.Parent],
                ChildOffset = link.ChildCount > 0 ? childStart : -1,
                ChildCount = link.ChildCount,
                ChildCapacity = link.ChildCount,
                IndexInParent = link.IndexInParent
            };
        }

        classArena.Clear();
        classArena.AddRange(newClasses);
        attributeArena.Clear();
        attributeArena.AddRange(newAttributes);
        childArena.Clear();
        childArena.AddRange(newChildren);

        // ⚠ The tail is cleared, and this is **insurance rather than a covered claim**. Nothing can
        // currently see the difference: every getter validates against `Count`, which has just come
        // down, and `CreateElement` writes every field of a slot before handing it out — so a
        // sabotage that deleted this broke nothing, which is how it came to be labelled honestly.
        // What it insures against is a getter that forgets to validate, or a field added to a slot
        // that `CreateElement` forgets to write, and in both of those the stale value is one that
        // describes a tree that no longer exists.
        Array.Clear(tags, live, count - live);
        Array.Clear(identifiers, live, count - live);
        Array.Clear(states, live, count - live);
        Array.Clear(classes, live, count - live);
        Array.Clear(attributes, live, count - live);
        Array.Clear(links, live, count - live);
        Array.Clear(blooms, live, count - live);
        Array.Clear(hasText, live, count - live);
        Array.Clear(alive, live, count - live);

        count = live;
        dead = 0;

        return live;
    }

    /// <summary>Moves an element to another position among its siblings.</summary>
    /// <param name="element">The element to move.</param>
    /// <param name="index">Where it should end up.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only within one parent, and deliberately so.</b> Reparenting would move an
    ///         element's slot relative to its new parent's, and a child whose index is below its
    ///         parent's breaks the same three passes a reused slot would — see
    ///         <see cref="Remove" />. A reconciler reorders siblings, which is the case that has to
    ///         be fast and the case that is safe.
    ///     </para>
    ///     <para>
    ///         The slot itself does not move. What moves is the entry in the parent's child run, and
    ///         <c>IndexInParent</c> for everything between the two positions — which is the field
    ///         <c>:nth-child</c> and the sibling combinators read, so a rotation that forgot it
    ///         would leave a reordered list striped in its old order.
    ///     </para>
    /// </remarks>
    public void Move(StyleNodeId element, int index) {
        var elementIndex = Validate(element);
        var parent = links[elementIndex].Parent;

        if (parent < 0) {
            throw new InvalidOperationException($"{element} has no parent, so it has no siblings to move among.");
        }

        ref var parentLinks = ref links[parent];
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, parentLinks.ChildCount);

        var from = links[elementIndex].IndexInParent;
        if (from == index) {
            return;
        }

        var offset = parentLinks.ChildOffset;
        var step = from < index ? 1 : -1;

        for (var i = from; i != index; i += step) {
            var moved = childArena[offset + i + step];
            childArena[offset + i] = moved;
            links[moved].IndexInParent = i;
        }

        childArena[offset + index] = elementIndex;
        links[elementIndex].IndexInParent = index;
    }

    /// <summary>Takes an element out of its parent's list of children.</summary>
    /// <remarks>
    ///     ⚠ The later siblings' <c>IndexInParent</c> has to come down with it. That field is what
    ///     <c>:nth-child</c> and the sibling combinators read, so a stale one leaves the third item
    ///     of a list still believing it is the fourth after the second is deleted — striped rows that
    ///     stripe wrongly, and a <c>:first-child</c> rule that lands on nothing.
    /// </remarks>
    void Detach(int index) {
        var parent = links[index].Parent;
        if (parent < 0) {
            return;
        }

        ref var parentLinks = ref links[parent];
        var position = links[index].IndexInParent;

        for (var i = position; i < parentLinks.ChildCount - 1; i++) {
            var moved = childArena[parentLinks.ChildOffset + i + 1];
            childArena[parentLinks.ChildOffset + i] = moved;
            links[moved].IndexInParent = i;
        }

        parentLinks.ChildCount--;
        links[index].Parent = NoParent;
    }

    /// <summary>Marks an element and its descendants as gone.</summary>
    void Kill(int index) {
        if (!alive[index]) {
            return;
        }

        alive[index] = false;
        dead++;

        var range = links[index];
        for (var i = 0; i < range.ChildCount; i++) {
            Kill(childArena[range.ChildOffset + i]);
        }

        // The children go with it, so the run is emptied rather than left addressable. A dead
        // element that still lists its dead children is a shape nothing needs and every walk has to
        // remember to check.
        links[index].ChildCount = 0;
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

    /// <summary>Says whether an element draws text, which is half of what <c>:empty</c> asks.</summary>
    /// <param name="element">The element.</param>
    /// <param name="hasText">Whether it has text.</param>
    /// <remarks>
    ///     ⚠ <b>Not a state flag, deliberately.</b> An <see cref="ElementState" /> would have come with
    ///     compaction, reparenting and invalidation already written, and would have been wrong the
    ///     first time a control assigned a whole state — <c>UiElement.State</c>'s setter replaces the
    ///     value rather than or-ing into it, so a hover would have quietly erased the element's text
    ///     as far as the cascade was concerned. It is also not transient, which is what that enum is
    ///     for.
    /// </remarks>
    public void SetHasText(StyleNodeId element, bool hasText) => this.hasText[Validate(element)] = hasText;

    /// <summary>Whether an element draws text.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Whether it does.</returns>
    public bool HasText(StyleNodeId element) => hasText[Validate(element)];

    /// <summary>Gives an element declarations of its own, or takes them away.</summary>
    /// <param name="element">The element.</param>
    /// <param name="style">The block, or <see langword="null" /> for none.</param>
    /// <remarks>
    ///     ⚠ <b>Which store the handle came from is not checked and cannot be.</b> An
    ///     <see cref="InlineStyleId" /> is an index and this type does not own the store it indexes —
    ///     <see cref="StyleEngine" /> does. A handle from another engine's store would resolve to
    ///     whatever declarations happened to sit at that offset, silently. That is the same hazard
    ///     <c>InlineStyleId</c>'s own remarks describe, one level up.
    /// </remarks>
    public void SetInlineStyle(StyleNodeId element, InlineStyleId? style) =>
        inlines[Validate(element)] = style?.Index ?? NoInline;

    /// <summary>An element's inline block, if it has one.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The handle, or <see langword="null" />.</returns>
    public InlineStyleId? GetInlineStyle(StyleNodeId element) => InlineAt(Validate(element));

    /// <summary>An element's inline block by slot, for the resolve pass.</summary>
    /// <param name="index">The slot.</param>
    /// <returns>The handle, or <see langword="null" />.</returns>
    /// <remarks>
    ///     By slot rather than by <see cref="StyleNodeId" /> because <c>ResolveAll</c> walks slots,
    ///     including the removed ones — the same reason <c>ParentOf</c> and <c>IsAliveAt</c> do.
    /// </remarks>
    public InlineStyleId? InlineAt(int index) =>
        inlines[index] < 0 ? null : new InlineStyleId(inlines[index]);

    /// <summary>Puts an element, and everything created under it afterwards, in a media scope.</summary>
    /// <param name="element">The element, which is a surface root in every current caller.</param>
    /// <param name="scope">The scope, from <see cref="MediaScopes.Create" />.</param>
    /// <remarks>
    ///     ⚠ <b>This element only, and it is deliberately not a subtree walk.</b> A surface root is
    ///     given its scope when the surface is created, which is before it can have any children —
    ///     and everything put inside it afterwards inherits through
    ///     <see cref="CreateElement" />, including a whole panel reparented in, because
    ///     <c>UiDocument.Reparent</c> rebuilds a moved subtree's slots rather than moving them. A
    ///     walk here would be work for a case that does not arise and a second answer to a question
    ///     that already has one.
    /// </remarks>
    public void SetScope(StyleNodeId element, int scope) {
        ArgumentOutOfRangeException.ThrowIfNegative(scope);
        scopes[Validate(element)] = scope;
    }

    /// <summary>Which media scope an element is in.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its scope.</returns>
    public int GetScope(StyleNodeId element) => scopes[Validate(element)];

    /// <summary>An element's media scope by slot, for the resolve pass.</summary>
    /// <param name="index">The slot.</param>
    /// <returns>Its scope.</returns>
    /// <remarks>By slot for the reason <see cref="InlineAt" /> is: the cascade walks slots.</remarks>
    public int ScopeAt(int index) => scopes[index];

    /// <summary>Makes an element a query container, putting everything under it in a container scope.</summary>
    /// <param name="element">The element that has a <c>container-type</c>.</param>
    /// <param name="scope">The scope its descendants are in, from <see cref="ContainerScopes.Enter" />.</param>
    /// <remarks>
    ///     ⚠ <b>It changes what the element <i>provides</i> and never what it asks</b>, so a rule
    ///     inside <c>@container (min-width: 400px)</c> cannot match the very box whose width it is
    ///     asking about. Children created afterwards inherit through <see cref="CreateElement" />;
    ///     children that already exist do not, which is why the caller re-assigns a subtree after a
    ///     pass rather than relying on creation order alone.
    /// </remarks>
    public void SetContainerScope(StyleNodeId element, int scope) {
        ArgumentOutOfRangeException.ThrowIfNegative(scope);
        providedContainers[Validate(element)] = scope;
    }

    /// <summary>Puts an element itself into a container scope, as re-assigning a subtree does.</summary>
    /// <param name="element">The element.</param>
    /// <param name="scope">The scope it is inside.</param>
    /// <remarks>
    ///     Sets both what it asks and, for anything that is not itself a container, what it provides —
    ///     so a walk that assigns a subtree top-down and calls <see cref="SetContainerScope" /> on the
    ///     containers it passes leaves every slot consistent.
    /// </remarks>
    public void SetContainedIn(StyleNodeId element, int scope) {
        ArgumentOutOfRangeException.ThrowIfNegative(scope);

        var index = Validate(element);
        containers[index] = scope;
        providedContainers[index] = scope;
    }

    /// <summary>Which container chain an element's <c>@container</c> answers are about.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its container scope.</returns>
    public int GetContainerScope(StyleNodeId element) => containers[Validate(element)];

    /// <summary>The container scope an element offers its children.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its own scope, unless it is a container.</returns>
    public int GetProvidedContainerScope(StyleNodeId element) => providedContainers[Validate(element)];

    /// <summary>An element's container scope by slot, for the resolve pass.</summary>
    /// <param name="index">The slot.</param>
    /// <returns>Its container scope.</returns>
    /// <remarks>By slot for the reason <see cref="ScopeAt" /> is: the cascade walks slots.</remarks>
    public int ContainerAt(int index) => containers[index];

    /// <summary>Adds a class to an element.</summary>
    /// <param name="element">The element.</param>
    /// <param name="className">The class.</param>
    /// <returns>Whether it was not already there.</returns>
    /// <remarks>
    ///     <para>
    ///         The class list is re-pointed to the end of the arena, leaking the old run, for the
    ///         same reason <see cref="SetAttribute" /> does: the arena is compacted when the tree is
    ///         rebuilt, and a free list is complexity no measurement has asked for.
    ///     </para>
    ///     <para>
    ///         The expensive half is the bloom. Every descendant carries a summary of its
    ///         <i>ancestors'</i> names, so a class appearing on an ancestor has to appear in all of
    ///         them or a descendant combinator gets a false negative — and a false negative in a
    ///         bloom filter is a rule that silently stops matching. It is only an OR per descendant
    ///         rather than a rebuild, and it is skipped entirely when nothing in the tree has
    ///         children.
    ///     </para>
    /// </remarks>
    public bool AddClass(StyleNodeId element, string className) {
        var index = Validate(element);
        ArgumentNullException.ThrowIfNull(className);

        var classId = names.Intern(className);
        if (HasClass(index, classId)) {
            return false;
        }

        var range = classes[index];
        var start = classArena.Count;

        for (var i = 0; i < range.Count; i++) {
            classArena.Add(classArena[range.Start + i]);
        }

        classArena.Add(classId);
        classes[index] = new ClassRange(start, range.Count + 1);

        AddToDescendantBlooms(index, classId);
        return true;
    }

    /// <summary>Removes a class from an element.</summary>
    /// <param name="element">The element.</param>
    /// <param name="className">The class.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     Descendants' blooms are deliberately <i>not</i> touched, and not because a bloom cannot
    ///     un-set a bit. A stale bit makes the filter say "an ancestor might be called this" when
    ///     none is, which costs the tree walk that would have happened anyway and cannot change an
    ///     answer. The filter is allowed to be conservative; it is never allowed to be wrong.
    /// </remarks>
    public bool RemoveClass(StyleNodeId element, string className) {
        var index = Validate(element);
        ArgumentNullException.ThrowIfNull(className);

        var classId = names.Lookup(className);
        if (classId == NameTable.None || !HasClass(index, classId)) {
            return false;
        }

        var range = classes[index];
        var start = classArena.Count;

        for (var i = 0; i < range.Count; i++) {
            if (classArena[range.Start + i] != classId) {
                classArena.Add(classArena[range.Start + i]);
            }
        }

        classes[index] = new ClassRange(start, range.Count - 1);
        return true;
    }

    /// <summary>Whether an element carries a class.</summary>
    /// <param name="element">The element.</param>
    /// <param name="className">The class.</param>
    /// <returns>Whether it does.</returns>
    public bool HasClass(StyleNodeId element, string className) {
        var index = Validate(element);
        ArgumentNullException.ThrowIfNull(className);

        var classId = names.Lookup(className);
        return classId != NameTable.None && HasClass(index, classId);
    }

    /// <summary>The transient state of an element.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its state.</returns>
    public ElementState GetState(StyleNodeId element) => states[Validate(element)];

    /// <summary>An element's tag name.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The tag.</returns>
    public string GetTagName(StyleNodeId element) => names.NameOf(tags[Validate(element)]);

    /// <summary>An attribute's value, if the element carries it.</summary>
    /// <param name="element">The element.</param>
    /// <param name="name">The attribute name.</param>
    /// <returns>The value, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>A name nothing has ever interned cannot be on any element</b>, so an unknown name is
    ///     answered without touching the arena — which is what makes asking every element in a
    ///     subtree for an attribute nobody set cost a dictionary probe rather than a walk.
    /// </remarks>
    public string? GetAttribute(StyleNodeId element, string name) {
        var index = Validate(element);
        ArgumentNullException.ThrowIfNull(name);

        var nameId = names.Lookup(name);

        return nameId != NameTable.None && TryGetAttribute(index, nameId, out var valueId)
            ? names.NameOf(valueId)
            : null;
    }

    /// <summary>An element's id attribute.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The id, or <see langword="null" /> if it has none.</returns>
    public string? GetId(StyleNodeId element) {
        var id = identifiers[Validate(element)];
        return id == NameTable.None ? null : names.NameOf(id);
    }

    /// <summary>An element's classes, in the order they were added.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The class names.</returns>
    /// <remarks>
    ///     Allocates, and is meant to: this is for inspectors, diagnostics and tests rebuilding a
    ///     tree, none of which run per frame. Matching never asks — it compares interned ids.
    /// </remarks>
    public string[] GetClassNames(StyleNodeId element) {
        var range = classes[Validate(element)];
        var result = new string[range.Count];

        for (var i = 0; i < range.Count; i++) {
            result[i] = names.NameOf(classArena[range.Start + i]);
        }

        return result;
    }

    /// <summary>An element's attributes, in the order they were set.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The name/value pairs.</returns>
    /// <remarks>
    ///     Allocates, like <see cref="GetClassNames" /> and for the same callers — an inspector, a
    ///     diagnostic, and the one that made it necessary: reparenting, which rebuilds an element's
    ///     slot under a different parent and would otherwise silently drop everything the tree knows
    ///     about it that nothing can read back.
    /// </remarks>
    public (string Name, string Value)[] GetAttributes(StyleNodeId element) {
        var range = attributes[Validate(element)];
        var result = new (string, string)[range.Count];

        for (var i = 0; i < range.Count; i++) {
            var entry = attributeArena[range.Start + i];
            result[i] = (names.NameOf(entry.Name), names.NameOf(entry.Value));
        }

        return result;
    }

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

    internal bool IsAliveAt(int index) => alive[index];

    internal int TagOf(int index) => tags[index];

    internal int IdOf(int index) => identifiers[index];

    internal ElementState StateOf(int index) => states[index];

    internal int ParentOf(int index) => links[index].Parent;

    internal int IndexInParentOf(int index) => links[index].IndexInParent;

    internal int ChildCountOf(int index) => links[index].ChildCount;

    internal bool HasTextAt(int index) => hasText[index];

    internal int SiblingCountOf(int index) {
        var parent = links[index].Parent;
        return parent < 0 ? 1 : links[parent].ChildCount;
    }

    internal int PreviousSiblingOf(int index) {
        var parent = links[index].Parent;
        var position = links[index].IndexInParent;
        return parent < 0 || position == 0 ? NoParent : childArena[links[parent].ChildOffset + position - 1];
    }

    /// <summary>The element's one-based position among the siblings that share its tag.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted rather than looked up, and it is the one structural test that cannot be
    ///     O(1) here.</b> <see cref="IndexInParentOf" /> is stored because a child's position is
    ///     maintained by every insertion anyway; an of-type position is a position in a subsequence
    ///     that depends on which tag is being asked about, so a stored copy would be one number per
    ///     element per tag. Browsers count it too. The walk is over the parent's child list, which
    ///     is contiguous in the arena, and it runs only for a selector that asked for it.
    /// </remarks>
    internal int TypeIndexOf(int index) {
        var parent = links[index].Parent;
        if (parent < 0) {
            return 1;
        }

        var tag = tags[index];
        var offset = links[parent].ChildOffset;
        var position = links[index].IndexInParent;
        var seen = 0;

        for (var i = 0; i <= position; i++) {
            if (tags[childArena[offset + i]] == tag) {
                seen++;
            }
        }

        return seen;
    }

    /// <summary>How many of the element's siblings — itself included — share its tag.</summary>
    internal int TypeCountOf(int index) {
        var parent = links[index].Parent;
        if (parent < 0) {
            return 1;
        }

        var tag = tags[index];
        var offset = links[parent].ChildOffset;
        var count = links[parent].ChildCount;
        var seen = 0;

        for (var i = 0; i < count; i++) {
            if (tags[childArena[offset + i]] == tag) {
                seen++;
            }
        }

        return seen;
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

    internal int ClassCountOf(int index) => classes[index].Count;

    internal void ForEachClass(int index, Action<int> visit) {
        var range = classes[index];
        for (var i = 0; i < range.Count; i++) {
            visit(classArena[range.Start + i]);
        }
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

    /// <summary>ORs a name into the ancestor bloom of everything below an element.</summary>
    /// <param name="index">The element whose name it now is.</param>
    /// <param name="nameId">The name.</param>
    /// <remarks>
    ///     Elements are created parents-before-children, so a descendant always has a higher index
    ///     than its ancestors — which means one ascending sweep does the whole subtree without a
    ///     stack, and without visiting anything above it.
    /// </remarks>
    void AddToDescendantBlooms(int index, int nameId) {
        for (var i = index + 1; i < count; i++) {
            if (alive[i] && IsDescendantOf(i, index)) {
                blooms[i].Add(nameId);
            }
        }
    }

    bool IsDescendantOf(int candidate, int ancestor) {
        for (var walk = links[candidate].Parent; walk >= 0; walk = links[walk].Parent) {
            if (walk == ancestor) {
                return true;
            }

            if (walk < ancestor) {
                // Parents always have lower indices, so once the climb passes the ancestor's index
                // without hitting it, it never will.
                return false;
            }
        }

        return false;
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

    /// <summary>How many slots a parent's first child reserves.</summary>
    /// <remarks>
    ///     Four, because a control is a handful of parts — a switch is a track and a knob, a scroll
    ///     view a viewport and two bars — so most parents never relocate at all, and a leaf wastes
    ///     nothing because it never asks for a run.
    /// </remarks>
    const int InitialChildCapacity = 4;

    /// <summary>Adds a child to a parent's run, relocating it only when it has outgrown its space.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to be O(children) per append whenever anything else had grown into the
    ///         arena behind it, which makes building a wide parent quadratic.</b> The old version kept
    ///         a run packed tight and had only one way to add a slot: if the run was not the last
    ///         thing in the arena, copy the whole run to the end and append there. Building
    ///         parent-then-children keeps the run at the end and hides it — which is why every control
    ///         in the library is clear of it and why it survived — but the interleaved case is not
    ///         exotic. Two panels filled a row at a time, a reconciler walking a keyed list, a
    ///         <c>DataGrid</c> adding a cell to each of its columns: all of them alternate parents,
    ///         and each of the n-th parent's appends copies n entries.
    ///     </para>
    ///     <para>
    ///         The fix is the one every growable array uses and the field for it was already
    ///         here — <c>ChildCapacity</c> existed, was set to zero, and was read by nothing. A run
    ///         now reserves space beyond its count; an append with room writes in place, and one
    ///         without relocates <i>and doubles</i>, so the copies fall off geometrically and the
    ///         amortised cost is constant.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The reserved slots hold whatever was there before and nothing may read them.</b>
    ///         Every reader — <c>GetChild</c>, <c>PreviousSibling</c>, <c>Detach</c>, <c>Move</c>,
    ///         <c>Kill</c> — is bounded by <c>ChildCount</c> rather than by the arena's length, which
    ///         is what makes reservation safe. <c>Compact</c> rebuilds runs tight, which is correct:
    ///         a compacted tree is one nobody is mid-way through building.
    ///     </para>
    /// </remarks>
    void AppendChild(int parent, int child) {
        ref var parentLinks = ref links[parent];

        if (parentLinks.ChildOffset < 0) {
            parentLinks.ChildOffset = childArena.Count;
            parentLinks.ChildCapacity = InitialChildCapacity;

            for (var i = 0; i < InitialChildCapacity; i++) {
                childArena.Add(0);
            }
        } else if (parentLinks.ChildCount == parentLinks.ChildCapacity) {
            if (parentLinks.ChildOffset + parentLinks.ChildCapacity == childArena.Count) {
                // The run is the last thing in the arena, so it can grow where it stands — no copy,
                // whatever its size.
                for (var i = 0; i < parentLinks.ChildCapacity; i++) {
                    childArena.Add(0);
                }

                parentLinks.ChildCapacity *= 2;
            } else {
                // Boxed in. Move to the end with twice the room, so the next several appends land in
                // reserved space and this copy is paid once per doubling rather than once per child.
                var moved = childArena.Count;

                for (var i = 0; i < parentLinks.ChildCount; i++) {
                    childArena.Add(childArena[parentLinks.ChildOffset + i]);
                }

                var capacity = parentLinks.ChildCapacity * 2;

                for (var i = parentLinks.ChildCount; i < capacity; i++) {
                    childArena.Add(0);
                }

                parentLinks.ChildOffset = moved;
                parentLinks.ChildCapacity = capacity;
            }
        }

        childArena[parentLinks.ChildOffset + parentLinks.ChildCount] = child;
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
        Array.Resize(ref inlines, next);
        Array.Resize(ref scopes, next);
        Array.Resize(ref containers, next);
        Array.Resize(ref providedContainers, next);
        Array.Resize(ref hasText, next);
        Array.Resize(ref alive, next);
    }

    /// <summary>How many entries the child arena holds, reserved space included.</summary>
    /// <remarks>
    ///     Exposed for <c>ChildArenaTests</c>, which measures the growth policy by counting slots
    ///     rather than by timing it — a copy appends a whole run again, so the number says directly
    ///     whether appending is amortised, and it does not fail on a loaded machine the way a clock
    ///     would. It is also the honest way to state the cost of reservation: the arena is bigger than
    ///     the children in it, on purpose, and by how much is checkable.
    /// </remarks>
    public int ChildArenaLength => childArena.Count;

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
