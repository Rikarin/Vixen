// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What this document tells <c>@media</c> about the surface it is on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three of the five axes were always known here and none of them was ever handed
///         over.</b> <c>StyleEngine.Load</c> has taken a <see cref="MediaContext" /> since the cascade
///         was written and <see cref="UiDocument.Load" /> passed nothing, so every sheet in every real
///         document was evaluated against <c>default</c> — a surface nought pixels wide, at 1×, with
///         no colour-scheme preference and an sRGB gamut. The consequences were not subtle and were
///         entirely invisible:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Every responsive variant was dead.</b> <c>md:p-4</c> compiles to
///             <c>@media (min-width: 768px)</c>, and <c>0 ≥ 768</c> is false in every window at every
///             size, so the block was dropped at load and the class matched nothing. The same for
///             <c>sm:</c>, <c>lg:</c>, <c>xl:</c>, <c>2xl:</c> and any breakpoint a theme names.
///         </item>
///         <item>
///             <b><c>dark:</c> under the <c>media</c> strategy was dead</b>, for the same reason: the
///             preference was never anything but <see cref="ColorSchemePreference.NoPreference" />.
///             The <c>class</c> strategy compiles to a <c>.dark</c> ancestor and was unaffected, which
///             is why the editor never noticed.
///         </item>
///         <item>
///             <b><c>@media (color-gamut: p3)</c> could never match.</b> The swapchain knows what it
///             was granted and <c>UiGeometryBuilder</c> is already told — it maps every colour it
///             emits against it — and the same fact never reached the cascade.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Two of the five have to be given and three cannot be.</b> The size and the scale are
///         the primary surface's and are read off it, so they are right by construction and follow a
///         resize without anyone being told. <see cref="UiDocument.Gamut" /> and
///         <see cref="UiDocument.ColorScheme" /> are facts about the <i>presentation</i>, which a
///         document has no way to discover: the honest source for the first is
///         <c>ISwapChain.Gamut</c> — what the surface was granted, not what the monitor advertises —
///         and for the second it is the platform's appearance setting. Both default to the
///         conservative answer, so a host that says nothing gets sRGB and no preference rather than a
///         guess.
///     </para>
///     <para>
///         <b>The primary surface and not each surface, which is a real limitation and is stated
///         rather than papered over.</b> <c>@media</c> is evaluated at load and the rules it produces
///         are shared by every surface of the document — that is the same sharing that keeps one theme
///         across a torn-off window — so a document cannot currently answer <c>max-width</c>
///         differently in two windows. Per-surface media would mean a rule set per surface, which is a
///         much larger change than this one and is owed rather than done.
///     </para>
/// </remarks>
public sealed partial class UiDocument {
    ColorGamut gamut = ColorGamut.Srgb;
    ColorSchemePreference colorScheme = ColorSchemePreference.NoPreference;

    /// <summary>What the surface can actually show, which decides <c>@media (color-gamut: …)</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The swapchain's <i>granted</i> gamut, read back from <c>ISwapChain.Gamut</c>, never the
    ///     one that was asked for and never the display's specification sheet.</b> A surface that
    ///     offered no wide colour space with enough precision behind it stays in sRGB whatever was
    ///     requested, and a stylesheet told otherwise picks colours that the presentation then maps
    ///     away again — so the rule that <c>UiGeometryBuilder.Gamut</c> follows is the rule this
    ///     follows, out of the same field, set at the same two moments: when the swapchain is created
    ///     and when it is recreated.
    /// </remarks>
    public ColorGamut Gamut {
        get => gamut;
        set {
            if (gamut == value) {
                return;
            }

            gamut = value;
            Remedia();
        }
    }

    /// <summary>Whether the platform's appearance is light or dark.</summary>
    /// <remarks>
    ///     What <c>@media (prefers-color-scheme: …)</c> asks, and therefore what <c>dark:</c> asks
    ///     under a theme whose <c>--dark-mode</c> is <c>media</c>. A theme using the <c>class</c>
    ///     strategy — the editor's — compiles the variant to a <c>.dark</c> ancestor instead and does
    ///     not read this at all.
    /// </remarks>
    public ColorSchemePreference ColorScheme {
        get => colorScheme;
        set {
            if (colorScheme == value) {
                return;
            }

            colorScheme = value;
            Remedia();
        }
    }

    /// <summary>The whole context, as the cascade is currently evaluating it.</summary>
    public MediaContext Media =>
        new(Primary.Width, Primary.Height, Primary.DpiScale, ColorScheme, Gamut);

    /// <summary>Tells the cascade what the surface is now, and reloads it if that changed an answer.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Forget()" /> only when something actually reloaded, and the <c>if</c> is the
    ///     whole reason a window drag is still affordable.</b> <see cref="StyleEngine.SetMedia" />
    ///     replays the conditions the loader saw and returns false unless one of them changed its
    ///     mind, so a document with no <c>@media</c> in it — which is every stylesheet this repository
    ///     ships — pays a hash-set walk over nothing and keeps every computed style it had.
    /// </remarks>
    void Remedia() {
        if (Styles.SetMedia(Media)) {
            Forget();
        }
    }
}
