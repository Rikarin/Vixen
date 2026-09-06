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
