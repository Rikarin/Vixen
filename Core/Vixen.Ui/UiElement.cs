// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;
using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>One node of the user interface.</summary>
/// <remarks>
///     <para>
///         <b>Elements are classes, and that is a deliberate departure from the rest of the
///         engine.</b> An ECS component is a struct because there are a million of them in a hot
///         loop; a UI node has identity, virtual behaviour and event handlers, and there are ten
///         thousand of them. The struct-of-arrays discipline lives where the loops actually are —
///         the layout store and, later, the draw list — and this type is a handle into them.
///     </para>
///     <para>
///         It holds no geometry and no style of its own. The cascade owns the computed style, the
///         layout tree owns the result, and everything read from here is a lookup into one of the
///         two. That is what keeps a hundred identical list rows from being a hundred copies of
///         anything.
///     </para>
/// </remarks>
public partial class UiElement : Composition.IComposable {
    readonly List<UiElement> children = [];
    List<UiElement>? ordered;
    bool orderDirty = true;
    int zIndex;
    int flexOrder;
    int paintKey;
    List<HandlerRegistration>? handlers;
    UiDocument? document;

    /// <summary>Creates a detached element.</summary>
    /// <remarks>
    ///     ⚠ <b>Parameterless, and it has to be.</b> A subclass is the ordinary way to write a
    ///     control, and a base constructor taking a document and two internal node handles would put
    ///     those handles in every subclass's signature — in another assembly, where they are not
    ///     visible. So construction and registration are two steps: <see cref="UiDocument.Create{T}" />
    ///     makes one and then binds it. Markup will want the same shape, since a generated
    ///     <c>new Button()</c> cannot know a document either.
    /// </remarks>
    /// <remarks>
    ///     Public rather than protected because <see cref="UiDocument.Create{T}" /> is constrained on
    ///     <c>new()</c>, and a plain <see cref="UiElement" /> is itself a usable element. An instance
    ///     that has not been bound throws from <see cref="Document" /> rather than pretending.
    /// </remarks>
    public UiElement() {
        Tag = string.Empty;
        Style = ComputedStyle.Empty;
    }

    /// <summary>The document this belongs to.</summary>
    /// <exception cref="InvalidOperationException">If it has not been added to one, or has been removed.</exception>
    /// <remarks>
    ///     ⚠ <b>A removed element throws rather than answering</b>, and the message says which of the
    ///     two it is. Everything an element can do reaches the document through here, so this is the
    ///     one place a use-after-removal has to be caught — and the alternative is worse than a
    ///     crash: the node ids it still holds address slots the trees have handed to somebody else,
    ///     so reading a removed element's width returns another element's width and setting its class
    ///     restyles a stranger.
    /// </remarks>
    public UiDocument Document =>
        IsRemoved
            ? throw new InvalidOperationException($"this {GetType().Name} has been removed from its document")
            : document ?? throw new InvalidOperationException(
                $"this {GetType().Name} is not in a document — create it with UiDocument.Create or UiElement.Add"
            );

    /// <summary>Whether it has been taken out of its document.</summary>
    public bool IsRemoved { get; private set; }

    /// <summary>Its element name, which selectors match on.</summary>
    public string Tag { get; private set; }

    /// <summary>The element name this type answers to when a caller does not choose one.</summary>
    /// <remarks>
    ///     <para>
    ///         A control overrides this with a literal, so that <c>new Button()</c> and the
    ///         <c>button { … }</c> its theme is written against cannot come apart. Everything a
    ///         stylesheet can say about a control is said through this name, which makes it part of
    ///         the type's contract rather than a detail of how it was constructed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A literal rather than the type name lowercased.</b> Deriving it would give
    ///         <c>iconbutton</c> for <c>IconButton</c> and nothing readable at all for a generic
    ///         type, would make renaming a class a silent restyle of every document that used it,
    ///         and would put a reflection call on the creation path of every element in a framework
    ///         that has to run trimmed.
    ///     </para>
    /// </remarks>
    protected internal virtual string TagName => "div";

    /// <summary>Where content written inside this element's tag actually goes.</summary>
    /// <remarks>
    ///     <para>
    ///         Itself, for everything that is only an element. A control with a scrolling viewport,
    ///         a popover with a panel, a card with a body — anything whose visible interior is a
    ///         <i>part</i> rather than the control — answers with that part.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The control-side mirror of <c>Component.Content</c>, and markup is what needs
    ///         it.</b> <c>&lt;ScrollView&gt;&lt;row /&gt;&lt;/ScrollView&gt;</c> means the row is in
    ///         the scrolled area; hung off the control itself it would sit beside the viewport and
    ///         the scrollbars, be laid out by neither, and never move when the view scrolled. Code
    ///         that builds by hand writes <c>view.Content.Add(…)</c> and says the same thing;
    ///         markup has no <c>.Content</c> to write, so the element answers for itself.
    ///     </para>
    /// </remarks>
    protected internal virtual UiElement ContentHost => this;

    /// <summary>Where content addressed to one of this control's named slots goes.</summary>
    /// <param name="name">The name, as written in <c>slot="…"</c>.</param>
    /// <returns>The element to build under, or <see langword="null" /> when the control publishes
    ///     nothing under that name.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="ContentHost" /> is one place and some controls are two things.</b> An
    ///         <c>Expander</c> is a header and a body. Markup could fill the body, because that is
    ///         what <see cref="ContentHost" /> answers, and had no spelling at all for the header —
    ///         so every panel wanting an icon, a remove button or a grab handle on a foldout header
    ///         built its whole loop in C#. That one missing name is what kept the last portable
    ///         editor panel out of <c>.vxml</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A lookup rather than one property per part, and <see cref="ContentHost" /> stays
    ///         the default.</b> A control publishes the extra places it has and no more; a name it
    ///         does not publish throws in <c>BuildContext.Into</c> rather than falling back on the
    ///         content host, because content landing silently in the body when the author asked for
    ///         the header draws a panel that is wrong in a way nothing reports.
    ///     </para>
    ///     <para>
    ///         The <see cref="Composition.Component" /> mirror is <c>&lt;slot name="…" /&gt;</c>,
    ///         which fills the dictionary this virtual stands in for. <c>BuildContext.Into</c> is
    ///         the one call site of both.
    ///     </para>
    /// </remarks>
    protected internal virtual UiElement? NamedHost(string name) => null;

    /// <summary>Its parent, or <c>null</c> for the root.</summary>
    public UiElement? Parent { get; private set; }

    /// <summary>The surface this element <i>is</i> the root of, if it is one.</summary>
    /// <remarks>
    ///     ⚠ <b>Not "the surface this element is in" — that is
    ///     <see cref="UiDocument.SurfaceOf" />.</b> Three passes need to know where one window's tree
    ///     stops and the next one's begins, and they all ask the same question of a child: is this
    ///     one somewhere else? A property that answered "which window am I in" would be true of every
    ///     element and would answer none of them.
    /// </remarks>
    public UiSurface? SurfaceRoot { get; private set; }

    /// <summary>Its children, in document order.</summary>
    public IReadOnlyList<UiElement> Children => children;

    /// <summary>The same children, as the list they are.</summary>
    /// <remarks>
    ///     ⚠ <b>For the walks that run every frame, and for no other reason.</b> A <c>foreach</c> over
    ///     <see cref="Children" /> enumerates an interface, which boxes <c>List&lt;T&gt;</c>'s struct
    ///     enumerator — forty bytes per element with children, per walk, per frame. The JIT elides it
    ///     wherever it can prove the concrete type and cannot always, and "sometimes free" is not what
    ///     a per-frame pass can be built on. <see cref="UiDocument" />'s <c>Apply</c> and
    ///     <c>Accumulate</c> take this; everything that runs on a mutation takes
    ///     <see cref="Children" />, because there the clarity is worth more than the enumerator.
    ///
    ///     Internal, and a mutable list only incidentally: the outside reads <see cref="Children" />,
    ///     and nothing on this side of the wall may write through this.
    /// </remarks>
    internal List<UiElement> ChildList => children;

    /// <summary>Its <c>line-height</c> in pixels, or <see cref="float.NaN" /> for the font's own.</summary>
    /// <remarks>
    ///     Computed each style pass and inherited in that form, the same way <see cref="FontSize" />
    ///     is and for the same reason: the property takes relative units, so a child inheriting the
    ///     text <c>1.5em</c> would resolve it against its own font size rather than the ancestor's.
    ///     NaN rather than zero for "whatever the font recommends", because zero is a line height
    ///     somebody might mean.
    /// </remarks>
    public float LineHeight { get; internal set; } = float.NaN;

    /// <summary>Its <c>letter-spacing</c> in pixels.</summary>
    /// <remarks>Computed and inherited like <see cref="LineHeight" />. Zero when nothing said.</remarks>
    public float LetterSpacing { get; internal set; }

    /// <summary>Its <c>word-spacing</c> in pixels: extra added to every word-separator character.</summary>
    /// <remarks>
    ///     <para>
    ///         Computed and inherited like <see cref="LineHeight" />, and for the same reason — it
    ///         takes relative units, so <c>0.5em</c> has to be resolved against the element that wrote
    ///         it rather than against each descendant that inherits it. Zero when nothing said.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not <see cref="LetterSpacing" /> applied to spaces.</b> CSS Text 3 § 8.2
    ///         names a closed list of word-separator characters — in practice the space and the
    ///         no-break space — and a tab, a line separator and a zero-width space are all excluded
    ///         from it. <c>TextRun</c> carries the list.
    ///     </para>
    /// </remarks>
    public float WordSpacing { get; internal set; }

    /// <summary>Its <c>text-indent</c> in pixels: how far the <i>first</i> line is pushed in.</summary>
    /// <remarks>
    ///     <para>
    ///         Computed and inherited like <see cref="LineHeight" />, and for the same reason — the
    ///         property takes relative units. Zero when nothing said.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It narrows the first line as well as moving it, which is what makes it an
    ///         indent rather than a translation.</b> <see cref="Block(float)" /> hands the wrapper two
    ///         widths, so the first line wraps a word earlier and the ones after it do not — and the
    ///         indent then travels on the line as <c>TextLine.Offset</c>, which the draw list, the
    ///         caret and the hit test all read. A negative value hangs the first line out to the left,
    ///         which is CSS's hanging indent and needs nothing extra.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the <i>computed</i> value and a percentage is not in it.</b> CSS measures
    ///         a percentage against the containing block's width, which is a layout result the style
    ///         pass does not have, so it travels separately in <see cref="TextIndentPercent" /> and
    ///         the two are joined by <see cref="UsedTextIndent" /> at the moment a width exists. This
    ///         property used to be the whole story and a percentage resolved to zero, which is the
    ///         initial value — a declaration thrown away with nothing in the frame to say so.
    ///     </para>
    /// </remarks>
    public float TextIndent { get; internal set; }

    /// <summary>The percentage a <c>text-indent</c> named, or NaN when it named a length.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second field rather than a percent-capable <see cref="TextIndent" />, which is
    ///         the same shape <c>line-height</c> already has one property over.</b> A unitless
    ///         <c>line-height</c> travels as <c>ComputedText.LineHeightFactor</c> beside the pixels
    ///         for exactly this reason: the two are different kinds of answer and a struct that held
    ///         one slot would have to encode "which kind" in a sentinel. Here the sentinel is NaN and
    ///         it is on the half that is absent, so the common case — no percentage anywhere — costs
    ///         one float per element and no branch a caller can get wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It inherits as a percentage, not as the ancestor's resolved pixels.</b> That is
    ///         CSS: <c>text-indent</c> inherits its computed value, and a percentage's computed value
    ///         <i>is</i> the percentage. A panel at 25% and a paragraph inside it half as wide are
    ///         indented by different numbers of pixels, which is the whole reason the resolution
    ///         cannot happen in the style pass.
    ///     </para>
    /// </remarks>
    internal float TextIndentPercent { get; set; } = float.NaN;

