// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Core.Contexts;
using Vixen.Core.Mathematics;

namespace Vixen.Graphics.OpenGL;

/// <summary>What to build an <see cref="EglContext" /> out of.</summary>
/// <param name="NativeWindow">
///     The native window to present to — an <c>ANativeWindow*</c> on Android — or zero for a device
///     that renders offscreen.
/// </param>
/// <param name="OffscreenSize">How big the offscreen surface is, when there is no window.</param>
/// <param name="NativeDisplay">
///     The platform's display, or <see cref="EglConstants.DefaultDisplay" /> for the one it has.
/// </param>
/// <param name="Profile">
///     Which dialect to ask for, or <see langword="null" /> for the highest the driver will give —
///     see <see cref="EglContext" />'s version ladder.
/// </param>
/// <param name="DepthBits">The default framebuffer's depth, as a floor.</param>
/// <param name="StencilBits">Its stencil, as a floor.</param>
/// <param name="Samples">Its sample count; one asks for no multisample config at all.</param>
/// <param name="Debug">Whether to ask for a debug context, which EGL 1.5 and up can give.</param>
/// <param name="ShareContext">A context to share objects with, or zero.</param>
/// <param name="SwapInterval">
///     How many vertical intervals a present waits for — one for vsync, zero for none.
/// </param>
public readonly record struct EglContextOptions(
    nint NativeWindow,
    Int2 OffscreenSize = default,
    nint NativeDisplay = EglConstants.DefaultDisplay,
    GlProfile? Profile = null,
    int DepthBits = 24,
    int StencilBits = 8,
    int Samples = 1,
    bool Debug = false,
    nint ShareContext = 0,
    int SwapInterval = 1
);

/// <summary>A GLES context on an EGL display, and the surface it presents to.</summary>
/// <remarks>
///     <para>
///         <b>The thing the GLES profiles were waiting for.</b> <see cref="GlProfile" /> has modelled
///         GLES 3.0 and 3.2 since the backend was written and the translation layer has differed by
///         profile throughout — what was missing was a context to run one on, because
///         <c>Silk.NET.OpenGLES</c> binds <c>libGLESv2</c> and nothing in Silk.NET 2 binds EGL. This
///         is that context: about twenty entry points behind <see cref="IEglApi" />, and the
///         sequence that turns a native window into something <see cref="SilkGlesApi" /> can load
///         from.
///     </para>
///     <para>
///         <b>It is a Silk <c>IGLContext</c>, which is what makes the loading one line.</b>
///         <c>GL.GetApi(context)</c> asks for every entry point by name; supplying the interface
///         means <see cref="SilkGlesApi" /> needs no knowledge of EGL, and a windowing layer that
///         already has a context of its own — SDL, an <c>Activity</c>'s <c>GLSurfaceView</c> — can
///         be passed to the same constructor instead of this.
///     </para>
///     <para>
///         <b>The version ladder.</b> A driver is asked for GLES 3.2 and then, if it refuses, for
///         3.0. That is the whole of the profile detection, and it is deliberately a request rather
///         than a query: <c>GL_VERSION</c> can only be read through a context that already exists, so
///         choosing by reading it would mean creating a context to decide which context to create.
///         Every refusal in between is an EGL error, drained before the next attempt so a later
///         failure is never reported with an earlier reason. A caller who names
///         <see cref="EglContextOptions.Profile" /> gets one attempt and the driver's own error if it
///         fails, because "3.2 or nothing" is a legitimate thing to want and silently getting 3.0 —
///         with no compute, no storage buffers and no indirect draws — is not an answer to it.
///     </para>
///     <para>
///         <b>Teardown is the construction sequence backwards, and it runs on a failed
///         construction too.</b> EGL leaks quietly: a display that was initialised and a context that
///         was created outlive a constructor that threw between them, and on Android that is a
///         process that cannot bring up graphics again after one bad start. So every step past the
///         first is inside a <c>try</c> whose <c>catch</c> releases what exists so far.
///     </para>
///     <para>
///         <b>One display, terminated on dispose.</b> <c>eglTerminate</c> marks every resource on a
///         display for deletion, so a second context sharing this display would lose its own when the
///         first is disposed. Nothing in the engine creates two — a device owns a context and there
///         is one device — and the case is called out here rather than guarded against, because a
///         reference count that has never had two holders is a reference count nobody has tested.
///     </para>
///     <para>
///         <b>Threading.</b> A context is current on one thread, which is the thread that constructed
///         it and the thread <see cref="GlDevice" /> replays on. <see cref="Dispose" /> releases
///         EGL's per-thread state as well as the objects.
///     </para>
/// </remarks>
public sealed class EglContext : IGLContext {
    readonly IEglApi egl;
    readonly nint display;
    readonly nint config;

    nint context;
    nint surface;
    bool disposed;

