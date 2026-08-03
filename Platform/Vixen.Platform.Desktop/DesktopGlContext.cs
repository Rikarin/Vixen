// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Silk.NET.SDL;
using SdlWindow = Silk.NET.SDL.Window;

namespace Vixen.Platform.Desktop;

/// <summary>An OpenGL context on an SDL window.</summary>
/// <remarks>
///     <para>
///         <b>The piece that was missing, and the reason <c>Vixen.Graphics.OpenGL</c> could not be
///         booted by an app head.</b> That backend has been complete and under test since ADR-001
///         made it the RHI's abstraction validator, but a <c>GlDevice</c> needs entry points over a
///         context that is <i>already current</i>, and nothing in <c>Vixen.Platform</c> created one
///         — so the only way to reach it was to hand in an <c>IGlApi</c> yourself, which is what the
///         tests do and what an application cannot.
///     </para>
///     <para>
///         ⚠ <b>The window has to have been created with the OpenGL flag.</b> SDL decides a
///         window's graphics API at creation and will not change it, and the OpenGL and Vulkan flags
///         are mutually exclusive — so this is asked for through
///         <c>DesktopPlatformOptions.RequestGlContext</c> <i>before</i> any window exists, and a
///         window made for Vulkan refuses with that as the reason rather than failing obscurely
///         inside the driver.
///     </para>
///     <para>
///         ⚠ <b>Attributes are set immediately before the context is created, not once at
///         start-up.</b> They are global state in SDL that the <i>next</i>
///         <c>SDL_GL_CreateContext</c> reads, so setting them anywhere else means a second window
///         silently inheriting the first one's request.
///     </para>
/// </remarks>
sealed unsafe class DesktopGlContext : IGlContext {
    readonly Sdl sdl;
    readonly SdlWindow* window;

    void* context;

    DesktopGlContext(Sdl sdl, SdlWindow* window, void* context, bool embedded, int major, int minor) {
        this.sdl = sdl;
        this.window = window;
        this.context = context;

        IsEmbedded = embedded;
        MajorVersion = major;
        MinorVersion = minor;
    }

    /// <inheritdoc />
    public bool IsEmbedded { get; }

    /// <inheritdoc />
    public int MajorVersion { get; }

    /// <inheritdoc />
    public int MinorVersion { get; }

    /// <inheritdoc />
    public int SwapInterval {
        get => sdl.GLGetSwapInterval();

        // ⚠ The failure is swallowed on purpose. A compositor that forces vsync, or a driver with no
        // late-swap-tearing support, refuses this — and a renderer that threw because it could not
        // turn vsync off would stop working on exactly the machines where the setting does not
        // matter. The getter reports what actually happened.
        set => sdl.GLSetSwapInterval(value);
    }

    /// <summary>Creates a context on a window, or says why it cannot.</summary>
    internal static bool TryCreate(
        Sdl sdl,
        SdlWindow* window,
        in GlContextRequest request,
        out IGlContext? created,
        out string? reason
    ) {
        created = null;

        // ⚠ No pre-check that the window carries SDL_WINDOW_OPENGL, and that is not an oversight —
        // it is what the first version did and it was wrong. SDL_GetWindowFlags does not reliably
        // echo the flag back: on macOS a window created with SDL_WINDOW_OPENGL reports
        // SDL_WINDOW_METAL instead, because that is what the NSView actually is underneath. Reading
        // it back rejected every GL window on the platform whose GL is most worth testing.
        //
        // SDL_GL_CreateContext already knows the answer and gives a real message, so it is asked
        // rather than second-guessed, and the hint about RequestGlContext is appended to whatever it
        // says — because "the window is not a GL window" is the likeliest cause and the least
        // guessable from SDL's wording.
        sdl.GLSetAttribute(GLattr.ContextMajorVersion, request.MajorVersion);
        sdl.GLSetAttribute(GLattr.ContextMinorVersion, request.MinorVersion);

        sdl.GLSetAttribute(
            GLattr.ContextProfileMask,
            (int)(request.UseEmbedded ? GLprofile.ES : GLprofile.Core)
        );

        if (request.Debug) {
            sdl.GLSetAttribute(GLattr.ContextFlags, (int)GLcontextFlag.DebugFlag);
        }

        var context = sdl.GLCreateContext(window);

        if (context is null) {
            reason = $"SDL_GL_CreateContext failed: {sdl.GetErrorS()} (asked for "
                + $"{(request.UseEmbedded ? "GLES" : "GL")} {request.MajorVersion}.{request.MinorVersion}). "
                + "If the window was not created with DesktopPlatformOptions.RequestGlContext, that is "
                + "the cause: SDL fixes a window's graphics API when the window is made.";

            return false;
        }

        if (sdl.GLMakeCurrent(window, context) != 0) {
            reason = $"SDL_GL_MakeCurrent failed: {sdl.GetErrorS()}";
            sdl.GLDeleteContext(context);

            return false;
        }

        // What the driver gave, which is allowed to exceed what was asked for: a request for 4.5
        // core is routinely satisfied with 4.6, and the profile decides which dialect the shader
        // translator emits, so reading it back is not a formality.
        int major = 0, minor = 0, profile = 0;

        sdl.GLGetAttribute(GLattr.ContextMajorVersion, ref major);
        sdl.GLGetAttribute(GLattr.ContextMinorVersion, ref minor);
        sdl.GLGetAttribute(GLattr.ContextProfileMask, ref profile);

        created = new DesktopGlContext(sdl, window, context, ((GLprofile)profile & GLprofile.ES) != 0, major, minor);
        reason = null;

        return true;
    }

    /// <inheritdoc />
    public nint GetProcAddress(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var utf8 = Marshal.StringToHGlobalAnsi(name);

        try {
            return (nint)sdl.GLGetProcAddress((byte*)utf8);
        } finally {
            Marshal.FreeHGlobal(utf8);
        }
    }

    /// <inheritdoc />
    public void MakeCurrent() {
        ObjectDisposedException.ThrowIf(context is null, this);

        if (sdl.GLMakeCurrent(window, context) != 0) {
            throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {sdl.GetErrorS()}");
        }
    }

    /// <inheritdoc />
    public void SwapBuffers() {
        ObjectDisposedException.ThrowIf(context is null, this);

        sdl.GLSwapWindow(window);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (context is null) {
            return;
        }

        sdl.GLDeleteContext(context);
        context = null;
    }
}
