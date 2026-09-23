// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What this document tells <c>@container</c> about the boxes it measured.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half of container queries that turns the other half on.</b>
///         <see cref="StyleEngine.ContainerScopes" />, <see cref="ContainerConditions" /> and
///         <see cref="ContainerQuery" /> were built and tested a day before this and nothing called
///         <see cref="ContainerScopes.Enter" />, so every element of every live document sat at
///         <see cref="ContainerScopes.Root" /> — where no query has an eligible container and all of
///         them are false. That is this repository's commonest shape of missing feature, and the
///         thing that distinguishes it from a bug is that a query which never matches is perfectly
///         good CSS: nothing warns, nothing throws, the rule is in the rule set and simply never
///         wins. <see cref="Recontain()" /> is the caller that was owed.
///     </para>
///     <para>
///         ⚠ <b>It runs at the end of <see cref="Arrange" />, which is what makes it answerable at
///         all.</b> A verdict is about a <i>measured</i> box, so style decides layout and layout
///         decides style — and the only place in the frame where every box is final is after
///         <c>CalculateLayout</c>. Reading <c>container-type</c> in <c>Apply</c> as doc 43 § D3
///         suggested would be reading the declaration in the one pass that cannot yet see the
///         result of it; the declaration is read here instead, off the same
///         <see cref="UiElement.Style" /> that <c>Apply</c> just wrote.
///     </para>
///     <para>
///         ⚠ <b>The cycle closes in one extra pass, and the bound is
///         <see cref="SettlePasses" /> rather than a new mechanism.</b> Pass one cascades with every
///         element at the root scope, lays out, and enters the scopes; the walk sees the scopes move
///         and <see cref="Invalidate" />s, so <see cref="Settle" /> runs pass two, which cascades
///         with the verdicts in hand and lays out again. For a container whose inline size is a pure
///         function of its parent's — <c>width: auto</c> on a normal-flow block, which takes
///         <c>SizingMode.StretchFit</c> and is sized with no child consulted — pass two measures the
///         <i>same</i> box, so <see cref="ContainerScopes.Enter" /> interns to the same ids, nothing
///         moves and the loop converges with <see cref="Settled" /> true and
///         <see cref="SettlingPasses" /> equal to one. ⚠ A query container can no longer be sized
///         by its own contents on the axis it answers — <c>container-type</c> applies size
///         containment through <c>ContainmentReader</c> — so the loop that remains runs through its
///         surroundings: a verdict that changes its height can move it onto another flex line or into
///         another track. That can flip on every pass; it does not hang, it exhausts the budget and
///         reports <see cref="Settled" /> false, which is the visible failure doc 43 § D3 said it
///         would be.
///     </para>
///     <para>
///         ⚠ <b>Which is why <see cref="Settle" /> no longer returns early when nothing is listening
///         to <see cref="LayoutFinished" />.</b> It used to, and that early return was correct while
///         a handler was the only thing that could dirty a document after a layout. This walk is a
///         second such thing, and it is one no application registers for — so a document with a
///         container query and no <c>LayoutFinished</c> handler would have entered its scopes, marked
///         itself dirty, and gone home, showing the verdicts one whole frame late. A panel that
///         resizes visibly a frame after it was dragged is exactly the defect the settle loop exists
///         to prevent.
///     </para>
///     <para>
///         ⚠ <b>Nothing walks at all unless a sheet actually declared a <c>@container</c></b>, which
///         is the same <c>if</c> that makes <c>Remedia</c> affordable and is worth as much here: no
///         group means no query can be true whatever the scopes say, so the entire walk — and the
///         cold cascade that follows a first assignment — is skipped for every document in this
///         repository that does not use the feature.
///     </para>
/// </remarks>
public sealed partial class UiDocument {
    /// <summary>How many chains may be interned before the table is rebuilt from scratch.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The eviction policy <see cref="ContainerScopes" /> deferred to whoever wrote the
    ///         wiring, and now that the wiring exists the number it wanted is knowable.</b> Scopes are
    ///         interned by value, so a container being dragged wider interns one new chain per pixel
    ///         per frame and nothing ever removes one — the previous frame's chain is still in the
    ///         table, still holding its cached verdicts, still keyed. That is bounded by nothing at
    ///         all over a session, and a drag is not a rare event in a dockable editor.
    ///     </para>
    ///     <para>
    ///         The generation stamp the class's remarks sketch cannot be built without renumbering:
    ///         a scope id is an index into a list and elements hold it, so sweeping the middle of the
    ///         list invalidates every id written on the tree. Rebuilding wholesale has that same
    ///         property and is honest about it — <see cref="ContainerScopes.Reset" /> is documented as
    ///         safe in exactly one order, reset then re-assign then re-cascade, and the walk below is
    ///         the re-assign. So the policy is a ceiling rather than a sweep, and it costs one cold
    ///         cascade on the frame it fires.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured rather than argued, and the branch had never run until it was.</b>
    ///         <c>ContainerWiringTests.The_scope_table_is_rebuilt_at_the_ceiling_and_the_document_still_answers</c>
    ///         drags a window a pixel a frame until it fires. Two things it settled: the frame costs
    ///         <b>two</b> extra settle passes and not one — one for the drag that moved the box and
    ///         one for the rebuild — and the table never carries more than
    ///         <see cref="ContainerScopeCeiling" /> chains across a frame boundary, because the
    ///         settle loop arranges twice and the chain that trips the branch is interned and swept
    ///         inside the same <see cref="Update" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the re-assign on the next line is an optimisation, not a correctness
    ///         requirement — which is the opposite of what the comment inside the branch implies.</b>
    ///         Removing it leaves the document correct, because the settle loop's own next pass
    ///         re-assigns every scope anyway; what it costs is a third pass. That is worth knowing
    ///         because a test asserting only that the document still answers passes that sabotage,
    ///         and the pass count is the only thing that does not.
    ///     </para>
    ///     <para>
    ///         Four thousand is about a minute of continuous dragging at sixty frames a second and a
    ///         pixel a frame, and far more distinct chains than any static document has — interning
    ///         by value collapses a thousand equally-sized rows to one. It is deliberately high
    ///         enough that a document reaches it only by moving.
    ///     </para>
    /// </remarks>
    public const int ContainerScopeCeiling = 4096;

