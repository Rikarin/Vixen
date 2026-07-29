// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.OpenGL;

/// <summary>The attribute lists <see cref="EglContext" /> asks EGL for things with.</summary>
/// <remarks>
///     <para>
///         EGL takes every request as a <c>EGL_NONE</c>-terminated array of key-value pairs, and the
///         difference between a context that comes up and one that does not is entirely in what goes
///         into those arrays. That makes them the part worth having on their own, in a form a test
///         can read without a driver.
///     </para>
///     <para>
///         Every list here is built rather than held as a constant, because two of the three depend
///         on what was asked for and the third — a window surface's — is empty and would be a
///         shared mutable array if it were a field.
///     </para>
/// </remarks>
static class EglAttributes {
    /// <summary>What a config has to be able to do.</summary>
    /// <param name="options">What the caller asked for.</param>
    /// <param name="window">Whether this is for a window surface rather than a pbuffer.</param>
    /// <returns>The attribute list.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Eight bits of alpha, always.</b> The RHI's swapchain format on this backend is
    ///         <c>Rgba8UNorm</c> (see <see cref="GlSwapChain" />), and a config with no alpha channel
    ///         gives a default framebuffer that cannot hold what the engine blits into it. On Android
    ///         this also decides whether the window is translucent, which is the surface view's
    ///         business rather than the config's — a pixel format is not a compositing mode.
    ///     </para>
    ///     <para>
    ///         <b><c>EGL_OPENGL_ES3_BIT</c> rather than <c>EGL_OPENGL_ES2_BIT</c>.</b> The ES2 bit
    ///         matches configs that can run an ES 2.0 context and says nothing about ES 3, which is
    ///         the floor <see cref="GlProfile.Es30" /> describes. Asking for the ES3 bit is what makes
    ///         "this driver is too old" a failure at <c>eglChooseConfig</c> — before a context, a
    ///         surface and a first frame — rather than a shader that will not compile.
    ///     </para>
    ///     <para>
    ///         <b>Depth and stencil are a floor, not a request.</b> EGL matches a config with
    ///         <em>at least</em> the sizes given, so zero means "do not care" rather than "must have
    ///         none". This backend allocates its own depth attachments, so the default framebuffer's
    ///         depth buffer is unused — but a driver asked for none may hand back a config that
    ///         cannot present one, and the cost of asking is nothing.
    ///     </para>
    ///     <para>
    ///         <b>Samples are omitted when there is one of them.</b> <c>EGL_SAMPLES 1</c> is not the
    ///         same request as leaving it out: it asks for a multisample config with one sample, which
    ///         some drivers have and some do not, and none of them need. Multisampling on this backend
    ///         happens in the engine's own attachments anyway; the default framebuffer is a blit
    ///         target.
    ///     </para>
    /// </remarks>
    public static int[] Config(in EglContextOptions options, bool window) {
        List<int> attributes = [
            EglConstants.SurfaceType, window ? EglConstants.WindowBit : EglConstants.PbufferBit,
            EglConstants.RenderableType, EglConstants.OpenGlEs3Bit,
            EglConstants.RedSize, 8,
            EglConstants.GreenSize, 8,
            EglConstants.BlueSize, 8,
            EglConstants.AlphaSize, 8,
            EglConstants.DepthSize, Math.Max(0, options.DepthBits),
            EglConstants.StencilSize, Math.Max(0, options.StencilBits)
        ];

        if (options.Samples > 1) {
            attributes.Add(EglConstants.SampleBuffers);
            attributes.Add(1);
            attributes.Add(EglConstants.Samples);
            attributes.Add(options.Samples);
        }

        attributes.Add(EglConstants.None);
        return [.. attributes];
    }

    /// <summary>What client version a context is being asked for.</summary>
    /// <param name="profile">The profile — <see cref="GlProfile.Es30" /> or <see cref="GlProfile.Es32" />.</param>
    /// <param name="debug">Whether to ask for a debug context.</param>
    /// <param name="eglMinor">The EGL minor version the driver reported; the major is always 1.</param>
    /// <returns>The attribute list.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The minor version is omitted when it is zero</b>, and that is the whole
    ///         compatibility story of this list. <c>EGL_CONTEXT_MAJOR_VERSION</c> is
    ///         <c>EGL_CONTEXT_CLIENT_VERSION</c> under a newer name and the same value, so every
    ///         driver back to EGL 1.3 understands it. <c>EGL_CONTEXT_MINOR_VERSION</c> is EGL 1.5, or
    ///         <c>EGL_KHR_create_context</c> before it — and a driver with neither answers
    ///         <c>EGL_BAD_ATTRIBUTE</c> to an attribute it does not recognise, whatever its value. So
    ///         a plain ES 3.0 request carries the major version alone and works everywhere, and only
    ///         the ES 3.2 request, which genuinely needs the minor, risks the older refusal.
    ///     </para>
    ///     <para>
    ///         <b>The debug attribute is EGL 1.5 only</b> and is dropped below it rather than
    ///         translated. <c>EGL_CONTEXT_FLAGS_KHR</c> would be the 1.4 spelling, and it is not worth
    ///         a second path: a debug context buys <c>KHR_debug</c> output, which
    ///         <see cref="GlProfiles.HasDebugOutput" /> already reports absent on
    ///         <see cref="GlProfile.Es30" /> — the profile most likely to be on such a driver.
    ///     </para>
    /// </remarks>
    public static int[] Context(GlProfile profile, bool debug, int eglMinor) {
        List<int> attributes = [EglConstants.ContextMajorVersion, 3];

        if (Minor(profile) is var minor and > 0) {
            attributes.Add(EglConstants.ContextMinorVersion);
            attributes.Add(minor);
        }

        if (debug && eglMinor >= 5) {
            attributes.Add(EglConstants.ContextDebug);
            attributes.Add(EglConstants.True);
        }

        attributes.Add(EglConstants.None);
        return [.. attributes];
    }

    /// <summary>The size of an offscreen surface.</summary>
    /// <param name="size">How big, in pixels.</param>
    /// <returns>The attribute list.</returns>
    /// <remarks>
    ///     Clamped to at least one pixel in each direction. A zero-sized pbuffer is
    ///     <c>EGL_BAD_PARAMETER</c>, and a device created for offscreen rendering that was given no
    ///     size at all means a caller who never intends to present — which is a one-pixel surface's
    ///     job, not a failure.
    /// </remarks>
    public static int[] PbufferSurface(Int2 size) => [
        EglConstants.Width, Math.Max(1, size.X),
        EglConstants.Height, Math.Max(1, size.Y),
        EglConstants.None
    ];

    /// <summary>A window surface's, which asks for nothing beyond the config.</summary>
    /// <returns>The attribute list.</returns>
    /// <remarks>
    ///     Deliberately empty. <c>EGL_GL_COLORSPACE</c> is the attribute that would go here, and this
    ///     backend does not set it: sRGB encoding is a property of the attachment's format in the RHI,
    ///     the engine renders into its own textures and blits, and asking the default framebuffer to
    ///     encode as well would apply the transform twice.
    /// </remarks>
    public static int[] WindowSurface() => [EglConstants.None];

    /// <summary>The GLES minor version a profile means.</summary>
    static int Minor(GlProfile profile) => profile is GlProfile.Es32 ? 2 : 0;
}
