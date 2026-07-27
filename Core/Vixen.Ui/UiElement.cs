// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
    /// <exception cref="InvalidOperationException">If it has not been added to one.</exception>
    public UiDocument Document =>
        document ?? throw new InvalidOperationException(
            $"this {GetType().Name} is not in a document — create it with UiDocument.Create or UiElement.Add"
        );

    /// <summary>Its element name, which selectors match on.</summary>
    public string Tag { get; private set; }

    /// <summary>Its parent, or <c>null</c> for the root.</summary>
    public UiElement? Parent { get; private set; }

    /// <summary>Its children, in document order.</summary>
    public IReadOnlyList<UiElement> Children => children;

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
    /// <param name="tag">Its element name.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The new element.</returns>
    public T Add<T>(string tag, string? id = null, params ReadOnlySpan<string> classNames)
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

    /// <summary>Its left edge in document space, after the last layout pass.</summary>
    public float AbsoluteLeft { get; internal set; }

    /// <summary>Its top edge in document space.</summary>
    public float AbsoluteTop { get; internal set; }

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

    internal void Attach(UiElement child) => children.Add(child);

    readonly record struct HandlerRegistration(
        Type EventType,
        Delegate Handler,
        RoutingStrategy Strategy,
        bool HandledEventsToo
    );
}
