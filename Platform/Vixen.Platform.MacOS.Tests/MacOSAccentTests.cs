// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Xunit;

namespace Vixen.Platform.MacOS.Tests;

/// <summary>The accent read that fills <c>--accent</c>, against the colour AppKit actually answers.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An accent reader is the one shape of platform read whose failure is invisible.</b>
///         Every plausible wrong answer here is a colour — a hard-coded blue, the defaults key's
///         index mistaken for a colour, the alpha dropped — and a window painted the wrong blue
///         looks exactly like a window painted the right one to anybody who has not compared it with
///         the machine's own settings. So this does not assert that the read produced <i>a</i>
///         colour; it asserts that it produced the <i>same</i> colour a raw
///         <c>+[NSColor controlAccentColor]</c> gives, read here rather than trusted from there.
///     </para>
///     <para>
///         ⚠ <b>The stock-machine trap this reader exists to avoid.</b> <c>AppleAccentColor</c> and
///         <c>AppleHighlightColor</c> in <c>NSGlobalDomain</c> are the obvious source and are both
///         <i>absent</i> on a Mac whose user has never changed their accent — which is most of them
///         — so a reader built on the defaults answers "no accent" on almost every machine while the
///         window in front of it is drawing blue. <c>MacOSAccent</c> reads the semantic colour
///         instead, and the assertion below that the accent is opaque and non-default is what a
///         defaults-based reader would fail on this machine.
///     </para>
///     <para>
///         ⚠ <b>And one wrong answer this cannot see, measured rather than reasoned about.</b>
///         Replacing the read with the constant <c>#007AFF</c> — macOS's default blue — leaves this
///         test <i>green</i>, because the machine it was written on is sitting at that default and
///         the comparison is against the same colour. Every other hard-coded value is caught (a
///         green constant reds it at "Expected: 0, Actual: 0.1"), and so is a dropped alpha, a wrong
///         prototype and a colour space never resolved. Closing the last one needs a machine whose
///         accent has been moved off the default, which is a property of the runner rather than of
///         the code: a Mac with a custom accent is where this test is worth the most.
///     </para>
///     <para>
///         Whether AppKit answers at all from a process with no <c>NSApplication</c> is
///         <see cref="MacOSSemanticColorTests" />' question, and it is answered there — on a thread
///         of its own, which is the harder version. This one is about the reader.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public class MacOSAccentTests {
    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheAccentReadIsTheColourAppKitAnswersRatherThanAConstant() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Sends Objective-C messages.");
        Assert.True(ObjC.Load());

        var accent = MacOSAccent.Read();

        // The instrument first: a reader that reached nothing answers `Unknown`, and `Unknown` is
        // also what a machine with no accent would answer if such a machine existed. It does not —
        // every Mac has one — so this failing means the read is broken rather than the Mac unusual.
        Assert.True(accent.IsKnown, "controlAccentColor could not be read at all.");

        var read = accent.Color!.Value;

        // ⚠ Component by component against an independent read of the same colour. A hard-coded
        // blue, a dropped alpha and a wrong `objc_msgSend` prototype all survive "it is a colour"
        // and none of them survives this.
        var (r, g, b, a) = RawControlAccent();

        Assert.Equal(r, read.R, 0.001);
        Assert.Equal(g, read.G, 0.001);
        Assert.Equal(b, read.B, 0.001);
        Assert.Equal(a, read.A, 0.001);

        // The accent is opaque — an accent read that came back at zero alpha would be invisible
        // rather than wrong, and that is the failure a colour comparison alone would call a pass on
        // a machine whose accent happens to be black.
        Assert.True(read.A > 0.99, $"controlAccentColor was {read}");

        // ⚠ And the pair, because half a read does not move the theme's token: `PlatformInput`
        // puts `SystemPalette.PlatformAccentClass` on for the accent AND its text or not at all.
        Assert.NotNull(accent.Text);
        Assert.True(accent.Text!.Value.A > 0.99, $"alternateSelectedControlTextColor was {accent.Text}");
    }

    /// <summary>
    ///     <c>+[NSColor controlAccentColor]</c> resolved into sRGB, written out here rather than
    ///     shared with the reader under test.
    /// </summary>
    static (double R, double G, double B, double A) RawControlAccent() {
        var dynamic = ObjC.Send(ObjC.GetClass("NSColor"), ObjC.Selector("controlAccentColor"));

        Assert.NotEqual(0, dynamic);

        var srgb = ObjC.Send(
            dynamic,
            ObjC.Selector("colorUsingColorSpace:"),
            ObjC.Send(ObjC.GetClass("NSColorSpace"), ObjC.Selector("sRGBColorSpace"))
        );

        Assert.NotEqual(0, srgb);

        return (
            ObjC.SendDouble(srgb, ObjC.Selector("redComponent")),
            ObjC.SendDouble(srgb, ObjC.Selector("greenComponent")),
            ObjC.SendDouble(srgb, ObjC.Selector("blueComponent")),
            ObjC.SendDouble(srgb, ObjC.Selector("alphaComponent"))
        );
    }
}
