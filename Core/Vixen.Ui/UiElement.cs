// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

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
public partial class UiElement {
    readonly List<UiElement> children = [];
    List<UiElement>? ordered;
    bool orderDirty = true;
    int zIndex;
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

    /// <summary>Its parent, or <c>null</c> for the root.</summary>
    public UiElement? Parent { get; private set; }

    /// <summary>Its children, in document order.</summary>
    public IReadOnlyList<UiElement> Children => children;

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
    /// </remarks>
    internal IReadOnlyList<UiElement> PaintOrder {
        get {
            if (!orderDirty) {
                return ordered ?? (IReadOnlyList<UiElement>) children;
            }

            orderDirty = false;

            if (!AnyChildIsLifted()) {
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

            // Stable by construction: equal indices keep document order, which is what makes
            // `z-10` on one child leave every other child exactly where it was.
            ordered.Sort(static (left, right) =>
                left.zIndex != right.zIndex
                    ? left.zIndex.CompareTo(right.zIndex)
                    : left.paintKey.CompareTo(right.paintKey));

            return ordered;
        }
    }

    bool AnyChildIsLifted() {
        foreach (var child in children) {
            if (child.zIndex != 0) {
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

        Document.Invalidate();
        return true;
    }

    /// <summary>Removes a class.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveClass(string className) {
        if (!Document.Styles.Tree.RemoveClass(StyleNode, className)) {
            return false;
        }

        Document.Invalidate();
        return true;
    }

    /// <summary>Whether it carries a class.</summary>
    /// <param name="className">The class.</param>
    /// <returns>Whether it does.</returns>
    public bool HasClass(string className) => Document.Styles.Tree.HasClass(StyleNode, className);

    /// <summary>Its interaction state — hover, focus, active — which selectors match on.</summary>
    public ElementState State {
        get => Document.Styles.Tree.GetState(StyleNode);
        set {
            if (Document.Styles.Tree.GetState(StyleNode) == value) {
                return;
            }

            Document.Styles.Tree.SetState(StyleNode, value);
            Document.Invalidate();
        }
    }

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
    ///         every word. Rich text is a run list inside one element when it arrives, and it is owed.
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

    /// <summary>Its text shaped at its font size, or <c>null</c> if it has none to shape.</summary>
    /// <remarks>
    ///     Goes through the document's shaping cache, so the measure pass and the draw pass shape
    ///     once between them rather than once each — and so do ten thousand list rows saying the same
    ///     word. The cache is keyed on the font and the string and not on the size, because shaping
    ///     is size-independent.
    /// </remarks>
    public TextRun? Run() {
        if (string.IsNullOrEmpty(Text)) {
            return null;
        }

        var font = Document.Fonts.Resolve(
            Document.FontFamilyOf(Style),
            Document.FontWeightOf(Style),
            Document.FontStyleOf(Style)
        );

        return font is null
            ? null
            : new TextRun(font, Document.Shaping.Shape(font, Text), FontSize, LetterSpacing, LineHeight);
    }

    void OnTextChanged(string? previous, string? current) {
        // ⚠ The measure function is attached and detached rather than left in place answering zero.
        // The layout algorithm asks a node with one whether it is a leaf and refuses to lay out its
        // children — so an element that once had text and now has none would silently stop laying
        // out everything inside it.
        if (string.IsNullOrEmpty(previous) != string.IsNullOrEmpty(current)) {
            Document.Layout.SetContext(LayoutNode, string.IsNullOrEmpty(current) ? null : this);
            Document.Layout.SetMeasureFunction(LayoutNode, string.IsNullOrEmpty(current) ? null : TextRun.Measure);
        } else if (!string.IsNullOrEmpty(current)) {
            // Attaching or detaching the measure function already dirties the node, so the only case
            // left is one string becoming another — and the layout tree refuses a hand-dirtied node
            // that does not measure itself, on the grounds that nothing else about it can have
            // changed without a style or a child changing. ⚠ Which makes the emptiness test
            // load-bearing rather than tidy: null and "" are both "no text", so setting one to the
            // other reaches here with no measure function attached and throws.
            Document.Layout.MarkDirty(LayoutNode);
        }

        Document.Invalidate();
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
    ///     ⚠ <b>Invalidates the document rather than only the positions.</b> There is no cheaper
    ///     pass to ask for — <see cref="UiDocument.Update" /> is what recomputes absolute positions —
    ///     and it costs a walk that changes nothing: no element's computed style has changed, so the
    ///     reference comparison skips every one of them, and no layout node is dirty, so flexbox
    ///     returns without measuring. A scroll is therefore two walks of the tree and no work, which
    ///     is the point.
    /// </remarks>
    void OnOffsetChanged(float previous, float current) => Document.Invalidate();

    /// <summary>Its left edge in document space, after the last layout pass.</summary>
    public float AbsoluteLeft { get; internal set; }

    /// <summary>Its top edge in document space.</summary>
    public float AbsoluteTop { get; internal set; }

    /// <summary>Where it is in document space, after the last layout pass.</summary>
    public Rectangle Bounds => new(AbsoluteLeft, AbsoluteTop, Width, Height);

    /// <summary>Whether a pointer can land on it. <c>pointer-events: none</c> makes it false.</summary>
    /// <remarks>
    ///     Read from the computed style rather than stored, because it is a stylesheet's decision and
    ///     a stylesheet can change it between frames. An element that is not hit-testable does not
    ///     stop its children from being — that is what CSS says, and it is what makes an overlay
    ///     usable.
    /// </remarks>
    public bool IsHitTestVisible => !Document.PointerEventsNone(Style);

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

    internal void Attach(UiElement child) {
        children.Add(child);
        orderDirty = true;
    }

    internal void Insert(UiElement child, int index) {
        children.Insert(index, child);
        orderDirty = true;
    }

    internal void Detach(UiElement child) {
        children.Remove(child);
        orderDirty = true;
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

    readonly record struct HandlerRegistration(
        Type EventType,
        Delegate Handler,
        RoutingStrategy Strategy,
        bool HandledEventsToo
    );
}
