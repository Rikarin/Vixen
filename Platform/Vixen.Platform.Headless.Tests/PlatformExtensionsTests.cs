// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Platform.Headless.Tests;

/// <summary>
///     <see cref="PlatformExtensions" />, exercised here rather than in <c>Vixen.Platform.Tests</c>
///     because the questions it answers are about a platform's window list, and the headless platform
///     is the only implementation a test can build — the desktop one needs a display server, and on
///     macOS a window created off the process's main thread aborts the process.
/// </summary>
public sealed class PlatformExtensionsTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();
    readonly HeadlessPlatform platform;

    public PlatformExtensionsTests() {
        platform = new(new() { FileSystem = files });
    }

    [Fact]
    public void NobodyHasFocusUntilSomebodyIsGivenIt() {
        using var window = platform.CreateWindow(new());

        Assert.Null(platform.FocusedWindow());
    }

    [Fact]
    public void TheFocusedWindowIsTheOneThatHasFocus() {
        using var first = (HeadlessWindow)platform.CreateWindow(new());
        using var second = (HeadlessWindow)platform.CreateWindow(new());

        second.SetFocused(true);

        Assert.Same(second, platform.FocusedWindow());
    }

    /// <summary>
    ///     The regression. A window closed by its title bar is disposed during the pump that
    ///     delivered the close request, and the platform keeps it in the list until the start of the
    ///     <em>next</em> pump so that the list does not change under an application walking it inside
    ///     its own event handling. So for the rest of that frame the list contains a disposed window
    ///     — and every member of <see cref="IWindow" /> but <see cref="IWindow.IsClosed" /> throws on
    ///     one.
    /// </summary>
    /// <remarks>
    ///     Found by closing the Hello Triangle sample with the title bar's button: the frame limiter
    ///     asks which window has focus at the end of every frame, including the frame that destroyed
    ///     the only window, and the process went down with an <see cref="ObjectDisposedException" />
    ///     instead of exiting.
    /// </remarks>
    [Fact]
    public void AWindowDisposedThisFrameIsSkippedRatherThanAsked() {
        var window = (HeadlessWindow)platform.CreateWindow(new());
        window.SetFocused(true);
        window.Dispose();

        // Still listed — that is the precondition, not an accident. Without it this test would pass
        // against the bug it exists for.
        Assert.Same(window, Assert.Single(platform.Windows));
        Assert.True(window.IsClosed);

        Assert.Null(platform.FocusedWindow());
    }

    /// <summary>
    ///     And the surviving window is still found, so the guard skips the closed entry rather than
    ///     stopping at it.
    /// </summary>
    [Fact]
    public void AClosedWindowDoesNotHideAFocusedOneBehindIt() {
        var closed = (HeadlessWindow)platform.CreateWindow(new());
        using var open = (HeadlessWindow)platform.CreateWindow(new());

        closed.Dispose();
        open.SetFocused(true);

        Assert.Equal(2, platform.Windows.Count);
        Assert.Same(open, platform.FocusedWindow());
    }

    public void Dispose() {
        platform.Dispose();
        files.Dispose();
    }
}
