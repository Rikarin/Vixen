// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.OpenGL;
using Vixen.Graphics.Vulkan;
using Vixen.Graphics.WebGPU;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>Choosing a graphics device, and saying which one was chosen and why.</summary>
/// <remarks>
///     <para>
///         <see cref="PlatformHost" />'s counterpart: it walks
///         <see cref="GraphicsOptions.Backends" /> in order, asks each candidate to open, and
///         returns the first that does. A head that wants a device it already owns hands one to
///         <see cref="AppBuilder.WithGraphics" /> and never reaches this; a head that wants an API
///         this package does not reference implements <see cref="IGraphicsBackend" /> itself.
///     </para>
///     <para>
///         ⚠ <b>Every rejection is reported, not just the last.</b> A chain that fell all the way
///         through used to leave one sentence about whichever candidate happened to be final, which
///         is the least useful of the answers available — "no Vulkan" says nothing about why WebGPU
///         was not used either. <see cref="Create(GraphicsOptions, IWindow?, ILoggerFactory?, out string?)" />
///         joins them, so one log line explains the whole decision.
///     </para>
///     <para>
///         <b>The fallback to a device that draws nothing is opt-in, and is not a failure.</b>
///         <a href="../../docs/plan/17-app-heads-and-shipping.md">Doc 17</a> makes
///         <c>Vixen.Graphics.Null</c> a shipping backend rather than only a test one: it is what the
///         dedicated server runs on, and running the whole frame against it is what keeps a server
///         and a client one program rather than two code paths that drift. It happens when
///         <see cref="GraphicsBackend.Null" /> is in the list — which <see cref="Default" /> puts
///         there — and not otherwise, because an operator who asked for Vulkan alone is owed the
///         answer rather than a silent downgrade.
///     </para>
/// </remarks>
public static class GraphicsHost {
    /// <summary>What is tried when an application named no preference.</summary>
    /// <remarks>
    ///     ⚠ <b>Vulkan then Null, which is exactly what this package did before the list existed.</b>
    ///     WebGPU is deliberately absent: it is a supported choice, not a default one, and promoting
    ///     it here would silently change which API every existing head runs on. A game that wants it
    ///     asks — <c>config.Graphics.Backends = [WebGpu, Vulkan, Null]</c> — or an operator does,
    ///     with <c>--vixen-backend</c>.
    /// </remarks>
    public static IReadOnlyList<GraphicsBackend> Default { get; } = [GraphicsBackend.Vulkan, GraphicsBackend.Null];

    /// <summary>This class as an <see cref="IGraphicsBackend" />, which is what a builder takes.</summary>
    /// <remarks>
    ///     <see cref="PlatformHost.Instance" />'s counterpart, there for the same reason: the
    ///     builder is in <c>Core/</c> and cannot name Vulkan, WebGPU, OpenGL or Null, which are the
    ///     four things this class exists to choose between.
    /// </remarks>
    public static IGraphicsBackend Instance { get; } = new Backend();

