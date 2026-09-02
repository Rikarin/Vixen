// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

// Lives in Vixen.Core rather than in Vixen.Platform because it is the seam between two layers that
// must not depend on each other. A window produces one of these and a swapchain consumes one, but
// Vixen.Graphics sits in Core and cannot reference Vixen.Platform (docs/plan/00 § Layer discipline),
// so the type they both name has to sit below both. Two nints and a discriminant is little enough
// to be worth the placement.

/// <summary>Which windowing system a surface handle belongs to, and therefore how to read it.</summary>
/// <remarks>
///     The set is closed on purpose: a graphics backend switches over it exhaustively, and a new
///     entry should be a compile error in every backend rather than a runtime surprise in one.
/// </remarks>
public enum SurfaceKind : byte {
    /// <summary>Nothing to present to.</summary>
    /// <remarks>
    ///     What a headless platform reports. A backend that sees this renders offscreen and never
    ///     builds a swapchain — which is the dedicated server's path and the golden-image tests'
    ///     path, so it is exercised constantly rather than being a special case that rots.
    /// </remarks>
    None = 0,

    /// <summary>Win32. <see cref="SurfaceHandle.Display" /> is the <c>HINSTANCE</c>,
    /// <see cref="SurfaceHandle.Handle" /> the <c>HWND</c>.</summary>
    Win32 = 1,

    /// <summary>X11. <see cref="SurfaceHandle.Display" /> is the <c>Display*</c>,
    /// <see cref="SurfaceHandle.Handle" /> the <c>Window</c> XID.</summary>
    Xlib = 2,

    /// <summary>Wayland. <see cref="SurfaceHandle.Display" /> is the <c>wl_display*</c>,
    /// <see cref="SurfaceHandle.Handle" /> the <c>wl_surface*</c>.</summary>
    Wayland = 3,

    /// <summary>Apple. <see cref="SurfaceHandle.Handle" /> is a <c>CAMetalLayer*</c>, which is what
    /// <c>VK_EXT_metal_surface</c> wants and what MoltenVK presents through.</summary>
    /// <remarks>
    ///     Deliberately the layer and not the <c>NSView</c>/<c>UIView</c>: the older
    ///     <c>VK_MVK_*_surface</c> extensions take a view and are deprecated, and going through the
    ///     view means MoltenVK creates the layer itself, which takes the choice of pixel format and
    ///     display sync away from us.
    /// </remarks>
    Metal = 4,

    /// <summary>Android. <see cref="SurfaceHandle.Handle" /> is the <c>ANativeWindow*</c>.</summary>
    /// <remarks>
    ///     Destroyed and recreated across a suspend, so the handle is only valid between a
    ///     <c>PlatformEventKind.Resumed</c> and the next
    ///     <c>PlatformEventKind.Suspending</c>.
    /// </remarks>
    Android = 5,

    /// <summary>A browser canvas, identified by its element id rather than by a pointer.</summary>
    Web = 6,

    /// <summary>A presentable surface with no window behind it. Neither handle is read.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <see cref="None" />, and the difference is the whole point.</b>
    ///         <see cref="None" /> says "there is nothing to present to", and a backend that sees it
    ///         renders offscreen and never builds a swapchain. This says "build the real swapchain;
    ///         the images simply go nowhere" — every image is acquired, presented and recycled by an
    ///         actual presentation engine, so <c>vkAcquireNextImageKHR</c> and
    ///         <c>vkQueuePresentKHR</c> are exercised rather than stubbed.
    ///     </para>
    ///     <para>
    ///         It exists because presentation was the one path in the RHI with no automated coverage
    ///         at all, and the stated reason was that it needs a window — which on macOS means the
    ///         main thread, because AppKit aborts a process that makes a window anywhere else. That
    ///         reason turned out not to hold: <c>VK_EXT_headless_surface</c> is an ordinary
    ///         <c>VkSurfaceKHR</c>, and MoltenVK and lavapipe both carry it. Nothing a platform
    ///         reports is ever this kind; it is asked for by a test, a benchmark or a CI leg that
    ///         wants the presenting path and has no display server.
    ///     </para>
    ///     <para>
    ///         Only the Vulkan backend answers it. WebGPU and OpenGL refuse it the way they refuse
    ///         any surface kind they have no descriptor for — WebGPU's swapchain <em>is</em> its
    ///         surface, and OpenGL presents by swapping a window's own buffers.
    ///     </para>
    /// </remarks>
    Headless = 7
}

/// <summary>The native handles a graphics backend needs to present to a window.</summary>
/// <remarks>
///     <para>
///         Two <see cref="nint" />s and a discriminant covers every windowing system Vixen targets,
///         which is why the RHI can take a swapchain target without knowing what a window is. The
///         alternative — a Vulkan-shaped type here — would put a Silk.NET type in the platform
///         layer's public surface and break the rule in ADR-001 §3 that keeps the RHI mappable to a
///         second backend.
///     </para>
///     <para>
///         Nothing here is owned. The window owns its native resources and these stop being valid
///         when it is disposed, or, on Android, when the process is suspended.
///     </para>
/// </remarks>
/// <param name="Kind">Which windowing system produced the handles.</param>
/// <param name="Display">The connection or instance handle, where the platform has one.</param>
/// <param name="Handle">The window, surface or layer handle.</param>
public readonly record struct SurfaceHandle(SurfaceKind Kind, nint Display, nint Handle) {
    /// <summary>The handle a headless platform reports: nothing to present to.</summary>
    public static SurfaceHandle None => default;

    /// <summary>A surface with no window behind it, for a run that wants the presenting path anyway.</summary>
    /// <remarks>See <see cref="SurfaceKind.Headless" />. Both handles are zero and neither is read.</remarks>
    public static SurfaceHandle Windowless => new(SurfaceKind.Headless, Display: 0, Handle: 0);

    /// <summary>Whether this handle can back a swapchain.</summary>
    /// <remarks>
    ///     ⚠ <b>True for <see cref="SurfaceKind.Headless" />, which has no window.</b> The question
    ///     this answers is "is there something a swapchain can be built on", not "is there something
    ///     a person can see" — a headless surface answers the first and not the second, and it is the
    ///     first that every caller here is asking.
    /// </remarks>
    public bool CanPresent => Kind != SurfaceKind.None;
}