    int containerType;
    int containerName;
    int containerShorthand;
    int inlineSizeKeyword;
    int sizeKeyword;

    /// <summary>The containers whose own box moved during the last <see cref="Arrange" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What <see cref="Settled" /> could not say.</b> A container its surroundings keep
    ///         moving can flip on every pass — the verdict changes its height, the height moves it
    ///         onto another flex line, the line gives it another width — and the settle loop answers
    ///         that by exhausting its budget and reporting <c>false</c>. That is a document-level
    ///         boolean about a document-level symptom, and the thing an author has to change is one
    ///         element; this says <i>which box</i> to go and give a definite width to. (The other
    ///         loop, a container sized by its own contents, is closed by containment rather than
    ///         reported — see <c>ContainmentReader</c>.)
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rewritten on every walk and read only when the loop gives up</b>, which is what
    ///         makes it a measurement rather than a prediction. A predicate over the style — "is this
    ///         container sized by its contents" — has to know about flex bases, intrinsic grid tracks
    ///         and four content keywords, and would be a guess in exactly the arrangements it was
    ///         written for. A box that changed between two passes of one frame changed; there is
    ///         nothing to be wrong about.
    ///     </para>
    ///     <para>
    ///         ⚠ Every container is in here on the first pass of a fresh document, because a chain
    ///         entered from <see cref="ContainerScopes.Root" /> has moved by definition. That costs
    ///         one add per container per walk on documents that declare a <c>@container</c> at all,
    ///         and nothing reads it unless the budget ran out.
    ///     </para>
    /// </remarks>
    readonly List<UiElement> unsettledContainers = [];

