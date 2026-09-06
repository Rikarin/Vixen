// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>The platform's appearance and contrast settings reaching the CSS system colours.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The join, on the terms <see cref="AccessibilityWiringTests" /> states them.</b>
///         <c>SystemPalette</c> could be filled by anybody and a test that wrote one itself would
///         pass against a palette nothing ever fills — which is exactly the shape the
///         <c>forced-colors</c> media feature was in for its whole life. So these assertions go
///         through <c>PlatformInput</c> and read the colour a sheet would get.
///     </para>
///     <para>
///         ⚠ <b>The palette is a product of two settings and neither <c>Apply…</c> method knows
///         both.</b> A high-contrast machine wants the forced table in either appearance, so the two
///         are resolved together in <c>Repalette</c>; the ordering tests below are what would go red
///         if either method went back to writing its own half.
///     </para>
/// </remarks>
public class SystemPaletteWiringTests {
    static Color4 Canvas(UiDocument document) => document.SystemColors[SystemColor.Canvas];

    [Fact]
    public void A_dark_appearance_gives_the_dark_palette() {
        using var document = new UiDocument(200f, 100f);

        Assert.NotEqual(Light(SystemColor.Canvas), Dark(SystemColor.Canvas));
        Assert.Equal(Light(SystemColor.Canvas), Canvas(document));

        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);

