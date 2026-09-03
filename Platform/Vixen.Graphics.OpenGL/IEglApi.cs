// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>Every EGL entry point <see cref="EglContext" /> calls.</summary>
/// <remarks>
///     <para>
///         <b>The same seam as <see cref="IGlApi" />, one layer out.</b> What can be wrong about
///         bringing up a context is not the calls — those are transcription — but the
///         <em>sequence</em>: which attribute list is built for a window and which for a pbuffer,
///         what happens when a driver refuses GLES 3.2, whether a half-built context is torn down in
///         the reverse of the order it was built. All of that is decidable from the call stream, and
///         none of it is decidable on a machine with no EGL — which is every machine this repository
///         is developed on except an Android device.
///     </para>
///     <para>
///         So the calls go through an interface, the loading lives behind
///         <see cref="NativeEglApi" />, and the tests drive a recorder.
///     </para>
///     <para>
///         <b>Booleans rather than <c>EGLBoolean</c>, and <c>nint</c> for every handle.</b> EGL's
///         handle types are opaque pointers and its boolean is an <c>int</c>; carrying either as its
///         own type here would put the binding in the interface. Failure is reported the way EGL
///         reports it — a false return or a null handle, with <see cref="GetError" /> holding the
///         reason — rather than by throwing, because the caller's response to
///         <c>EGL_BAD_MATCH</c> from <c>eglCreateContext</c> is to ask for less rather than to give
///         up.
///     </para>
/// </remarks>
public interface IEglApi {
    // ── The display ─────────────────────────────────────────────────────────────────────────

    /// <summary><c>eglGetDisplay</c>.</summary>
    /// <param name="nativeDisplay">The platform's display, or <see cref="EglConstants.DefaultDisplay" />.</param>
    /// <returns>The display, or <see cref="EglConstants.NoDisplay" />.</returns>
    nint GetDisplay(nint nativeDisplay);

    /// <summary><c>eglInitialize</c>.</summary>
    /// <param name="display">The display.</param>
    /// <param name="major">The EGL major version the driver implements.</param>
    /// <param name="minor">Its minor version.</param>
    /// <returns>Whether it initialised.</returns>
    bool Initialise(nint display, out int major, out int minor);

    /// <summary><c>eglTerminate</c>.</summary>
    bool Terminate(nint display);

    /// <summary><c>eglBindAPI</c>.</summary>
    /// <param name="api">Which client API this thread is about to use.</param>
    /// <returns>Whether the driver has it.</returns>
    bool BindApi(uint api);

    /// <summary><c>eglQueryString</c>.</summary>
    /// <param name="display">The display, or <see cref="EglConstants.NoDisplay" /> for the client's own.</param>
    /// <param name="name">Which string.</param>
    /// <returns>The string, or <see langword="null" />.</returns>
    string? QueryString(nint display, int name);

    // ── Configs, contexts and surfaces ──────────────────────────────────────────────────────

    /// <summary><c>eglChooseConfig</c>.</summary>
    /// <param name="display">The display.</param>
    /// <param name="attributes">The attribute list, terminated by <see cref="EglConstants.None" />.</param>
    /// <param name="configs">Where to put the matches.</param>
    /// <param name="count">How many were written.</param>
    /// <returns>Whether the query itself succeeded, which is not the same as it having matched anything.</returns>
    bool ChooseConfig(nint display, ReadOnlySpan<int> attributes, Span<nint> configs, out int count);

    /// <summary><c>eglGetConfigAttrib</c>.</summary>
    /// <param name="display">The display.</param>
    /// <param name="config">The config, as <see cref="ChooseConfig" /> handed it back.</param>
    /// <param name="attribute">Which attribute.</param>
    /// <param name="value">Its value.</param>
    /// <returns>Whether it could be read.</returns>
    /// <remarks>
    ///     ⚠ <b>Added because a config that matched is not yet a config a window will accept.</b>
    ///     <c>eglChooseConfig</c> answers with something whose colour depth satisfies the request,
    ///     and on Android the <c>ANativeWindow</c> behind the surface has a buffer format of its own
    ///     that has to be set to this config's <see cref="EglConstants.NativeVisualId" /> before
    ///     <see cref="CreateWindowSurface" /> is called — otherwise the driver answers
    ///     <c>EGL_BAD_MATCH</c>. Nothing in the call stream said so, because the recording fake had
    ///     no reason to refuse and neither did the interface.
    /// </remarks>
    bool GetConfigAttrib(nint display, nint config, int attribute, out int value);

