// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>The EGL enumerant values <see cref="EglContext" /> passes to <see cref="IEglApi" />.</summary>
/// <remarks>
///     <para>
///         Spelled out for the reason <see cref="GlConstants" /> is, and with one more: there is no
///         <c>Silk.NET.EGL</c> for Silk.NET 2 to take them from. That package stops at 1.9.0, and
///         Silk.NET 2's own GLES windowing reaches EGL through GLFW or SDL rather than binding it —
///         so the registry is the source, and these are the values in it.
///     </para>
///     <para>
///         Only what the context passes is here. EGL is a large specification and almost none of it
///         is reached by "make a GLES context current on this window".
///     </para>
/// </remarks>
public static class EglConstants {
    // ── Booleans and null handles ───────────────────────────────────────────────────────────

    /// <summary><c>EGL_FALSE</c>.</summary>
    public const int False = 0;

    /// <summary><c>EGL_TRUE</c>.</summary>
    public const int True = 1;

    /// <summary><c>EGL_DEFAULT_DISPLAY</c> — "whatever this platform's one display is".</summary>
    /// <remarks>
    ///     Zero, and the same zero as <see cref="NoDisplay" />, which is a genuine wart in the
    ///     specification rather than a mistake here: it is an argument to <c>eglGetDisplay</c> and
    ///     never a result from it.
    /// </remarks>
    public const nint DefaultDisplay = 0;

    /// <summary><c>EGL_NO_DISPLAY</c>.</summary>
    public const nint NoDisplay = 0;

    /// <summary><c>EGL_NO_CONTEXT</c>, which is also how a context is unbound.</summary>
    public const nint NoContext = 0;

    /// <summary><c>EGL_NO_SURFACE</c>.</summary>
    public const nint NoSurface = 0;

    // ── Errors ──────────────────────────────────────────────────────────────────────────────

    /// <summary><c>EGL_SUCCESS</c>.</summary>
    public const uint Success = 0x3000;

    /// <summary><c>EGL_NOT_INITIALIZED</c>.</summary>
    public const uint NotInitialised = 0x3001;

    /// <summary><c>EGL_BAD_ACCESS</c>.</summary>
    public const uint BadAccess = 0x3002;

    /// <summary><c>EGL_BAD_ALLOC</c>.</summary>
    public const uint BadAlloc = 0x3003;

    /// <summary><c>EGL_BAD_ATTRIBUTE</c>.</summary>
    public const uint BadAttribute = 0x3004;

    /// <summary><c>EGL_BAD_CONFIG</c>.</summary>
    public const uint BadConfig = 0x3005;

    /// <summary><c>EGL_BAD_CONTEXT</c>.</summary>
    public const uint BadContext = 0x3006;

    /// <summary><c>EGL_BAD_CURRENT_SURFACE</c>.</summary>
    public const uint BadCurrentSurface = 0x3007;

    /// <summary><c>EGL_BAD_DISPLAY</c>.</summary>
    public const uint BadDisplay = 0x3008;

    /// <summary><c>EGL_BAD_MATCH</c>.</summary>
    /// <remarks>
    ///     The one to expect from <c>eglCreateContext</c> when a driver cannot give the client
    ///     version asked for. See <see cref="EglContext" />'s version ladder.
    /// </remarks>
    public const uint BadMatch = 0x3009;

    /// <summary><c>EGL_BAD_NATIVE_PIXMAP</c>.</summary>
    public const uint BadNativePixmap = 0x300A;

    /// <summary><c>EGL_BAD_NATIVE_WINDOW</c>.</summary>
    public const uint BadNativeWindow = 0x300B;

    /// <summary><c>EGL_BAD_PARAMETER</c>.</summary>
    public const uint BadParameter = 0x300C;

    /// <summary><c>EGL_BAD_SURFACE</c>.</summary>
    public const uint BadSurface = 0x300D;

    /// <summary><c>EGL_CONTEXT_LOST</c>.</summary>
    /// <remarks>
    ///     A power event took the GPU, which on Android is an ordinary Tuesday rather than a fault.
    ///     The RHI's device-loss path is what handles it; this is only the code that names it.
    /// </remarks>
    public const uint ContextLost = 0x300E;