        Assert.Equal(Dark(SystemColor.Canvas), Canvas(document));
    }

    [Fact]
    public void An_unknown_appearance_leaves_the_light_defaults() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Unknown);

        // Consistent with `ApplyColorScheme`'s own rule: nothing expressed is not the same as dark,
        // and CSS answers both `prefers-color-scheme` queries no.
        Assert.Equal(Light(SystemColor.Canvas), Canvas(document));
    }

    [Fact]
    public void High_contrast_replaces_the_palette_with_the_forced_one() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));

        Assert.Equal(Forced(SystemColor.Canvas), Canvas(document));
        Assert.Equal(Forced(SystemColor.CanvasText), document.SystemColors[SystemColor.CanvasText]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void High_contrast_outlasts_an_appearance_change_whichever_order_they_arrive_in(bool contrastFirst) {
        using var document = new UiDocument(200f, 100f);

        if (contrastFirst) {
            PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));
            PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        } else {
            PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
            PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));
        }

        // ⚠ The defect two independent writers would have: whichever platform event arrived last
        // would win, so a high-contrast user would lose the forced palette the first time they
        // toggled dark mode — and every assertion about the palette in isolation would still pass.
        Assert.Equal(Forced(SystemColor.Canvas), Canvas(document));
    }

    [Fact]
    public void Turning_high_contrast_off_returns_the_appearance_the_platform_last_reported() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));
        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: false));

        Assert.Equal(Dark(SystemColor.Canvas), Canvas(document));
    }

    [Fact]
    public void A_sheet_that_names_a_system_colour_follows_the_platform_without_being_reloaded() {
        using var document = new UiDocument(200f, 100f);
        document.Load(".probe { width: 10px; height: 10px; background-color: Canvas; }");
        document.Root.Add("div", classNames: "probe");
        document.Update();
        document.Draw();

        Assert.Equal(Light(SystemColor.Canvas), Fill(document));

        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        document.Draw();

        // ⚠ No `Load` between the two draws, which is the shape #838 asked for: `SetMedia`
        // re-evaluates media conditions and deliberately does not reload sheets, so a semantic colour
        // that needed a reload to change would have been the wrong answer however well it worked.
        Assert.Equal(Dark(SystemColor.Canvas), Fill(document));
    }

    /// <summary>
    ///     A colour the host read from the operating system outlives every later appearance and
    ///     contrast change, and a sheet naming that role gets it without a reload.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the seam the remaining half of #838 needs, and the arrangement it
    ///         replaces did not survive one toggle.</b> <c>Repalette</c> resets the whole table, and
    ///         it is called from <i>both</i> <c>ApplyColorScheme</c> and <c>ApplyAccessibility</c> —
    ///         two events, from two places, on two cadences. So the instruction a host was given —
    ///         write the real palette over the top afterwards — held its colours only until whichever
    ///         of the two arrived next, and then the window went quietly back to the browser
    ///         defaults. No error, no picture that looks broken, just the wrong blue.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The roles nobody supplied still have to move</b>, which is the half that makes
    ///         this a substitution rather than a freeze. A partial read is the normal case rather
    ///         than a special one — a platform answers the roles it has and no others.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><s>On macOS that is the accent and the highlight and no more, because
    ///         <c>NSColor</c> wants an <c>NSApplication</c> an SDL process has not made.</s>
    ///         Measured on 2026-09-06 and false</b> — see <c>PlatformInput.Repalette</c>'s remarks.
    ///         With <c>NSApp</c> nil, <c>+[NSColor labelColor]</c> and its siblings resolve, follow
    ///         the system appearance, and follow <c>+[NSAppearance setCurrentAppearance:]</c> when
    ///         one is named, on a secondary thread. The refusal was true of
    ///         <c>NSApp.effectiveAppearance</c> and was carried across to <c>NSColor</c>, which is a
    ///         class method and does not need the application object.
    ///     </para>
    ///     <para>
    ///         <c>Canvas</c> rather than <c>Highlight</c>, ⚠ <b>which used to be forced and is now
    ///         only a habit.</b> ExCSS normalised the five CSS2 system colours it knows into fixed
    ///         <c>rgb()</c> at stylesheet parse time, so a test written on one of them was asserting
    ///         the CSS parser's constants against a palette nothing filled;
    ///         <c>StyleSheetLoader.CarrySystemColours</c> closed that, and all fifteen keywords reach
    ///         <see cref="SystemPalette" /> now.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_colour_read_from_the_platform_outlives_an_appearance_change() {
        using var document = new UiDocument(200f, 100f);
        document.Load(".probe { width: 10px; height: 10px; background-color: Canvas; }");
        document.Root.Add("div", classNames: "probe");

        var platform = new Color(0x2E, 0xC4, 0x7A).ToLinear();

        document.SystemColors.SetPlatform(SystemColor.Canvas, platform);
        document.Update();
        document.Draw();

        Assert.True(document.SystemColors.IsFromPlatform(SystemColor.Canvas));
        Assert.Equal(platform, Fill(document));

        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        document.Draw();

        Assert.Equal(platform, Fill(document));

        // The roles nobody read still follow the appearance, or this would be a frozen palette
        // rather than a substituted one.
        Assert.Equal(Dark(SystemColor.CanvasText), document.SystemColors[SystemColor.CanvasText]);

        // And the other of the two events, which is the one that arrives from somewhere else and is
        // what the old arrangement lost the palette to.
        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));
        document.Draw();

        Assert.Equal(platform, Fill(document));
        Assert.Equal(Forced(SystemColor.CanvasText), document.SystemColors[SystemColor.CanvasText]);

        // ⚠ And giving it back is a decision the host makes rather than one a reset makes for it:
        // the role returns to the tables at the next repalette and not before, because the palette
        // holds no memory of which of the three tables it was last filled from.
        Assert.True(document.SystemColors.ClearPlatform(SystemColor.Canvas));

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: false));
        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Light);
        document.Draw();

        Assert.Equal(Light(SystemColor.Canvas), Fill(document));
    }

    /// <summary>`Set` is the one-off it says it is, so the two writes cannot be confused.</summary>
    /// <remarks>
    ///     The pair the test above needs to mean anything. If every write survived a reset, an
    ///     appearance change would stop working the moment anybody touched the palette, and
    ///     "survives" would be a claim about nothing.
    /// </remarks>
    [Fact]
    public void A_plain_set_does_not_outlive_the_next_repalette() {
        using var document = new UiDocument(200f, 100f);

        var once = new Color(0x2E, 0xC4, 0x7A).ToLinear();

        document.SystemColors.Set(SystemColor.Canvas, once);

        Assert.Equal(once, document.SystemColors[SystemColor.Canvas]);
        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.Canvas));

        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);

        Assert.Equal(Dark(SystemColor.Canvas), document.SystemColors[SystemColor.Canvas]);
    }

    static Color4 Fill(UiDocument document) =>
        document.Drawing.Commands.First(command => command.Kind == DrawCommandKind.Rectangle).Color;

    static Color4 Light(SystemColor colour) => Of(SystemPalette.Light, colour);

    static Color4 Dark(SystemColor colour) => Of(SystemPalette.Dark, colour);

    static Color4 Forced(SystemColor colour) => Of(SystemPalette.HighContrast, colour);

    static Color4 Of(ReadOnlySpan<uint> table, SystemColor colour) {
        var packed = table[(int)colour];
        return new Color((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed).ToLinear();
    }
}
