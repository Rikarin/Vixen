// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Vixen.Core;

namespace Vixen.Graphics.WebGPU.Native;

/// <summary>A window's native handles, as a WebGPU surface.</summary>
/// <remarks>
///     <c>webgpu.h</c> chains a platform-specific descriptor onto a generic one, the way Vulkan
///     chains a <c>VkSurfaceCreateInfo</c> — so this is a switch over
///     <see cref="SurfaceKind" /> and nothing more. It is exhaustive on purpose: a new windowing
///     system should be a compile error here rather than a runtime surprise.
/// </remarks>
static unsafe class NativeWebGpuSurfaces {
    /// <summary>Creates a surface for a window, or says why it could not.</summary>
    /// <param name="api">The loaded API.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="handle">The window's native handles.</param>
    /// <param name="surface">The surface, when one was created.</param>
    /// <param name="reason">Why it was not, when it was not.</param>
    public static bool TryCreate(
        Silk.NET.WebGPU.WebGPU api,
        Instance* instance,
        SurfaceHandle handle,
        out Surface* surface,
        [NotNullWhen(false)] out string? reason
    ) {
        surface = null;

        switch (handle.Kind) {
            case SurfaceKind.Win32: {
                var chained = new SurfaceDescriptorFromWindowsHWND {
                    Chain = new() { SType = SType.SurfaceDescriptorFromWindowsHwnd },
                    Hinstance = (void*)handle.Display,
                    Hwnd = (void*)handle.Handle
                };

                surface = Create(api, instance, &chained.Chain);
                break;
            }

            case SurfaceKind.Xlib: {
                var chained = new SurfaceDescriptorFromXlibWindow {
                    Chain = new() { SType = SType.SurfaceDescriptorFromXlibWindow },
                    Display = (void*)handle.Display,
                    Window = (ulong)handle.Handle
                };

                surface = Create(api, instance, &chained.Chain);
                break;
            }

            case SurfaceKind.Wayland: {
                var chained = new SurfaceDescriptorFromWaylandSurface {
                    Chain = new() { SType = SType.SurfaceDescriptorFromWaylandSurface },
                    Display = (void*)handle.Display,
                    Surface = (void*)handle.Handle
                };

                surface = Create(api, instance, &chained.Chain);
                break;
            }

            case SurfaceKind.Metal: {
                // The CAMetalLayer, not the view — the same choice SurfaceKind.Metal documents for
                // Vulkan, and for the same reason: going through the view lets the implementation
                // create the layer itself and take the pixel format and display sync with it.
                var chained = new SurfaceDescriptorFromMetalLayer {
                    Chain = new() { SType = SType.SurfaceDescriptorFromMetalLayer },
                    Layer = (void*)handle.Handle
                };

                surface = Create(api, instance, &chained.Chain);
                break;
            }

            case SurfaceKind.Android: {
                var chained = new SurfaceDescriptorFromAndroidNativeWindow {
                    Chain = new() { SType = SType.SurfaceDescriptorFromAndroidNativeWindow },
                    Window = (void*)handle.Handle
                };

                surface = Create(api, instance, &chained.Chain);
                break;
            }

            case SurfaceKind.Web:
                reason = "A browser canvas was handed to the native WebGPU surface. In a browser "
                    + "WebGPU is reached through navigator.gpu and not through webgpu.h at all — use "
                    + "Vixen.Graphics.WebGPU.Browser.";

                return false;

            case SurfaceKind.None:
                reason = "There is no window to present to.";
                return false;

            default:
                reason = $"SurfaceKind.{handle.Kind} has no WebGPU surface descriptor.";
                return false;
        }

        if (surface is null) {
            reason = $"wgpuInstanceCreateSurface returned nothing for a {handle.Kind} window.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Which of these a WebGPU implementation could present to at all.</summary>
    /// <param name="kind">The windowing system.</param>
    public static bool CanPresent(SurfaceKind kind) =>
        kind is SurfaceKind.Win32 or SurfaceKind.Xlib or SurfaceKind.Wayland or SurfaceKind.Metal
            or SurfaceKind.Android;

    static Surface* Create(Silk.NET.WebGPU.WebGPU api, Instance* instance, ChainedStruct* chain) {
        var label = SilkMarshal.StringToPtr("Vixen", NativeStringEncoding.UTF8);

        try {
            var descriptor = new SurfaceDescriptor { NextInChain = chain, Label = (byte*)label };
            return api.InstanceCreateSurface(instance, &descriptor);
        } finally {
            SilkMarshal.Free(label);
        }
    }
}
