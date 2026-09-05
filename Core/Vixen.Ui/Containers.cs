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
///         <see cref="SettlingPasses" /> equal to one. A container sized by its contents can flip on
///         every pass instead; that does not hang, it exhausts the budget and reports
///         <see cref="Settled" /> false, which is the visible failure doc 43 § D3 said it would be.
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

        var moved = Styles.Tree.GetContainerScope(node) != scope
            || Styles.Tree.GetProvidedContainerScope(node) != provided;

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
}