    /// <summary>Brings up a context, or throws saying which step failed and what EGL said.</summary>
    /// <param name="egl">The EGL entry points — <see cref="NativeEglApi" />, or a fake.</param>
    /// <param name="options">What to build it out of.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The requested profile is not one EGL can give — see
    ///     <see cref="EglContextOptions.Profile" />.
    /// </exception>
    /// <exception cref="InvalidOperationException">A step failed; the message says which.</exception>
    public EglContext(IEglApi egl, in EglContextOptions options) {
        this.egl = egl ?? throw new ArgumentNullException(nameof(egl));

        var ladder = Ladder(options.Profile);
        var windowed = options.NativeWindow != 0;

        display = egl.GetDisplay(options.NativeDisplay);

        if (display == EglConstants.NoDisplay) {
            throw Failed("eglGetDisplay", "there is no EGL display on this platform");
        }

        if (!egl.Initialise(display, out var major, out var minor)) {
            throw Failed("eglInitialize", "the display could not be initialised");
        }

        EglVersion = (major, minor);

        try {
            // Before anything is created, because EGL's current client API is per-thread state and
            // its default only holds for a thread that has not already used EGL for something else.
            if (!egl.BindApi(EglConstants.OpenGlEsApi)) {
                throw Failed("eglBindAPI", "this driver does not offer OpenGL ES at all");
            }

            config = ChooseConfig(options, windowed);
            (context, Profile) = CreateContext(ladder, options, minor);
            surface = CreateSurface(options, windowed);

            if (!egl.MakeCurrent(display, surface, surface, context)) {
                throw Failed("eglMakeCurrent", "the context could not be made current");
            }

            // Best effort, and only meaningful on a window surface: eglSwapInterval on a pbuffer is
            // defined to fail, and a driver that ignores the request presents at whatever rate it
            // presents at. Neither is a reason to refuse a working context.
            if (windowed) {
                egl.SwapInterval(display, options.SwapInterval);
            }
        } catch {
            Release();
            throw;
        }
    }

    /// <summary>Which dialect the driver gave, which is the one <see cref="GlDevice" /> should be told.</summary>
    public GlProfile Profile { get; }

    /// <summary>The EGL version the driver implements.</summary>
    public (int Major, int Minor) EglVersion { get; }

    /// <summary>The EGL display.</summary>
    public nint Display => display;

    /// <summary>The surface being presented to.</summary>
    public nint Surface => surface;

    /// <summary>The context, which is what Silk.NET calls the handle.</summary>
    public nint Handle => context;

    /// <summary>Always <see langword="null" />.</summary>
    /// <remarks>
    ///     Silk's <c>IGLContextSource</c> is a window that owns a context. This one owns itself:
    ///     nothing above it is a window, which is the point of a backend that can also come up
    ///     headless.
    /// </remarks>
    public IGLContextSource? Source => null;

    /// <inheritdoc />
    public bool IsCurrent => context != EglConstants.NoContext && egl.GetCurrentContext() == context;

    /// <summary>How big the surface is now, in pixels.</summary>
    /// <remarks>
    ///     Queried rather than remembered. A window surface follows its window, so a rotation or a
    ///     resize changes this without anything here being told — which is precisely why the
    ///     swapchain asks at the moment it needs to know rather than holding a copy.
    /// </remarks>
    public Int2 Size {
        get {
            ObjectDisposedException.ThrowIf(disposed, this);

            var width = egl.QuerySurface(display, surface, EglConstants.Width, out var w) ? w : 0;
            var height = egl.QuerySurface(display, surface, EglConstants.Height, out var h) ? h : 0;

            return new(width, height);
        }
    }

