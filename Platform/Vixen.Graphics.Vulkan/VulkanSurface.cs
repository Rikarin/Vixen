// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Vixen.Core;

namespace Vixen.Graphics.Vulkan;

/// <summary>Turning a window's native handles into a <c>VkSurfaceKHR</c>.</summary>
/// <remarks>
///     <para>
///         Each windowing system has its own extension and its own create-info struct, and the
///         extension has to be enabled on the <em>instance</em> — which is created before anything
///         knows what a window is. So <see cref="RequiredExtensions" /> is asked first and answers
///         from the surface kind alone, and it is deliberately a pure function of that.
///     </para>
///     <para>
///         On Apple this is <c>VK_EXT_metal_surface</c> over the <c>CAMetalLayer</c> the platform
///         layer already created ([10](../../docs/plan/10-platforms.md) § macOS). The older
///         <c>VK_MVK_macos_surface</c> takes an <c>NSView</c> and is deprecated, and going through the
///         view would let MoltenVK create the layer itself — which takes the choice of pixel format
///         and display sync away from us.
///     </para>
/// </remarks>
static unsafe class VulkanSurface {
    /// <summary>Which instance extensions a surface of this kind needs.</summary>
    /// <param name="kind">Which windowing system produced the handles.</param>
    public static string[] RequiredExtensions(SurfaceKind kind) => kind switch {
        SurfaceKind.None => [],
        SurfaceKind.Win32 => [KhrSurface.ExtensionName, KhrWin32Surface.ExtensionName],
        SurfaceKind.Xlib => [KhrSurface.ExtensionName, KhrXlibSurface.ExtensionName],
        SurfaceKind.Wayland => [KhrSurface.ExtensionName, KhrWaylandSurface.ExtensionName],
        SurfaceKind.Metal => [KhrSurface.ExtensionName, ExtMetalSurface.ExtensionName],
        SurfaceKind.Android => [KhrSurface.ExtensionName, KhrAndroidSurface.ExtensionName],
        _ => []
    };

    /// <summary>Creates a surface for a window.</summary>
    /// <param name="instance">The instance, whose extensions must already include the right one.</param>
    /// <param name="handle">The window's native handles.</param>
    /// <param name="surface">The surface, when one was created.</param>
    /// <param name="reason">Why one was not, when one was not.</param>
    public static bool TryCreate(
        VulkanInstance instance,
        SurfaceHandle handle,
        out SurfaceKHR surface,
        [NotNullWhen(false)] out string? reason
    ) {
        surface = default;
        var api = instance.Api;

        switch (handle.Kind) {
            case SurfaceKind.Win32: {
                if (!api.TryGetInstanceExtension(instance.Handle, out KhrWin32Surface win32)) {
                    reason = Missing(KhrWin32Surface.ExtensionName);
                    return false;
                }

                var info = new Win32SurfaceCreateInfoKHR {
                    SType = StructureType.Win32SurfaceCreateInfoKhr,
                    Hinstance = handle.Display,
                    Hwnd = handle.Handle
                };

                return Finish(win32.CreateWin32Surface(instance.Handle, &info, null, out surface), out reason);
            }

            case SurfaceKind.Xlib: {
                if (!api.TryGetInstanceExtension(instance.Handle, out KhrXlibSurface xlib)) {
                    reason = Missing(KhrXlibSurface.ExtensionName);
                    return false;
                }

                var info = new XlibSurfaceCreateInfoKHR {
                    SType = StructureType.XlibSurfaceCreateInfoKhr,
                    Dpy = (nint*)handle.Display,
                    Window = handle.Handle
                };

                return Finish(xlib.CreateXlibSurface(instance.Handle, &info, null, out surface), out reason);
            }

            case SurfaceKind.Wayland: {
                if (!api.TryGetInstanceExtension(instance.Handle, out KhrWaylandSurface wayland)) {
                    reason = Missing(KhrWaylandSurface.ExtensionName);
                    return false;
                }

                var info = new WaylandSurfaceCreateInfoKHR {
                    SType = StructureType.WaylandSurfaceCreateInfoKhr,
                    Display = (nint*)handle.Display,
                    Surface = (nint*)handle.Handle
                };

                return Finish(
                    wayland.CreateWaylandSurface(instance.Handle, &info, null, out surface),
                    out reason
                );
            }

            case SurfaceKind.Metal: {
                if (!api.TryGetInstanceExtension(instance.Handle, out ExtMetalSurface metal)) {
                    reason = Missing(ExtMetalSurface.ExtensionName);
                    return false;
                }

                var info = new MetalSurfaceCreateInfoEXT {
                    SType = StructureType.MetalSurfaceCreateInfoExt,
                    PLayer = (nint*)handle.Handle
                };

                return Finish(metal.CreateMetalSurface(instance.Handle, &info, null, out surface), out reason);
            }

            case SurfaceKind.Android: {
                if (!api.TryGetInstanceExtension(instance.Handle, out KhrAndroidSurface android)) {
                    reason = Missing(KhrAndroidSurface.ExtensionName);
                    return false;
                }

                var info = new AndroidSurfaceCreateInfoKHR {
                    SType = StructureType.AndroidSurfaceCreateInfoKhr,
                    Window = (nint*)handle.Handle
                };

                return Finish(
                    android.CreateAndroidSurface(instance.Handle, &info, null, out surface),
                    out reason
                );
            }

            case SurfaceKind.None:
                reason = "A swapchain was asked for on a platform that has nothing to present to.";
                return false;

            case SurfaceKind.Web:
            default:
                reason = $"The Vulkan backend has no surface path for {handle.Kind}.";
                return false;
        }
    }

    static string Missing(string extension) =>
        $"The instance was created without {extension}, so no surface of this kind can be made. "
        + "The extension list comes from the window's surface kind, so this means the window changed "
        + "kind after the instance was created.";

    static bool Finish(Result result, [NotNullWhen(false)] out string? reason) {
        if (result == Result.Success) {
            reason = null;
            return true;
        }

        reason = $"Creating the Vulkan surface failed with {result}.";
        return false;
    }
}
