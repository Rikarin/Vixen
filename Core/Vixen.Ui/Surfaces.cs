// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

public sealed partial class UiDocument {
    readonly List<UiSurface> surfaces = [];

    /// <summary>The next surface id, never reused within a document.</summary>
    /// <remarks>
    ///     ⚠ Monotonic rather than an index into <see cref="surfaces" />. A host keys its windows by
    ///     the id, and an id that came back after a close would deliver the closed window's next
    ///     resize to whatever had taken its place in the list.
    /// </remarks>
    int nextSurface = 1;

    UiSurface? keySurface;

    /// <summary>Everywhere this document is shown, the primary one first.</summary>
    public IReadOnlyList<UiSurface> Surfaces => surfaces;

    /// <summary>The surface the document was created with.</summary>
    /// <remarks>
    ///     Its root is <see cref="Root" />. It is what every call that does not name a surface means,
    ///     which is what keeps a single-window application from having to know surfaces exist.
    /// </remarks>
    public UiSurface Primary => surfaces[0];

    /// <summary>The surface the window manager says the user is in, or <c>null</c> if none is.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>NSApp.keyWindow</c>, and until this existed there was no answer to the
    ///         question.</b> Keys are not routed by surface — <see cref="Dispatch(KeyEvent)" /> takes
    ///         none, unlike the pointer and the wheel, because the focus is the document's — so with
    ///         nothing focused every keystroke landed on <see cref="Primary" />'s root. In a
    ///         one-window application that is right by construction; in one that has torn a panel off
    ///         it means a key pressed in the inspector ran against the main window.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written by whoever bridges the platform, and it was being thrown away.</b>
    ///         <c>PlatformEventKind.WindowFocusGained</c> and <c>WindowFocusLost</c> are produced by
    ///         every backend the engine has and the UI bridge had no arm for either, so they fell to
    ///         its <c>default</c> and were dropped — a producer with no consumer, which is this
    ///         repository's standing defect with the ends swapped.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does not move the focus and must not.</b> <see cref="Focused" /> stays a single
    ///         document-global element: this is the fallback for when there is none, not a second
    ///         focus. A per-surface <c>Focused</c> is the larger change and is owed; what this buys is
    ///         that the fallback is the window the user is looking at rather than the first one the
    ///         application happened to open.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The surface belongs to another document.</exception>
    public UiSurface? KeySurface {
        get => keySurface;
        set {
            if (value is not null && !ReferenceEquals(value.Document, this)) {
                throw new ArgumentException("that surface belongs to another document.", nameof(value));
            }

            keySurface = value;
        }
    }

    /// <summary>Where a keystroke goes when nothing holds the focus.</summary>
    /// <remarks>
    ///     ⚠ Three <c>Dispatch</c> overloads and <see cref="CommandRoute.Origin" /> all wrote
    ///     <c>Focused ?? Root</c> independently, which is four places that had to agree about a rule
    ///     none of them stated. They are one expression now, and the key surface slots into the middle
    ///     of it rather than into four.
    /// </remarks>
    internal UiElement KeyTarget => Focused ?? keySurface?.Root ?? Root;

    /// <summary>Raised when a surface is added or taken away.</summary>
    /// <remarks>
    ///     What a host hangs a window on. It fires <i>after</i> the surface is in
    ///     <see cref="Surfaces" /> and after a removed one is out of it, so a handler that walks the
    ///     list sees the state the event is about rather than the one before it.
    /// </remarks>
    public event Action<UiDocument, UiSurface>? SurfaceAdded;

    /// <inheritdoc cref="SurfaceAdded" />
    public event Action<UiDocument, UiSurface>? SurfaceRemoved;

