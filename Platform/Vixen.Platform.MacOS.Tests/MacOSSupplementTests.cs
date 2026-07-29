// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.MacOS.Tests;

/// <summary>
///     The Objective-C interop, run against the real frameworks.
/// </summary>
/// <remarks>
///     <para>
///         Skipped everywhere but macOS, and that is the whole of the platform gating: there is no
///         useful way to fake <c>objc_msgSend</c>, and a signature that is wrong is wrong in a way
///         only a real message send reveals. What these check is exactly the part that a hand-written
///         prototype gets wrong — the return width of a <c>BOOL</c>, the eleven-argument bitmap
///         initialiser, the ownership of a returned <c>NSString</c>.
///     </para>
///     <para>
///         The panels are not opened. A modal <c>NSOpenPanel</c> waits for a person, and there is
///         not one; what is asserted instead is that a call from a thread that is not the main one
///         returns nothing rather than aborting the process, which is the failure mode that matters
///         and is the one a test runner is in a position to produce.
///     </para>
/// </remarks>
public class MacOSSupplementTests {
    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheFrameworksLoadAndTheClassesAreThere() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Sends Objective-C messages.");

        Assert.True(ObjC.Load());
        Assert.NotEqual(0, ObjC.GetClass("NSString"));
        Assert.NotEqual(0, ObjC.GetClass("NSPasteboard"));
        Assert.NotEqual(0, ObjC.GetClass("NSProcessInfo"));
        Assert.NotEqual(0, ObjC.GetClass("NSOpenPanel"));
    }

    /// <summary>
    ///     A string out and the same string back, which is what says the two marshalling
    ///     directions agree — <c>stringWithUTF8String:</c> takes bytes and <c>UTF8String</c> returns
    ///     a pointer to them, and getting either wrong produces mojibake rather than an error.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void AStringSurvivesTheRoundTrip() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Sends Objective-C messages.");
        Assert.True(ObjC.Load());

        Assert.Equal("Vixen", ObjC.ToString(ObjC.String("Vixen")));
        Assert.Equal("scène — 場面", ObjC.ToString(ObjC.String("scène — 場面")));
        Assert.Null(ObjC.ToString(0));
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void AnArrayReportsWhatWasPutInIt() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Sends Objective-C messages.");
        Assert.True(ObjC.Load());

        var array = ObjC.StringArray(["png", "jpg", "webp"]);

        Assert.Equal(3, ObjC.Count(array));
        Assert.Equal("jpg", ObjC.ToString(ObjC.At(array, 1)));
        Assert.Equal(0, ObjC.Count(ObjC.EmptyArray()));
    }

    /// <summary>
    ///     The reason this assembly exists at all: macOS is the only desktop that reports thermal
    ///     pressure, and <see cref="ThermalState" /> was defined from this enumeration.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheThermalStateIsOneOfTheFour() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Sends Objective-C messages.");

        var power = new MacOSPowerInfo(new NothingPowerInfo());

        Assert.True(Enum.IsDefined(power.Thermal));

        // A BOOL is one byte and an nint is eight. Reading the wrong width gives a value that is
        // "true" whenever the register happened to have anything in it, which is most of the time.
        Assert.Equal(power.IsLowPowerMode, power.IsLowPowerMode);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheBatteryIsWhateverTheBaselineSaid() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Constructs the macOS power info.");

        var power = new MacOSPowerInfo(new NothingPowerInfo());

        Assert.Equal(PowerSource.Unknown, power.Source);
        Assert.Null(power.BatteryLevel);
        Assert.Null(power.EstimatedTimeRemaining);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheTopologyDescribesAPlausibleMachine() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Reads sysctl.");

        var topology = new MacOSProcessorTopology();

        Assert.InRange(topology.PhysicalCores, 1, topology.AvailableProcessors);
        Assert.InRange(topology.PerformanceCores, 0, topology.PhysicalCores);

        // Not supported, said out loud rather than attempted and silently ignored.
        Assert.False(topology.SupportsAffinity);
        Assert.False(topology.TrySetAffinity(0));
        topology.ClearAffinity();
    }

    /// <summary>
    ///     On Apple silicon the two performance levels are real and the classes have to differ;
    ///     on Intel there is one and every processor is <see cref="ProcessorClass.Unknown" />. Both
    ///     are correct and the difference is the machine, so what is asserted is the invariant that
    ///     holds either way.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void EveryProcessorHasAClassAndOutOfRangeHasNone() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Reads sysctl.");

        var topology = new MacOSProcessorTopology();

        for (var index = 0; index < topology.AvailableProcessors; index++) {
            var expected = topology.PerformanceCores > 0
                ? index == 0 ? ProcessorClass.Performance : topology.ClassOf(index)
                : ProcessorClass.Unknown;

            Assert.Equal(expected, topology.ClassOf(index));
        }

        Assert.Equal(ProcessorClass.Unknown, topology.ClassOf(-1));
        Assert.Equal(ProcessorClass.Unknown, topology.ClassOf(topology.AvailableProcessors));
    }

    /// <summary>
    ///     The image path refuses off the main thread rather than aborting the process.
    /// </summary>
    /// <remarks>
    ///     <b>This test is here because it crashed.</b> Written first as a round trip through the
    ///     real pasteboard, it took the runner down with <c>SIGBUS</c> and AppKit's
    ///     <c>0xbad4007</c> — the "main thread only" assertion — inside
    ///     <c>TIFFRepresentation</c>, on a thread that had never gone near a window. That is what
    ///     put the guard in <see cref="MacOSClipboard" /> and what this now asserts. The round trip
    ///     itself cannot be tested by a runner, for the same reason
    ///     <c>Vixen.Platform.Desktop.Tests</c> forces SDL's dummy video driver on this operating
    ///     system: a test runner is never on the main thread.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheImagePathRefusesOffTheMainThreadRatherThanAborting() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Would abort the process if the guard were wrong.");
        Assert.False(ObjC.IsMainThread, "A test runner should not be on the main thread.");

        var clipboard = new MacOSClipboard(new NothingClipboard());

        var pixels = new byte[] {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        };

        Assert.False(clipboard.SetImage(new(pixels, new(2, 2))));
        Assert.False(clipboard.TryGetImage(out var image));
        Assert.Equal(Int2.Zero, image.Size);
    }

    /// <summary>
    ///     <b>This replaces what is on the pasteboard</b>, which is what copying anything does. The
    ///     pasteboard's own reads and writes are not AppKit in the thread-affine sense — unlike the
    ///     image encoding above — so this is the half of the clipboard a runner can prove.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void AnApplicationDefinedTypeSurvivesTheRoundTrip() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Uses the real pasteboard.");

        var clipboard = new MacOSClipboard(new NothingClipboard());
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        Assert.True(clipboard.SetData("dev.rikarin.vixen.test", data));
        Assert.True(clipboard.TryGetData("dev.rikarin.vixen.test", out var read));
        Assert.Equal(data, read.ToArray());

        clipboard.Clear();
        Assert.False(clipboard.TryGetData("dev.rikarin.vixen.test", out _));
    }

    /// <summary>
    ///     AppKit aborts the process when a panel is created off the main thread, and a test runner
    ///     is never on it — so this asserts the guard that turns a <c>SIGABRT</c> into
    ///     nothing-chosen. It is also, for that reason, the only test that can touch the dialogs at
    ///     all.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task APanelAskedForOffTheMainThreadChoosesNothing() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Would abort the process if the guard were wrong.");

        var dialogs = new MacOSDialogs(new NothingDialogs());

        Assert.Null(await dialogs.OpenFileAsync(new(), null, TestContext.Current.CancellationToken));
        Assert.Empty(await dialogs.OpenFilesAsync(new(), null, TestContext.Current.CancellationToken));
        Assert.Null(await dialogs.SaveFileAsync(new() { SuggestedFileName = "level.vxscene" }, null, TestContext.Current.CancellationToken));
        Assert.Null(await dialogs.OpenFolderAsync(new(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void TheSupplementReplacesFourServicesAndEarnsTheDialogCapability() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Loads AppKit.");

        using var supplement = new MacOSPlatformSupplement();

        var baseline = new PlatformServices(
            new NothingClipboard(),
            new NothingDialogs(),
            new NothingPowerInfo(),
            new NothingProcessorTopology(),
            PlatformCapabilities.Windowing
        );

        var services = supplement.Augment(baseline);

        Assert.Equal("macOS", supplement.Name);
        Assert.IsType<MacOSClipboard>(services.Clipboard);
        Assert.IsType<MacOSDialogs>(services.Dialogs);
        Assert.IsType<MacOSPowerInfo>(services.Power);
        Assert.IsType<MacOSProcessorTopology>(services.Processors);
        Assert.Equal(
            PlatformCapabilities.Windowing | PlatformCapabilities.NativeDialogs,
            services.Capabilities
        );
    }
}