    /// <summary>The indent this element actually gets in a box of a given width.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The used-value stage this property had no equivalent of, and the reason
    ///         `Rikarin/Vixen#457` could not be a one-line fix.</b> Box lengths carry a percentage
    ///         into <c>Vixen.Ui.Layout</c> through <c>StyleLength.Percent</c> and are resolved there
    ///         against a real containing block; the text properties had no such hand-off, so a
    ///         <c>text-indent: 25%</c> was resolved to zero in the style pass and never mentioned
    ///         again. <see cref="Block(float)" /> is where the hand-off belongs because it is the one
    ///         place that has both the declaration and the laid-out width.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unbounded width gives zero, which is CSS's own answer and not a fallback.</b>
    ///         An intrinsic measurement has no containing block to be a percentage of — CSS Sizing
    ///         resolves percentages against an indefinite size as <c>auto</c>, and for this property
    ///         that is no indent. Returning <see cref="TextIndent" /> there is right for the same
    ///         reason: when a percentage was declared, <see cref="TextIndent" /> is zero.
    ///     </para>
    /// </remarks>
    /// <param name="width">The width the text is being wrapped to, or infinity.</param>
    /// <returns>The indent in pixels.</returns>
    internal float UsedTextIndent(float width) =>
        float.IsNaN(TextIndentPercent) || !float.IsFinite(width)
            ? TextIndent
            : TextIndentPercent / 100f * width;

    /// <summary>The OpenType features its text is shaped with.</summary>
    /// <remarks>
    ///     <c>font-feature-settings</c> and <c>font-variant-numeric</c> between them, resolved once
    ///     per style pass — see <c>UiDocument.ResolveText</c>. <see cref="FontFeatureSet.None" /> for
    ///     text that asked for nothing, which is almost all of it, and it is a singleton so the
    ///     common case costs a reference comparison.
    /// </remarks>
    public FontFeatureSet FontFeatures { get; internal set; } = FontFeatureSet.None;

    /// <summary>The base direction its text is laid out at.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>direction</c>, resolved once per style pass — see <c>UiDocument.DirectionOf</c>,
    ///         which is also where the reasoning for <see cref="ParagraphDirection.Auto" /> being the
    ///         value of an element that did not state one lives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It changes what the element <i>measures</i>, not only how it is painted.</b> A
    ///         paragraph's base level decides which side its neutrals fall on and therefore where
    ///         every glyph after them sits, so this is one of the properties the style pass has to
    ///         mark the layout node dirty for — beside <c>line-height</c> and <c>letter-spacing</c>,
    ///         and for the same reason.
    ///     </para>
    /// </remarks>
    public ParagraphDirection ParagraphDirection { get; internal set; } = ParagraphDirection.Auto;

    /// <summary>Where it sits among its siblings when they overlap.</summary>
    /// <remarks>
    ///     <para>
    ///         Resolved from <c>z-index</c> each style pass. Zero when nothing said, and <c>auto</c>
    ///         reads as zero too — this engine has no stacking context for <c>auto</c> to defer to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It orders siblings, and only siblings.</b> CSS lets a positioned descendant with
    ///         a z-index paint above an element that is not its parent's sibling — the behaviour a
    ///         dropdown escaping its row relies on — and that needs stacking contexts, which needs
    ///         the whole of CSS 2.1 Appendix E. Here a high z-index lifts a child above its brothers
    ///         and no further, so an overlay that must cover the whole window belongs to a container
    ///         near the root rather than to the row that opened it. Said plainly because the two
    ///         models agree until the moment they matter.
    ///     </para>
    ///     <para>
    ///         ⚠ Unlike CSS, this applies to <i>every</i> element rather than only positioned ones.
    ///         The restriction exists in CSS because a static element establishes no stacking context
    ///         for the index to be measured in; sibling ordering needs no such thing, and requiring
    ///         <c>position: relative</c> before <c>z-10</c> did anything would be a rule with no
    ///         reason behind it here.
    ///     </para>
    /// </remarks>
    public int ZIndex {
        get => zIndex;

        internal set {
            if (zIndex == value) {
                return;
            }

            zIndex = value;
            Parent?.InvalidateOrder();
        }
    }

    /// <summary>The <c>order</c> the layout is using for this item, mirrored for paint order.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept here rather than read back out of the layout tree on each sort.</b> The two
    ///     stores are deliberately unaware of each other — <c>LayoutStyleBuilder</c> is the only
    ///     wire — and the draw walk asking the layout arena for a style per child per frame would
    ///     put that dependency in the hottest loop there is. <c>UiDocument</c> writes it from the
    ///     same built <see cref="LayoutStyle" /> it hands to <c>SetStyle</c>, so they cannot drift.
    /// </remarks>
    internal int FlexOrder {
        get => flexOrder;

        set {
            if (flexOrder == value) {
                return;
            }

            flexOrder = value;
            Parent?.InvalidateOrder();
        }
    }

    /// <summary>Its children in the order they are painted, back to front.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one place paint order is decided</b>, read forwards by the draw list and
    ///         backwards by hit testing. The two have to agree — an element drawn on top must be the
    ///         one a click lands on — and the cheapest way to guarantee that is for neither of them
    ///         to have its own opinion.
    ///     </para>
    ///     <para>
    ///         Document order costs nothing: with no z-index anywhere among the children this is the
    ///         children list itself, not a copy of it. The sorted list is built only when some child
    ///         has an index, and then cached until the children or one of their indices change.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="List{T}" /> rather than <see cref="IReadOnlyList{T}" />, and the
    ///         difference was the whole of the steady frame's allocation.</b> Both branches return a
    ///         list, so the type is exact and the draw walk's <c>foreach</c> gets the struct
    ///         enumerator — where the interface boxed one per element with children, every frame, on a
    ///         document nothing had changed in. That is the same defect <see cref="ChildList" />
    ///         exists for, and it showed up here first because <c>UiDocument.Draw</c> is the only pass
    ///         a <i>settled</i> document still runs: the criterion it broke was the zero-allocation one,
    ///         which is a claim about doing nothing.
    ///     </para>
    /// </remarks>
    internal List<UiElement> PaintOrder {
        get {
            if (!orderDirty) {
                return ordered ?? children;
            }

            orderDirty = false;

            if (!AnyChildIsReordered()) {
                ordered = null;
                return children;
            }

            ordered ??= [];
            ordered.Clear();
            ordered.AddRange(children);

            // Stamped rather than looked up, because the tie-break has to be the child's document
            // position and finding that with IndexOf inside the comparison turns an n log n sort
            // into an n² log n one.
            for (var i = 0; i < children.Count; i++) {
                children[i].paintKey = i;
            }

            // Stable by construction: equal keys keep document order, which is what makes `z-10` on
            // one child leave every other child exactly where it was.
            //
            // ⚠ <b>`order` sits between the two, because CSS Flexbox §5.4 makes it modify document
            // order rather than override the stacking one.</b> A flex container paints its items in
            // *order-modified document order*, and `z-index` then reorders that — so `order` is the
            // tie-break among children sharing an index, and never the other way round. Getting the
            // two the wrong way round would let `order-1` hoist a child above a `z-10` sibling,
            // which no browser does.
            ordered.Sort(static (left, right) =>
                left.zIndex != right.zIndex ? left.zIndex.CompareTo(right.zIndex)
                : left.flexOrder != right.flexOrder ? left.flexOrder.CompareTo(right.flexOrder)
                : left.paintKey.CompareTo(right.paintKey));

            return ordered;
        }
    }

    /// <summary>Whether anything among the children moves it off the plain document list.</summary>
    /// <remarks>
    ///     ⚠ <b><c>order</c> counts as well as <c>z-index</c>.</b> Checking only the latter is what
    ///     would make the property lay out correctly and paint in the old positions — the exact
    ///     half-implemented shape the utilities inventory exists to catch.
    /// </remarks>
    bool AnyChildIsReordered() {
        foreach (var child in children) {
            if (child.zIndex != 0 || child.flexOrder != 0) {
                return true;
            }
        }

        return false;
    }

    internal void InvalidateOrder() => orderDirty = true;

    /// <summary>What the cascade decided. Interned, so two alike elements share one object.</summary>
    public ComputedStyle Style { get; internal set; }

    /// <summary>Its resolved font size in pixels, which every <c>em</c> on it measures against.</summary>
    public float FontSize { get; internal set; } = LengthContext.InitialFontSize;

    internal StyleNodeId StyleNode { get; private set; }

    internal LayoutNodeId LayoutNode { get; private set; }

    /// <summary>Its left edge, relative to its parent, after the last layout pass.</summary>
    public float Left => Document.Layout.GetLeft(LayoutNode);

    /// <summary>Its top edge, relative to its parent.</summary>
    public float Top => Document.Layout.GetTop(LayoutNode);

    /// <summary>Its width.</summary>
    public float Width => Document.Layout.GetWidth(LayoutNode);

    /// <summary>Its height.</summary>
    public float Height => Document.Layout.GetHeight(LayoutNode);

    /// <summary>Adds a child element.</summary>
    /// <param name="tag">Its element name.</param>
    /// <param name="id">Its identifier, for an <c>#id</c> selector.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The new element.</returns>
    public UiElement Add(string tag, string? id = null, params ReadOnlySpan<string> classNames) =>
        Document.Create(tag, this, id, classNames);

    /// <summary>Adds a child of a particular element type.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="tag">Its element name, or <c>null</c> to take the one the type answers to.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The new element.</returns>
    /// <remarks>
    ///     The tag defaults so that <c>parent.Add&lt;Button&gt;()</c> is the whole of adding a
    ///     control — see <see cref="TagName" /> for why a control naming itself is worth the
    ///     defaulted parameter.
    /// </remarks>
    public T Add<T>(string? tag = null, string? id = null, params ReadOnlySpan<string> classNames)
        where T : UiElement, new() =>
        Document.Create<T>(tag, this, id, classNames);

    /// <summary>Adds a class, and invalidates what that could have changed.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it was not already there.</returns>
    public bool AddClass(string className) {
        if (!Document.Styles.Tree.AddClass(StyleNode, className)) {
            return false;
        }

        Document.InvalidateClass(StyleNode, className);
        Document.RecordDirty(this, UiInvalidationKind.Class);

        return true;
    }

    /// <summary>Removes a class.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveClass(string className) {
        if (!Document.Styles.Tree.RemoveClass(StyleNode, className)) {
            return false;
        }

        Document.InvalidateClass(StyleNode, className);
        Document.RecordDirty(this, UiInvalidationKind.Class);

