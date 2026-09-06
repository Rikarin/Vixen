// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Xunit;

namespace Vixen.Platform.MacOS.Tests;

/// <summary>
///     Whether AppKit's semantic colours can be read at all, which three files said they could not.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A tripwire under a refusal, not a feature.</b> <c>PlatformInput.Repalette</c>,
///         <c>SystemPaletteWiringTests</c> and <see cref="MacOSAccessibility" /> all recorded that
///         the platform's own palette could not be read because "an SDL process has no
///         <c>NSApplication</c> and <c>NSColor</c> wants one", and #838 was left open against that
///         claim through two batches. It is false, and it is false in the way a refusal usually is:
///         it was true of the thing it was first written about — <c>NSApp.effectiveAppearance</c> is
///         a message to nil and returns zero, which reads as light — and was then carried across to
///         a class method that never needed the application object.
///     </para>
///     <para>
///         So this is here to make the correction cost something to undo. The day AppKit stops
///         answering, this goes red and #838's plan changes back; a comment saying "measured, and it
///         works" has already been re-refuted twice by people who had no way to check it.
///     </para>
///     <para>
///         ⚠ <b>Driven from a thread of this test's own, deliberately.</b> AppKit aborts the process
///         from a non-main thread for plenty of calls — see <see cref="ObjC.IsMainThread" />, which
///         exists because one of them killed a runner — so "these particular reads are safe off the
///         main thread" is a claim that has to be made by a thread that is provably not the main one
///         rather than by whichever one xUnit happened to use. <c>+setCurrentAppearance:</c> is
///         thread-local, which is what makes the two passes independent.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public class MacOSSemanticColorTests {
    /// <summary>What one <c>NSColor</c> is, in sRGB.</summary>
    readonly record struct Rgba(double R, double G, double B, double A);

    /// <summary>
    ///     The semantic colours resolve with no application object, and follow the appearance they
    ///     are asked for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two passes are the instrument. A single reading would be satisfied by a constant,
    ///         and a constant is exactly what a message to nil returns — so what is asserted is that
    ///         light and dark <em>disagree</em>, in the direction they must: a label is dark on a
    ///         light appearance and light on a dark one, and the text background is the other way
    ///         round.
    ///     </para>
    ///     <para>
    ///         ⚠ The alpha is asserted too, and it is not one. <c>labelColor</c> is 84.7% opaque in
    ///         both appearances — that is the value AppKit ships — so a reader that dropped the alpha
    ///         component would put pure black text on a Mac that draws it slightly soft, and the
    ///         difference is invisible in a screenshot and obvious in a comparison. It is also the
    ///         component a wrong <c>objc_msgSend</c> prototype is most likely to return as garbage.
    ///     </para>
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void SemanticColoursResolveOffTheMainThreadAndFollowTheAppearanceTheyAreAskedFor() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Sends Objective-C messages.");
        Assert.True(ObjC.Load());

        var (light, dark) = ReadOnAThreadOfItsOwn();

        // The instrument, before the measurement: a thread that never reached AppKit answers zeroes,
        // and zero is also a plausible colour component.
        Assert.NotEqual(default, light.Label);
        Assert.NotEqual(default, dark.Label);

        // A label is dark on Aqua and light on Dark Aqua. Compared as "one is below a third and the
        // other above two thirds" rather than against Apple's exact bytes, which are theirs to
        // change: what is under test is that the appearance reached the colour at all.
        Assert.True(light.Label.R < 0.34, $"Aqua labelColor was {light.Label}");
        Assert.True(dark.Label.R > 0.66, $"Dark Aqua labelColor was {dark.Label}");

        // And the background is the other way round, which is what says the two passes are reading
        // a palette rather than one inverted number.
        Assert.True(light.TextBackground.R > 0.66, $"Aqua textBackgroundColor was {light.TextBackground}");
        Assert.True(dark.TextBackground.R < 0.34, $"Dark Aqua textBackgroundColor was {dark.TextBackground}");

        // ⚠ Not one, in either appearance. See the remarks.
        Assert.Equal(0.847, light.Label.A, 0.01);
        Assert.Equal(0.847, dark.Label.A, 0.01);

        // The accent is a whole colour and is the same in both, because it is the user's choice
        // rather than the appearance's — which is the pair #837 wants and this one does not use.
        Assert.Equal(light.Accent, dark.Accent);
        Assert.True(light.Accent.A > 0.99, $"controlAccentColor was {light.Accent}");
    }

    static ((Rgba Label, Rgba TextBackground, Rgba Accent) Light, (Rgba Label, Rgba TextBackground, Rgba Accent) Dark)
        ReadOnAThreadOfItsOwn() {
        var light = default((Rgba, Rgba, Rgba));
        var dark = default((Rgba, Rgba, Rgba));

        var worker = new Thread(
            () => {
                light = ReadUnder("NSAppearanceNameAqua");
                dark = ReadUnder("NSAppearanceNameDarkAqua");
            }
        );

        worker.Start();

        // ⚠ A ceiling and not a budget: these are three messages each and take microseconds. What it
        // is here for is a hang — an AppKit call that decides it wants the main thread waits for a
        // run loop that this process does not run, and a test that waited for ever would take the
        // whole assembly with it rather than failing.
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "The AppKit reads did not finish.");

        return (light, dark);
    }

    /// <remarks>
    ///     ⚠ The appearance is named by its <em>string</em> rather than by the exported constant.
    ///     <c>NSAppearanceNameAqua</c> is an <c>NSString *</c> symbol in AppKit, which would want a
    ///     <c>dlsym</c> and a dereference; the strings behind those two constants are documented and
    ///     are what <c>appearanceNamed:</c> compares against, and a wrong one returns nil — which the
    ///     assertion below catches rather than silently reading the system appearance twice.
    /// </remarks>
    static (Rgba Label, Rgba TextBackground, Rgba Accent) ReadUnder(string appearance) {
        var named = ObjC.Send(
            ObjC.GetClass("NSAppearance"),
            ObjC.Selector("appearanceNamed:"),
            ObjC.String(appearance)
        );

        Assert.NotEqual(0, named);

        ObjC.Send(ObjC.GetClass("NSAppearance"), ObjC.Selector("setCurrentAppearance:"), named);

        return (Component("labelColor"), Component("textBackgroundColor"), Component("controlAccentColor"));
    }

    /// <summary>One <c>+[NSColor …]</c> class colour, converted into sRGB and read out.</summary>
    /// <remarks>
    ///     <c>colorUsingColorSpace:</c> is the step that turns a dynamic catalogue colour into
    ///     components at all — the colour AppKit hands back has no <c>redComponent</c> until it has
    ///     been resolved against a colour space, and asking one for it raises. It is also where the
    ///     current appearance is consulted, which is why the appearance is set before this rather
    ///     than after.
    /// </remarks>
    static Rgba Component(string colour) {
        var dynamic = ObjC.Send(ObjC.GetClass("NSColor"), ObjC.Selector(colour));

        Assert.NotEqual(0, dynamic);

        var srgb = ObjC.Send(
            dynamic,
            ObjC.Selector("colorUsingColorSpace:"),
            ObjC.Send(ObjC.GetClass("NSColorSpace"), ObjC.Selector("sRGBColorSpace"))
        );

        Assert.NotEqual(0, srgb);

        return new(
            ObjC.SendDouble(srgb, ObjC.Selector("redComponent")),
            ObjC.SendDouble(srgb, ObjC.Selector("greenComponent")),
            ObjC.SendDouble(srgb, ObjC.Selector("blueComponent")),
            ObjC.SendDouble(srgb, ObjC.Selector("alphaComponent"))
        );
    }
}
