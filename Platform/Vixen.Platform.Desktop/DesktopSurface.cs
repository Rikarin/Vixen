// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.SDL;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Desktop;

/// <summary>The native handles a graphics backend presents to.</summary>
/// <remarks>
///     <para>
///         Two routes, because Apple needs a different one. Everywhere else
///         <c>SDL_GetWindowWMInfo</c> reports the windowing system and its handles directly. On
///         macOS the thing a Vulkan surface is created from is a <c>CAMetalLayer</c>, and the
///         <c>NSWindow</c> that <c>SDL_GetWindowWMInfo</c> returns is not one — so the layer comes
///         from <c>SDL_Metal_CreateView</c> plus <c>SDL_Metal_GetLayer</c>, which is SDL's supported
///         way to get one and avoids reaching into AppKit from here.
///     </para>
///     <para>
///         Resolved once and cached. The handles do not change for the life of the window on any
///         desktop; Android's do, which is why the contract says so and why this class is not the
///         one that will run there.
///     </para>
/// </remarks>
sealed unsafe class DesktopSurface(Sdl sdl, DesktopWindow window) : ISurface {
    SurfaceHandle resolved;
    void* metalView;
    bool attempted;

    public SurfaceHandle Handle {
        get {
            if (!attempted) {
                attempted = true;
                resolved = Resolve();
            }

            return resolved;
        }
    }

    public Int2 PixelSize => window.FramebufferSize;

    public void Release() {
        if (metalView is not null) {
            sdl.MetalDestroyView(metalView);
            metalView = null;
        }

        resolved = SurfaceHandle.None;
        attempted = false;
    }

    SurfaceHandle Resolve() {
        if (OperatingSystem.IsMacOS()) {
            metalView = sdl.MetalCreateView(window.Handle);

            if (metalView is null) {
                return SurfaceHandle.None;
            }

            var layer = sdl.MetalGetLayer(metalView);
            return layer is null ? SurfaceHandle.None : new(SurfaceKind.Metal, 0, (nint)layer);
        }

        SysWMInfo info = default;

        // SDL refuses to fill this in unless it is told which version of the struct the caller
        // compiled against — the field is an in/out parameter, and leaving it zero makes the call
        // fail with "That operation is not supported", which reads like a platform problem and is
        // not one.
        sdl.GetVersion(&info.Version);

        if (!sdl.GetWindowWMInfo(window.Handle, &info)) {
            return SurfaceHandle.None;
        }

        return info.Subsystem switch {
            SysWMType.Windows => new(
                SurfaceKind.Win32,
                (nint)info.Info.Win.HInstance,
                (nint)info.Info.Win.Hwnd
            ),
            SysWMType.X11 => new(SurfaceKind.Xlib, (nint)info.Info.X11.Display, (nint)info.Info.X11.Window),
            SysWMType.Wayland => new(
                SurfaceKind.Wayland,
                (nint)info.Info.Wayland.Display,
                (nint)info.Info.Wayland.Surface
            ),
            _ => SurfaceHandle.None
        };
    }
}