    /// <summary>Opens the first backend in the preference list that will open.</summary>
    /// <param name="options">What the application asked for.</param>
    /// <param name="window">The window it will present to, or <see langword="null" /> for none.</param>
    /// <param name="logs">Where the backend logs.</param>
    /// <param name="reason">
    ///     What each rejected candidate said, joined; or a note that the device cannot present, when
    ///     one opened but has no surface. <see langword="null" /> when a presenting device was made.
    /// </param>
    /// <returns>The device, or <see langword="null" /> when nothing in the list would open.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    public static IGraphicsDevice? Create(
        GraphicsOptions options,
        IWindow? window,
        ILoggerFactory? logs,
        out string? reason
    ) {
        ArgumentNullException.ThrowIfNull(options);

        // Copied rather than aliased: `IList<T>` does not derive from `IReadOnlyList<T>`, and at
        // four entries once per process the copy is not worth a second code path to avoid.
        IReadOnlyList<GraphicsBackend> order = options.Backends.Count > 0 ? [.. options.Backends] : Default;
        var refusals = new List<string>(order.Count);

        // ⚠ Asking for a picture is what buys a device with no surface. See TryOpen's comment for
        // why the refusal is there; this is the one statement of intent specific enough to overrule
        // it, because a capture written by the Null device is a black PNG and a passing run.
        var offscreen = options.CapturePath is { Length: > 0 };

        foreach (var backend in order) {
            if (TryOpen(backend, window, logs, offscreen, out var device, out var refusal)) {
                // ⚠ Opening is not presenting. A device can be perfectly healthy and still have no
                // surface — an application that asked for no window, or a window made without the
                // backend's surface flag — and the host logs that as the reason a picture is not
                // appearing. Asked once, here, where the answer can still be turned into a sentence.
                reason = Presentable(backend, offscreen && !(window?.Surface.Handle ?? SurfaceHandle.None).CanPresent, refusals);

                return device;
            }

            refusals.Add($"{Spell(backend)}: {refusal}");
        }

        reason = refusals.Count > 0
            ? "no graphics backend would open. " + string.Join(" ", refusals)
            : "no graphics backend was asked for. GraphicsOptions.Backends was empty and so was the "
            + "default order, which should not be possible — see GraphicsHost.Default.";

        return null;
    }

    /// <summary>Opens a device on the default order, for a caller with no options to hand.</summary>
    /// <param name="window">The window it will present to, or <see langword="null" /> for none.</param>
    /// <param name="logs">Where the backend logs.</param>
    /// <param name="reason">Why the device cannot present, when it cannot.</param>
    /// <returns>The device. Never null: <see cref="Default" /> ends in Null, which always opens.</returns>
    public static IGraphicsDevice Create(IWindow? window, ILoggerFactory? logs, out string? reason) =>
        Create(new GraphicsOptions(), window, logs, out reason)
        ?? throw new InvalidOperationException(
            "The default backend order ends in GraphicsBackend.Null, which cannot fail to open, so "
            + "reaching here means GraphicsHost.Default was changed and the guarantee this overload "
            + "makes was not."
        );

    /// <summary>Creates the swapchain a device presents through.</summary>
    /// <param name="device">The device.</param>
    /// <param name="window">The window, or <see langword="null" />.</param>
    /// <param name="options">What the application asked for.</param>
    /// <returns>The swapchain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     The implementation is <see cref="AppGraphics.SwapChainFor" /> and this is the name it
    ///     was reached by. It went to <c>Core/</c> with the rest of the host because nothing in it
    ///     is backend-specific — a surface handle, a size, and two format choices off
    ///     <see cref="GraphicsOptions" />.
    /// </remarks>
    public static ISwapChain CreateSwapChain(IGraphicsDevice device, IWindow? window, GraphicsOptions options) =>
        AppGraphics.SwapChainFor(device, window, options);

    /// <summary>The window's framebuffer size, never zero in either axis.</summary>
    /// <param name="window">The window.</param>
    /// <param name="options">Unused; kept so the pair reads alike at a call site.</param>
    /// <returns>The size to build a swapchain at.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window" /> is null.</exception>
    /// <remarks>See <see cref="AppGraphics.FramebufferOf" /> for why it is clamped and why it is
    ///     the framebuffer rather than the client size.</remarks>
    public static Int2 Framebuffer(IWindow window, GraphicsOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        return AppGraphics.FramebufferOf(window);
    }

    /// <summary>Asks one backend to open, in the one shape every backend now has.</summary>
    /// <remarks>
    ///     ⚠ <b>Four calls that look alike, which they did not before.</b> Vulkan reported through
    ///     <c>TryCreate</c>, WebGPU took two steps through its native binding, OpenGL and Null only
    ///     had constructors — so a selector had to know each one's shape. They each have the same
    ///     <c>TryCreate(options, out device, out reason)</c> now, and this method is four lines of
    ///     mapping rather than four special cases.
    /// </remarks>
    static bool TryOpen(
        GraphicsBackend backend,
        IWindow? window,
        ILoggerFactory? logs,
        bool offscreen,
        out IGraphicsDevice? device,
        out string? reason
    ) {
        var surface = window?.Surface.Handle ?? SurfaceHandle.None;

        // ⚠ A backend that can only draw to a window declines when there is not one, and this is
        // load-bearing rather than an optimisation. Vulkan creates perfectly happily with no surface
        // — `presenting` is false, no surface extensions are asked for, and a headless device comes
        // back — so a chain that simply tried it would hand a dedicated server a real GPU device
        // where it used to get the Null one. That is doc 17's server quietly changing backend, and
        // it would only be noticed as a machine that suddenly needs a driver.
        //
        // The test differs by backend and the difference is not cosmetic. Vulkan and WebGPU build a
        // swapchain on a surface, so what they need is a surface that can be presented to. OpenGL
        // has no swapchain at all — it draws into the window's own default framebuffer — so what it
        // needs is a window, and whether that window can give it a context is the window's answer.
        //
        // ⚠ `offscreen` is the one thing that lifts the first of those, and only for Vulkan. It
        // means the caller asked for a picture written to a file, which is a request the Null device
        // can only answer with a black PNG — so declining and falling through is the wrong answer
        // there, and VulkanOffscreenSwapChain is what makes the right one possible. WebGPU keeps the
        // refusal because it has no equivalent: its swapchain is its surface, and a WebGPU device
        // with none has nothing for the frame's last pass to write into.
        var refused = backend switch {
            GraphicsBackend.Vulkan when !surface.CanPresent && offscreen => null,
            GraphicsBackend.Vulkan or GraphicsBackend.WebGpu when !surface.CanPresent => window is null
                ? "the application asked for no window."
                : $"the window's surface is {surface.Kind}, which cannot be presented to.",
            GraphicsBackend.OpenGl when window is null => "the application asked for no window.",
            _ => null
        };

        if (refused is not null) {
            device = null;
            reason = refused;

            return false;
        }

        switch (backend) {
            case GraphicsBackend.Vulkan: {
                var opened = VulkanDevice.TryCreate(
                    new() { Surface = surface, Logger = logs?.CreateLogger("Vulkan") },
                    out var vulkan,
                    out reason
                );

                device = vulkan;
                return opened;
            }

            case GraphicsBackend.WebGpu: {
                var opened = WebGpuDevice.TryCreate(
                    new() { Surface = surface, Logger = logs?.CreateLogger("WebGPU") },
                    out var webgpu,
                    out reason
                );

                device = webgpu;
                return opened;
            }

            case GraphicsBackend.OpenGl: {
                device = null;

                // ⚠ The window decides, and it decided before this ran. SDL fixes a window's
                // graphics API at creation and the OpenGL and Vulkan flags are mutually exclusive,
                // so a window made for Vulkan can never yield a GL context — which is why
                // PlatformHost reads the preference list to choose the flag, and why a list of
                // [Vulkan, OpenGl, …] cannot fall back from one to the other in a single process.
                if (window is not IGlContextSource source) {
                    reason = "this window cannot produce an OpenGL context. Put GraphicsBackend.OpenGl "
                        + "first in GraphicsOptions.Backends so the window is created for it.";

                    return false;
                }

                if (!source.TryCreateGlContext(new(), out var context, out reason)) {
                    return false;
                }

                // ⚠ Current on this thread, and the entry points are loaded against it. Both are
                // GL's rules rather than ours: a table resolved with no context current is a table
                // of nulls that fails at the first draw instead of here.
                context!.MakeCurrent();

                if (ProfileOf(context) is not { } profile) {
                    reason = $"this driver's OpenGL is {context.MajorVersion}.{context.MinorVersion}"
                        + $"{(context.IsEmbedded ? " ES" : " core")}, and Vixen's GL backend needs 4.5 core "
                        + "or GLES 3.0. Below that there is no glClipControl and no way to make GL's "
                        + "clip space match Vulkan's, which every shader this engine compiles assumes. "
                        + "⚠ macOS caps OpenGL at 4.1 and has deprecated it, so this backend cannot run "
                        + "there at all.";

                    context.Dispose();

                    return false;
                }

                var api = SilkGlApi.FromProcAddress(context.GetProcAddress, profile);

                // Present is the swap. GlDeviceOptions has carried this hook since the backend was
                // written and nothing had one to give it — GL has no swapchain object, so
                // presenting *is* the windowing layer's call.
                var opened = GlDevice.TryCreate(new(api, context.SwapBuffers), out var gl, out reason);

                device = gl;

                if (!opened) {
                    api.Dispose();
                }

                return opened;
            }

            case GraphicsBackend.Null: {
                var opened = NullDevice.TryCreate(new() { Record = true }, out var none, out reason);

                device = none;
                return opened;
            }

            default: {
                device = null;
                reason = $"'{backend.ToString().ToLowerInvariant()}' is not a graphics backend.";

                return false;
            }
        }
    }

    /// <summary>What to say about the device that opened, or null when there is nothing to say.</summary>
    /// <remarks>
    ///     ⚠ <b>Two winners can have no surface, and they mean opposite things.</b> Null draws
    ///     nothing because that is what it is for; an offscreen Vulkan device draws the whole frame
    ///     and keeps it, because a capture asked for it. Both are "nothing will appear on a screen"
    ///     and only one of them is a downgrade, so they are worth different sentences — a run that
    ///     said "the Null backend draws nothing" while writing a real picture would send its reader
    ///     hunting for a bug that is not there.
    /// </remarks>
    static string? Presentable(GraphicsBackend backend, bool offscreen, List<string> refusals) {
        var note = backend switch {
            GraphicsBackend.Null => "the Null backend draws nothing by design.",
            _ when offscreen => $"{Spell(backend)} is drawing offscreen, because a capture was asked "
                + "for and there is nothing to present to.",
            _ => null
        };

        if (note is not null) {
            return refusals.Count > 0 ? string.Join(" ", refusals) + " " + note : note;
        }

        // Worth saying even on success: "Vulkan is running" reads very differently from "WebGPU was
        // asked for first and could not load, so Vulkan is running".
        return refusals.Count > 0 ? string.Join(" ", refusals) + $" Using {Spell(backend)}." : null;
    }

    /// <summary>Which dialect the context that was actually created speaks.</summary>
    /// <remarks>
    ///     ⚠ <b>Read back from the context, not from what was asked for.</b> A driver is entitled to
    ///     hand over more than the request — 4.5 core routinely arrives as 4.6 — and the profile is
    ///     what decides whether <c>GlslTranslator</c> emits <c>glClipControl</c> or the vertex-shader
    ///     fixup that stands in for it. Guessing here would be guessing about clip space.
    /// </remarks>
    static GlProfile? ProfileOf(IGlContext context) {
        var version = (context.MajorVersion, context.MinorVersion);

        if (!context.IsEmbedded) {
            // ⚠ 4.5 or nothing on the desktop, because Core45 is the only desktop profile this
            // backend has and the thing that makes it viable is glClipControl — without it GL's
            // clip space is not Vulkan's and every shader would need the fixup path that only the
            // GLES profiles carry. A 4.1 context is not "nearly 4.5"; it is a different target.
            return version is ( > 4, _) or (4, >= 5) ? GlProfile.Core45 : null;
        }

        return version switch {
            ( > 3, _) or (3, >= 2) => GlProfile.Es32,
            (3, >= 0) => GlProfile.Es30,
            _ => null
        };
    }

    /// <summary>The name an operator would have typed for a backend.</summary>
    static string Spell(GraphicsBackend backend) =>
        backend.ToString().ToLower(CultureInfo.InvariantCulture);

    sealed class Backend : IGraphicsBackend {
        public IGraphicsDevice? Create(
            GraphicsOptions options,
            IWindow? window,
            ILoggerFactory? logs,
            out string? reason
        ) => GraphicsHost.Create(options, window, logs, out reason);
    }
}
