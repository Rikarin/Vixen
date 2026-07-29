// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Desktop.Tests;

/// <summary>
///     The parts that need SDL actually running.
/// </summary>
/// <remarks>
///     Skipped rather than failed where SDL is missing or there is no display server: the library is
///     not shipped with the bindings and a machine without it was never going to run these. CI
///     installs SDL and sets <c>SDL_VIDEODRIVER=dummy</c>, which is a real SDL video driver that
///     creates real windows nobody can see — enough to exercise every line here.
/// </remarks>
public sealed class DesktopPlatformTests : IDisposable {
    readonly DesktopPlatform? platform;
    readonly string? unavailable;

    public DesktopPlatformTests() {
        if (!SdlLibrary.IsAvailable) {
            unavailable = "SDL2 is not installed on this machine.";
            return;
        }

        try {
            platform = new(
                new() {
                    Application = "Vixen.Tests",
                    EnableGameControllers = false,
                    VideoDriver = PreferredVideoDriver(),
                    RequestGpuSurface = PreferredVideoDriver() is not "dummy",

                    // The subject here is what SDL does, so the per-OS supplement is off. With it on
                    // these tests would assert three different things on three operating systems —
                    // and the clipboard and the pickers they check for the absence of are exactly
                    // what it supplies. Its own wiring is DesktopSupplementTests.
                    UseNativeSupplement = false
                }
            );
        } catch (PlatformNotSupportedException exception) {
            unavailable = $"SDL could not start a video driver: {exception.Message}";
        }
    }

    /// <summary>
    ///     The most real driver that can actually run here.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         macOS is forced to <c>dummy</c> and this is the interesting one: AppKit aborts the
    ///         process — <c>SIGABRT</c>, not an exception — if a window is created from anywhere but
    ///         the main thread, and a test runner never is. So on macOS these tests exercise our
    ///         translation, lifecycle and window bookkeeping against a driver that does not touch
    ///         Cocoa, and the real Cocoa path is proved by <c>Samples/01-HelloTriangle</c>, which has
    ///         a genuine main thread.
    ///     </para>
    ///     <para>
    ///         Linux with no display server gets <c>dummy</c> too, for the same reason a CI runner
    ///         has no display. Windows and a Linux desktop session run the real driver.
    ///     </para>
    /// </remarks>
    static string? PreferredVideoDriver() {
        if (OperatingSystem.IsMacOS()) {
            return "dummy";
        }

        if (!OperatingSystem.IsLinux()) {
            return null;
        }

        var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        return hasDisplay ? null : "dummy";
    }

    [Fact]
    public void ItReportsWhichVideoDriverItGot() {
        var sdl = Require();

        Assert.False(string.IsNullOrEmpty(sdl.VideoDriver));
        Assert.NotEqual("unknown", sdl.VideoDriver);
    }

    /// <summary>
    ///     Wayland refuses to tell a client where it is or let it choose, and the same Linux binary
    ///     under X11 can — so this is decided from what SDL reports rather than from the operating
    ///     system's name.
    /// </summary>
    [Fact]
    public void PositioningIsClaimedOnlyWhereTheCompositorAllowsIt() {
        var sdl = Require();

        Assert.Equal(
            !string.Equals(sdl.VideoDriver, "wayland", StringComparison.Ordinal),
            sdl.Has(PlatformCapabilities.WindowPositioning)
        );
    }

    [Fact]
    public void ItCanDoTheThingsSdlCanDo() {
        var sdl = Require();

        Assert.True(sdl.Has(PlatformCapabilities.Windowing));
        Assert.True(sdl.Has(PlatformCapabilities.MultiWindow));
        Assert.True(sdl.Has(PlatformCapabilities.Clipboard));
        Assert.True(sdl.Has(PlatformCapabilities.Cursor));

        // SDL 2 has no file picker, and the capability covers pickers and message boxes together.
        Assert.False(sdl.Has(PlatformCapabilities.NativeDialogs));

        // No desktop suspends a process.
        Assert.False(sdl.Has(PlatformCapabilities.Suspension));
    }