    // ── Config attributes ───────────────────────────────────────────────────────────────────

    /// <summary><c>EGL_ALPHA_SIZE</c>.</summary>
    public const int AlphaSize = 0x3021;

    /// <summary><c>EGL_BLUE_SIZE</c>.</summary>
    public const int BlueSize = 0x3022;

    /// <summary><c>EGL_GREEN_SIZE</c>.</summary>
    public const int GreenSize = 0x3023;

    /// <summary><c>EGL_RED_SIZE</c>.</summary>
    public const int RedSize = 0x3024;

    /// <summary><c>EGL_DEPTH_SIZE</c>.</summary>
    public const int DepthSize = 0x3025;

    /// <summary><c>EGL_STENCIL_SIZE</c>.</summary>
    public const int StencilSize = 0x3026;

    /// <summary><c>EGL_CONFIG_ID</c>.</summary>
    public const int ConfigId = 0x3028;

    /// <summary><c>EGL_SAMPLES</c>.</summary>
    public const int Samples = 0x3031;

    /// <summary><c>EGL_SAMPLE_BUFFERS</c>.</summary>
    public const int SampleBuffers = 0x3032;

    /// <summary><c>EGL_SURFACE_TYPE</c>.</summary>
    public const int SurfaceType = 0x3033;

    /// <summary><c>EGL_NONE</c>, which terminates every attribute list.</summary>
    public const int None = 0x3038;

    /// <summary><c>EGL_RENDERABLE_TYPE</c>.</summary>
    public const int RenderableType = 0x3040;

    /// <summary><c>EGL_PBUFFER_BIT</c>.</summary>
    public const int PbufferBit = 0x0001;

    /// <summary><c>EGL_WINDOW_BIT</c>.</summary>
    public const int WindowBit = 0x0004;

    /// <summary><c>EGL_OPENGL_ES2_BIT</c>.</summary>
    public const int OpenGlEs2Bit = 0x0004;

    /// <summary><c>EGL_OPENGL_ES3_BIT</c>.</summary>
    /// <remarks>
    ///     Core in EGL 1.5 and <c>EGL_KHR_create_context</c> below it, with the same value in both —
    ///     which is why one constant covers a driver of either vintage.
    /// </remarks>
    public const int OpenGlEs3Bit = 0x0040;

    // ── Surface and context attributes ──────────────────────────────────────────────────────

    /// <summary><c>EGL_HEIGHT</c>.</summary>
    public const int Height = 0x3056;

    /// <summary><c>EGL_WIDTH</c>.</summary>
    public const int Width = 0x3057;

    /// <summary><c>EGL_CONTEXT_MAJOR_VERSION</c>, spelled <c>EGL_CONTEXT_CLIENT_VERSION</c> before
    /// EGL 1.5.</summary>
    /// <remarks>One value, two names, and the older one is what an EGL 1.4 driver's headers say.</remarks>
    public const int ContextMajorVersion = 0x3098;

    /// <summary><c>EGL_CONTEXT_MINOR_VERSION</c>.</summary>
    public const int ContextMinorVersion = 0x30FB;

    /// <summary><c>EGL_CONTEXT_OPENGL_DEBUG</c>.</summary>
    public const int ContextDebug = 0x31B0;

    // ── Strings and the client API ──────────────────────────────────────────────────────────

    /// <summary><c>EGL_VENDOR</c>.</summary>
    public const int Vendor = 0x3053;

    /// <summary><c>EGL_VERSION</c>.</summary>
    public const int Version = 0x3054;

    /// <summary><c>EGL_EXTENSIONS</c>.</summary>
    public const int Extensions = 0x3055;

    /// <summary><c>EGL_OPENGL_ES_API</c>.</summary>
    /// <remarks>
    ///     Bound before anything is created. EGL's current API is per-thread state and its default is
    ///     <c>EGL_OPENGL_ES_API</c> — but a process that also uses desktop GL through EGL has changed
    ///     it, and the failure that produces is <c>eglCreateContext</c> returning a desktop context
    ///     for GLES attributes.
    /// </remarks>
    public const uint OpenGlEsApi = 0x30A0;
}
