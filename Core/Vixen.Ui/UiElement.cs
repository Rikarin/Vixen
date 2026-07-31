// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
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

        Document.InvalidateClass(StyleNode, className);
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
            Document.InvalidateState(StyleNode);
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

        if (!Document.WrapsOf(Style)) {
            width = float.PositiveInfinity;
        }

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
            && lineWidth.Equals(width)
            && lineSize.Equals(FontSize)
            && lineTracking.Equals(LetterSpacing)
            && lineLeading.Equals(LineHeight)) {
            return block;
        }

        var chain = new List<FontFace>();
        Document.Fonts.Chain(family, weight, slant, chain);

        if (chain.Count == 0) {
            return null;
        }

        var lines = ImmutableArray.CreateBuilder<TextLine>();
        var whole = Runs(Text, 0, chain);

        if ((float.IsPositiveInfinity(width) || whole.Width <= width) && !HasHardBreak(Text)) {
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
            Wrap(Text, whole, width, mode, chain, lines);
        }

        block = new TextLayout(lines.ToImmutable());
        lineText = Text;
        lineFamily = family;
        lineWeight = weight;
        lineStyle = slant;
        lineRevision = revision;
        lineMode = mode;
        lineWidth = width;
        lineSize = FontSize;
        lineTracking = LetterSpacing;
        lineLeading = LineHeight;

        return block;
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

    /// <summary>Shapes one stretch of the text into the runs its faces need.</summary>
    TextLine Runs(string text, int start, List<FontFace> chain, float width = float.NaN) {
        var spans = new List<FontSpan>();
        FontRegistry.Cover(text, chain, spans);

        var runs = ImmutableArray.CreateBuilder<TextRun>(spans.Count);

        foreach (var span in spans) {
            // ⚠ The whole string when there is one span, and a substring only when there is more than
            // one. Not a micro-optimisation: `text[0..Length]` is a fresh string every call, and the
            // shaping cache keys on the string's contents, so it would hash and compare the whole
            // label to find the entry it already had.
            var piece = spans.Count == 1 ? text : text.Substring(span.Start, span.Length);

            runs.Add(
                new TextRun(
                    span.Font,
                    Document.Shaping.Shape(span.Font, piece),
                    FontSize,
                    LetterSpacing,
                    LineHeight,
                    start + span.Start
                )
            );
        }

        return new TextLine(runs.MoveToImmutable(), width);
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
        List<FontFace> chain,
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
        LineWrapper.Wrap(text, advances, width, wrapped, mode);

        foreach (var line in wrapped) {
            // ⚠ Each line is shaped as its own string rather than sliced out of the paragraph's
            // shaping. That is what a line break *is* — a ligature does not cross one and an Arabic
            // word unjoins at one — and slicing would also need a run split in the middle of a
            // cluster, which has no meaning.
            // ⚠ The wrapper's own width, not the re-shaped line's. It excludes the whitespace at the
            // line's end, which is drawn but must not be measured — and since the advances handed to
            // it were in pixels, the number it gives back already is too.
            into.Add(Runs(text.Substring(line.Start, line.Length), line.Start, chain, line.Advance));
        }

        if (into.Count == 0) {
            into.Add(whole);
        }
    }

    TextLayout? block;
    string? lineText;
    string? lineFamily;
    int lineWeight;
    FontStyle lineStyle;
    int lineRevision;
    TextWrapMode lineMode;
    float lineWidth;
    float lineSize;
    float lineTracking;
    float lineLeading;

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