    void InternContainers() {
        containerType = Styles.Properties.Intern("container-type");
        containerName = Styles.Properties.Intern("container-name");
        containerShorthand = Styles.Properties.Intern("container");
        inlineSizeKeyword = Styles.Values.Intern("inline-size");
        sizeKeyword = Styles.Values.Intern("size");
    }

    /// <summary>How many extra chains the last <see cref="Update" /> interned, across all its passes.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Per <see cref="Update" /> and not per <see cref="Arrange" />, which is the
    ///         difference between a number that means something and one that is always nought.</b>
    ///         The settle loop arranges again, and the second arrange of a converged frame interns
    ///         nothing by construction — so a per-arrange counter would report the last pass rather
    ///         than the frame, and a document that interned a chain per element on its first pass
    ///         would read as zero.
    ///     </para>
    ///     <para>
    ///         Nought on a settled frame is the property worth asserting: a document whose boxes are
    ///         not moving must not be interning, or the ceiling above would be reached by standing
    ///         still.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And that includes an <see cref="Update" /> that ran no pass at all, which is the
    ///         half that was missing.</b> The early return clears this beside
    ///         <see cref="StylesApplied" />; until #596 it did not, so "nought on a settled frame"
    ///         held only for a frame that had done work and found none — a document standing still
    ///         reported whatever it had last interned, on every frame, for the life of the session.
    ///     </para>
    /// </remarks>
    public int ContainerScopesEntered { get; private set; }

    /// <summary>Enters a container scope for every measured query container, and re-cascades if any moved.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Invalidate" /> rather than <see cref="Forget()" />, and the difference is
    ///     one full rebuild of every layout style.</b> <c>Remedia</c> forgets, because it predates the
    ///     interning being trusted; it does not need to either. A moved verdict changes which rules
    ///     match, which changes the <see cref="ComputedStyle" /> the resolver interns, which changes
    ///     the reference <c>Apply</c> compares — so an element whose style genuinely moved rebuilds
    ///     and one whose style did not is left alone. Forgetting would rebuild the layout style of
    ///     every element in the document for a query that repainted one panel.
    /// </remarks>
    void Recontain() {
        // ⚠ No group means no query, and no query means the scopes cannot change an answer. See the
        // remarks on the class: this is the branch that keeps the feature free for the documents
        // that do not use it, and `Count` is 1 — the unconditional group — until a sheet declares
        // one.
        if (Styles.Containers.Count <= 1) {
            return;
        }

        // Cleared here rather than in the recursive half, which is entered once per element.
        unsettledContainers.Clear();

        var before = Styles.ContainerScopes.Count;

        if (before > ContainerScopeCeiling) {
            // ⚠ Reset, re-assign, re-cascade, in that order and with no early exit between them.
            // Every element is left pointing at a chain that no longer exists for the length of one
            // statement, which `VerdictsOf` answers conservatively rather than throwing for — and
            // which the walk on the next line repairs before anything reads a style.
            Styles.ContainerScopes.Reset();
            Recontain(Root, ContainerScopes.Root);
            ContainerScopesEntered += Styles.ContainerScopes.Count - 1;
            Invalidate();

            return;
        }

        var moved = Recontain(Root, ContainerScopes.Root);
        ContainerScopesEntered += Styles.ContainerScopes.Count - before;

        if (moved) {
            Invalidate();
        }

    }