    /// <summary>Adds somewhere else to show part of this document.</summary>
    /// <param name="width">Its width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="dpiScale">How many physical pixels one of those is.</param>
    /// <param name="owner">
    ///     Where in the tree the surface's root goes, or <c>null</c> for <see cref="Root" />.
    /// </param>
    /// <returns>The surface, whose <see cref="UiSurface.Root" /> is the application's to fill.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The root it hands back is a child of an element of this document, and content
    ///         goes inside it rather than in its place.</b> That is what keeps one style tree — a
    ///         torn-off panel matches the same stylesheets and inherits the same theme — and it is
    ///         what makes <see cref="Reparent" /> able to move a panel between windows at all, since
    ///         reparenting is within a document by construction.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="owner" /> is not decoration, and the default is the wrong answer
    ///         for a control.</b> A routed event bubbles up the element tree, so a control that opened
    ///         a window under the document root would never hear a click on anything inside it: the
    ///         chain from a torn-off tab runs to the surface root and then straight to the document,
    ///         missing the control that put it there. Naming the owner puts the window's contents
    ///         under it, and the tab in the second window is as reachable as the one in the first.
    ///     </para>
    ///     <para>
    ///         It is taken out of the <i>layout</i> tree's child list, though, because a second
    ///         window is not a flex item of the first. It is laid out on its own against its own
    ///         size, and <see cref="Update" /> is what does both.
    ///     </para>
    /// </remarks>
    public UiSurface CreateSurface(float width, float height, float dpiScale = 1f, UiElement? owner = null) {
        ThrowIfDisposed();
        owner ??= Root;

        if (!ReferenceEquals(owner.Document, this)) {
            throw new ArgumentException("that element belongs to another document.", nameof(owner));
        }

        var root = Create("ui-surface", owner);

        // ⚠ Out of the layout tree and not out of the element tree. Layout is the only one of the
        // three stores where being a child would be wrong: the style tree needs the parent link for
        // inheritance and selectors, and the element tree needs it both because an element outside a
        // document is a removed element and because that chain is what a routed event climbs.
        Layout.RemoveChild(owner.LayoutNode, root.LayoutNode);

        // ⚠ The colour scheme is carried over from the primary and the gamut is not, and the two
        // defaults differ because the two facts do. An appearance preference is a platform setting
        // that every window of an application shares; a gamut is negotiated per swapchain, so a new
        // window starts at the conservative sRGB and waits for its host to publish what it was
        // actually granted — see `EditorPane.Publish`.
        var surface = new UiSurface(
            this,
            nextSurface++,
            root,
            width,
            height,
            dpiScale,
            new DrawList(),
            Primary.ColorScheme
        ) {
            Scope = Styles.Scopes.Create(default)
        };

        // ⚠ On the surface root and on nothing else, because every element created under it inherits
        // the scope through `StyleTree.CreateElement` — including a whole panel reparented in, since
        // `Reparent` rebuilds a moved subtree's slots rather than moving them. This is the one write;
        // the rest is propagation the tree already does.
        Styles.Tree.SetScope(root.StyleNode, surface.Scope);

        Adopt(surface, width, height, dpiScale);

        // After `Adopt`, which is what measures it — a scope told about a nought-by-nought surface
        // would answer every `min-width` no until the first resize.
        Remedia(surface);

        SurfaceAdded?.Invoke(this, surface);
        return surface;
    }