    [Fact]
    public void AWindowIsCreatedAtTheSizeItWasAskedFor() {
        var sdl = Require();
        using var window = sdl.CreateWindow(new() { Title = "Vixen", Size = new(640, 480) });

        Assert.Equal("Vixen", window.Title);
        Assert.Equal(new Int2(640, 480), window.ClientSize);
        Assert.NotEqual(0u, window.Id);
        Assert.False(window.IsClosed);
    }

    /// <summary>
    ///     The window is sized in logical points and its swapchain in physical pixels. Confusing
    ///     them renders a quarter of the window or four times too much of it, so the two numbers have
    ///     to be separately available and consistent with the scale.
    /// </summary>
    [Fact]
    public void TheFramebufferIsTheClientSizeTimesTheScale() {
        var sdl = Require();
        using var window = sdl.CreateWindow(new() { Size = new(400, 300) });

        var scale = window.DpiScale;

        Assert.True(scale > 0f, "A window reported a non-positive scale factor.");
        Assert.Equal((int)(window.ClientSize.X * scale), window.FramebufferSize.X);
        Assert.Equal(window.FramebufferSize, window.Surface.PixelSize);
    }

    [Fact]
    public void WindowsAreFoundByTheIdTheirEventsCarry() {
        var sdl = Require();
        using var first = sdl.CreateWindow(new());
        using var second = sdl.CreateWindow(new());

        Assert.NotEqual(first.Id, second.Id);
        Assert.True(sdl.TryGetWindow(second.Id, out var found));
        Assert.Same(second, found);
        Assert.False(sdl.TryGetWindow(uint.MaxValue, out _));
    }

    [Fact]
    public void ADisposedWindowStopsBeingAWindowAtTheNextPump() {
        var sdl = Require();
        var window = sdl.CreateWindow(new());
        var id = window.Id;

        window.Dispose();
        sdl.PumpEvents();

        Assert.False(sdl.TryGetWindow(id, out _));
        Assert.DoesNotContain(window, sdl.Windows);
        Assert.Throws<ObjectDisposedException>(() => window.Title);
    }

    [Fact]
    public void ATitleSurvivesTheRoundTripThroughSdl() {
        var sdl = Require();
        using var window = sdl.CreateWindow(new());

        window.Title = "Ünïcödé — 日本語";

        Assert.Equal("Ünïcödé — 日本語", window.Title);
    }

    [Fact]
    public void ResizingIsReportedWithBothSizes() {
        var sdl = Require();
        using var window = sdl.CreateWindow(new() { Size = new(320, 240) });
        sdl.PumpEvents();

        window.ClientSize = new(800, 600);

        var resized = Drain(sdl, PlatformEventKind.WindowResized);

        // A window manager may refuse, so this asserts on what SDL reported rather than on what was
        // asked for — but it must report *something*, and the pixel size must match the window.
        Assert.NotEmpty(resized);
        Assert.Equal(window.ClientSize, resized[^1].Size);
        Assert.Equal(window.FramebufferSize, resized[^1].PixelSize);
    }

    /// <summary>
    ///     Timestamps are the OS's, expressed in <see cref="System.Diagnostics.Stopwatch" /> ticks —
    ///     the difference between when a key was pressed and when the loop got round to it is the
    ///     whole of input latency, so the original number has to survive translation.
    /// </summary>
    [Fact]
    public void EventTimestampsAreOnTheEnginesMonotonicClock() {
        var sdl = Require();
        using var window = sdl.CreateWindow(new());
        window.Show();

        var before = System.Diagnostics.Stopwatch.GetTimestamp();
        var events = sdl.PumpEvents().ToArray();
        var after = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.NotEmpty(events);

        foreach (var item in events) {
            // A second of slack each way: SDL's millisecond clock and Stopwatch are anchored to each
            // other at startup, not sampled together, so they drift by rounding rather than by much.
            Assert.InRange(
                item.Timestamp,
                before - System.Diagnostics.Stopwatch.Frequency,
                after + System.Diagnostics.Stopwatch.Frequency
            );
        }
    }

    [Fact]
    public void ShowingAWindowSaysSo() {
        var sdl = Require();
        using var window = sdl.CreateWindow(new());
        sdl.PumpEvents();

        window.Show();

        Assert.NotEmpty(Drain(sdl, PlatformEventKind.WindowShown));
        Assert.True(window.IsVisible);
    }

