// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The theme's <c>--accent</c> following the operating system, and still losing to an author.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half of the accent chain that lives in the stylesheet</b>, and the half a
///         platform test cannot reach: <c>Vixen.Platform.Ui</c> writes
///         <c>SystemPalette.PlatformAccentClass</c> onto the root and this sheet is what that class
///         means. Thirty-one declarations in <c>ControlTheme.vcss</c> read <c>--accent</c> — the
///         focus ring, the switch, the spinner, the selection — and until <c>root.system-accent</c>
///         existed the token was a constant nothing outside the sheet could move.
///     </para>
///     <para>
///         ⚠ <b>Both directions are asserted, because only one of them is the feature.</b> The class
///         absent has to leave the theme's own blue alone — a machine nobody could ask must look
///         exactly as it shipped — and an author sheet has to beat the platform, which is what makes
///         this a supplied <i>fallback</i> rather than the operating system overruling a brand
///         colour. A test that only checked the first would pass with the rule deleted, and one that
///         only checked the second would pass with the token hard-wired to <c>AccentColor</c>.
///     </para>
///     <para>
///         The colour is read off a drawn rectangle rather than off the palette, for
///         <c>SystemPaletteWiringTests</c>' reason: what is under test is the value a sheet gets,
///         and the palette is a thing any test could fill for itself.
///     </para>
/// </remarks>
public class ThemeAccentTokenTests {
    /// <summary>The light palette's own accent, as <c>ControlTheme.vcss</c> declares it.</summary>
    static readonly Color4 ThemeAccent = new Color(0x3B, 0x6C, 0xF0).ToLinear();

    /// <summary>Nothing like either blue, so a stale value cannot be mistaken for a fresh one.</summary>
    static readonly Color4 Platform = new Color(0xE8, 0x5D, 0x04).ToLinear();

    [Fact]
    public void The_theme_accent_stands_while_no_platform_has_supplied_one() {
        using var document = Probe();

        Assert.Equal(ThemeAccent, Fill(document));
    }

    [Fact]
    public void The_class_points_the_token_at_the_platform_palette() {
        using var document = Probe();

        Supply(document);

        Assert.Equal(Platform, Fill(document));
    }

    /// <summary>And taking the class off puts the theme's own back, rather than freezing it.</summary>
    [Fact]
    public void Taking_the_class_off_gives_the_theme_its_accent_back() {
        using var document = Probe();

        Supply(document);
        document.Root.RemoveClass(SystemPalette.PlatformAccentClass);

        Assert.Equal(ThemeAccent, Fill(document));
    }

    /// <summary>
    ///     ⚠ An author that declares <c>--accent</c> keeps it on a machine whose user chose
    ///     something else.
    /// </summary>
    /// <remarks>
    ///     The rule is in the user-agent origin and in the <c>base</c> layer, which is two reasons it
    ///     loses; this asserts the outcome rather than either mechanism, because the outcome is what
    ///     the promise is. An application with a brand colour is not asking the operating system.
    /// </remarks>
    [Fact]
    public void An_author_sheet_that_declares_the_token_beats_the_platform() {
        using var document = Probe();

        var brand = new Color(0x12, 0xA5, 0x94).ToLinear();

        document.Load("root { --accent: #12a594; }");
        Supply(document);

        Assert.Equal(brand, Fill(document));
    }

    /// <summary>A document with the control theme and one element painted with the token.</summary>
    static UiDocument Probe() {
        var document = new UiDocument(200f, 100f);

        ControlTheme.Install(document);
        document.Load(".accent-probe { width: 10px; height: 10px; background-color: var(--accent); }");
        document.Root.Add("div", classNames: "accent-probe");

        return document;
    }

    /// <summary>What a host that has read an accent does, without the platform assembly.</summary>
    static void Supply(UiDocument document) {
        document.SystemColors.SetPlatform(SystemColor.AccentColor, Platform);
        document.SystemColors.SetPlatform(SystemColor.AccentColorText, new Color4(1f, 1f, 1f, 1f));
        document.Root.AddClass(SystemPalette.PlatformAccentClass);
    }

    static Color4 Fill(UiDocument document) {
        document.Update();
        document.Draw();

        return document.Drawing.Commands.First(command => command.Kind == DrawCommandKind.Rectangle).Color;
    }
}
