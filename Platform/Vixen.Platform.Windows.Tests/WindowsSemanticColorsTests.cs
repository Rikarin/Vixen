// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Xunit;

namespace Vixen.Platform.Windows.Tests;

/// <summary>The classic system colours, read as a palette only while a high-contrast scheme is on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assertion that matters is the negative one, and it is the one every runner can
///         make.</b> Outside a high-contrast scheme <c>GetSysColor(COLOR_WINDOW)</c> answers white
///         whatever the app theme is set to, so a reader that supplied the classic table
///         unconditionally would put a white canvas under a dark document — a wrong answer that
///         reads as a choice. No CI runner has a high-contrast scheme on, so the gate is what is
///         asserted everywhere and the palette itself is asserted only where a scheme is on.
///     </para>
///     <para>
///         ⚠ <b>And the wrong answer this cannot see</b>: a machine with a scheme on whose colours
///         happen to equal the browser's high-contrast table. On such a machine "the read" and
///         "the default" are the same bytes, and this passes for the wrong reason exactly as
///         <c>WindowsAccentTests</c> does on a stock selection blue.
///     </para>
/// </remarks>
public class WindowsSemanticColorsTests {
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ThePaletteIsSuppliedOnlyUnderAHighContrastScheme() {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Reads Windows system colours.");

        var contrast = WindowsAccessibility.HighContrast();
        var palette = WindowsSemanticColors.Read();

        Assert.SkipWhen(contrast is null, "SPI_GETHIGHCONTRAST could not be read on this machine.");

        if (contrast is false) {
            // The classic table is a light palette here whatever the app theme says, so nothing
            // may be supplied: every role stays on `SystemPalette`'s own appearance-following table.
            Assert.Equal(SystemSemanticColors.Unknown, palette);
            return;
        }

        // Under a scheme, the whole classic table — every role that has a Windows name.
        Assert.True(palette.IsKnown, "a high-contrast scheme is on and nothing was read.");
        Assert.NotNull(palette.Canvas);
        Assert.NotNull(palette.CanvasText);
        Assert.NotNull(palette.ButtonFace);
        Assert.NotNull(palette.ButtonText);
        Assert.NotNull(palette.GrayText);
        Assert.NotNull(palette.LinkText);

        // The two things Windows has one word for, and the guarantee a scheme makes.
        Assert.Equal(palette.Canvas, palette.Field);
        Assert.Equal(palette.CanvasText, palette.FieldText);
        Assert.NotEqual(palette.Canvas, palette.CanvasText);

        // And the selection pair is the accent read, because under a scheme the selection is the
        // accent — two readers of one system colour must agree or one of them is decoding wrongly.
        var accent = WindowsAccent.Read();

        Assert.Equal(accent.Color, palette.Highlight);
        Assert.Equal(accent.Text, palette.HighlightText);
    }
}
