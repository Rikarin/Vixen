// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;

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
///     <para>
///         ⚠ <b>And <see cref="Media" /> is per surface for the same reason, which took longer to
///         arrive.</b> <c>50vw</c> in a torn-off inspector has always meant half of <i>that</i>
///         window, while <c>@media (min-width: 640px)</c> meant the main one — the size was read off
///         the surface and the breakpoint was read off the document. The inconsistency was not an
///         oversight but a consequence: <c>@media</c> was decided at load, so its verdict lived in
///         the rule set, and a rule set is shared by every surface. The verdict is a
///         <see cref="MediaScopes" /> entry now, and the two questions finally answer about the same
///         rectangle.
///     </para>
/// </remarks>
public sealed class UiSurface {
    ColorGamut gamut = ColorGamut.Srgb;
    ColorSchemePreference colorScheme = ColorSchemePreference.NoPreference;
    MediaPreferences preferences;

    internal UiSurface(
        UiDocument document,
        int id,
        UiElement root,
        float width,
        float height,
        float dpiScale,
        DrawList drawing,
        ColorSchemePreference colorScheme,
        MediaPreferences preferences
    ) {
        Document = document;
        Id = id;
        Root = root;
        Drawing = drawing;

        Width = width;
        Height = height;
        DpiScale = dpiScale;
        this.colorScheme = colorScheme;
        this.preferences = preferences;
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

    /// <summary>What this surface can actually show, which decides <c>@media (color-gamut: …)</c> here.</summary>
    /// <remarks>
    ///     ⚠ <b>The swapchain's <i>granted</i> gamut for <i>this</i> window, read back from
    ///     <c>ISwapChain.Gamut</c>.</b> A surface that offered no wide colour space with enough
    ///     precision behind it stays in sRGB whatever was requested, and a stylesheet told otherwise
    ///     picks colours that the presentation maps away again — so this is the same field
    ///     <c>UiGeometryBuilder.Gamut</c> is set from, on the same pane, at the same two moments:
    ///     when the swapchain is created and when it is recreated.
    ///     <para>
    ///         Per surface, and that is the point. An editor with a palette dragged onto a wide
    ///         display and its main window on an ordinary one now gets a different answer in each,
    ///         where before the primary window's answer was the whole document's — and answering
    ///         from whichever pane recreated its swapchain last would have been worse than that.
    ///     </para>
    /// </remarks>
    public ColorGamut Gamut {
        get => gamut;
        set {
            if (gamut == value) {
                return;
            }

            gamut = value;
            Document.Remedia(this);
        }
    }

    /// <summary>Whether the platform's appearance is light or dark for this window.</summary>
    /// <remarks>
    ///     What <c>@media (prefers-color-scheme: …)</c> asks here, and therefore what <c>dark:</c>
    ///     asks under a theme whose <c>--dark-mode</c> is <c>media</c>. A new surface starts from the
    ///     primary's, because appearance is a platform-wide setting rather than a negotiation per
    ///     window — unlike <see cref="Gamut" />, which starts at sRGB and waits to be told.
    /// </remarks>
    public ColorSchemePreference ColorScheme {
        get => colorScheme;
        set {
            if (colorScheme == value) {
                return;
            }

            colorScheme = value;
            Document.Remedia(this);
        }
    }

    /// <summary>The platform's accessibility settings, as this window's <c>@media</c> answers them.</summary>
    /// <remarks>
    ///     <para>
    ///         What <c>motion-reduce:</c>, <c>contrast-more:</c>, <c>forced-colors:</c>,
    ///         <c>inverted-colors:</c> and the two pointer families ask. Set as one value, because
    ///         all six come out of the same platform read — see <see cref="MediaPreferences" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two of the six axes now have an operating system behind them and four still do
    ///         not.</b> <c>PlatformInput.ApplyAccessibility</c> writes <see cref="MediaPreferences.Motion" />
    ///         and the forced-colours pair from <c>IPlatform.Accessibility</c>, on the same terms as
    ///         the appearance beside it; the two pointer axes and <see cref="MediaPreferences.InvertedColors" />
    ///         have no reader on any platform, so a query about those is still answered truthfully
    ///         from what the host has said, which is nothing. Left settable and defaulted to "nothing
    ///         unusual" rather than guessed at, which is the bargain <see cref="Gamut" /> makes one
    ///         property up.
    ///     </para>
    /// </remarks>
    public MediaPreferences Preferences {
        get => preferences;
        set {
            if (preferences == value) {
                return;
            }

            preferences = value;
            Document.Remedia(this);
        }
    }

    /// <summary>What <c>@media</c> is answered against in this window.</summary>
    public MediaContext Media => new(Width, Height, DpiScale, ColorScheme, Gamut, Preferences);

    /// <summary>Its entry in the document's <see cref="MediaScopes" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the same number as <see cref="Id" /> and deliberately not derived from it.</b> A
    ///     scope is allocated by the style engine and written on every element created under
    ///     <see cref="Root" />; an id is the host's handle on a window. Tying them together would
    ///     mean an engine shared between two documents — which nothing does today and nothing should
    ///     be prevented from doing by an accident of numbering.
    /// </remarks>
    internal int Scope { get; init; }

    internal void Measure(float width, float height, float dpiScale, float rootFontSize) {
        Width = width;
        Height = height;
        DpiScale = dpiScale <= 0f ? 1f : dpiScale;

        Metrics = LengthContext.ForViewport(width, height, rootFontSize);
    }

    internal void Retire() => IsRemoved = true;
}