        return true;
    }

    /// <summary>Whether it carries a class.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it does.</returns>
    public bool HasClass(string className) => Document.Styles.Tree.HasClass(StyleNode, className);

    /// <summary>An attribute's value, if it has one.</summary>
    /// <param name="name">The attribute name.</param>
    /// <returns>Its value, or <see langword="null" /> if the element does not carry it.</returns>
    /// <remarks>
    ///     ⚠ <b>Attributes exist here for selectors and are readable because binding needs them.</b>
    ///     An <c>[data-kind="warning"]</c> rule was the whole use until markup grew
    ///     <c>binding-path</c> — which names a member of an editing target and is joined to it after
    ///     the tree is built, so something has to be able to walk the tree and ask. See doc 36 § P4.
    /// </remarks>
    public string? Attribute(string name) => Document.Styles.Tree.GetAttribute(StyleNode, name);

    /// <summary>Sets an attribute.</summary>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">Its value.</param>
    /// <remarks>
    ///     ⚠ <b><c>class</c> is not this</b> — it is a set rather than a value, and
    ///     <see cref="AddClass" /> is how one is added. <c>BuildContext.Attribute</c> handles the
    ///     difference for markup; this is the same write for anybody building a tree by hand.
    /// </remarks>
    public void SetAttribute(string name, string value) {
        Document.Styles.Tree.SetAttribute(StyleNode, name, value);
        Document.Invalidate();
    }

    /// <summary>The attribute a language is declared in, and it is HTML's spelling on purpose.</summary>
    const string LanguageAttribute = "lang";

    /// <summary>What language this element's own text is written in, as a BCP-47 tag.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An attribute rather than a style property, and CSS is the authority for that
    ///         rather than convenience.</b> There is no <c>lang</c> property in any CSS module —
    ///         a language is a fact about the <i>document</i> that stylesheets <i>select on</i>, and
    ///         a property would let a theme assert what language somebody's words are in. That is
    ///         exactly the assertion the five consumers of this must not let a theme make: font
    ///         fallback for a Han character, Turkish dotted-i casing, and the hyphenation patterns
    ///         #546 is about are all wrong answers if a stylesheet can supply the language.
    ///     </para>
    ///     <para>
    ///         So a stylesheet <i>reads</i> it, through the attribute selectors this engine already
    ///         has: <c>[lang|="de"]</c> is <c>:lang(de)</c>'s own definition — CSS Selectors 4
    ///         defines the <c>|=</c> operator as the BCP-47 prefix match for exactly this — so
    ///         <c>de-AT</c> matches it and <c>de</c> does too. Markup writes it as
    ///         <c>lang="de"</c> with no mapping, because <c>BuildContext.Attribute</c> already
    ///         writes an attribute of that name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see langword="null" /> means "declares none", which is not "English" and not
    ///         the machine's locale.</b> See <see cref="ResolvedLanguage" />. Assigning
    ///         <see langword="null" /> writes the empty tag rather than removing the attribute,
    ///         because <c>StyleTree</c> appends attributes and never removes one; an empty tag reads
    ///         back as <see langword="null" /> and inherits from the ancestor above, which is what
    ///         taking a declaration off is supposed to mean.
    ///     </para>
    /// </remarks>
    public string? Language {
        get => Document.Styles.Tree.GetAttribute(StyleNode, LanguageAttribute) is { Length: > 0 } tag ? tag : null;
        set => SetAttribute(LanguageAttribute, value ?? string.Empty);
    }

    /// <summary>The language in force here: this element's, or the nearest ancestor's, or the document's.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Inheritance by tree rather than by cascade</b>, which is how <c>lang</c> inherits
    ///         in HTML and the only way it can inherit here: it is not a style property, so the
    ///         cascade has nothing to carry down. A German paragraph inside an English shell is
    ///         <c>lang="de"</c> on the paragraph and nothing on anything inside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty is a working answer and the deliberate default.</b> It means undetermined,
    ///         and it reaches <c>TextShaper</c> as "leave HarfBuzz's language unset" — which is what
    ///         keeps a document's shaping identical on every machine. ⚠ Nothing here reads
    ///         <c>CultureInfo.CurrentCulture</c>: taking the language from the process locale would
    ///         wrap a paragraph differently on a German developer's laptop than on CI, and the
    ///         failure would surface as a golden image red on one machine only. A host that wants
    ///         the user's language says so, on <see cref="UiDocument.Language" />.
    ///     </para>
    /// </remarks>
    public string ResolvedLanguage {
        get {
            for (var element = this; element is not null; element = element.Parent) {
                if (element.Language is { } declared) {
                    return declared;
                }
            }

            return Document.Language;
        }
    }

    /// <summary>The state bits whose changing is something a screen reader would announce.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two of the seven, and the narrowness is the design.</b> A control's accessible
    ///         view is computed and never stored, so nothing here mirrors a state — this only decides
    ///         whether the coalesced <see cref="UiDocument.AccessibilityInvalidated" /> flag is worth
    ///         setting, and a bridge that caches and diffs re-reads the computed answer itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ElementState.Checked" /> is the carrier for three different announced
    ///         states</b>, which is why one bit covers so much: a toggle is ticked with it, a tab, an
    ///         option, a tree row, a grid row and a docked tab are <i>selected</i> with it, and an
    ///         expander header is <i>open</i> with it. Each control says which of the three its flag
    ///         means, in its own <see cref="NativeAccessibleState" />; what they share is the write.
    ///     </para>
    ///     <para>
    ///         <b><see cref="ElementState.Hover" /> and <see cref="ElementState.Active" /> are
    ///         excluded because they are not announced</b>, and including them would set the flag on
    ///         every frame the pointer moves — which is a bridge diffing its whole cached tree for
    ///         the mouse crossing a button. The three focus bits are excluded for the opposite
    ///         reason: <c>UiDocument.Focus</c> already invalidates, and a second raise from the
    ///         restate it performs would be the same frame's work done twice.
    ///     </para>
    /// </remarks>
    const ElementState AnnouncedStates = ElementState.Checked | ElementState.Disabled;

    /// <summary>Its interaction state — hover, focus, active — which selectors match on.</summary>
    public ElementState State {
        get => Document.Styles.Tree.GetState(StyleNode);
        set {
            var previous = Document.Styles.Tree.GetState(StyleNode);

            if (previous == value) {
                return;
            }

            Document.Styles.Tree.SetState(StyleNode, value);
            Document.InvalidateState(StyleNode);
            Document.RecordDirty(this, UiInvalidationKind.State);

            // ⚠ The one thing a restyle does *not* already tell a bridge. The claim that a computed
            // state reaches one "through the restyle it already causes" was false: a restyle is a
            // cascade invalidation and touches nothing accessible, so a consumer that re-read only
            // when told missed every tick of every checkbox in the document.
            if (((previous ^ value) & AnnouncedStates) != 0) {
                Document.InvalidateAccessibility();
            }
        }
    }

    /// <summary>The character that reaches it with Alt held, or <c>'\0'</c> for none.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>'\0'</c> rather than a nullable, because "no access key" is what every element in
    ///         the document has and a nullable would put a box around each of them. Compared
    ///         case-insensitively — Alt-S reaches an element whose key is <c>s</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Set explicitly; nothing infers one from a label.</b> <see cref="AccessKey.Parse" />
    ///         is the marker convention for a caller that wants <c>"_Save"</c> to mean both, and it
    ///         is opt-in — inferring would reinterpret every existing label that happens to contain
    ///         an underscore.
    ///     </para>
    /// </remarks>
    [UiProperty]
    public partial char AccessKey { get; set; }

    /// <summary>Whether the focus can rest on it.</summary>
    /// <remarks>
    ///     False by default, because most elements are boxes. A control sets it, and setting it is
    ///     what puts an element in the tab order — see <see cref="TabIndex" /> for the exception.
    /// </remarks>
    [UiProperty]
    public partial bool Focusable { get; set; }

    /// <summary>Where it comes in the tab order.</summary>
    /// <remarks>
    ///     <para>
    ///         HTML's rule, and it is stranger than it looks. <b>Zero</b> means "in the tab order, in
    ///         document order", which is what almost everything wants. <b>Negative</b> means
    ///         focusable by a click or by code but skipped by Tab — the escape hatch for a pane that
    ///         can hold focus without being a stop on the way round. <b>Positive</b> means "before
    ///         every zero, in numeric order", which is a foot-gun everyone who has used it regrets:
    ///         one element with <c>tabindex="1"</c> jumps to the front of a form it was written at
    ///         the bottom of.
    ///     </para>
    ///     <para>
    ///         Implemented faithfully rather than sanely, because a UI framework that quietly
    ///         reinterprets the rule produces a tab order nobody can predict from the markup.
    ///     </para>
    /// </remarks>
    [UiProperty]
    public partial int TabIndex { get; set; }

    /// <summary>Whether tab navigation stays inside it.</summary>
    /// <remarks>
    ///     What makes a dialog modal to the keyboard. Tab moves within the innermost scope that
    ///     contains the focus and wraps there rather than escaping into the window behind.
    /// </remarks>
    [UiProperty]
    public partial bool IsFocusScope { get; set; }

    /// <summary>Whether the focus is on it.</summary>
    public bool IsFocused => ReferenceEquals(Document.Focused, this);

    /// <summary>The text it draws, if any.</summary>
    /// <remarks>
    ///     <para>
    ///         An element with text measures itself from it, which is what
    ///         <see cref="OnTextChanged" /> arranges: the layout tree gets a measure function and
    ///         this element as its context, so flexbox asks the text how big it is rather than being
    ///         told.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Text belongs to an element rather than being a node of its own</b>, which is the
    ///         departure from the DOM. A text node buys mixed content — <c>hello &lt;b&gt;there&lt;/b&gt;</c>
    ///         as three children of one paragraph — and costs a node, a style and a layout box for
    ///         every word. Rich text is a run list inside one element instead, and
    ///         <see cref="TextLine" /> is that list: it already carries a face, a size, a tracking and
    ///         a leading per run, and already draws as a command each. What is owed is the other end
    ///         — the markup and the cascade that would say <i>which</i> stretch is bold. Per-character
    ///         font fallback is the first thing that builds several runs, and it is built.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An element with text cannot have children</b>, and the layout tree is what says
    ///         so: a node that measures itself and also has children would have its size decided
    ///         twice, by two rules that do not have to agree, so setting either on an element that
    ///         has the other throws. That is a real constraint rather than an oversight — mixed
    ///         content is exactly the thing the run list above is for — and it is worth knowing that
    ///         a text element is a leaf, full stop.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnTextChanged))]
    public partial string? Text { get; set; }

    /// <summary>Its text, shaped and wrapped to the width it is being laid out in.</summary>
    /// <remarks>
    ///     <para>
    ///         Goes through the document's shaping cache, so the measure pass and the draw pass shape
    ///         once between them rather than once each — and so do ten thousand list rows saying the
    ///         same word. The cache is keyed on the font and the string and not on the size, because
    ///         shaping is size-independent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The block itself is cached too, and that is what makes per-character fallback and
    ///         wrapping affordable.</b> Deciding which face draws which cluster asks the font about
    ///         every code point, which is a native call each — cheap once and ruinous twice a frame
    ///         per element. The cache is invalidated by comparing everything the answer depends on,
    ///         including the width and <see cref="FontRegistry.Revision" />: registering a face
    ///         changes what a declaration resolves to without changing anything on the element.
    ///     </para>
    /// </remarks>
    public TextLayout? Block() => Block(WrapWidth);

    /// <summary>Its text wrapped to a given width.</summary>
    /// <param name="width">How wide a line may be, in pixels, or infinity not to wrap.</param>
    /// <returns>The block, or null if it has no text or no font.</returns>
    public TextLayout? Block(float width) {
        if (string.IsNullOrEmpty(Text)) {
            block = null;
            lineText = null;
            return null;
        }

        var family = Document.FontFamilyOf(Style);
        var weight = Document.FontWeightOf(Style);
        var slant = Document.FontStyleOf(Style);
        var revision = Document.Fonts.Revision;
        var mode = Document.WrapModeOf(Style);
        var breaking = Document.WordBreakOf(Style);
        var transform = Document.TextTransformOf(Style);
        var clamp = Document.LineClampOf(Style);

        // ⚠ The count and not the pixels, because the pixels need a font and the font chain is
        // resolved below — but the count is what has to be in the key. Everything the count is
        // multiplied by (the family, the weight, the slant, the size, the font revision) is already
        // compared here, so keying on it keys on the stop.
        var tabSize = Document.TabSizeOf(Style);
        var hyphens = Document.HyphensOf(Style);
        var language = ResolvedLanguage;

        if (!Document.WrapsOf(Style)) {
            width = float.PositiveInfinity;
        }

        // ⚠ Before the cache key rather than after it, because a percentage indent is a function of
        // the width and the key already compares the width — but the key compares the *indent* too,
        // and comparing the computed one there would let a paragraph keep a block wrapped at the old
        // percentage of a width that has since changed. The used value is the thing the block was
        // built with, so the used value is what the key has to hold.
        var indent = UsedTextIndent(width);

        // Floats compared with Equals rather than ==, because `LineHeight` is NaN for "the font's own
        // recommendation" and NaN is not equal to itself — a line height nobody set would rebuild the
        // block every single call. It is also why the width compares that way: infinity is ordinary
        // here and NaN arrives from a layout pass that has not decided yet.
        if (block is not null
            && ReferenceEquals(lineText, Text)
            && string.Equals(lineFamily, family, StringComparison.Ordinal)
            && lineWeight == weight
            && lineStyle == slant
            && lineRevision == revision
            && lineMode == mode
            && lineBreaking == breaking

            // ⚠ In the key for the same reason `word-spacing` is: a case mapping changes how wide
            // the text is, so a block built before the transform arrived is a paragraph measured at
            // the wrong width and wrapped at the wrong characters. It is *not* covered by the
            // reference test on `Text` above — the element's own string did not change.
            && lineTransform == transform

            // ⚠ And the clamp, which is the one entry in this key that changes the block's *height*
            // rather than its width. A stale clamp is a paragraph that measured five lines and draws
            // three, so the box is two lines too tall and the gap looks like a margin nobody wrote.
            && lineClamp == clamp

            // ⚠ In the key for `word-spacing`'s reason and one of its own: a tab's advance is the
            // distance to the next stop, so moving the stops moves every wrap point after the first
            // tab on the line. A block built at the old spacing rewraps at the old columns and goes
            // on doing so until something else invalidates it.
            && lineTabSize.Equals(tabSize)

            // ⚠ In the key because it changes where the paragraph breaks *and* what the broken line
            // draws — the mode decides both whether a soft hyphen is an opportunity and whether the
            // line that took one ends in a visible hyphen. A stale mode is a paragraph split at a
            // word the author asked to keep whole.
            && lineHyphens == hyphens
            && lineWidth.Equals(width)
            && lineSize.Equals(FontSize)
            && lineTracking.Equals(LetterSpacing)

            // ⚠ In the key because it changes the *width* of a run, which is what decides where the
            // paragraph wraps. A property that reaches the shaping but not this test produces a
            // paragraph that redraws at the new spacing and keeps the old line breaks until
            // something else happens to invalidate it — a picture that is wrong only until it is
            // touched, which is the hardest kind to see reported.
            && lineWords.Equals(WordSpacing)
            && lineIndent.Equals(indent)
            && ReferenceEquals(lineFeatures, FontFeatures)
            && lineDirection == ParagraphDirection
            && string.Equals(lineLanguage, language, StringComparison.Ordinal)
            && lineLeading.Equals(LineHeight)) {
            return block;
        }

        var chain = new List<FontFace>();
        Document.Fonts.Chain(family, weight, slant, chain);

        if (chain.Count == 0) {
            return null;
        }

        // ⚠ <b>The transform happens here, before anything is shaped or measured.</b> `drawn.Text`
        // is what the runs, the wrapper and the ellipsis all work in; `drawn` itself is the map back
        // to what the author wrote, and every index this block hands out goes through it. When
        // nothing moved it *is* the element's own string, instance and all, so the shaping cache's
        // fast path and every reference test below carry on meaning what they meant.
        var drawn = TransformedText.Of(Text, transform);
        var text = drawn.Text;

        var lines = ImmutableArray.CreateBuilder<TextLine>();
        var tabStop = TabStop(text, tabSize, chain);
        var whole = Runs(text, 0, chain, drawn, offset: indent, tabStop: tabStop);

        // ⚠ The indent narrows the fast path's test as well as the wrapper's, and leaving it out is
        // the shape of bug that only shows on the one paragraph where it matters: a line that fits
        // in the box but not in the box *minus the indent* would take the unwrapped path, be shifted
        // right by the indent, and hang over the edge.
        if ((float.IsPositiveInfinity(width) || whole.Width <= width - indent) && !HasHardBreak(text)) {
            // ⚠ The unwrapped path is not an optimisation, it is the answer. A paragraph that fits
            // needs no break opportunities computed and no line re-shaped, and this is every label in
            // an interface.
            //
            // ⚠ **And it has to ask about hard breaks first**, which cost a failing test to notice. A
            // newline is a break because the text says so and not because the text is too wide, so a
            // fast path guarded on width alone draws "a\nb" on one line — with the newline shaped as
            // whatever glyph the font has for it — however wide the box is.
            lines.Add(whole);
        } else {
            Wrap(text, whole, width, mode, breaking, indent, chain, drawn, tabStop, hyphens, lines);
        }

        // ⚠ <b>The clamp drops the lines here, in the measure path, and this is where it differs from
        // every other truncation in this file.</b> An ellipsis is a fact about the picture — see
        // `Ellipsized`, which is why that one happens at paint. A clamp is a fact about the *height*:
        // a three-line block is three lines tall to its parent, so a budget applied after layout
        // would reserve room for lines that are never drawn and leave a hole under the text.
        //
        // ⚠ Only the *count* is applied here and not the marker. The lines that remain are still
        // whole substrings of the text, so `TextLine.Start`, the caret and the selection go on
        // meaning what they mean; the ellipsis on the last kept line is `Ellipsized`'s, put there at
        // paint like every other ellipsis, and `clamped` is what tells it to.
        clamped = clamp > 0 && lines.Count > clamp;

        if (clamped) {
            lines.Count = clamp;
        }

        block = new TextLayout(lines.ToImmutable());
        lineText = Text;
        lineTransform = transform;
        lineClamp = clamp;
        lineTabSize = tabSize;
        lineHyphens = hyphens;
        lineTabStop = tabStop;
        lineTransformed = drawn;
        lineFamily = family;
        lineWeight = weight;
        lineStyle = slant;
        lineRevision = revision;
        lineMode = mode;
        lineBreaking = breaking;
        lineWidth = width;
        lineSize = FontSize;
        lineTracking = LetterSpacing;
        lineWords = WordSpacing;
        lineIndent = indent;
        lineFeatures = FontFeatures;
        lineDirection = ParagraphDirection;
        lineLanguage = language;
        lineLeading = LineHeight;

        return block;
    }

    /// <summary>The character CSS names for the overflow marker, and the only one it allows.</summary>
    const string Ellipsis = "…";

    /// <summary>
    ///     Its text as it should be <i>drawn</i> in a box this wide: the same block as
    ///     <see cref="Block()" /> unless <c>text-overflow: ellipsis</c> is in force and a line does
    ///     not fit, in which case that line ends in an ellipsis.
    /// </summary>
    /// <param name="contentWidth">The content box to draw into, in pixels.</param>
    /// <returns>The block to draw, or null if there is no text or no font.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second block, and <see cref="Block()" /> is deliberately left alone.</b> That
    ///         one is what the caret and hit testing read — <c>TextField</c> and <c>CodeEditor</c>
    ///         both index into its lines — so a truncated block behind that name would put the caret
    ///         in the wrong character and break the sideways scrolling a single-line field depends
    ///         on. Truncation is a fact about the picture and not about the text, so it lives on a
    ///         path only the draw list takes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it happens here, at paint, because this is the first moment the number
    ///         exists.</b> Under <c>white-space: nowrap</c> — which <c>truncate</c> always sets —
    ///         <see cref="Block(float)" /> is handed an infinite width on purpose, so the wrap pass
    ///         cannot know what the line has to fit into. The box only gets its real width when its
    ///         parent shrinks it, which is after layout. Measuring is <i>supposed</i> to report the
    ///         untruncated width: that is what makes the parent shrink it in the first place, and an
    ///         ellipsis applied during measure would report a box that always fits and therefore
    ///         never needed one.
    ///     </para>
    /// </remarks>
    public TextLayout? Ellipsized(float contentWidth) {
        var source = Block();

        // ⚠ <b>A clamp implies the marker, which is what makes `line-clamp-3` one class rather than
        // two.</b> CSS says so — a `-webkit-line-clamp` ellipsises the last kept line without
        // `text-overflow` being written anywhere — and it is also the only reading that is any use:
        // three lines that simply stop are indistinguishable from three lines that were all there
        // was. `clamped` belongs to the block `Block()` has just returned, so it is read after it.
        if (source is null || !(Document.EllipsisOf(Style) || clamped)) {
            return source;
        }

        // Not `<= 0` — an infinite or undecided width is every bit as unusable as a negative one, and
        // NaN fails this test rather than passing it the way `> 0f` negated would.
        if (!(contentWidth > 0f) || float.IsPositiveInfinity(contentWidth)) {
            return source;
        }

        if (ellipsizedFor.Equals(contentWidth) && ReferenceEquals(ellipsizedFrom, source)) {
            return ellipsized;
        }

        var family = Document.FontFamilyOf(Style);
        var chain = new List<FontFace>();
        Document.Fonts.Chain(family, Document.FontWeightOf(Style), Document.FontStyleOf(Style), chain);

        if (chain.Count == 0) {
            return source;
        }

        var marker = Runs(Ellipsis, 0, chain);
        var lines = ImmutableArray.CreateBuilder<TextLine>(source.Lines.Length);
        var cut = false;

        // The block that is being truncated was built from this, so it is the string the line's
        // runs index — and the one the kept prefix has to be cut out of. `Block()` above is what
        // guarantees it is not stale.
        var drawn = lineTransformed ?? TransformedText.Of(Text, TextTransform.None);

        for (var i = 0; i < source.Lines.Length; i++) {
            var line = source.Lines[i];

            // ⚠ The last line of a clamped block is marked whether or not it fits, and that is the
            // whole difference between the two features. An ellipsis says "this line was too wide";
            // a clamp says "there was more text after this" — the line it lands on is a line that
            // fitted perfectly, and testing the width first would silently drop the marker on every
            // clamped paragraph whose last kept line happens to be short.
            var truncating = clamped && i == source.Lines.Length - 1;

            // The indent is part of what has to fit: an indented line whose glyphs are narrower than
            // the box can still run past its right-hand edge, and that is exactly the line an
            // ellipsis is for.
            if (!truncating && line.Offset + line.Width <= contentWidth) {
                lines.Add(line);
                continue;
            }

            lines.Add(Truncate(line, marker, contentWidth, chain, drawn));
            cut = true;
        }

        // The block is returned unchanged when nothing was cut, so the common case — a label that
        // fits, carrying the class for the day it does not — allocates one builder and no layout, and
        // the draw list goes on sharing the block every other reader already has.
        ellipsized = cut ? new TextLayout(lines.ToImmutable()) : source;
        ellipsizedFor = contentWidth;
        ellipsizedFrom = source;

        return ellipsized;
    }

    /// <summary>Replaces the tail of one line with an ellipsis so that what is left fits.</summary>
    /// <remarks>
    ///     ⚠ <b>The kept text and the ellipsis are shaped as <i>one</i> string.</b> Appending a
    ///     separately shaped marker would leave the shaper unable to kern across the join, and would
    ///     cut a cursive script mid-word without unjoining it — the same reason
    ///     <c>UiElement.Wrap</c> re-shapes each line instead of slicing the paragraph's shaping.
    /// </remarks>
    TextLine Truncate(
        TextLine line,
        TextLine marker,
        float contentWidth,
        List<FontFace> chain,
        TransformedText transformed
    ) {
        // ⚠ <b>The transformed text and transformed indices throughout.</b> `TextLine.Start` speaks
        // the element's own string, which is a different number the moment a case mapping expanded
        // anything — so the prefix would be cut a character short of where the glyphs actually end
        // and the ellipsis would eat a letter that fitted.
        var text = transformed.Text;
        var start = transformed.ToDrawn(line.Start);
        var end = transformed.ToDrawn(line.Start + line.Length);

        var advances = new float[text.Length + 1];

        foreach (var run in line.Runs) {
            var scale = run.Scale;
            var measured = LineWrapper.Advances(run.Shaped);

            for (var i = 0; i < measured.Length - 1 && run.Start + i < text.Length; i++) {
                advances[run.Start + i] = measured[i] * scale;
            }
        }

        var room = contentWidth - marker.Width - line.Offset;
        var boundaries = new List<int>();
        GraphemeBreaker.Collect(text.AsSpan(start, end - start), boundaries);

        // ⚠ <b>The stop `Block` measured with, and the pen it is measured from is the line's own
        // offset.</b> The tab stops are laid out from the line box's start edge, so a cut that summed
        // advances from zero would snap this line's tabs to columns half a stop away from the ones
        // the layout chose, and the ellipsis would land inside a column instead of after it.
        var stop = lineTabStop;
        var pen = line.Offset;

        var kept = start;

        if (room > 0f) {
            var width = pen;
            var at = start;

            foreach (var boundary in boundaries) {
                var here = start + boundary;

                if (here <= at || here > end) {
                    continue;
                }

                for (var i = at; i < here; i++) {
                    // The snap rule of `TextLine.NextStop` and `LineWrapper.Width`: strictly the next
                    // stop, so two tabs in a row are two columns.
                    width = text[i] == '\t'
                        ? stop > 0f ? (MathF.Floor(width / stop) + 1f) * stop : width
                        : width + advances[i];
                }

                if (width - pen > room) {
                    break;
                }

                kept = here;
                at = here;
            }
        }

        // ⚠ The marker alone when not one cluster fits, rather than nothing. A box too narrow for a
        // single character still has to say that something was elided; drawing an empty line would be
        // indistinguishable from an element with no text, and the clip will trim the glyph if even it
        // does not fit.
        if (kept <= start) {
            return marker.Runs.Length == 0
                ? line
                : Runs(Ellipsis, start, chain, transformed, offset: line.Offset, tabStop: stop);
        }

        // ⚠ Trailing space is trimmed before the ellipsis, the way a browser does it: `"ab  "` cut at
        // the space reads as `"ab …"` with a hole in it otherwise.
        var last = kept;

        while (last > start && char.IsWhiteSpace(text[last - 1])) {
            last--;
        }

        var body = last > start ? text[start..last] : text[start..kept];

        // ⚠ The map is carried so that this line's `Start` still speaks the element's own string
        // like every other line's — but only its start is meaningful. The string handed to the
        // shaper is a *prefix plus a marker*, so an index inside the marker maps to nothing the
        // author wrote. That is sound because a truncated line is a fact about the picture and
        // nothing puts a caret on one: see `Ellipsized`'s remarks on why `Block()` is left alone.
        return Runs(body + Ellipsis, start, chain, transformed, offset: line.Offset, tabStop: stop);
    }

    /// <summary>The width this element wraps its text to, from the layout it was last given.</summary>
    /// <remarks>
    ///     ⚠ <b>The content box of the <i>last</i> pass.</b> Drawing happens after layout, so this is
    ///     the width the text ended up in — which is the right one to draw at and is not always the
    ///     one the measure pass was offered. Where a parent stretched the element past what it asked
    ///     for, the text re-wraps to fewer lines than the height reserved for it, and the difference
    ///     shows as space at the bottom rather than as clipped text.
    /// </remarks>
    float WrapWidth {
        get {
            // ⚠ No `white-space` test here, and an earlier draft had one. `Block(float)` already
            // refuses to wrap when the style says not to, and a second guard that agreed with it read
            // as a rule while being insurance — sabotaging either one alone failed no test, which is
            // exactly how a duplicated condition hides.
            var content = Width
                - Document.Layout.GetComputedBorder(LayoutNode, Edge.Left)
                - Document.Layout.GetComputedBorder(LayoutNode, Edge.Right)
                - Document.Layout.GetComputedPadding(LayoutNode, Edge.Left)
                - Document.Layout.GetComputedPadding(LayoutNode, Edge.Right);

            return content > 0f ? content : float.PositiveInfinity;
        }
    }

    /// <summary>Whether the text carries a break that is not about how wide the box is.</summary>
    /// <remarks>
    ///     The characters UAX#14 gives class BK, CR, LF or NL. Scanned here rather than asked of
    ///     <c>LineBreaker</c> because the question is "is there one at all", and the answer is no for
    ///     every label in an interface — which is the case the fast path above exists for.
    /// </remarks>
    static bool HasHardBreak(string text) => text.AsSpan().IndexOfAny(HardBreaks) >= 0;

    static readonly System.Buffers.SearchValues<char> HardBreaks =
        System.Buffers.SearchValues.Create("\n\r\f\v\u0085\u2028\u2029");

    /// <summary>Shapes one stretch of the text into the runs its faces and its levels need.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two things cut a line into runs, and a run has to be a stretch over which both are
    ///         constant.</b> <see cref="FontRegistry.Cover" /> cuts where the face changes; UAX#9 cuts
    ///         where the embedding level changes. A run gets one shaping call and one position on the
    ///         line, so it cannot straddle either — and <see cref="TextLine" /> reorders whole runs by
    ///         L2, which is sound only because each of them has a single level.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cutting on coverage alone is the bug this shape exists to prevent, and it renders
    ///         plausibly.</b> Runs laid down in logical order reorder within each face and not across
    ///         the boundary between them, so a line whose Arabic and whose Latin come from different
    ///         files draws both words correctly, at the right total width, in the wrong order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And cutting on level is not the same as cutting on script</b>, which is why the
    ///         itemiser's items are merged back together where only their script differs. The shaper
    ///         re-itemises by script internally and does it with the whole string in the buffer, which
    ///         is how an Arabic letter finds out whether its neighbour joins; splitting here would
    ///         hand it substrings and take that context away. Level boundaries are safe to split at
    ///         because a level change is a change of strong direction, and no script joins across one.
    ///     </para>
    /// </remarks>
    TextLine Runs(
        string text,
        int start,
        List<FontFace> chain,
        TransformedText? transformed = null,
        float width = float.NaN,
        float offset = 0f,
        float tabStop = 0f
    ) {
        var spans = new List<FontSpan>();
        FontRegistry.Cover(text, chain, spans);

        var levels = Levels(text);
        var runs = ImmutableArray.CreateBuilder<TextRun>(spans.Count);

        foreach (var span in spans) {
            foreach (var level in levels) {
                var from = Math.Max(span.Start, level.Start);
                var to = Math.Min(span.Start + span.Length, level.Start + level.Length);

                if (from >= to) {
                    continue;
                }

                // ⚠ <b>A third cut, and it is the only one that is not about the characters being
                // shapeable together.</b> A tab's width is the distance to the next stop, which is a
                // fact about where it *sits* — see `TextRun.IsTab` — so it has to be alone in a run
                // for the line to be able to give it one. Nothing is lost by cutting there: no
                // shaper joins across a tab, and CSS Text 3 makes it a space rather than a glyph.
                //
                // ⚠ Only when there is a tab, so every label in an interface takes the loop it took
                // before — one shaping call for the whole string, hitting the cache entry it already
                // had rather than one per fragment.
                foreach (var (cut, end) in Segments(text, from, to)) {
                    // ⚠ The whole string when one face and one level cover it, and a substring only
                    // otherwise. Not a micro-optimisation: `text[0..Length]` is a fresh string every
                    // call, and the shaping cache keys on the string's contents, so it would hash and
                    // compare the whole label to find the entry it already had. This is every label in
                    // an interface, and it is the same fast path the coverage-only version had.
                    var piece = cut == 0 && end == text.Length ? text : text[cut..end];

                    // ⚠ The level's own direction, not the paragraph's. The piece has one level
                    // throughout, so which way it is drawn is decided — handing the shaper the
                    // paragraph's `Auto` would make it guess again from the piece's first strong
                    // character, and a piece of neutrals between two Arabic words would guess wrong.
                    var direction = (level.Level & 1) != 0
                        ? ParagraphDirection.RightToLeft
                        : ParagraphDirection.LeftToRight;

                    runs.Add(
                        new TextRun(
                            span.Font,
                            Document.Shaping.Shape(
                                span.Font,
                                piece,
                                direction,
                                features: FontFeatures,
                                language: ResolvedLanguage
                            ),
                            FontSize,
                            LetterSpacing,
                            LineHeight,
                            start + cut,
                            level.Level,
                            WordSpacing
                        )
                    );
                }
            }
        }

        return new TextLine(runs.ToImmutable(), width, offset, transformed, tabStop);
    }

    /// <summary>A line, with a soft hyphen it ends on replaced by one that draws.</summary>
    /// <param name="line">The line's own text, cut out of the paragraph.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The visible half of <c>hyphens: manual</c>, and its absence was a defect rather
    ///         than a gap.</b> <see cref="LineBreaker" /> has always offered a break after U+00AD —
    ///         <c>"sup­ply"</c> and <c>"sup-ply"</c> return the identical opportunity list — so
    ///         Vixen already broke <c>sup|ply</c>. It then drew no hyphen at all, because U+00AD is
    ///         <c>Default_Ignorable</c> and <c>TextShaper</c> sets
    ///         <c>BufferFlags.RemoveDefaultIgnorables</c>: seven characters shaped to six glyphs
    ///         <i>even though the face has the glyph</i>. A word split with nothing to show for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>U+002D and not U+2010, which is what the sizing for this said and is measurably
    ///         wrong.</b> <c>FontFace.GlyphFor(0x2010)</c> is <b>0</b> — <c>.notdef</c> — in Open
    ///         Sans and in <c>TestShapeLana</c> alike. What glyph 0 <i>draws</i> then differs by
    ///         face, and both outcomes are bad: <c>TestShapeLana</c>'s has two contours and is the
    ///         familiar hollow box, while Open Sans' has <b>zero</b> and draws nothing at all. So the
    ///         prescribed substitution either shows a tofu box at every hyphenation point or, in the
    ///         engine's own interface face, silently reproduces the defect it was meant to fix — and
    ///         the silent one is the worse, because it looks like the change never took. U+002D is
    ///         glyph 16 in both, and is what a browser draws here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One character for one, which is the property that makes this safe.</b> Every
    ///         index the block hands out — <c>TextLine.Start</c>, the caret, the selection — is an
    ///         index into the element's own string, and a substitution of equal length moves none of
    ///         them. Appending a hyphen instead would move all of them by one from here to the end of
    ///         the paragraph.
    ///     </para>
    ///     <para>
    ///         Only the <i>last</i> character, because that is the only soft hyphen a line ending
    ///         proves anything about. One in the middle of a line was not used as a break and must go
    ///         on drawing nothing, which is what the shaper already does with it.
    ///     </para>
    /// </remarks>
    static string Hyphenated(string line) =>
        line.Length > 0 && line[^1] == '­' ? string.Concat(line.AsSpan(0, line.Length - 1), "-") : line;

    /// <summary>What the hyphen a broken line draws costs, in pixels.</summary>
    /// <param name="chain">The resolved font chain, whose first face draws it.</param>
    /// <remarks>
    ///     ⚠ <b>U+002D and not U+00AD, because U+00AD measures zero — the shaper deletes it.</b>
    ///     Measuring the character the author wrote would tell the wrapper the hyphen is free, which
    ///     is precisely the wrong answer and the one this code had before the measurement existed:
    ///     the paragraph broke as though the hyphen cost nothing and then drew it, so a hyphenated
    ///     line overflowed its box by exactly one hyphen. What has to be measured is the character
    ///     <see cref="Hyphenated" /> substitutes.
    /// </remarks>
    float HyphenWidth(List<FontFace> chain) {
        if (chain.Count == 0) {
            return 0f;
        }

        var font = chain[0];
        var shaped = Document.Shaping.Shape(font, "-", ParagraphDirection.LeftToRight, features: FontFeatures);

        return shaped.Advance * (FontSize / font.UnitsPerEm);
    }

    /// <summary>How far apart this element's tab stops are, in pixels, or zero for no tab.</summary>
    /// <param name="text">The text as it will be drawn, after any case mapping.</param>
    /// <param name="tabSize">How many spaces wide a stop is. <c>UiDocument.TabSizeOf</c>.</param>
    /// <param name="chain">The resolved font chain, whose first face owns the space that is counted.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero whenever the text has no tab in it, which is free rather than a special
    ///         case.</b> Zero is a real stop distance downstream — a tab at it occupies nothing — and
    ///         a string with no tab has none to occupy anything, so the two answers coincide. What
    ///         the early return buys is the substring and the shaping call per fragment in
    ///         <c>Segments</c>, which is the expensive half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not a sentinel, and it used to be.</b> Reading a non-positive stop as
    ///         "measure the tab as a glyph" made <c>tab-size: 0</c> indistinguishable from "no tabs
    ///         here" and gave the first of them the width of a .notdef box that
    ///         <see cref="TextRun.Place" /> then refused to draw — invisible width, which is the
    ///         worse of the two failures it sat between.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The space of the chain's <i>first</i> face, not of whichever face covers the tab.</b>
    ///         CSS Text 3 § 6.1 counts advances of "the element's font", and a fallback is not that —
    ///         it is a face the element never asked for. Counting per-run would also make a tab stop
    ///         depend on the script beside it, so the columns of a paragraph that fell back once would
    ///         stop lining up with the columns of one that did not.
    ///     </para>
    ///     <para>
    ///         The shaping goes through the same cache every other string does, so the one-space
    ///         measurement is a dictionary hit after the first paragraph in a given face and size.
    ///     </para>
    /// </remarks>
    float TabStop(string text, float tabSize, List<FontFace> chain) {
        if (tabSize <= 0f || chain.Count == 0 || !text.Contains('\t')) {
            return 0f;
        }

        var font = chain[0];
        var space = Document.Shaping.Shape(font, " ", ParagraphDirection.LeftToRight, features: FontFeatures);

        return tabSize * space.Advance * (FontSize / font.UnitsPerEm);
    }

    /// <summary>A range, cut so that every tab in it is a piece of its own.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One entry — the range itself — whenever there is no tab</b>, which is every
    ///         string in an interface. The iterator allocates either way; what the fast path saves is
    ///         the substring and the shaping call per fragment, which is the expensive half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cut does not depend on how wide the stops are, and reading it that way was a
    ///         defect.</b> A version of this skipped the split when the stop was zero, on the
    ///         reasoning that a zero stop meant "no tabs to place" — so under <c>tab-size: 0</c> the
    ///         tab stayed inside its neighbours' run, <see cref="TextRun.IsTab" /> was false for it,
    ///         and it was measured as the .notdef glyph the face maps U+0009 to. What makes a tab
    ///         need its own run is that it *is* a tab, not that the stops happen to be wide.
    ///     </para>
    /// </remarks>
    static IEnumerable<(int Start, int End)> Segments(string text, int from, int to) {
        if (text.AsSpan(from, to - from).IndexOf('\t') < 0) {
            yield return (from, to);
            yield break;
        }

        var at = from;

        for (var i = from; i < to; i++) {
            if (text[i] != '\t') {
                continue;
            }

            if (i > at) {
                yield return (at, i);
            }

            yield return (i, i + 1);
            at = i + 1;
        }

        if (at < to) {
            yield return (at, to);
        }
    }

    /// <summary>The text's bidi levels, as the longest stretches over which one level holds.</summary>
    /// <remarks>
    ///     <see cref="TextItemizer.Itemize" /> cuts on script as well as on level, and only the level
    ///     half is a boundary a <see cref="TextRun" /> has to respect — so its items are merged back
    ///     wherever two adjacent ones agree about the level. See the remarks on <see cref="Runs" />
    ///     for why giving the shaper the longest run it can have is not tidiness.
    /// </remarks>
    List<TextItem> Levels(string text) {
        var items = TextItemizer.Itemize(text, ParagraphDirection);
        var merged = new List<TextItem>(items.Count);

        // One level over the whole string is what almost every label is, and this collapses it to a
        // single entry — which is what lets `Runs` hand the shaper the string it was given rather
        // than a substring of it.
        foreach (var item in items) {
            if (merged.Count > 0 && merged[^1].Level == item.Level) {
                merged[^1] = merged[^1] with { Length = merged[^1].Length + item.Length };
                continue;
            }

            merged.Add(item);
        }

        return merged;
    }

    /// <summary>Breaks the text into lines and shapes each of them.</summary>
    /// <remarks>
    ///     ⚠ <b>The advances are built in pixels across the runs, which is the whole reason wrapping
    ///     lives here.</b> A line mixing a Latin face and a fallback has no single design-unit scale,
    ///     so <c>LineWrapper</c>'s <c>ShapedText</c> overload cannot measure it — the per-character
    ///     array is assembled a run at a time, each scaled by its own font, and handed to the overload
    ///     that takes one.
    /// </remarks>
    void Wrap(
        string text,
        TextLine whole,
        float width,
        TextWrapMode mode,
        WordBreakMode breaking,
        float indent,
        List<FontFace> chain,
        TransformedText transformed,
        float tabStop,
        HyphenMode hyphens,
        ImmutableArray<TextLine>.Builder into
    ) {
        var advances = new float[text.Length + 1];

        foreach (var run in whole.Runs) {
            var scale = run.Scale;
            var measured = LineWrapper.Advances(run.Shaped);

            for (var i = 0; i < measured.Length - 1 && run.Start + i < text.Length; i++) {
                advances[run.Start + i] = measured[i] * scale;
            }
        }

        var wrapped = new List<WrappedLine>();
        // ⚠ What the hyphen this paragraph might draw would cost, so the wrapper can break with it
        // in hand. Measured only when there is a soft hyphen to pay for, in the chain's first face
        // for `TabStop`'s reason — a fallback is a face the element never asked for, and the hyphen
        // that gets drawn comes from the run the substitution lands in, which is the same face the
        // letters before it are in.
        var hyphen = hyphens == HyphenMode.Manual && text.Contains('­') ? HyphenWidth(chain) : 0f;

        LineWrapper.Wrap(text, advances, width, wrapped, mode, breaking, indent, tabStop, hyphens, hyphen);

        foreach (var line in wrapped) {
            // ⚠ Each line is shaped as its own string rather than sliced out of the paragraph's
            // shaping. That is what a line break *is* — a ligature does not cross one and an Arabic
            // word unjoins at one — and slicing would also need a run split in the middle of a
            // cluster, which has no meaning.
            // ⚠ The wrapper's own width, not the re-shaped line's. It excludes the whitespace at the
            // line's end, which is drawn but must not be measured — and since the advances handed to
            // it were in pixels, the number it gives back already is too.
            // ⚠ The indent lands on the first line and on no other, which is what CSS Text 3 § 8.1
            // says and is also the only reading the wrapper's own arithmetic supports: it is the
            // first line that was measured against the narrower width.
            into.Add(
                Runs(
                    // ⚠ <b>Only a line the wrapper actually broke, which `line.End < text.Length`
                    // is exactly.</b> A soft hyphen the paragraph merely *ends* on was not used as
                    // a break and must go on drawing nothing — `"sup­"` in a wide box is one line
                    // reading `sup`, not `sup-`. The test is on the paragraph rather than on the
                    // mode because it is also what makes the last line right under `hyphens: none`,
                    // where the opportunity was suppressed but the character is still sitting there.
                    line.End < text.Length
                        ? Hyphenated(text.Substring(line.Start, line.Length))
                        : text.Substring(line.Start, line.Length),
                    line.Start,
                    chain,
                    transformed,
                    line.Advance,
                    line.Start == 0 ? indent : 0f,
                    tabStop
                )
            );
        }

        if (into.Count == 0) {
            into.Add(whole);
        }
    }

    TextLayout? block;

    // The truncated picture, and what it was truncated from. Keyed on the block's identity rather
    // than on the ten fields `Block` compares: a new block is a new object, so one reference test
    // stands in for every reason the text might have been laid out again.
    TextLayout? ellipsized;
    TextLayout? ellipsizedFrom;
    float ellipsizedFor = float.NaN;
    string? lineText;
    string? lineFamily;
    int lineWeight;
    FontStyle lineStyle;
    int lineRevision;
    TextWrapMode lineMode;
    WordBreakMode lineBreaking;
    float lineWidth;
    float lineSize;
    float lineTracking;
    float lineWords;
    float lineIndent;
    TextTransform lineTransform;
    int lineClamp;
    float lineTabSize;
    HyphenMode lineHyphens;

    // ⚠ The stop the current `block` was measured with, in pixels, kept for the same reason
    // `lineTransformed` is: `Ellipsized` measures the line it is cutting, and measuring it with a
    // different stop than `Block` used would put the ellipsis at a column the layout never chose.
    float lineTabStop;

    // Whether `Block` dropped lines to honour `-webkit-line-clamp`, which is what tells `Ellipsized`
    // to mark the last one even though it fits. Belongs to the current `block` and is rewritten
    // whenever that is.
    bool clamped;

    // ⚠ The transformed text the current `block` was built from, kept so that `Ellipsized` cuts the
    // string the runs were actually shaped from. Rebuilding it there instead would be a second
    // place the transform is applied, and the two would agree until one of them was changed.
    TransformedText? lineTransformed;

    // ⚠ Reference equality, not `Equals`, and it is sound because `ResolveText` produces one
    // instance per style pass and hands the same one to every element that resolved alike. Two
    // equal sets built in different passes compare unequal here, which costs one rebuild of a
    // block whose features did not change — and never the other way round, which is the direction
    // that would draw stale glyphs.
    FontFeatureSet? lineFeatures;

    // ⚠ Nullable, so that the first `Block()` of an element's life is always a miss. The enum's
    // default is `Auto`, which is also the commonest resolved value — so a non-nullable field would
    // read as "already shaped that way" against a block that had never been built.
    ParagraphDirection? lineDirection;
    float lineLeading;

    /// <summary>The language the block was shaped in, so that changing it reshapes.</summary>
    /// <remarks>
    ///     ⚠ In the key for the reason `lineWords` states one paragraph up: the language reaches the
    ///     shaper, so it changes the *width* of a run, so it changes where the paragraph wraps. A
    ///     property that reaches the shaping and not this test gives a block that redraws at the new
    ///     language and keeps the old line breaks until something else invalidates it.
    /// </remarks>
    string? lineLanguage;

    void OnTextChanged(string? previous, string? current) {
        // ⚠ The measure function is attached and detached rather than left in place answering zero.
        // The layout algorithm asks a node with one whether it is a leaf and refuses to lay out its
        // children — so an element that once had text and now has none would silently stop laying
        // out everything inside it.
        if (string.IsNullOrEmpty(previous) != string.IsNullOrEmpty(current)) {
            Document.Layout.SetContext(LayoutNode, string.IsNullOrEmpty(current) ? null : this);
            Document.Layout.SetMeasureFunction(LayoutNode, string.IsNullOrEmpty(current) ? null : TextLayout.Measure);
        } else if (!string.IsNullOrEmpty(current)) {
            // Attaching or detaching the measure function already dirties the node, so the only case
            // left is one string becoming another — and the layout tree refuses a hand-dirtied node
            // that does not measure itself, on the grounds that nothing else about it can have
            // changed without a style or a child changing. ⚠ Which makes the emptiness test
            // load-bearing rather than tidy: null and "" are both "no text", so setting one to the
            // other reaches here with no measure function attached and throws.
            Document.Layout.MarkDirty(LayoutNode);
        }

        // The cascade's half of the same fact, and what makes `:empty` mean what CSS means by it —
        // an element with words in it is not empty, however few children it has.
        Document.Styles.Tree.SetHasText(StyleNode, !string.IsNullOrEmpty(current));

        // ⚠ **Only the emptiness is a cold pass, and the remark this replaced said the whole
        // assignment was.** `HasTextAt` has exactly one reader — `SelectorMatcher`'s `:empty` — so
        // one string becoming another string changes nothing any selector in the engine can test,
        // and the pass it used to buy re-cascaded the document to arrive at the styles it already
        // had. That is the cost #598 attributed to row realisation: a scrolled `DataGrid` assigns
        // one cell label per visible cell, seventy-two of them on the editor-shell fixture, and
        // every one of those came through `Invalidate`. Crossing between text and no text can move
        // a `:empty` verdict on this element and on anything selecting off it, so that half stays
        // conservative.
        if (string.IsNullOrEmpty(previous) != string.IsNullOrEmpty(current)) {
            Document.Invalidate();
        } else {
            Document.InvalidatePositions();
        }
    }

    /// <summary>Builds whatever this element is made of, once, as it joins a document.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The constructor a control cannot have.</b> A switch is a track and a knob, a scroll
    ///         view is a viewport and two bars — parts made of elements, and an element can only be
    ///         made by a document. <see cref="UiDocument.Create{T}" /> is what binds this one to
    ///         hers, so anything that needs children has to wait until after that, and this is
    ///         immediately after: bound, attached, and not yet returned to the caller.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Once, and only from <see cref="UiDocument.Create{T}" />.</b> An element that has
    ///         been removed and is being reused does not get a second one — there is no such
    ///         element, because removal is final. That makes this the right place to attach handlers
    ///         and the wrong place to read anything about layout, which has not happened yet: every
    ///         box is zero here and will be until the next <see cref="UiDocument.Update" />.
    ///     </para>
    ///     <para>
    ///         An override must call its base, in the usual direction — a derived control's parts
    ///         belong after the ones it inherited, in painting order and in the tab order both.
    ///     </para>
    /// </remarks>
    protected internal virtual void OnCreated() {
    }

    /// <summary>Called on the parent, once a child of it has been created and initialised.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What lets a container be written as nested tags.</b> A control whose contents are a
    ///         <i>set</i> rather than a subtree — a menu's items, a radio group's choices, a
    ///         breadcrumb's steps — has always had an <c>AddItem</c>-shaped method, because adding
    ///         the element is only half of what arriving means: the other half is being registered,
    ///         numbered, restated or wired. Markup has no way to call a method, so before this
    ///         existed a nested tag drew and did nothing at all — no diagnostic, no exception, a
    ///         choice with no value and no exclusivity.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the rule for a container is: do the registering <i>here</i>, and let the
    ///         <c>AddX</c> method be sugar over <c>Add&lt;T&gt;()</c> and a property or two.</b> Both
    ///         routes then arrive at the same state by the same code, which is the only arrangement
    ///         where markup and C# cannot disagree — and a container that registers in its method
    ///         instead has an <c>AddX</c> that works and a tag that silently does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>After the child's <see cref="OnCreated" />, which is why it is not
    ///         <c>Attach</c>.</b> A control builds its parts in <c>OnCreated</c>, so a parent told
    ///         any earlier gets a <c>MenuItem</c> with no label element and a
    ///         <c>Select</c> with no popover — and the first thing a registrar does is read one of
    ///         those.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Creation only, and never a reparent.</b> Docking moves panels between groups
    ///         several times per drag and a select's popover is built under the root; a hook that
    ///         also fired on <see cref="UiDocument.Reparent" /> would register the same child once
    ///         per move. What a container needs to know about a move it already knows, because it is
    ///         the thing doing the moving.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The child is a child of <c>this</c>, not of <see cref="ContentHost" />.</b> Those
    ///         differ for every control that has parts: a <c>&lt;Card&gt;</c>'s nested tag lands in
    ///         its body, so it is the <i>body</i> that hears about it. A container that routes its
    ///         children elsewhere and registers them here has to override this on whatever it routed
    ///         them to — see <c>SelectBase</c>, whose options live in a popover at the root.
    ///     </para>
    ///     <para>
    ///         An override must call its base.
    ///     </para>
    /// </remarks>
    /// <param name="child">The child that arrived.</param>
    protected internal virtual void OnChildAdded(UiElement child) {
    }

    /// <summary>Called once, as the element leaves the document.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The other end of <see cref="OnCreated" />, and what an overlay needs to exist.</b> A
    ///         menu, a select's popover, a dialog and a tooltip all parent their popup on the
    ///         <i>root</i>, because painting order is document order and a popup inside the control
    ///         that opened it is clipped by every <c>overflow: hidden</c> between the two. That is the
    ///         right structure and it leaves the popup with no way to hear that its owner is gone: the
    ///         two are not related, so removing a panel full of menus left their popups in the
    ///         document for ever, still styled, still hit-testable, still drawn the moment anything
    ///         opened them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Called top-down, before anything is detached, and that order is deliberate.</b> An
    ///         override's whole job is to reach things — the popup it parented elsewhere, a
    ///         subscription on an ancestor — and both are unreachable once the subtree is out of the
    ///         document. The alternative, calling it after the stores are cleaned, hands every
    ///         implementer an element that throws on almost every question.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An override may remove other elements and must not remove this one.</b> Removing
    ///         a popup from inside this is the case it exists for and is safe: the popup is elsewhere
    ///         in the tree. Removing an ancestor of the subtree already being removed is not, and is
    ///         refused by <see cref="UiDocument.Remove" /> rather than left to corrupt the walk.
    ///     </para>
    ///     <para>
    ///         An override must call its base.
    ///     </para>
    /// </remarks>
    protected internal virtual void OnRemoved() {
    }

    /// <summary>Raised after any generated UI property changes.</summary>
    /// <remarks>
    ///     ⚠ Overriding this is how a subclass reacts to a property it did not declare — the
    ///     per-property <c>Changed</c> callback is for the type that owns the property, and a base
    ///     class needs to hear about its derived types' properties without knowing them. Called
    ///     only when the value actually differs, so a setter that writes the same value twice is
    ///     silent.
    /// </remarks>
    /// <param name="key">Which property changed.</param>
    protected internal virtual void OnPropertyChanged(UiPropertyKey key) {
    }

    /// <summary>Raised after any generated UI property changes.</summary>
    /// <remarks>
    ///     The outside world's version of <see cref="OnPropertyChanged" />. The override is for a
    ///     type reacting to its own tree; this is for something that is not the element at all — a
    ///     two-way binding, an inspector, a recorder — and neither can be expressed as the other.
    /// </remarks>
    public event Action<UiElement, UiPropertyKey>? PropertyChanged;

    /// <summary>Tells the override and the subscribers that a property changed.</summary>
    /// <param name="key">Which property.</param>
    /// <remarks>
    ///     ⚠ What the generated setter calls, rather than <see cref="OnPropertyChanged" /> directly.
    ///     Routing both through one non-virtual method is what stops an override that forgets to
    ///     call its base from silently unsubscribing every two-way binding on the element — a bug
    ///     that would show up as a text box that stops writing back, in a type that never mentioned
    ///     binding.
    /// </remarks>
    protected void RaisePropertyChanged(UiPropertyKey key) {
        OnPropertyChanged(key);
        PropertyChanged?.Invoke(this, key);
    }

    /// <summary>Draws whatever this element is, beyond what a stylesheet can describe.</summary>
    /// <param name="context">What to draw with.</param>
    /// <remarks>
    ///     <para>
    ///         The escape hatch out of the declarative side. A stylesheet describes boxes and most of
    ///         an interface is boxes; a chart, a sparkline, a knob and a hand-drawn icon are not, and
    ///         there is no property for those. Overriding this is how a control draws itself.
    ///     </para>
    ///     <para>
    ///         Called after the element's background, border and text and before its children, which
    ///         is where CSS puts an element's own content — so custom drawing sits over the
    ///         background it was given and under anything nested inside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read-only as far as the tree is concerned.</b> This runs in the middle of a walk
    ///         that is emitting commands in painting order, so changing a style or adding an element
    ///         from here changes what is being walked. Nothing stops it and nothing can, short of a
    ///         mode flag on the whole document; said plainly instead.
    ///     </para>
    /// </remarks>
    protected internal virtual void OnDraw(DrawContext context) {
    }

    /// <summary>How far this element and everything inside it is shifted from where layout put it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A translation applied after layout rather than a position given to it</b>, and the
    ///         difference is the whole reason it exists. Scrolling a list, sliding a drawer in,
    ///         dragging a preview under the cursor and putting a popup beside its anchor are all
    ///         "the same boxes, somewhere else" — and expressing them as layout would mean a cascade
    ///         and a flexbox pass per frame for something that has not changed shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It moves the element, not the space it occupies.</b> Siblings do not move out of
    ///         the way and the parent does not grow, which is what makes it right for an overlay and
    ///         wrong for anything that is meant to take up room — the same bargain as CSS's
    ///         <c>transform: translate</c>, which is what this is.
    ///     </para>
    ///     <para>
    ///         Hit testing and drawing both read the accumulated position, so a shifted element is
    ///         clicked where it is drawn without either of them knowing this exists.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnOffsetChanged))]
    public partial float OffsetX { get; set; }

    /// <summary>Ditto, vertically.</summary>
    [UiProperty(Changed = nameof(OnOffsetChanged))]
    public partial float OffsetY { get; set; }

    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Asks for a pass without asking for a restyle</b>, because an offset cannot change
    ///         what any selector matches. <see cref="UiDocument.Update" /> is what recomputes absolute
    ///         positions, so a pass is what has to be asked for; the cascade has nothing to do in it,
    ///         and no layout node is dirty either, so flexbox returns without measuring. A scroll is
    ///         two walks of the tree and no work.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That last sentence was here before and was not true.</b> It described
    ///         <c>Apply</c>, whose reference comparison does skip every element — while the pass above
    ///         it re-cascaded the document, because a plain <c>Invalidate</c> was the only way to ask
    ///         for anything. Measured on a themed document of 8 001 elements, that was 9.5 ms and
    ///         8.9 MB per frame of a scroll. See <c>UiDocument.InvalidatePositions</c>.
    ///     </para>
    /// </remarks>
    void OnOffsetChanged(float previous, float current) => Document.InvalidatePositions();

    /// <summary>Its left edge in document space, after the last layout pass.</summary>
    public float AbsoluteLeft { get; internal set; }

    /// <summary>Its top edge in document space.</summary>
    public float AbsoluteTop { get; internal set; }

    /// <summary>Where it is in document space, after the last layout pass.</summary>
    /// <remarks>
    ///     ⚠ <b>Its untransformed box, and that stays true under <see cref="Transform" />.</b> A
    ///     rotated element is not a rectangle, so there is no honest rectangle this could return for
    ///     one; what it returns is the box layout gave it, which is what every existing caller — arrow
    ///     navigation, scroll-into-view, the editor's overlays — actually wants. A caller that needs
    ///     the painted extent asks <see cref="UiTransform.Bounds" /> for it and gets a bound rather
    ///     than a box, which is the distinction worth making the caller state.
    /// </remarks>
    public Rectangle Bounds => new(AbsoluteLeft, AbsoluteTop, Width, Height);

    /// <summary>The affine its <c>rotate</c> and <c>scale</c> paint it under, or null for neither.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Paint and hit testing only — layout has never seen it and must not.</b> CSS
    ///         Transforms 1 §3 applies a transform after layout, so this element still occupies
    ///         <see cref="Bounds" />, its siblings do not move for it, and its parent does not resize
    ///         around it. <c>UiDocument.Accumulate</c> composes it and deliberately does not pass it
    ///         down: the children accumulate from the untransformed position and are carried along by
    ///         this element's composited group instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null and not <see cref="UiTransform.Identity" />, and the distinction is a
    ///         viewport-sized surface.</b> A non-null value is what makes
    ///         <c>DrawListBuilder</c> open a composited group for the subtree, which costs a surface
    ///         and a render pass; <c>rotate: 0deg</c> is written far too often for that to be spent on
    ///         a picture that is identical either way. <see cref="TransformReader" /> collapses an
    ///         identity composition back to null for exactly that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Six floats on every element, which is a cost this class does not normally pay.</b>
    ///         The alternative is for the hit test to re-read the two properties off the computed style
    ///         on every element of every pointer move — this is a nullable struct precisely so the
    ///         common answer is a null check rather than two dictionary lookups. It is stored rather
    ///         than recomputed for the same reason <see cref="AbsoluteLeft" /> is.
    ///     </para>
    /// </remarks>
    public UiTransform? Transform { get; internal set; }

    /// <summary>
    ///     Whether a pointer can land on it. <c>pointer-events: none</c> and
    ///     <c>visibility: hidden</c> each make it false.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read from the computed style rather than stored, because it is a stylesheet's decision
    ///         and a stylesheet can change it between frames. An element that is not hit-testable does
    ///         not stop its children from being — that is what CSS says, and it is what makes an
    ///         overlay usable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two reasons look alike here and are not the same rule, which is why they are
    ///         two calls rather than one.</b> <c>pointer-events: none</c> is not inherited and says
    ///         only "let the pointer through me"; <c>visibility: hidden</c> <i>is</i> inherited, so
    ///         reading it per element gives a hidden subtree for free and still lets a descendant
    ///         that declares <c>visible</c> be clicked — the same asymmetry the paint walk relies on.
    ///         Neither takes the box out of layout; that is <c>display: none</c>, and it never reaches
    ///         a hit test because layout gave it no rectangle to be inside.
    ///     </para>
    /// </remarks>
    public bool IsHitTestVisible => !Document.PointerEventsNone(Style) && !Document.Invisible(Style);

    /// <summary>Whether <c>display: none</c> has taken this element and its subtree out of the tree.</summary>
    /// <remarks>
    ///     ⚠ <b>Reads the private field rather than <see cref="Document" />, so a detached element
    ///     answers false instead of throwing.</b> <see cref="UiDocument.TabOrder" /> is public, static
    ///     and takes any element, and it has never needed a document; making it throw on one that was
    ///     built but not yet added would be a new failure in a method that used to be a tree walk.
    ///     An unbound element's <see cref="Style" /> is <c>ComputedStyle.Empty</c>, which declares
    ///     nothing, so the answer would be false either way.
    /// </remarks>
    internal bool IsUndisplayed => document is not null && document.Undisplayed(Style);

    /// <summary>Whether <c>visibility</c> hides this element. Inherited, so it is a per-element read.</summary>
    internal bool IsStyleHidden => document is not null && document.Invisible(Style);

    /// <summary>Listens for an event on its way through this element.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="handler">What to run.</param>
    /// <param name="strategy">Which leg of the route to listen on.</param>
    /// <param name="handledEventsToo">
    ///     Whether to run even after something has handled it. For the listeners that need to know
    ///     an event happened rather than to act on it — a focus manager, a diagnostic overlay.
    /// </param>
    public void AddHandler<T>(Action<UiElement, T> handler, RoutingStrategy strategy = RoutingStrategy.Bubble, bool handledEventsToo = false)
        where T : UiEvent {
        ArgumentNullException.ThrowIfNull(handler);

        handlers ??= [];
        handlers.Add(new HandlerRegistration(typeof(T), handler, strategy, handledEventsToo));
    }

    /// <summary>Stops listening.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="handler">The handler that was added.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveHandler<T>(Action<UiElement, T> handler) where T : UiEvent {
        ArgumentNullException.ThrowIfNull(handler);

        if (handlers is null) {
            return false;
        }

        for (var i = 0; i < handlers.Count; i++) {
            if (handlers[i].Handler.Equals(handler)) {
                handlers.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Sends an event to this element and along its route.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="args">The event.</param>
    public void Raise<T>(T args) where T : UiEvent {
        ArgumentNullException.ThrowIfNull(args);
        EventRouter.Raise(this, args);
    }

    internal void Invoke<T>(T args, RoutingStrategy strategy) where T : UiEvent {
        if (handlers is null) {
            return;
        }

        // ⚠ Indexed, and the count is re-read every step. A handler is entitled to add or remove
        // handlers while it runs — a button that unsubscribes on click is the ordinary case — and a
        // foreach over the list would throw halfway through delivering the event that caused it.
        for (var i = 0; i < handlers.Count; i++) {
            var registration = handlers[i];

            if (registration.Strategy != strategy
                || registration.EventType != typeof(T)
                || (args.Handled && !registration.HandledEventsToo)) {
                continue;
            }

            args.Current = this;
            ((Action<UiElement, T>) registration.Handler)(this, args);
        }
    }

    internal void Bind(UiDocument owner, string tag, UiElement? parent, StyleNodeId styleNode, LayoutNodeId layoutNode) {
        document = owner;
        Tag = tag;
        Parent = parent;
        StyleNode = styleNode;
        LayoutNode = layoutNode;
    }

    /// <summary>Points this element at the slot its style moved to.</summary>
    /// <remarks>
    ///     ⚠ Only <c>UiDocument.CompactStyles</c> may call this, and only while it is applying a
    ///     mapping to the whole tree. Moving one element's slot on its own would leave it wearing
    ///     whatever style belongs to a different element, which is why this is not a settable
    ///     property.
    /// </remarks>
    internal void Restyle(StyleNodeId styleNode) => StyleNode = styleNode;

    /// <summary>Takes this element and everything under it out of its document.</summary>
    /// <remarks>
    ///     ⚠ Removing twice throws, and that is the contract rather than an oversight — see
    ///     <c>RemovalTests.Removing_the_same_element_twice_says_so</c>. A control whose
    ///     <see cref="OnRemoved" /> tears down something it does not solely own asks
    ///     <see cref="IsRemoved" /> first.
    /// </remarks>
    public void Remove() => Document.Remove(this);

    /// <summary>What the last pass wrote through to the layout store.</summary>
    /// <remarks>
    ///     Kept on the element rather than in a list beside it, so that removing one takes its
    ///     bookkeeping with it instead of leaving a hole in a parallel array.
    /// </remarks>
    internal ComputedStyle? AppliedStyle { get; set; }

    /// <summary>The font size that went with it.</summary>
    internal float AppliedFontSize { get; set; } = float.NaN;

    /// <summary>The line height that went with it.</summary>
    internal float AppliedLineHeight { get; set; } = float.NaN;

    /// <summary>The letter spacing that went with it.</summary>
    internal float AppliedLetterSpacing { get; set; } = float.NaN;

    /// <summary>And the indent, which changes what the element measures just as the other two do.</summary>
    internal float AppliedTextIndent { get; set; } = float.NaN;

    /// <summary>
    ///     And the percentage, which is the half of the indent that <see cref="AppliedTextIndent" />
    ///     cannot see.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Two fields because the computed value stays at zero across the change that matters.</b>
    ///     Going from no <c>text-indent</c> to <c>text-indent: 25%</c> leaves
    ///     <see cref="TextIndent" /> at zero both times, so a key holding only that would never mark
    ///     the node dirty and the paragraph would go on measuring itself unindented until something
    ///     else happened to invalidate it.
    /// </remarks>
    internal float AppliedTextIndentPercent { get; set; } = float.NaN;

    /// <summary>And the features, which change the glyphs and therefore the width.</summary>
    internal FontFeatureSet? AppliedFontFeatures { get; set; }

    /// <summary>
    ///     And the base direction, which changes where the glyphs go — nullable so that the first
    ///     style pass of an element's life is a change whatever the property resolved to.
    /// </summary>
    internal ParagraphDirection? AppliedParagraphDirection { get; set; }

    // ⚠ The three structural edits all set the accessibility flag, because the shape of the tree is
    // the one thing a bridge caches that no property setter can tell it about. It is a store to a
    // bool that is already dirty for all but the first element of a build, which is what makes it
    // affordable on the path a panel of four hundred elements runs four hundred times.
    internal void Attach(UiElement child) {
        children.Add(child);
        orderDirty = true;
        document?.InvalidateAccessibility();
    }

    internal void Insert(UiElement child, int index) {
        children.Insert(index, child);
        orderDirty = true;
        document?.InvalidateAccessibility();
    }

    internal void Detach(UiElement child) {
        children.Remove(child);
        orderDirty = true;
        document?.InvalidateAccessibility();
    }

    /// <summary>Points this element at its new parent.</summary>
    /// <remarks>
    ///     ⚠ Only <c>UiDocument.Reparent</c> may call this, and only as part of moving all three
    ///     stores at once. <see cref="Parent" /> is what the event router walks and what removal
    ///     climbs; one changed on its own would give an element that is a child of one thing and
    ///     claims to be a child of another.
    /// </remarks>
    internal void Adopt(UiElement parent) => Parent = parent;

    internal void MoveChild(UiElement child, int index) {
        children.Remove(child);
        children.Insert(index, child);
        orderDirty = true;
    }

    /// <summary>Where this element sits among its siblings, or -1 if it has no parent.</summary>
    public int IndexInParent => Parent?.children.IndexOf(this) ?? -1;

    internal void Retire() => IsRemoved = true;

    /// <summary>Makes this element the root of a surface, or stops it being one.</summary>
    /// <remarks>
    ///     ⚠ Only <c>UiDocument</c> may call this. A surface's root is the boundary three passes
    ///     stop at — the accumulator, the hit test and the draw list — so an element that claimed to
    ///     be one without a surface behind it would be a hole in the middle of the document that
    ///     nothing draws and nothing can click.
    /// </remarks>
    internal void MarkSurface(UiSurface? surface) => SurfaceRoot = surface;

    readonly record struct HandlerRegistration(
        Type EventType,
        Delegate Handler,
        RoutingStrategy Strategy,
        bool HandledEventsToo
    );
}
