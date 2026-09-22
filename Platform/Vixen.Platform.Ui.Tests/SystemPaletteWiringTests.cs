// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
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

    /// <summary>
    ///     ⚠ The accent read reaching a sheet, which is the link this whole chain was missing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>SetPlatform</c> had no production caller anywhere in the repository and no
    ///         platform read one existed</b> — the palette carried an
    ///         <c>AccentColor</c>/<c>AccentColorText</c> pair, <c>ControlTheme.vcss</c> drew
    ///         thirty-one declarations with <c>--accent</c>, and nothing joined them. So this
    ///         asserts the join through <c>PlatformInput</c> and reads the colour off a drawn
    ///         rectangle rather than off the palette, for the reason this file's own remarks give.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sRGB-to-linear conversion is asserted as a <i>difference</i>, not as an
    ///         equality alone.</b> <see cref="SystemPalette" /> holds linear and every platform
    ///         reports sRGB; its own remarks record that handing it an sRGB colour makes a palette
    ///         that is visibly too bright with nothing anywhere reporting it. A test that only
    ///         compared against <c>Color4.FromSrgb</c> would also pass if both sides forgot, so the
    ///         raw value is denied by name.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_platform_accent_reaches_a_sheet_and_outlives_an_appearance_change() {
        using var document = new UiDocument(200f, 100f);
        document.Load(".probe { width: 10px; height: 10px; background-color: AccentColor; }");
        document.Root.Add("div", classNames: "probe");

        var srgb = new Color4(0.5f, 0.25f, 0.75f, 1f);

        PlatformInput.ApplyAccent(document, new SystemAccent(srgb, new Color4(1f, 1f, 1f, 1f)));
        document.Update();
        document.Draw();

        Assert.True(document.SystemColors.IsFromPlatform(SystemColor.AccentColor));
        Assert.Equal(Color4.FromSrgb(srgb), document.SystemColors[SystemColor.AccentColor]);
        Assert.Equal(Color4.FromSrgb(srgb), Fill(document));

        // ⚠ And not the sRGB numbers themselves, which is the failure that looks like a working
        // palette until somebody compares two screenshots.
        Assert.NotEqual(srgb, Fill(document));

        // The two events that re-apply a default table, from two places on two cadences. An accent
        // written with `Set` rather than `SetPlatform` would be gone after the first of these.
        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        document.Draw();

        Assert.Equal(Color4.FromSrgb(srgb), Fill(document));

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));

        // ⚠ Read off the palette rather than off the frame for this third stage, and the reason is
        // worth writing down: under forced colours the document paints its own `Canvas` behind
        // everything, so the first rectangle in the list is the root's and not the probe's. A
        // `Fill(document)` here reports black and would look exactly like a lost accent.
        Assert.Equal(Color4.FromSrgb(srgb), document.SystemColors[SystemColor.AccentColor]);

        // And the roles nobody read still follow, or this would be a frozen palette.
        Assert.Equal(Forced(SystemColor.Canvas), document.SystemColors[SystemColor.Canvas]);
    }

    /// <summary>A platform that stops answering gives the role back rather than freezing it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>ClearPlatform</c> forgets and does not revert</b> — the palette holds no memory
    ///     of which of its three tables it was last filled from — so <c>ApplyAccent</c> repalettes
    ///     after a clear. Without that, a host that lost its accent read would keep painting the
    ///     last one until the user next toggled dark mode, which is a stale colour nothing
    ///     announces.
    /// </remarks>
    [Fact]
    public void An_accent_the_platform_stops_reporting_goes_back_to_the_table() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyAccent(
            document,
            new SystemAccent(new Color4(0.5f, 0.25f, 0.75f, 1f), new Color4(1f, 1f, 1f, 1f))
        );

        Assert.True(document.Root.HasClass(SystemPalette.PlatformAccentClass));

        PlatformInput.ApplyAccent(document, SystemAccent.Unknown);

        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.AccentColor));
        Assert.False(document.Root.HasClass(SystemPalette.PlatformAccentClass));
        Assert.Equal(Light(SystemColor.AccentColor), document.SystemColors[SystemColor.AccentColor]);
    }

    /// <summary>Half a read supplies the role and does not repaint the theme's token.</summary>
    /// <remarks>
    ///     ⚠ <b>The class is what points <c>--accent</c> at the palette, and it goes on for the
    ///     <i>pair</i>.</b> <c>SystemColor</c>'s ordering exists because a palette guarantees
    ///     contrast within a pair and guarantees nothing across two — so an accent taken from the
    ///     system with the theme's own text colour left on top of it is how a pale accent gets white
    ///     text. A platform that can answer only half still supplies that half to anything writing
    ///     <c>AccentColor</c> directly; what it does not get to do is move the token.
    /// </remarks>
    [Fact]
    public void An_accent_without_its_text_colour_fills_the_role_but_not_the_token() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyAccent(document, new SystemAccent(new Color4(0.5f, 0.25f, 0.75f, 1f)));

        Assert.True(document.SystemColors.IsFromPlatform(SystemColor.AccentColor));
        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.AccentColorText));
        Assert.False(document.Root.HasClass(SystemPalette.PlatformAccentClass));
    }

    /// <summary>
    ///     The platform's label colour reaches a sheet that names <c>CanvasText</c>, without a
    ///     reload, and outlives the two events that reset the tables underneath it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The done-looks-like of #838, end to end through the method the hosts call.</b>
    ///         <c>A_colour_read_from_the_platform_outlives_an_appearance_change</c> above proves the
    ///         seam by writing <c>SetPlatform</c> itself, which is what a test can do and a host
    ///         should not; this goes in through <see cref="PlatformInput.ApplySemanticColors" /> with
    ///         the sRGB value a platform reader hands over, and reads the frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>CanvasText</c> and not <c>Canvas</c>, deliberately.</b> Under forced colours the
    ///         document paints its own <c>Canvas</c> behind everything, so a probe filled with it is
    ///         the one rectangle this frame cannot tell from the root's — the trap the accent test
    ///         above records. A label colour is nobody's backdrop.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_platform_label_colour_reaches_a_sheet_and_outlives_an_appearance_change() {
        using var document = new UiDocument(200f, 100f);
        document.Load(".probe { width: 10px; height: 10px; background-color: CanvasText; }");
        document.Root.Add("div", classNames: "probe");

        // AppKit's dark-appearance labelColor as measured: white at 84.7% alpha. The alpha is the
        // component a reader is likeliest to drop, so it is the one asserted through.
        var label = new Color4(1f, 1f, 1f, 0.847f);

        PlatformInput.ApplySemanticColors(document, new SystemSemanticColors(CanvasText: label));
        document.Update();
        document.Draw();

        Assert.True(document.SystemColors.IsFromPlatform(SystemColor.CanvasText));
        Assert.Equal(Color4.FromSrgb(label), Fill(document));
        Assert.Equal(0.847f, Fill(document).A, 0.001f);

        // ⚠ And a partial read is the normal case: every role not supplied still follows the table.
        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.Canvas));
        Assert.Equal(Light(SystemColor.Canvas), document.SystemColors[SystemColor.Canvas]);

        // No `Load` between the draws — the shape the issue asked for — across both of the events
        // that reset the default tables, from two places on two cadences.
        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        document.Draw();

        Assert.Equal(Color4.FromSrgb(label), Fill(document));
        Assert.Equal(Dark(SystemColor.Canvas), document.SystemColors[SystemColor.Canvas]);

        PlatformInput.ApplyAccessibility(document, new SystemAccessibility(HighContrast: true));

        Assert.Equal(Color4.FromSrgb(label), document.SystemColors[SystemColor.CanvasText]);
        Assert.Equal(Forced(SystemColor.Canvas), document.SystemColors[SystemColor.Canvas]);
    }

    /// <summary>Every role the platform type carries lands on the role of the same name, and only there.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Eleven distinct colours, because the mapping is eleven hand-written lines and a
    ///         transposition between two of them — <c>Field</c> written into <c>FieldText</c> — is a
    ///         palette that looks fine until a field is drawn.</b> Each role is given a colour that
    ///         encodes its own index, so a swap fails on the pair it swapped and names both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the count is asked of the type rather than trusted, which is the half a list
    ///         of eleven cannot supply about itself.</b> "Eleven rows" is "every role" only while the
    ///         record declares eleven; a twelfth added to <see cref="SystemSemanticColors" /> and
    ///         forgotten in <c>ApplySemanticColors</c> would leave every assertion below green and
    ///         that role following <c>SystemPalette</c>'s browser table for ever. Comparing the two
    ///         sets by <i>name</i> rather than by count also pins the correspondence this whole type
    ///         is built on: a platform role is spelled exactly like the CSS system colour it fills.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_semantic_role_lands_on_its_own_palette_entry() {
        using var document = new UiDocument(200f, 100f);

        (SystemColor Role, Color4 Colour)[] rows = [
            (SystemColor.Canvas, Keyed(1)),
            (SystemColor.CanvasText, Keyed(2)),
            (SystemColor.LinkText, Keyed(3)),
            (SystemColor.ButtonFace, Keyed(4)),
            (SystemColor.ButtonText, Keyed(5)),
            (SystemColor.ButtonBorder, Keyed(6)),
            (SystemColor.Field, Keyed(7)),
            (SystemColor.FieldText, Keyed(8)),
            (SystemColor.Highlight, Keyed(9)),
            (SystemColor.HighlightText, Keyed(10)),
            (SystemColor.GrayText, Keyed(11))
        ];

        var declared = typeof(SystemSemanticColors)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(role => role.PropertyType == typeof(Color4?))
            .Select(role => role.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared, rows.Select(row => row.Role.ToString()).ToHashSet(StringComparer.Ordinal));

        PlatformInput.ApplySemanticColors(
            document,
            new SystemSemanticColors(
                Canvas: Keyed(1),
                CanvasText: Keyed(2),
                LinkText: Keyed(3),
                ButtonFace: Keyed(4),
                ButtonText: Keyed(5),
                ButtonBorder: Keyed(6),
                Field: Keyed(7),
                FieldText: Keyed(8),
                Highlight: Keyed(9),
                HighlightText: Keyed(10),
                GrayText: Keyed(11)
            )
        );

        foreach (var (role, colour) in rows) {
            Assert.True(document.SystemColors.IsFromPlatform(role), $"{role} was not supplied.");
            Assert.Equal((role, Color4.FromSrgb(colour)), (role, document.SystemColors[role]));
        }

        // ⚠ And not the accent pair, which `ApplyAccent` owns: two writers of one cell answer with
        // whichever ran last.
        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.AccentColor));
        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.AccentColorText));

        static Color4 Keyed(int index) => new(index / 16f, (index * 3 % 16) / 16f, (index * 7 % 16) / 16f, 1f);
    }

    /// <summary>A role the platform stops answering for goes back to the table rather than freezing.</summary>
    /// <remarks>
    ///     ⚠ <b>The real case is Windows leaving a high-contrast scheme</b>: <c>WindowsSemanticColors</c>
    ///     answers the classic table only while one is on, so the read goes from eleven roles to
    ///     none in one poll — and <c>ClearPlatform</c> forgets without reverting, so without the
    ///     repalette every role would keep the scheme's colours until the user next toggled dark
    ///     mode.
    /// </remarks>
    [Fact]
    public void A_semantic_role_the_platform_stops_reporting_goes_back_to_the_table() {
        using var document = new UiDocument(200f, 100f);

        PlatformInput.ApplyColorScheme(document, SystemColorScheme.Dark);
        PlatformInput.ApplySemanticColors(
            document,
            new SystemSemanticColors(CanvasText: new Color4(1f, 0f, 0f, 1f), GrayText: new Color4(0f, 1f, 0f, 1f))
        );

        Assert.True(document.SystemColors.IsFromPlatform(SystemColor.CanvasText));
        Assert.True(document.SystemColors.IsFromPlatform(SystemColor.GrayText));

        // Half the read goes away. The role that stays supplied keeps its colour; the one that went
        // returns to the *dark* table, which is the appearance the platform last reported.
        PlatformInput.ApplySemanticColors(document, new SystemSemanticColors(CanvasText: new Color4(1f, 0f, 0f, 1f)));

        Assert.True(document.SystemColors.IsFromPlatform(SystemColor.CanvasText));
        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.GrayText));
        Assert.Equal(Dark(SystemColor.GrayText), document.SystemColors[SystemColor.GrayText]);

        PlatformInput.ApplySemanticColors(document, SystemSemanticColors.Unknown);

        Assert.False(document.SystemColors.IsFromPlatform(SystemColor.CanvasText));
        Assert.Equal(Dark(SystemColor.CanvasText), document.SystemColors[SystemColor.CanvasText]);
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