    [Fact]
    public void AnIconOfTheWrongSizeIsRejectedRatherThanStored() {
        var sdl = Require();
        using var window = sdl.CreateWindow(new());

        Assert.Throws<ArgumentException>(() => window.SetIcon(new byte[10], new(16, 16)));
        window.SetIcon(new byte[16 * 16 * 4], new(16, 16));
    }

    /// <summary>
    ///     What a Vulkan swapchain is built from. On macOS it must be a <c>CAMetalLayer</c> and not
    ///     the <c>NSWindow</c> — going through the window is how MoltenVK ends up owning the layer
    ///     and choosing the pixel format for us.
    /// </summary>
    [Fact]
    public void TheSurfaceCarriesTheHandleThisPlatformsVulkanNeeds() {
        var sdl = Require();
        Assert.SkipWhen(sdl.VideoDriver == "dummy", "The dummy video driver has no native window.");

        using var window = sdl.CreateWindow(new());
        var handle = window.Surface.Handle;

        var expected = OperatingSystem.IsMacOS() ? SurfaceKind.Metal
            : OperatingSystem.IsWindows() ? SurfaceKind.Win32
            : sdl.VideoDriver == "wayland" ? SurfaceKind.Wayland : SurfaceKind.Xlib;

        Assert.Equal(expected, handle.Kind);
        Assert.True(handle.CanPresent);
        Assert.NotEqual(0, handle.Handle);
    }

    [Fact]
    public void ClipboardTextSurvivesTheRoundTrip() {
        var sdl = Require();
        Assert.SkipWhen(sdl.VideoDriver == "dummy", "The dummy video driver has no clipboard.");

        Assert.True(sdl.Clipboard.SetText("vixen clipboard"));
        Assert.True(sdl.Clipboard.TryGetText(out var text));
        Assert.Equal("vixen clipboard", text);
    }

    /// <summary>
    ///     SDL 2 supports text and nothing else, and says so rather than throwing — images and
    ///     application formats wait for the per-OS assemblies.
    /// </summary>
    [Fact]
    public void TheClipboardRefusesWhatSdlCannotDo() {
        var sdl = Require();

        Assert.False(sdl.Clipboard.HasImage);
        Assert.False(sdl.Clipboard.TryGetImage(out _));
        Assert.False(sdl.Clipboard.SetData("application/x-vixen", [1, 2, 3]));
    }

    [Fact]
    public void QuittingIsARequestThatCanBeWithdrawn() {
        var sdl = Require();

        sdl.Lifecycle.RequestQuit();
        Assert.True(sdl.Lifecycle.IsQuitRequested);
        Assert.NotEmpty(Drain(sdl, PlatformEventKind.Quit));

        sdl.Lifecycle.CancelQuit();
        Assert.False(sdl.Lifecycle.IsQuitRequested);
    }

    [Fact]
    public void PowerIsReportedOrHonestlyUnknown() {
        var sdl = Require();

        // Every answer is legitimate — a desktop with no battery, a laptop on mains, a laptop
        // discharging. What must not happen is a level outside its range.
        if (sdl.Power.BatteryLevel is { } level) {
            Assert.InRange(level, 0f, 1f);
        }

        Assert.Equal(ThermalState.Nominal, sdl.Power.Thermal);
    }

    [Fact]
    public void ProcessorCountsComeFromTheRuntimeRatherThanFromSdl() {
        var sdl = Require();

        Assert.Equal(Environment.ProcessorCount, sdl.Processors.AvailableProcessors);
        Assert.False(sdl.Processors.SupportsAffinity);
        Assert.False(sdl.Processors.TrySetAffinity(0));
    }

    [Fact]
    public void UsingThePlatformFromAnotherThreadIsRefusedRatherThanRaced() {
        var sdl = Require();
        Exception? caught = null;

        var thread = new Thread(
            () => {
                try {
                    sdl.PumpEvents();
                } catch (Exception exception) {
                    caught = exception;
                }
            }
        );

        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(caught);
    }

    public void Dispose() => platform?.Dispose();

    DesktopPlatform Require() {
        Assert.SkipWhen(unavailable is not null, unavailable ?? string.Empty);
        return platform!;
    }

    static PlatformEvent[] Drain(DesktopPlatform platform, PlatformEventKind kind) =>
        platform.PumpEvents().ToArray().Where(item => item.Kind == kind).ToArray();
}