    /// <summary>Assigns a subtree's container scopes, returning whether any of them changed.</summary>
    /// <param name="element">The subtree's root.</param>
    /// <param name="scope">The chain it is inside.</param>
    /// <returns>Whether anything now answers differently.</returns>
    /// <remarks>
    ///     ⚠ <b>Both slots are written for every element, not only for the containers.</b> An element
    ///     that <i>stops</i> being a container — a <c>container-type</c> removed with a class — has a
    ///     stale provided scope that its children would keep inheriting through
    ///     <c>CreateElement</c>, so a walk that only wrote the containers would leave a box answering
    ///     queries about a containment it no longer declares.
    /// </remarks>
    bool Recontain(UiElement element, int scope) {
        // ⚠ A second window starts its own chain. Its root is a child of an element of the main
        // window's tree — that is what keeps one theme across a torn-off panel — but it is not
        // *inside* that element's box in any sense a size query could be about, and inheriting the
        // chain would have a floating inspector answering `@container` off the dock it was pulled
        // out of. `Accumulate` restarts coordinates at every surface for the same reason.
        if (element.SurfaceRoot is not null) {
            scope = ContainerScopes.Root;
        }

        var node = element.StyleNode;
        var provided = scope;
        var kind = KindOf(element.Style, out var name);

        if (kind != ContainerKind.Normal) {
            provided = Styles.ContainerScopes.Enter(scope, name, BoxOf(element, kind));
        }

        var wasProvided = Styles.Tree.GetProvidedContainerScope(node);
        var moved = Styles.Tree.GetContainerScope(node) != scope || wasProvided != provided;

        // ⚠ Recorded off the *provided* slot and only for an element that declares a containment,
        // which is what makes the entry mean "this container's own box moved" rather than "something
        // above it did". An ordinary element's chain changes whenever any ancestor container is
        // resized, and naming those would bury the one box an author can fix under its whole subtree.
        if (kind != ContainerKind.Normal && wasProvided != provided) {
            unsettledContainers.Add(element);
        }

        Styles.Tree.SetContainedIn(node, scope);

        if (provided != scope) {
            Styles.Tree.SetContainerScope(node, provided);
        }

        foreach (var child in element.ChildList) {
            // ⚠ Not `||`, which would stop walking at the first element that moved and leave the
            // rest of the subtree holding last frame's chain. Every element has to be assigned on
            // every pass; the boolean is a report, not a control flow.
            moved |= Recontain(child, provided);
        }

        return moved;
    }

    /// <summary>The length context an element's children resolve <c>cq*</c> units in.</summary>
    /// <param name="element">The element, which may or may not declare a containment.</param>
    /// <param name="style">Its computed style, as the caller already has it.</param>
    /// <param name="metrics">The context the element itself resolved in.</param>
    /// <returns>The same context, with this element's box in it if it is a query container.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read off the style walk and not off <see cref="ContainerScopes" />, which is the
    ///         one design decision in this method.</b> The scope chain exists to answer
    ///         <c>@container</c> rules, and <see cref="Recontain()" /> gives up before entering a
    ///         single scope when no sheet declares a container group — the branch that keeps the
    ///         feature free for documents that do not use it. But a <c>cqw</c> needs no
    ///         <c>@container</c> rule at all: <c>container-type: inline-size</c> on a panel and
    ///         <c>width: 50cqi</c> on its child is a complete, legal stylesheet, and reading the
    ///         chain would have resolved it against the viewport with nothing said.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The block axis keeps whatever ancestor answered it, and that is not tidiness.</b>
    ///         An <c>inline-size</c> container leaves its height to its content, so it cannot answer
    ///         <c>cqb</c> — the nearest <c>size</c> container above it still does, and may be several
    ///         levels further out or absent. Collapsing the two into one box would have an
    ///         <c>inline-size</c> container shadow an outer <c>size</c> one on an axis it never
    ///         claimed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The box is the previous layout pass's, exactly as <see cref="Recontain()" />'s
    ///         is.</b> Styles are resolved before layout runs, so a <c>cqw</c> is one pass stale on
    ///         the frame a container resizes, and the settle loop is what closes it — the same
    ///         staleness, from the same cause, as every <c>@container</c> verdict in this file.
    ///     </para>
    /// </remarks>
    LengthContext WithContainerOf(UiElement element, ComputedStyle style, in LengthContext metrics) {
        var kind = KindOf(style, out _);

        if (kind == ContainerKind.Normal) {
            // ⚠ <b>An element that STOPS being a query container forgets the box it used to hand
            // out, and the guard is what keeps that free.</b> The field is the settle loop's driver:
            // it requests a pass when a container's box moves, by comparing against what it handed
            // out last time. Left standing across a `container-type` that a class change removed, it
            // would still hold that box — so an element that became a container again at the same
            // measured size would compare equal, and the pass a first-frame container unit needs
            // would not be asked for. `Kind` is `Normal` in the sentinel and in nothing `BoxOf`
            // returns, so this writes only on the transition and not on the overwhelming majority of
            // elements that were never containers.
            if (element.AppliedContainerBox.Kind != ContainerKind.Normal) {
                element.AppliedContainerBox = new(float.NaN, float.NaN, ContainerKind.Normal);
            }

            return metrics;
        }

        var box = BoxOf(element, kind);

        // ⚠ <b>The settle pass a container unit needs, asked for here and ONLY when nothing else
        // will ask for it.</b> Styles are built before layout runs, so the first pass resolves every
        // `cqw` against a container nobody has measured — a box of nothing — and a second pass
        // happens only because something invalidated the document. `Recontain` is that something
        // whenever a sheet declares a container group: a container's box is part of the key its
        // scope is interned under, so a box that moves is a scope that moved and it invalidates
        // already. What it cannot cover is the document that uses no `@container` rule at all,
        // because it gives up before walking — and a container unit needs no rule.
        //
        // ⚠ So the condition is `Recontain`'s own early-out, spelled the same way on purpose. An
        // unconditional invalidate here costs every document with a query container one extra settle
        // pass it did not need, which `ContainerWiringTests` measures to the pass and is right to:
        // "two would mean the container's own size moved in response to its descendants' styles".
        var moved = element.AppliedContainerBox != box;
        element.AppliedContainerBox = box;

        if (moved && Styles.Containers.Count <= 1) {
            Invalidate();
        }

        return kind == ContainerKind.Size
            ? metrics.WithContainer(box.Width, box.Height, ContainerKind.Size)
            : metrics.WithContainer(
                box.Width,
                metrics.ContainerBlockSize,
                metrics.ContainerAxes == ContainerKind.Size ? ContainerKind.Size : ContainerKind.InlineSize
            );
    }