    /// <summary><c>eglCreateContext</c>.</summary>
    /// <param name="display">The display.</param>
    /// <param name="config">The config.</param>
    /// <param name="share">A context to share objects with, or <see cref="EglConstants.NoContext" />.</param>
    /// <param name="attributes">The attribute list.</param>
    /// <returns>The context, or <see cref="EglConstants.NoContext" />.</returns>
    nint CreateContext(nint display, nint config, nint share, ReadOnlySpan<int> attributes);

    /// <summary><c>eglCreateWindowSurface</c>.</summary>
    /// <param name="display">The display.</param>
    /// <param name="config">The config.</param>
    /// <param name="window">The native window — an <c>ANativeWindow*</c> on Android.</param>
    /// <param name="attributes">The attribute list.</param>
    /// <returns>The surface, or <see cref="EglConstants.NoSurface" />.</returns>
    nint CreateWindowSurface(nint display, nint config, nint window, ReadOnlySpan<int> attributes);

    /// <summary><c>eglCreatePbufferSurface</c>, which is how a device renders with no window.</summary>
    /// <param name="display">The display.</param>
    /// <param name="config">The config.</param>
    /// <param name="attributes">The attribute list, carrying the size.</param>
    /// <returns>The surface, or <see cref="EglConstants.NoSurface" />.</returns>
    nint CreatePbufferSurface(nint display, nint config, ReadOnlySpan<int> attributes);

    /// <summary><c>eglDestroySurface</c>.</summary>
    bool DestroySurface(nint display, nint surface);

    /// <summary><c>eglDestroyContext</c>.</summary>
    bool DestroyContext(nint display, nint context);

    /// <summary><c>eglQuerySurface</c>.</summary>
    /// <param name="display">The display.</param>
    /// <param name="surface">The surface.</param>
    /// <param name="attribute">Which attribute.</param>
    /// <param name="value">Its value.</param>
    /// <returns>Whether it could be read.</returns>
    bool QuerySurface(nint display, nint surface, int attribute, out int value);

    // ── Currency and presentation ───────────────────────────────────────────────────────────

    /// <summary><c>eglMakeCurrent</c>.</summary>
    /// <param name="display">The display.</param>
    /// <param name="draw">The draw surface.</param>
    /// <param name="read">The read surface, which here is always the draw one.</param>
    /// <param name="context">The context, or <see cref="EglConstants.NoContext" /> to unbind.</param>
    /// <returns>Whether it became current.</returns>
    bool MakeCurrent(nint display, nint draw, nint read, nint context);

    /// <summary><c>eglGetCurrentContext</c>.</summary>
    nint GetCurrentContext();

    /// <summary><c>eglSwapBuffers</c>.</summary>
    bool SwapBuffers(nint display, nint surface);

    /// <summary><c>eglSwapInterval</c>.</summary>
    bool SwapInterval(nint display, int interval);

    /// <summary><c>eglReleaseThread</c>.</summary>
    /// <remarks>
    ///     EGL keeps per-thread state — the bound API and the current context — and a thread that
    ///     ends without releasing it leaks that state for the life of the process.
    /// </remarks>
    bool ReleaseThread();

    // ── Entry points ────────────────────────────────────────────────────────────────────────

    /// <summary><c>eglGetProcAddress</c>.</summary>
    /// <param name="name">The symbol.</param>
    /// <returns>Its address, or zero.</returns>
    nint GetProcAddress(string name);

    /// <summary>The same symbol in the client library, when there is one to ask.</summary>
    /// <param name="name">The symbol.</param>
    /// <returns>Its address, or zero.</returns>
    /// <remarks>
    ///     <b>Asked first, and that is not an optimisation.</b> Before EGL 1.5, only <em>extension</em>
    ///     entry points had to come back from <c>eglGetProcAddress</c> — a core function like
    ///     <c>glDrawArrays</c> was allowed to return null, and on several drivers it does. The
    ///     addresses that do come back are also permitted to be dispatch thunks rather than the
    ///     functions themselves. So <c>libGLESv2</c>'s own symbol table answers first and
    ///     <see cref="GetProcAddress" /> covers what is not in it, which is the extensions.
    /// </remarks>
    nint GetClientProcAddress(string name);

    /// <summary><c>eglGetError</c>, which clears as it reads.</summary>
    /// <returns>The last error on this thread, or <see cref="EglConstants.Success" />.</returns>
    uint GetError();
}
