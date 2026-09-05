// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What this document tells <c>@media</c> about the surfaces it is on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three of the five axes were always known here and none of them was ever handed
///         over.</b> <c>StyleEngine.Load</c> has taken a <see cref="MediaContext" /> since the cascade
///         was written and <see cref="UiDocument.Load" /> passed nothing, so every sheet in every real
///         document was evaluated against <c>default</c> — a surface nought pixels wide, at 1×, with
///         no colour-scheme preference and an sRGB gamut. Every responsive variant was dead, every
///         <c>dark:</c> under the <c>media</c> strategy was dead, and
///         <c>@media (color-gamut: p3)</c> could not match on any hardware.
///     </para>
///     <para>
///         ⚠ <b>Two of the five have to be given and three cannot be.</b> The size and the scale are
///         the surface's and are read off it, so they are right by construction and follow a
///         resize without anyone being told. <see cref="UiSurface.Gamut" /> and
///         <see cref="UiSurface.ColorScheme" /> are facts about the <i>presentation</i>, which a
///         document has no way to discover: the honest source for the first is
///         <c>ISwapChain.Gamut</c> — what the surface was granted, not what the monitor advertises —
///         and for the second it is the platform's appearance setting. Both default to the
///         conservative answer, so a host that says nothing gets sRGB and no preference rather than a
///         guess.
///     </para>
///     <para>
///         ⚠ <b>Per surface, which it was not, and the fix was not the obvious one.</b> Rules are
///         shared by every surface of a document — that is the same sharing that keeps one theme
///         across a torn-off window — so while <c>@media</c> was decided at <i>load</i> the verdict
///         lived in the rule set and there could only be one of it. Compiling the sheets again per
///         surface is the obvious answer and is the wrong one: a reload was measured at 42 ms for the
///         editor's twelve sheets, so a four-window editor would pay 170 ms of ExCSS on one drag, and
///         it would also mean four rule sets, four matchers and four interning caches for a set of
///         windows whose whole point is that they share a theme. What moved instead is the verdict:
///         a block's rules are loaded with the <see cref="MediaConditions" /> group they came from,
///         and each surface carries its own answers. The rules stay shared; only the yes-or-no is per
///         window. See <see cref="MediaScopes" />.
///     </para>
///     <para>
///         The properties on this class are the <i>primary</i> surface's, kept because a
///         single-window application should not have to know surfaces exist — the same rule
///         <see cref="UiDocument.Resize(float,float)" /> and <see cref="UiDocument.HitTest(float,float)" />
///         follow. A host with more than one window sets them on <see cref="UiSurface" />.
///     </para>
/// </remarks>
public sealed partial class UiDocument {
    /// <summary>What the primary surface can actually show, which decides <c>@media (color-gamut: …)</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The swapchain's <i>granted</i> gamut, read back from <c>ISwapChain.Gamut</c>, never the
    ///     one that was asked for and never the display's specification sheet.</b> A surface that
    ///     offered no wide colour space with enough precision behind it stays in sRGB whatever was
    ///     requested, and a stylesheet told otherwise picks colours that the presentation then maps
    ///     away again — so the rule that <c>UiGeometryBuilder.Gamut</c> follows is the rule this
    ///     follows, out of the same field, set at the same two moments: when the swapchain is created
    ///     and when it is recreated.
    ///     <para>
    ///         <see cref="Primary" />'s. A second window has its own, on <see cref="UiSurface.Gamut" />,
    ///         because two windows of one editor are routinely on two displays and only one of them
    ///         wide.
    ///     </para>
    /// </remarks>
    public ColorGamut Gamut {
        get => Primary.Gamut;
        set => Primary.Gamut = value;
    }

    /// <summary>Whether the platform's appearance is light or dark, on the primary surface.</summary>
    /// <remarks>
    ///     What <c>@media (prefers-color-scheme: …)</c> asks, and therefore what <c>dark:</c> asks
    ///     under a theme whose <c>--dark-mode</c> is <c>media</c>. A theme using the <c>class</c>
    ///     strategy — the editor's — compiles the variant to a <c>.dark</c> ancestor instead and does
    ///     not read this at all.
    /// </remarks>
    public ColorSchemePreference ColorScheme {
        get => Primary.ColorScheme;
        set => Primary.ColorScheme = value;
    }

    /// <summary>The platform's accessibility settings, on the primary surface.</summary>
    /// <remarks>
    ///     ⚠ <b>A sixth axis that has to be given and cannot be discovered, joining the two the
    ///     paragraph above names.</b> Reduced motion, contrast, forced colours, inverted colours and
    ///     what is pointing at the window are all platform settings, and a document has no way to
    ///     read any of them — so the whole group defaults to "nothing unusual" and waits, exactly as
    ///     the gamut waits at sRGB. ⚠ And the wait is real: nothing in this repository sets this
    ///     yet, nor <see cref="ColorScheme" />, which has been in the same position since it was
    ///     added. That is a hole in the platform layer and it is above the cascade — the queries
    ///     answer truthfully from what the host said.
    /// </remarks>
    public MediaPreferences Preferences {
        get => Primary.Preferences;
        set => Primary.Preferences = value;
    }

    /// <summary>The primary surface's context, as the cascade is evaluating it there.</summary>
    public MediaContext Media => Primary.Media;

    /// <summary>Tells one surface's cascade what it is now, and forgets styles if that moved an answer.</summary>
    /// <param name="surface">The surface.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Forget()" /> only when a verdict actually moved, and the <c>if</c> is
    ///         the whole reason a window drag is affordable.</b> <c>StyleEngine.SetMedia</c> evaluates
    ///         the groups the loader registered and returns false unless one of them changed its
    ///         mind, so a document with no <c>@media</c> in it — which is every stylesheet this
    ///         repository ships — compares one boolean and keeps every computed style it had.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No diagnostic drain here any more, and that is a consequence of the fix rather
    ///         than a hole in it.</b> This used to reload, and a reload could refuse a <c>@media</c>
    ///         block for the first time — the block having been dropped unread at a narrower size.
    ///         Nothing is dropped unread now: the loader recurses into every conditional group
    ///         whatever the verdict, so a condition it cannot read is refused once, at load, where
    ///         <see cref="DrainStyleDiagnostics" /> already collects it. A query that could only be
    ///         reported by a window that happened to grow is exactly the silence this subsystem keeps
    ///         producing, and there is now no size at which one first appears.
    ///     </para>
    /// </remarks>
    internal void Remedia(UiSurface surface) {
        if (Styles.SetMedia(surface.Scope, surface.Media)) {
            Forget();
        }
    }
}