    /// <summary>Reads <c>container-type</c>, <c>container-name</c> and the <c>container</c> shorthand.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <param name="name">Receives its container name, or empty.</param>
    /// <returns>Which axes it may be asked about.</returns>
    /// <remarks>
    ///     ⚠ <b>The shorthand is read, and reading it is not optional.</b> ExCSS hands
    ///     <c>container: card / inline-size</c> through as one ordinary declaration and expands
    ///     nothing, so a document that used the shorthand — which is how the specification's own
    ///     examples are written — would get a container that silently never contained. That is the
    ///     defect this whole section exists to stop shipping, arriving through the one spelling
    ///     nobody tested.
    ///     <para>
    ///         The longhands win over it because they are declared afterwards in the sense the
    ///         cascade has already settled: both reach here as separate properties, and CSS says a
    ///         longhand later in the cascade beats the shorthand that set it. The cascade cannot
    ///         express that without shorthand expansion, so the order is fixed here, which is the
    ///         same answer for every sheet that does not write both on one element — and writing both
    ///         on one element is already ambiguous.
    ///     </para>
    ///     ⚠ <b><c>container: card</c> with no slash is <c>container-type: normal</c></b>, CSS
    ///     Containment 3 § 3.3, and it is the trap in the shorthand: naming a box does not make it a
    ///     query container, so a sheet that writes only the name gets a name nothing can ask for.
    /// </remarks>
    ContainerKind KindOf(ComputedStyle style, out string name) {
        name = string.Empty;
        var kind = ContainerKind.Normal;

        if (style.TryGet(containerShorthand, out var shorthand)) {
            var text = Styles.Values.NameOf(shorthand).AsSpan();
            var slash = text.IndexOf('/');

            if (slash >= 0) {
                kind = KindOf(text[(slash + 1)..].Trim());
                text = text[..slash];
            }

            name = NameOf(text.Trim());
        }

        if (style.TryGet(containerType, out var declared)) {
            kind = declared == inlineSizeKeyword
                ? ContainerKind.InlineSize
                : declared == sizeKeyword
                    ? ContainerKind.Size
                    : ContainerKind.Normal;
        }

        if (style.TryGet(containerName, out var declaredName)) {
            name = declaredName == none ? string.Empty : NameOf(Styles.Values.NameOf(declaredName));
        }

        return kind;
    }

