// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>What kind of OpenGL context to ask the windowing layer for.</summary>
/// <remarks>
///     ⚠ <b>Version and profile are decided when the context is created; the pixel format is
///     decided when the <i>window</i> is.</b> Depth and stencil bits belong to the framebuffer the
///     window was made with, so they are the platform's option rather than this record's — asking
///     for them here would be a request that arrives after the only moment it could be honoured.
/// </remarks>
public readonly record struct GlContextRequest() {
    /// <summary>The major version to ask for.</summary>
    public int MajorVersion { get; init; } = 4;

    /// <summary>The minor version to ask for.</summary>
    public int MinorVersion { get; init; } = 5;

    /// <summary>Whether to ask for OpenGL ES rather than desktop OpenGL.</summary>
    /// <remarks>
    ///     GLES 3.0 is what a phone and a WebGL2 canvas have, and asking for it on a desktop is a
    ///     legitimate way to test the path a mobile build will take against a driver that is easier
    ///     to debug on.
    /// </remarks>
    public bool UseEmbedded { get; init; }

    /// <summary>Whether to ask for a debug context.</summary>
    /// <remarks>
    ///     A debug context is what makes <c>GL_KHR_debug</c> report anything, which is GL's nearest
    ///     equivalent to Vulkan's validation layers. Costly enough that it is off by default and
    ///     worth turning on for every build that is not a shipping one.
    /// </remarks>
    public bool Debug { get; init; }
}

/// <summary>An OpenGL context, and the two calls a renderer needs from the window it belongs to.</summary>
/// <remarks>
///     <para>
///         <b>Deliberately four members and no GL entry points.</b> Loading those is
///         <c>Vixen.Graphics.OpenGL</c>'s job and it does it from
///         <see cref="GetProcAddress" />; what a windowing layer uniquely knows is how to make a
///         context current and how to put the back buffer on the screen. Keeping the two apart is
///         what lets this interface be named by <c>Core/</c> without <c>Core/</c> learning what a
///         texture is.
///     </para>
///     <para>
///         ⚠ <b>The context belongs to the window that made it and dies with it.</b> Disposing it
///         early is allowed and releases the driver's context; not disposing it is also fine,
///         because a window cannot outlive its context on any platform that has both. That is the
///         one ownership rule, and it is this way round because it is the one the underlying APIs
///         already enforce.
///     </para>
///     <para>
///         ⚠ <b>Current on one thread at a time, and on the thread that called
///         <see cref="MakeCurrent" />.</b> This is GL's oldest and least forgiving rule: every entry
///         point loaded from a context is only valid while that context is current, so a renderer
///         that records on a job thread has to make it current there and nowhere else. It is also
///         the reason <c>Vixen.Graphics.OpenGL</c> reports no async queues.
///     </para>
/// </remarks>
public interface IGlContext : IDisposable {
    /// <summary>Looks up a GL entry point by name.</summary>
    /// <param name="name">The function name, such as <c>glDrawArrays</c>.</param>
    /// <returns>The address, or zero when the driver does not have it.</returns>
    /// <remarks>
    ///     ⚠ <b>Zero is an answer, not a failure.</b> Every GL loader probes for functions the
    ///     driver may not export — that is how extensions are detected — so returning zero has to be
    ///     ordinary rather than an exception.
    /// </remarks>
    nint GetProcAddress(string name);

    /// <summary>Makes this context current on the calling thread.</summary>
    void MakeCurrent();

    /// <summary>Presents the default framebuffer.</summary>
    void SwapBuffers();

    /// <summary>How many display refreshes a swap waits for: 0 for none, 1 for vsync.</summary>
    /// <remarks>
    ///     ⚠ <b>Setting it can silently do nothing.</b> A driver or a compositor is entitled to
    ///     refuse — late swap tearing, forced vsync in a control panel — so the value that comes
    ///     back is what happened rather than what was asked for.
    /// </remarks>
    int SwapInterval { get; set; }

    /// <summary>Whether the context that was actually created is OpenGL ES.</summary>
    bool IsEmbedded { get; }

    /// <summary>The major version the driver gave, which may exceed the one requested.</summary>
    int MajorVersion { get; }

    /// <summary>The minor version the driver gave.</summary>
    int MinorVersion { get; }
}

/// <summary>A window that can produce an OpenGL context.</summary>
/// <remarks>
///     <para>
///         <b>A separate interface rather than a member on <see cref="IWindow" />, because it is a
///         capability rather than a property of being a window.</b> A headless window has no
///         context and never will; a browser canvas has one and no Vulkan surface. Putting it on
///         <see cref="IWindow" /> would make every implementation answer a question most of them
///         cannot, and would break each of them to add it.
///     </para>
///     <para>
///         ⚠ <b>Implementing this is not the same as being able to honour it.</b> On SDL the window
///         has to have been created with the OpenGL flag, and that flag is mutually exclusive with
///         the Vulkan one — a window is made for one API and cannot change its mind. So a window
///         made for Vulkan implements this interface and refuses, with that as the reason.
///     </para>
/// </remarks>
public interface IGlContextSource {
    /// <summary>Creates a context on this window, or says why it cannot.</summary>
    /// <param name="request">What kind of context to ask for.</param>
    /// <param name="context">The context, when one was created.</param>
    /// <param name="reason">Why it was not, when it was not.</param>
    /// <returns>Whether a context was created.</returns>
    /// <remarks>
    ///     Reports rather than throws, because "this window is not an OpenGL window" and "this
    ///     driver has no 4.5" are ordinary outcomes of asking — the same reason
    ///     <c>VulkanDevice.TryCreate</c> reports.
    /// </remarks>
    bool TryCreateGlContext(in GlContextRequest request, out IGlContext? context, out string? reason);
}
