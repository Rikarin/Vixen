// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>Choosing a graphics device, and saying which one was chosen and why.</summary>
/// <remarks>
///     <para>
///         <see cref="PlatformHost" />'s counterpart, and the same shape for the same reason: there
///         are two backends an app head can boot on its own, the choice between them is one question
///         — is there a surface to present to — and a registry for something with two answers would
///         be machinery for its own sake. A head that wants OpenGL, WebGPU, or a device it already
///         owns hands one to <see cref="AppBuilder.WithGraphics" /> and never reaches this.
///     </para>
///     <para>
///         <b>The fallback is to a device that draws nothing, and it is not a failure.</b>
///         [Doc 17](../../docs/plan/17-app-heads-and-shipping.md) makes <c>Vixen.Graphics.Null</c> a
///         shipping backend rather than only a test one: it is what the dedicated server runs on, and
///         running the whole frame against it is what keeps a server and a client one program rather
///         than two code paths that drift. A machine with no Vulkan gets the same treatment, loudly —
///         a build that wanted a picture and is not drawing one has to say so, exactly as the
///         headless platform fallback does.
///     </para>
/// </remarks>
public static class GraphicsHost {
    /// <summary>Builds the device an application will draw with.</summary>
    /// <param name="window">The window it will present to, or <see langword="null" /> for none.</param>
    /// <param name="logs">Where the backend logs.</param>
    /// <param name="reason">
    ///     Why the device cannot present, when it cannot — no window, no surface, or the message the
    ///     Vulkan backend gave. <see langword="null" /> when a presenting device was created.
    /// </param>
    /// <returns>The device. Never null; it may be one that draws nothing.</returns>
    public static IGraphicsDevice Create(IWindow? window, ILoggerFactory? logs, out string? reason) {
        if (window is null) {
            reason = "the application asked for no window.";
            return Offscreen();
        }

        // A window made without the backend's surface flag has no surface to present to, and the
        // failure otherwise arrives several frames later as a swapchain that cannot be created. Asked
        // here, once, where the answer can still be turned into a sentence.
        if (!window.Surface.Handle.CanPresent) {
            reason = $"the window's surface is {window.Surface.Handle.Kind}, which cannot be presented to.";
            return Offscreen();
        }

        var options = new VulkanDeviceOptions {
            Surface = window.Surface.Handle,
            Logger = logs?.CreateLogger("Vulkan")
        };

        if (!VulkanDevice.TryCreate(options, out var device, out var failure)) {
            reason = failure;
            return Offscreen();
        }

        reason = null;
        return device;
    }

    /// <summary>Creates the swapchain a device presents through.</summary>
    /// <param name="device">The device.</param>
    /// <param name="window">The window, or <see langword="null" />.</param>
    /// <param name="options">What the application asked for.</param>
    /// <returns>The swapchain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Sized from <c>FramebufferSize</c> rather than <c>ClientSize</c></b>, because the
    ///     framebuffer is what a swapchain image is measured in and the two disagree by the display's
    ///     scale factor. A swapchain built from the client size on a 2× display is a quarter of the
    ///     window, and what it looks like is a game rendered into the top-left corner.
    /// </remarks>
    public static ISwapChain CreateSwapChain(IGraphicsDevice device, IWindow? window, GraphicsOptions options) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);

        var surface = window?.Surface.Handle ?? SurfaceHandle.None;
        var size = window is null ? options.WindowlessSize : Framebuffer(window, options);

        return device.CreateSwapChain(new(surface, size, options.Format, options.PresentMode));
    }

    /// <summary>The window's framebuffer size, never zero in either axis.</summary>
    /// <remarks>
    ///     A minimised window reports zero, and every backend refuses a zero-sized swapchain — so a
    ///     host that passed it straight through would turn "the user minimised the window" into a
    ///     crash on the resize that follows.
    /// </remarks>
    public static Int2 Framebuffer(IWindow window, GraphicsOptions options) {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(options);

        return new(Math.Max(window.FramebufferSize.X, 1), Math.Max(window.FramebufferSize.Y, 1));
    }

    /// <summary>The backend that records a whole frame and draws none of it.</summary>
    /// <remarks>
    ///     <c>Record</c> is on so that everything downstream of a draw call still happens — the graph
    ///     is built, the passes are ordered, the barriers are placed, the descriptor sets are written
    ///     — and can be asserted. That is what makes <c>--vixen-frames 10</c> a smoke test of the
    ///     whole renderer on a machine with no GPU, which is the only kind of machine CI has.
    /// </remarks>
    static IGraphicsDevice Offscreen() => new NullDevice(new() { Record = true });
}
