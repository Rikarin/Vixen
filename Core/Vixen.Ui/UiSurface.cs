// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>One rectangle a document is laid out into, drawn onto and clicked in.</summary>
/// <remarks>
///     <para>
///         <b>A document is a tree; a surface is a place to show part of one.</b> Until this existed
///         the two were the same thing — one root, one size, one draw list — and a second window
///         therefore meant a second document. It cannot: a panel dragged from the main window into a
///         torn-off one has to keep its scroll offset, its selection and whatever the user has
///         half-typed, and the only operation that preserves those is
///         <see cref="UiDocument.Reparent" />, which is <i>within</i> a document by construction.
///         Making a window a surface rather than a document turns "move a panel to another window"
///         into the reparent the docking host already performs.
///     </para>
///     <para>
///         ⚠ <b>Every surface after the first is an ordinary element under
///         <see cref="UiDocument.Root" />, and that is deliberate.</b> It keeps one style tree, so a
///         torn-off panel inherits the theme, matches the same stylesheets and resolves <c>rem</c>
///         against the same root — and it keeps one focus, one pointer capture and one gesture
///         recogniser, which is what lets a drag that starts in one window finish in another. What
///         the surface root does <i>not</i> do is take part in its parent's flex layout: it is
///         removed from the layout tree's child list and laid out on its own, against its own size.
///     </para>
///     <para>
///         ⚠ <b><see cref="DpiScale" /> is per surface, because two windows are routinely on two
///         displays.</b> It is not a scale the document applies to anything — lengths stay in
///         logical points everywhere above the renderer — it is the grid the finished layout is
///         snapped to, so that a one-pixel border on a 2× display is one physical pixel rather than
///         one and a half.
///     </para>
/// </remarks>
public sealed class UiSurface {
    internal UiSurface(UiDocument document, int id, UiElement root, float width, float height, float dpiScale, DrawList drawing) {
        Document = document;
        Id = id;
        Root = root;
        Drawing = drawing;

        Width = width;
        Height = height;
        DpiScale = dpiScale;
    }

    /// <summary>What tells the surfaces of one document apart.</summary>
    /// <remarks>
    ///     Zero is the primary surface and never reused; a host that keys its windows by this can
    ///     rely on a closed surface's id not coming back on the next one.
    /// </remarks>
    public int Id { get; }

    /// <summary>The document it shows part of.</summary>
    public UiDocument Document { get; }

    /// <summary>The element it is laid out from.</summary>
    /// <remarks>
    ///     <see cref="UiDocument.Root" /> for the primary surface, and an element under it for every
    ///     other one. Application content goes <i>inside</i> this rather than replacing it.
    /// </remarks>
    public UiElement Root { get; }

    /// <summary>Whether this is the surface the document was created with.</summary>
    /// <remarks>It cannot be removed, for the reason the root cannot: a document is its tree.</remarks>
    public bool IsPrimary => Id == 0;

    /// <summary>Its width in device-independent pixels.</summary>
    public float Width { get; private set; }

    /// <summary>Its height.</summary>
    public float Height { get; private set; }

    /// <summary>How many physical pixels one device-independent one is here.</summary>
    public float DpiScale { get; private set; }

    /// <summary>The commands the last draw produced for it.</summary>
    /// <remarks>One list per surface, because one window's frame is not another's.</remarks>
    public DrawList Drawing { get; }

    /// <summary>Whether it has been taken out of the document.</summary>
    public bool IsRemoved { get; private set; }

    /// <summary>The lengths <c>vw</c>, <c>vh</c> and <c>rem</c> measure against here.</summary>
    /// <remarks>
    ///     ⚠ <b>Per surface, and that is the whole reason it is not read off the document.</b>
    ///     <c>50vw</c> in a torn-off inspector means half of <i>that</i> window; resolving it against
    ///     the main window would size a 400-pixel palette against a 3840-pixel display.
    /// </remarks>
    public LengthContext Metrics { get; private set; }

    internal void Measure(float width, float height, float dpiScale, float rootFontSize) {
        Width = width;
        Height = height;
        DpiScale = dpiScale <= 0f ? 1f : dpiScale;

        Metrics = LengthContext.ForViewport(width, height, rootFontSize);
    }

    internal void Retire() => IsRemoved = true;
}
