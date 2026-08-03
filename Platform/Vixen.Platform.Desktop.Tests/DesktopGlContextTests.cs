// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Desktop.Tests;

/// <summary>The OpenGL context a window can produce, and the window flag that decides whether it can.</summary>
/// <remarks>
///     ⚠ <b>The piece whose absence made <c>Vixen.Graphics.OpenGL</c> unbootable.</b> That backend
///     has been complete since ADR-001 made it the RHI's abstraction validator, and a
///     <c>GlDevice</c> needs entry points over a context that is already current — which nothing in
///     <c>Vixen.Platform</c> created, so the only callers were tests handing in their own
///     <c>IGlApi</c>. These assert the two halves that had to be true for an app head to reach it:
///     the window carries SDL's OpenGL flag, and a context can be made current on it.
/// </remarks>
public sealed class DesktopGlContextTests : IDisposable {
    readonly DesktopPlatform? platform;
    readonly string? unavailable;

    public DesktopGlContextTests() {
        if (!SdlLibrary.IsAvailable) {
            unavailable = "SDL2 is not installed on this machine.";
            return;
        }

        try {
            platform = new(
                new() {
                    Application = "Vixen.Tests",
                    EnableGameControllers = false,
                    VideoDriver = DesktopPlatformTests.PreferredVideoDriver(),
                    UseNativeSupplement = false,

                    // The subject. ⚠ Mutually exclusive with RequestGpuSurface, which defaults on —
                    // SDL fixes a window's graphics API at creation and a drawable is a Vulkan
                    // surface or a GL framebuffer, never both.
                    RequestGlContext = true
                }
            );
        } catch (PlatformNotSupportedException exception) {
            unavailable = $"SDL could not start a video driver: {exception.Message}";
        }
    }

    public void Dispose() => platform?.Dispose();

    /// <summary>The capability is reported, so a settings screen can offer the choice.</summary>
    [Fact]
    public void ThePlatformSaysItCanMakeGlContexts() {
        if (platform is null) {
            Assert.SkipWhen(true, unavailable!);
            return;
        }

        Assert.True(platform.Capabilities.HasFlag(PlatformCapabilities.GlContext));
    }

    /// <summary>A window asked for with the flag offers a context, and it can be made current.</summary>
    /// <remarks>
    ///     ⚠ <b>Skipped rather than failed where there is no GL, and there are two such places.</b>
    ///     The dummy video driver — what CI and every macOS test run uses — has no GL at all and
    ///     refuses at <i>window creation</i>, before a context is ever asked for. And SDL on Apple
    ///     Silicon builds windows backed by Metal, so it refuses there too. Insisting would turn
    ///     "this machine cannot run OpenGL" into a red build; what is asserted unconditionally is
    ///     that the refusal is a sentence rather than a crash, which
    ///     <see cref="AVulkanWindowRefusesAndSaysWhy" /> covers.
    /// </remarks>
    [Fact]
    public void AGlWindowProducesAContextThatCanBeMadeCurrent() {
        if (!TryGlWindow(out var window, out var skip)) {
            Assert.SkipWhen(true, skip);
            return;
        }

        using (window) {
            var source = Assert.IsAssignableFrom<IGlContextSource>(window);

            if (!source.TryCreateGlContext(new(), out var context, out var reason)) {
                Assert.SkipWhen(true, $"no GL context here: {reason}");
                return;
            }

            Assert.NotNull(context);

            context.MakeCurrent();

            // The driver is allowed to exceed the request and routinely does — 4.5 core arrives as
            // 4.6. What must not happen is a zero, which is what an unread attribute looks like.
            Assert.True(context.MajorVersion >= 2, $"reported {context.MajorVersion}.{context.MinorVersion}");

            // ⚠ The one call that proves the context is real rather than a handle: an address for a
            // function every GL since 1.0 has. A context that is not current resolves this to zero
            // on several drivers, which is exactly the failure this path exists to avoid.
            Assert.NotEqual(0, context.GetProcAddress("glGetError"));
        }
    }

    /// <summary>Asking twice gives the same context rather than a second one.</summary>
    [Fact]
    public void AWindowHasOneContext() {
        if (!TryGlWindow(out var window, out var skip)) {
            Assert.SkipWhen(true, skip);
            return;
        }

        using (window) {
            var source = (IGlContextSource)window;

            if (!source.TryCreateGlContext(new(), out var first, out var reason)) {
                Assert.SkipWhen(true, $"no GL context here: {reason}");
                return;
            }

            Assert.True(source.TryCreateGlContext(new(), out var again, out _));
            Assert.Same(first, again);
        }
    }

    /// <summary>Opens a GL window, or says why this machine cannot have one.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal arrives as an exception from <c>CreateWindow</c>, not as a failed
    ///     context.</b> SDL rejects <c>SDL_WINDOW_OPENGL</c> outright when the video driver has no
    ///     GL — "OpenGL support is either not configured in SDL or not available in current SDL
    ///     video driver (dummy)" — so the window never exists to be asked. Catching it here is what
    ///     keeps the skip in one place rather than in every test.
    /// </remarks>
    bool TryGlWindow(out IWindow window, out string skip) {
        window = null!;

        if (platform is null) {
            skip = unavailable!;
            return false;
        }

        try {
            window = platform.CreateWindow(new() { Title = "GL", Size = new(320, 240) });
            skip = string.Empty;

            return true;
        } catch (PlatformNotSupportedException refusal) {
            skip = refusal.Message;
            return false;
        }
    }

    /// <summary>A window made for Vulkan refuses, and says which flag it needed.</summary>
    /// <remarks>
    ///     ⚠ <b>The case a preference list of <c>[Vulkan, OpenGl, Null]</c> lands in.</b> SDL will
    ///     not change a window's graphics API, so falling back from Vulkan to OpenGL inside one
    ///     process is not possible without recreating the window — and the refusal has to say so,
    ///     because "OpenGL did not work" would send somebody looking at their driver.
    /// </remarks>
    [Fact]
    public void AVulkanWindowRefusesAndSaysWhy() {
        if (!SdlLibrary.IsAvailable) {
            Assert.SkipWhen(true, "SDL2 is not installed on this machine.");
            return;
        }

        using var vulkan = new DesktopPlatform(
            new() {
                Application = "Vixen.Tests",
                EnableGameControllers = false,
                VideoDriver = DesktopPlatformTests.PreferredVideoDriver(),
                UseNativeSupplement = false,
                RequestGpuSurface = false,
                RequestGlContext = false
            }
        );

        using var window = vulkan.CreateWindow(new() { Title = "No GL", Size = new(320, 240) });

        Assert.False(((IGlContextSource)window).TryCreateGlContext(new(), out var context, out var reason));
        Assert.Null(context);
        Assert.Contains("RequestGlContext", reason, StringComparison.Ordinal);
    }
}