    /// <inheritdoc />
    public void MakeCurrent() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!egl.MakeCurrent(display, surface, surface, context)) {
            throw Failed("eglMakeCurrent", "the context could not be made current");
        }
    }

    /// <inheritdoc />
    /// <remarks>Unbinds whatever is current on this thread, which is what Silk means by "clear".</remarks>
    public void Clear() {
        ObjectDisposedException.ThrowIf(disposed, this);
        egl.MakeCurrent(display, EglConstants.NoSurface, EglConstants.NoSurface, EglConstants.NoContext);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     What <c>GlDeviceOptions.Present</c> should be pointed at. <see cref="GlSwapChain" /> has
    ///     already blitted the engine's colour target into the default framebuffer by the time this
    ///     is reached.
    /// </remarks>
    public void SwapBuffers() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!egl.SwapBuffers(display, surface)) {
            throw Failed("eglSwapBuffers", "the frame could not be presented");
        }
    }

    /// <inheritdoc />
    public void SwapInterval(int interval) {
        ObjectDisposedException.ThrowIf(disposed, this);
        egl.SwapInterval(display, interval);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The client library first and <c>eglGetProcAddress</c> second — see
    ///     <see cref="IEglApi.GetClientProcAddress" />, which sets out why that order is a
    ///     correctness matter on any driver older than EGL 1.5.
    /// </remarks>
    public nint GetProcAddress(string proc, int? slot = null) {
        ArgumentException.ThrowIfNullOrEmpty(proc);
        ObjectDisposedException.ThrowIf(disposed, this);

        var address = egl.GetClientProcAddress(proc);
        return address != 0 ? address : egl.GetProcAddress(proc);
    }

    /// <inheritdoc />
    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null) {
        ArgumentException.ThrowIfNullOrEmpty(proc);

        addr = disposed ? 0 : GetProcAddress(proc, slot);
        return addr != 0;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Surface, then context, then the thread's state, then the display — the construction
    ///     sequence backwards. The context is unbound first because destroying a current one only
    ///     flags it for deletion, which on a driver that then hands out the same handle again is a
    ///     leak that looks like a driver bug.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Release();
    }

    /// <summary>What profiles to try, in order.</summary>
    /// <remarks>
    ///     Highest first when nothing was named, because <see cref="GlProfile.Es32" /> is the profile
    ///     with compute, storage buffers and indirect draws — everything the renderer's GPU-driven
    ///     paths gate on — and a device that settled for 3.0 on a phone that has 3.2 would take the
    ///     fallback path forever without saying so.
    /// </remarks>
    static GlProfile[] Ladder(GlProfile? requested) => requested switch {
        null => [GlProfile.Es32, GlProfile.Es30],
        GlProfile.Es32 => [GlProfile.Es32],
        GlProfile.Es30 => [GlProfile.Es30],
        _ => throw new ArgumentOutOfRangeException(
            nameof(requested),
            requested,
            $"{requested} is not a profile EGL can create. EGL's client API here is OpenGL ES: "
            + "GlProfile.Core45 wants a desktop context from WGL, GLX or CGL, and GlProfile.WebGl2 "
            + "is a browser's context reached through Vixen.Platform.Web."
        )
    };

    nint ChooseConfig(in EglContextOptions options, bool windowed) {
        var attributes = EglAttributes.Config(options, windowed);
        Span<nint> configs = stackalloc nint[1];

        if (!egl.ChooseConfig(display, attributes, configs, out var count) || count == 0) {
            throw Failed(
                "eglChooseConfig",
                $"no config matches RGBA8888 with {Math.Max(0, options.DepthBits)} bits of depth, "
                + $"{Math.Max(0, options.StencilBits)} of stencil"
                + (options.Samples > 1 ? $", {options.Samples}× multisampling" : string.Empty)
                + $" and {(windowed ? "a window" : "a pbuffer")} surface, renderable by OpenGL ES 3"
            );
        }

        return configs[0];
    }

    (nint Context, GlProfile Profile) CreateContext(
        GlProfile[] ladder,
        in EglContextOptions options,
        int eglMinor
    ) {
        foreach (var profile in ladder) {
            var attributes = EglAttributes.Context(profile, options.Debug, eglMinor);
            var created = egl.CreateContext(display, config, options.ShareContext, attributes);

            if (created != EglConstants.NoContext) {
                return (created, profile);
            }

            // Drained between attempts. EGL keeps one error per thread until it is read, so the
            // refusal of 3.2 would otherwise be the reason reported for a later, unrelated failure.
            egl.GetError();
        }

        // Asked again for the last rung, so the message carries a real code rather than the
        // EGL_SUCCESS that draining leaves behind.
        var single = ladder[^1];

        throw Failed(
            "eglCreateContext",
            ladder.Length > 1
                ? "no OpenGL ES 3 context could be created — 3.2 and then 3.0 were both refused"
                : $"an OpenGL ES {(single is GlProfile.Es32 ? "3.2" : "3.0")} context was asked for "
                + "by name and refused"
        );
    }

    nint CreateSurface(in EglContextOptions options, bool windowed) {
        var created = windowed
            ? egl.CreateWindowSurface(display, config, options.NativeWindow, EglAttributes.WindowSurface())
            : egl.CreatePbufferSurface(display, config, EglAttributes.PbufferSurface(options.OffscreenSize));

        if (created == EglConstants.NoSurface) {
            throw Failed(
                windowed ? "eglCreateWindowSurface" : "eglCreatePbufferSurface",
                windowed
                    ? "the native window could not be made into a surface — on Android a window "
                    + "outlives its Surface by nothing at all, so check that surfaceDestroyed has "
                    + "not already arrived"
                    : "an offscreen surface could not be created"
            );
        }

        return created;
    }

    void Release() {
        if (display == EglConstants.NoDisplay) {
            return;
        }

        // Unbound before anything is destroyed: glDestroy* on a current object only flags it.
        egl.MakeCurrent(display, EglConstants.NoSurface, EglConstants.NoSurface, EglConstants.NoContext);

        if (surface != EglConstants.NoSurface) {
            egl.DestroySurface(display, surface);
            surface = EglConstants.NoSurface;
        }

        if (context != EglConstants.NoContext) {
            egl.DestroyContext(display, context);
            context = EglConstants.NoContext;
        }

        egl.Terminate(display);
        egl.ReleaseThread();
    }

    InvalidOperationException Failed(string entryPoint, string what) => new(
        $"{entryPoint} failed: {what}. EGL says {EglError.Describe(egl.GetError())}."
    );
}