    static ContainerKind KindOf(ReadOnlySpan<char> keyword) =>
        keyword.Equals("inline-size", StringComparison.OrdinalIgnoreCase) ? ContainerKind.InlineSize
        : keyword.Equals("size", StringComparison.OrdinalIgnoreCase) ? ContainerKind.Size
        : ContainerKind.Normal;

    /// <summary><c>none</c> is the absence of a name rather than a name, so it never matches one.</summary>
    static string NameOf(ReadOnlySpan<char> text) =>
        text.IsEmpty || text.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : text.ToString();

    /// <summary>The content box a query about this element is asked of.</summary>
    /// <param name="element">The container.</param>
    /// <param name="kind">Which axes it declared.</param>
    /// <returns>Its box.</returns>
    /// <remarks>
    ///     ⚠ <b>The content box and not the border box</b>, CSS Containment 3 § 5.2 — a query
    ///     container's size is the size its children have to fit in, so a panel 300 px wide with 16 px
    ///     of padding a side answers <c>(min-width: 280px)</c> and not <c>(min-width: 300px)</c>. The
    ///     difference is one padding away from every threshold an author picks, which makes it the
    ///     kind of wrong that reads as an off-by-one in the stylesheet rather than as a bug here.
    ///     <para>
    ///         Both numbers come off the layout tree's <i>results</i> rather than off the declared
    ///         style, so a percentage padding and a border resolved against the parent are already
    ///         the pixels they came out as.
    ///     </para>
    /// </remarks>
    ContainerBox BoxOf(UiElement element, ContainerKind kind) {
        var node = element.LayoutNode;

        var width = element.Width
            - Layout.GetComputedPadding(node, Edge.Left)
            - Layout.GetComputedPadding(node, Edge.Right)
            - Layout.GetComputedBorder(node, Edge.Left)
            - Layout.GetComputedBorder(node, Edge.Right);

        var height = element.Height
            - Layout.GetComputedPadding(node, Edge.Top)
            - Layout.GetComputedPadding(node, Edge.Bottom)
            - Layout.GetComputedBorder(node, Edge.Top)
            - Layout.GetComputedBorder(node, Edge.Bottom);

        return new ContainerBox(Math.Max(width, 0f), Math.Max(height, 0f), kind);
    }

    /// <summary>Names the containers that were still moving when the settle loop gave up.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called from the one branch that sets <see cref="Settled" /> false, and from
    ///         nowhere else.</b> A container moving is the ordinary case on the way to a fixed point
    ///         — two levels of nesting legitimately move a box on each of two passes — so the list is
    ///         news only on the pass after which there will be no more passes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A handler is the other way the budget runs out and this says nothing about
    ///         it.</b> A document with no <c>@container</c> never walks, so the list is empty and no
    ///         line is written; a document with both gets the containers it can name and the handler
    ///         stays <see cref="Settled" />'s business, because nothing here knows which handler
    ///         asked for what.
    ///     </para>
    /// </remarks>
    void ReportUnsettledContainers() {
        for (var index = 0; index < unsettledContainers.Count; index++) {
            var element = unsettledContainers[index];

            StyleLog.ContainerNeverSettled(
                logger,
                NameOrTagOf(element),
                element.Width,
                element.Height,
                SettlePasses
            );
        }
    }

    /// <summary>What to call a container in a message: its <c>container-name</c>, or its tag.</summary>
    /// <remarks>
    ///     ⚠ The name is what the stylesheet's <c>@container</c> writes, so it is the string an author
    ///     can search for. An unnamed container has only its element name, which is weak — but a
    ///     message naming a box weakly is still the difference between one element and a document.
    /// </remarks>
    string NameOrTagOf(UiElement element) {
        KindOf(element.Style, out var name);

        return name.Length != 0 ? name : element.TagName;
    }
}