    /// <summary>Takes a surface, and everything still in it, out of the document.</summary>
    /// <param name="surface">The surface.</param>
    /// <returns>Whether it was one of this document's, and not the primary.</returns>
    /// <remarks>
    ///     ⚠ <b>Whatever is left inside goes with it.</b> A window closing is not a reason to keep
    ///     its contents alive, and a caller that wants them keeps them by reparenting them out
    ///     first — which is exactly what the docking host does when a floating window is closed and
    ///     its panels are docked back.
    /// </remarks>
    public bool RemoveSurface(UiSurface surface) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.IsPrimary || !ReferenceEquals(surface.Document, this) || !surfaces.Remove(surface)) {
            return false;
        }

        // ⚠ Before `Retire`, and it is not tidiness: a closed window that stayed the key surface
        // would leave `KeyTarget` pointing into a subtree that has been removed from the document,
        // and `UiElement.Document` throws on one of those. The window manager will name the next key
        // window in its own time; until it does, the answer is the primary surface again.
        if (ReferenceEquals(keySurface, surface)) {
            keySurface = null;
        }

        surface.Root.MarkSurface(null);
        surface.Retire();

        Remove(surface.Root);
        SurfaceRemoved?.Invoke(this, surface);

        return true;
    }

    /// <summary>Changes a surface's size, and the scale of the display it is on.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="width">Its new width in device-independent pixels.</param>
    /// <param name="height">Its new height.</param>
    /// <param name="dpiScale">How many physical pixels one of those is now.</param>
    /// <remarks>
    ///     ⚠ <b>Forgets what every element applied, for two reasons rather than one.</b> The size is
    ///     <see cref="Resize(float,float)" />'s: nothing an element declared has changed, so its
    ///     interned computed style is the same object and the pass's reference test would skip it
    ///     with every <c>vw</c> in the window still holding the old number. The scale is the pixel
    ///     grid's: the rounding pass reuses a subtree it did not recompute, so a window dragged onto
    ///     a 2× display would keep the 1× grid for everything that did not otherwise change.
    /// </remarks>
    public void Resize(UiSurface surface, float width, float height, float dpiScale) {
        // ⚠ Covers `Resize(float, float)` as well, which is the primary surface's spelling of this.
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(surface);

        if (!ReferenceEquals(surface.Document, this)) {
            throw new ArgumentException("that surface belongs to another document.", nameof(surface));
        }

        var rescaled = !surface.DpiScale.Equals(dpiScale <= 0f ? 1f : dpiScale);
        surface.Measure(width, height, dpiScale, rootFontSize);

        // ⚠ The layout tree as well as the styles, and only the styles is not enough. `Forget` makes
        // the next pass write every element's layout style — but `SetStyle` compares and returns
        // without dirtying when nothing differs, and a scale change differs in nothing an element
        // declared. So `CalculateLayout` answers from the cache, the rounding pass never runs, and
        // the window keeps the previous display's grid for as long as nothing else about it changes.
        if (rescaled) {
            Layout.Invalidate(surface.Root.LayoutNode);
        }

        // ⚠ <b>This surface, not the primary one, and that is the second half of the fix.</b> A
        // resize is the one thing that can change what `@media` answers, and for two phases nobody
        // re-asked it at all; then it was re-asked only for the primary window, because the verdict
        // lived in the rule set and a rule set is shared. It lives on the surface now — see
        // `MediaScopes` — so a torn-off inspector crossing its own breakpoint restyles itself and
        // leaves the main window alone.
        Remedia(surface);

        Forget();
    }

    /// <summary>Which surface an element is shown in.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The surface, or <c>null</c> if the element is not in this document.</returns>
    /// <remarks>
    ///     A walk up the tree rather than a stored field, because reparenting moves whole subtrees
    ///     between windows and a field on every element would be a field every move had to rewrite.
    ///     Depth is small and the question is asked at human speed.
    /// </remarks>
    public UiSurface? SurfaceOf(UiElement element) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(element);

        if (!ReferenceEquals(element.Document, this)) {
            return null;
        }

        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (walk.SurfaceRoot is { } surface) {
                return surface;
            }
        }

        return null;
    }

    /// <summary>Where a position among an element's children lands among its layout node's.</summary>
    /// <param name="parent">The parent, in the element tree.</param>
    /// <param name="index">A position among <see cref="UiElement.Children" />.</param>
    /// <returns>The matching position among the layout node's children.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two child lists are not the same length, and every writer that hands one an
    ///         index taken from the other has to come through here.</b> <see cref="CreateSurface" />
    ///         takes a surface root out of the layout tree's child list and deliberately leaves it in
    ///         the element tree, so a parent that owns <i>n</i> surface roots has <i>n</i> more
    ///         element children than layout children — and an element index used raw as a layout one
    ///         is that much too high.
    ///     </para>
    ///     <para>
    ///         It read as an obscure corner and is the docking host's ordinary path: a floating
    ///         window's panels being docked back is a <see cref="Reparent" /> into the element that
    ///         owns the window's surface root, which is the exact shape that overshoots.
    ///         <c>LayoutTree.InsertChild</c> refused it, so the headline operation surfaces exist to
    ///         support threw — see <c>SurfaceIndexTests</c>.
    ///     </para>
    ///     <para>
    ///         The invariant this keeps is that the layout child list is the element child list with
    ///         the surface roots struck out, in the same order. Appending preserves it for free,
    ///         which is why <see cref="Adopt(UiElement,string,UiElement,string,System.ReadOnlySpan{string})" />
    ///         needs nothing; only an insertion at a position does.
    ///     </para>
    ///     <para>
    ///         O(index) rather than a counter kept per element, because it is asked at human
    ///         speed — a drag that ends, a panel docked, a hot reload — and a maintained count would
    ///         be a second fact about the tree that every mutation had to remember to update.
    ///     </para>
    /// </remarks>
    static int LayoutIndexOf(UiElement parent, int index) {
        var children = parent.ChildList;
        var layout = 0;

        for (var i = 0; i < index; i++) {
            if (children[i].SurfaceRoot is null) {
                layout++;
            }
        }

        return layout;
    }

    void Adopt(UiSurface surface, float width, float height, float dpiScale) {
        surface.Measure(width, height, dpiScale, rootFontSize);
        surface.Root.MarkSurface(surface);

        surfaces.Add(surface);
        Invalidate();
    }
}
