// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Headless.Tests;

public sealed class HeadlessPlatformTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();
    readonly HeadlessPlatform platform;

    public HeadlessPlatformTests() {
        platform = new(new() { FileSystem = files });
    }

    /// <summary>
    ///     The claim the whole assembly rests on: nothing here can be shown to anybody, and a
    ///     subsystem that needs a display therefore takes its fallback path on every test run rather
    ///     than on a server at three in the morning.
    /// </summary>
    [Fact]
    public void ItAdmitsToHavingNothing() {
        Assert.False(platform.Has(PlatformCapabilities.Windowing));
        Assert.False(platform.Has(PlatformCapabilities.Clipboard));
        Assert.False(platform.Has(PlatformCapabilities.NativeDialogs));
        Assert.False(platform.Has(PlatformCapabilities.DisplayEnumeration));
        Assert.Empty(platform.Displays.Displays);
        Assert.Null(platform.Displays.Primary);
    }

    /// <summary>
    ///     Asking for two capabilities and being told "yes" because one of them is present is never
    ///     what a caller meant.
    /// </summary>
    [Fact]
    public void AskingForSeveralCapabilitiesNeedsAllOfThem() {
        Assert.True(platform.Has(PlatformCapabilities.MultiWindow));
        Assert.False(platform.Has(PlatformCapabilities.MultiWindow | PlatformCapabilities.Windowing));
    }

    /// <summary>
    ///     A headless window is still a window — the dedicated server runs the desktop's frame loop
    ///     rather than a second one written for it.
    /// </summary>
    [Fact]
    public void AWindowExistsAndHasEverythingButAPicture() {
        using var window = platform.CreateWindow(new() { Title = "Server", Size = new(800, 600) });

        Assert.NotEqual(0u, window.Id);
        Assert.Equal("Server", window.Title);
        Assert.Equal(new Int2(800, 600), window.ClientSize);
        Assert.Equal(new Int2(800, 600), window.FramebufferSize);
        Assert.False(window.IsClosed);
        Assert.Same(window, Assert.Single(platform.Windows));
    }

    /// <summary>
    ///     What tells a graphics backend to render offscreen instead of building a swapchain.
    /// </summary>
    [Fact]
    public void ItsSurfaceIsHonestAboutHavingNothingToPresentTo() {
        using var window = platform.CreateWindow(new());

        Assert.Equal(SurfaceKind.None, window.Surface.Handle.Kind);
        Assert.False(window.Surface.Handle.CanPresent);
        Assert.Equal(window.FramebufferSize, window.Surface.PixelSize);
    }

    [Fact]
    public void WindowsAreFoundByTheIdTheirEventsCarry() {
        using var first = platform.CreateWindow(new());
        using var second = platform.CreateWindow(new());

        Assert.NotEqual(first.Id, second.Id);
        Assert.True(platform.TryGetWindow(second.Id, out var found));
        Assert.Same(second, found);
        Assert.False(platform.TryGetWindow(9999, out _));
    }

    [Fact]
    public void ADisposedWindowStopsBeingAWindowAtTheNextPump() {
        var window = platform.CreateWindow(new());
        var id = window.Id;

        window.Dispose();
        platform.PumpEvents();

        Assert.Empty(platform.Windows);
        Assert.False(platform.TryGetWindow(id, out _));
        Assert.True(window.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => window.Title);
    }

    /// <summary>
    ///     Resizing really resizes here, which is the point: a swapchain-rebuild path can be driven
    ///     without a display server.
    /// </summary>
    [Fact]
    public void ResizingRaisesTheEventWithBothSizes() {
        using var window = platform.CreateWindow(new() { Size = new(100, 100) });
        platform.PumpEvents();

        window.ClientSize = new(1280, 720);

        var resized = Assert.Single(Events(PlatformEventKind.WindowResized));
        Assert.Equal(window.Id, resized.WindowId);
        Assert.Equal(new Int2(1280, 720), resized.Size);
        Assert.Equal(new Int2(1280, 720), resized.PixelSize);
    }

    /// <summary>
    ///     Dragging a window between a 1× and a 2× monitor is the case that breaks swapchain sizing,
    ///     and it is not otherwise reachable from a test.
    /// </summary>
    [Fact]
    public void ChangingTheScaleFactorChangesTheFramebufferAndSaysSo() {
        using var window = (HeadlessWindow)platform.CreateWindow(new() { Size = new(1280, 720) });
        platform.PumpEvents();

        window.SetDpiScale(2f);

        var changed = Assert.Single(Events(PlatformEventKind.WindowDpiChanged));
        Assert.Equal(2f, changed.DpiScale);
        Assert.Equal(new Int2(1280, 720), window.ClientSize);
        Assert.Equal(new Int2(2560, 1440), window.FramebufferSize);
    }

    [Fact]
    public void SettingASizeItAlreadyHasSaysNothing() {
        using var window = platform.CreateWindow(new() { Size = new(640, 480) });
        platform.PumpEvents();

        window.ClientSize = new(640, 480);

        Assert.Empty(Events(PlatformEventKind.WindowResized));
    }

    [Fact]
    public void ShowingAndHidingAndFocusingEachSayWhatHappened() {
        using var window = (HeadlessWindow)platform.CreateWindow(new());
        platform.PumpEvents();

        window.Show();
        window.SetFocused(true);
        window.SetFocused(false);
        window.Hide();

        var kinds = platform.PumpEvents().ToArray().Select(item => item.Kind).ToArray();

        Assert.Equal(
            [
                PlatformEventKind.WindowShown,
                PlatformEventKind.WindowFocusGained,
                PlatformEventKind.WindowFocusLost,
                PlatformEventKind.WindowHidden
            ],
            kinds
        );
    }

    /// <summary>
    ///     Closing from the title bar is a request, not a fact — which is what makes "save before
    ///     quitting?" possible.
    /// </summary>
    [Fact]
    public void AskingAWindowToCloseDoesNotCloseIt() {
        using var window = (HeadlessWindow)platform.CreateWindow(new());
        platform.PumpEvents();

        window.RequestClose();

        Assert.Single(Events(PlatformEventKind.WindowCloseRequested));
        Assert.False(window.IsClosed);
        Assert.Single(platform.Windows);
    }

    [Fact]
    public void AnIconOfTheWrongSizeIsRejectedRatherThanStored() {
        using var window = platform.CreateWindow(new());

        Assert.Throws<ArgumentException>(() => window.SetIcon(new byte[10], new(16, 16)));
        window.SetIcon(new byte[16 * 16 * 4], new(16, 16));
        Assert.Equal(new Int2(16, 16), ((HeadlessWindow)window).IconSize);
    }

    [Fact]
    public void APostedEventComesBackFromThePump() {
        platform.Post(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 0, 42, Key.Escape, KeyModifiers.None));

        var pumped = Assert.Single(platform.PumpEvents().ToArray());

        Assert.Equal(Key.Escape, pumped.Key);
        Assert.Equal(42, pumped.Timestamp);
    }

    [Fact]
    public void UsingThePlatformFromAnotherThreadIsRefusedRatherThanRaced() {
        // A real thread rather than Task.Run: waiting on a pool task can inline it onto the waiting
        // thread, which is the same thread, which makes the test pass for the wrong reason roughly
        // half the time. It did.
        Exception? caught = null;
        var thread = new Thread(
            () => {
                try {
                    platform.PumpEvents();
                } catch (Exception exception) {
                    caught = exception;
                }
            }
        );

        thread.Start();
        thread.Join();

        var thrown = Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("owned by thread", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoShellToHandAUrlTo() => Assert.False(platform.TryOpenUrl("https://example.com"));

    [Fact]
    public void DisposingClosesEverythingItOpened() {
        var window = platform.CreateWindow(new());

        platform.Dispose();

        Assert.True(window.IsClosed);
        Assert.Empty(platform.Windows);
        Assert.Equal(ApplicationState.Stopping, platform.Lifecycle.State);
    }

    public void Dispose() {
        platform.Dispose();
        files.Dispose();
    }

    PlatformEvent[] Events(PlatformEventKind kind) =>
        platform.PumpEvents().ToArray().Where(item => item.Kind == kind).ToArray();
}
