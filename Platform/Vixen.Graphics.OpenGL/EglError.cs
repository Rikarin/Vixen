// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>What an EGL error code means, in words.</summary>
/// <remarks>
///     <para>
///         A bring-up failure is reported to somebody holding a phone, often through a log they
///         cannot filter, and <c>0x3009</c> tells them nothing. Every code here has a fixed meaning
///         in the specification and most of them have exactly one plausible cause during context
///         creation, so the description says the cause rather than the name.
///     </para>
///     <para>
///         The numbers are not sequential in the way an <c>enum</c> would let a compiler check, which
///         is why the default arm reports the raw value rather than pretending to recognise it.
///     </para>
/// </remarks>
static class EglError {
    /// <summary>The code, named and explained.</summary>
    /// <param name="code">What <c>eglGetError</c> returned.</param>
    /// <returns>A description fit for the end of a sentence.</returns>
    public static string Describe(uint code) => code switch {
        EglConstants.Success => "EGL_SUCCESS (the call failed and EGL reported no error, which "
            + "usually means the failure was a null handle rather than an error condition)",
        EglConstants.NotInitialised => "EGL_NOT_INITIALIZED (the display was not initialised, or the "
            + "driver could not be)",
        EglConstants.BadAccess => "EGL_BAD_ACCESS (something here is already current on another "
            + "thread, or a shared context is in use)",
        EglConstants.BadAlloc => "EGL_BAD_ALLOC (the driver could not allocate — on a window surface "
            + "this is usually a surface that already has one)",
        EglConstants.BadAttribute => "EGL_BAD_ATTRIBUTE (an attribute in the list is unrecognised or "
            + "out of range for this driver)",
        EglConstants.BadConfig => "EGL_BAD_CONFIG (the config does not belong to this display)",
        EglConstants.BadContext => "EGL_BAD_CONTEXT (the context is not one of this display's)",
        EglConstants.BadCurrentSurface => "EGL_BAD_CURRENT_SURFACE (the surface current on this "
            + "thread is no longer valid)",
        EglConstants.BadDisplay => "EGL_BAD_DISPLAY (the display handle is not a display)",
        EglConstants.BadMatch => "EGL_BAD_MATCH (the config, the context and the surface do not agree "
            + "— on eglCreateContext this is how a driver says it has no such client version)",
        EglConstants.BadNativePixmap => "EGL_BAD_NATIVE_PIXMAP (the native pixmap is not valid)",
        EglConstants.BadNativeWindow => "EGL_BAD_NATIVE_WINDOW (the native window is not valid — on "
            + "Android an ANativeWindow outlives its Surface by nothing at all)",
        EglConstants.BadParameter => "EGL_BAD_PARAMETER (an argument is out of range)",
        EglConstants.BadSurface => "EGL_BAD_SURFACE (the surface is not one of this display's, or has "
            + "been destroyed)",
        EglConstants.ContextLost => "EGL_CONTEXT_LOST (a power-management event took the GPU; every "
            + "object in the context is gone and the device has to be recreated)",
        _ => $"an unrecognised EGL error, 0x{code:X4}"
    };
}
