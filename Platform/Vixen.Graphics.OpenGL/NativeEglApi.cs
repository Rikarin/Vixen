// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Vixen.Platform.Native;

namespace Vixen.Graphics.OpenGL;

/// <summary>The EGL entry points, out of the platform's <c>libEGL</c>.</summary>
/// <remarks>
///     <para>
///         <b>Hand-loaded, because there is no binding to use.</b> <c>Silk.NET.EGL</c> exists only
///         for Silk.NET 1 — it stops at 1.9.0 — and Silk.NET 2's GLES windowing reaches EGL through
///         GLFW or SDL rather than binding it. Nineteen entry points is a small enough surface that
///         this is the cheaper answer, and it keeps the loading rules the same as every other native
///         dependency in the engine's: <see cref="NativeLibraries" /> searches the application's own
///         <c>runtimes/&lt;rid&gt;/native/</c> layout before the machine's, and knows about the
///         versioned soname that a runtime-only install actually has.
///     </para>
///     <para>
///         <b>Two libraries, not one.</b> <c>libEGL</c> has the context management and
///         <c>libGLESv2</c> has the GL functions, and the second is loaded here rather than by
///         <c>Silk.NET.OpenGLES</c> so that <see cref="GetClientProcAddress" /> can answer out of its
///         symbol table. That order matters on any driver older than EGL 1.5, where
///         <c>eglGetProcAddress</c> is only required to resolve <em>extension</em> entry points and
///         is allowed to return null for <c>glDrawArrays</c> — see
///         <see cref="IEglApi.GetClientProcAddress" />.
///     </para>
///     <para>
///         <b>Function pointers rather than <c>DllImport</c>.</b> The library's name is not known
///         until it is found, which is the whole point of the search; a <c>DllImport</c> names it at
///         compile time. It also means nothing here goes through
///         <c>Assembly.Location</c> — the NativeAOT gate <c>VulkanLoader</c> and
///         <c>OpenALLoader</c> both record.
///     </para>
///     <para>
///         The unmanaged calling convention is the platform default. EGL's headers say
///         <c>KHRONOS_APIENTRY</c>, which is <c>__stdcall</c> on 32-bit Windows and nothing anywhere
///         else — and 32-bit Windows is not a RID this engine ships.
///     </para>
///     <para>
///         <b>Not exercised by the test suite</b>, the same position <see cref="SilkGlApi" /> and
///         <see cref="SilkGlesApi" /> take. What can be wrong here is a signature, which a driver
///         finds immediately and a fake never would; what can be wrong about <em>using</em> EGL is in
///         <see cref="EglContext" />, and that is tested against <see cref="IEglApi" />.
///     </para>
/// </remarks>
public sealed unsafe class NativeEglApi : IEglApi {
    static readonly Lock Gate = new();

    static nint eglHandle;
    static nint clientHandle;
    static string? failure;

    readonly delegate* unmanaged<nint, nint> getDisplay;
    readonly delegate* unmanaged<nint, int*, int*, int> initialise;
    readonly delegate* unmanaged<nint, int> terminate;
    readonly delegate* unmanaged<uint, int> bindApi;
    readonly delegate* unmanaged<nint, int, byte*> queryString;
    readonly delegate* unmanaged<nint, int*, nint*, int, int*, int> chooseConfig;
    readonly delegate* unmanaged<nint, nint, int, int*, int> getConfigAttrib;
    readonly delegate* unmanaged<nint, nint, nint, int*, nint> createContext;
    readonly delegate* unmanaged<nint, nint, nint, int*, nint> createWindowSurface;
    readonly delegate* unmanaged<nint, nint, int*, nint> createPbufferSurface;
    readonly delegate* unmanaged<nint, nint, int> destroySurface;
    readonly delegate* unmanaged<nint, nint, int> destroyContext;
    readonly delegate* unmanaged<nint, nint, int, int*, int> querySurface;
    readonly delegate* unmanaged<nint, nint, nint, nint, int> makeCurrent;
    readonly delegate* unmanaged<nint> getCurrentContext;
    readonly delegate* unmanaged<nint, nint, int> swapBuffers;
    readonly delegate* unmanaged<nint, int, int> swapInterval;
    readonly delegate* unmanaged<int> releaseThread;
    readonly delegate* unmanaged<byte*, nint> getProcAddress;
    readonly delegate* unmanaged<uint> getError;

    readonly nint client;

    NativeEglApi(nint egl, nint client) {
        this.client = client;

        getDisplay = (delegate* unmanaged<nint, nint>)Export(egl, "eglGetDisplay");
        initialise = (delegate* unmanaged<nint, int*, int*, int>)Export(egl, "eglInitialize");
        terminate = (delegate* unmanaged<nint, int>)Export(egl, "eglTerminate");
        bindApi = (delegate* unmanaged<uint, int>)Export(egl, "eglBindAPI");
        queryString = (delegate* unmanaged<nint, int, byte*>)Export(egl, "eglQueryString");
        chooseConfig = (delegate* unmanaged<nint, int*, nint*, int, int*, int>)Export(egl, "eglChooseConfig");

        getConfigAttrib =
            (delegate* unmanaged<nint, nint, int, int*, int>)Export(egl, "eglGetConfigAttrib");

        createContext = (delegate* unmanaged<nint, nint, nint, int*, nint>)Export(egl, "eglCreateContext");

        createWindowSurface =
            (delegate* unmanaged<nint, nint, nint, int*, nint>)Export(egl, "eglCreateWindowSurface");

        createPbufferSurface =
            (delegate* unmanaged<nint, nint, int*, nint>)Export(egl, "eglCreatePbufferSurface");

        destroySurface = (delegate* unmanaged<nint, nint, int>)Export(egl, "eglDestroySurface");
        destroyContext = (delegate* unmanaged<nint, nint, int>)Export(egl, "eglDestroyContext");
        querySurface = (delegate* unmanaged<nint, nint, int, int*, int>)Export(egl, "eglQuerySurface");
        makeCurrent = (delegate* unmanaged<nint, nint, nint, nint, int>)Export(egl, "eglMakeCurrent");
        getCurrentContext = (delegate* unmanaged<nint>)Export(egl, "eglGetCurrentContext");
        swapBuffers = (delegate* unmanaged<nint, nint, int>)Export(egl, "eglSwapBuffers");
        swapInterval = (delegate* unmanaged<nint, int, int>)Export(egl, "eglSwapInterval");
        releaseThread = (delegate* unmanaged<int>)Export(egl, "eglReleaseThread");
        getProcAddress = (delegate* unmanaged<byte*, nint>)Export(egl, "eglGetProcAddress");
        getError = (delegate* unmanaged<uint>)Export(egl, "eglGetError");
    }

    /// <summary>Where <c>libEGL</c> was found, for logging at boot.</summary>
    public static string? ResolvedEglPath { get; private set; }

    /// <summary>Where <c>libGLESv2</c> was found, or <see langword="null" /> if it was not.</summary>
    public static string? ResolvedClientPath { get; private set; }

    /// <summary>Loads EGL, reporting failure rather than throwing.</summary>
    /// <param name="api">The entry points, when they loaded.</param>
    /// <param name="reason">Why they did not, when they did not.</param>
    /// <returns>Whether EGL is available on this machine.</returns>
    /// <remarks>
    ///     Reporting rather than throwing, because "this machine has no EGL" is the ordinary answer
    ///     on a desktop without ANGLE and is what backend selection is for. It is the same shape
    ///     <c>VulkanLoader.TryLoad</c> has, and for the same reason.
    /// </remarks>
    public static bool TryLoad(
        [NotNullWhen(true)] out NativeEglApi? api,
        [NotNullWhen(false)] out string? reason
    ) {
        lock (Gate) {
            if (eglHandle == 0 && failure is null) {
                Load();
            }

            if (eglHandle == 0) {
                api = null;
                reason = failure ?? InstallHint();
                return false;
            }

            try {
                api = new(eglHandle, clientHandle);
                reason = null;
                return true;
            } catch (EntryPointNotFoundException notFound) {
                // A library called libEGL that is not one. Recorded as the failure so a second
                // attempt does not repeat the search and arrive at the same wrong file.
                failure = $"{ResolvedEglPath} was loaded but is not an EGL: {notFound.Message}";
                eglHandle = 0;
                api = null;
                reason = failure;
                return false;
            }
        }
    }

    /// <inheritdoc />
    public nint GetDisplay(nint nativeDisplay) => getDisplay(nativeDisplay);

    /// <inheritdoc />
    public bool Initialise(nint display, out int major, out int minor) {
        int gotMajor;
        int gotMinor;
        var ok = initialise(display, &gotMajor, &gotMinor) != EglConstants.False;

        major = ok ? gotMajor : 0;
        minor = ok ? gotMinor : 0;
        return ok;
    }

    /// <inheritdoc />
    public bool Terminate(nint display) => terminate(display) != EglConstants.False;

    /// <inheritdoc />
    public bool BindApi(uint api) => bindApi(api) != EglConstants.False;

    /// <inheritdoc />
    public string? QueryString(nint display, int name) =>
        Marshal.PtrToStringUTF8((nint)queryString(display, name));

    /// <inheritdoc />
    public bool ChooseConfig(nint display, ReadOnlySpan<int> attributes, Span<nint> configs, out int count) {
        int written;
        int ok;

        fixed (int* first = attributes) {
            fixed (nint* into = configs) {
                ok = chooseConfig(display, first, into, configs.Length, &written);
            }
        }

        count = ok != EglConstants.False ? written : 0;
        return ok != EglConstants.False;
    }

    /// <inheritdoc />
    public bool GetConfigAttrib(nint display, nint config, int attribute, out int value) {
        int read;
        var ok = getConfigAttrib(display, config, attribute, &read) != EglConstants.False;

        value = ok ? read : 0;
        return ok;
    }

    /// <inheritdoc />
    public nint CreateContext(nint display, nint config, nint share, ReadOnlySpan<int> attributes) {
        fixed (int* first = attributes) {
            return createContext(display, config, share, first);
        }
    }

    /// <inheritdoc />
    public nint CreateWindowSurface(nint display, nint config, nint window, ReadOnlySpan<int> attributes) {
        fixed (int* first = attributes) {
            return createWindowSurface(display, config, window, first);
        }
    }

    /// <inheritdoc />
    public nint CreatePbufferSurface(nint display, nint config, ReadOnlySpan<int> attributes) {
        fixed (int* first = attributes) {
            return createPbufferSurface(display, config, first);
        }
    }

    /// <inheritdoc />
    public bool DestroySurface(nint display, nint surface) =>
        destroySurface(display, surface) != EglConstants.False;

    /// <inheritdoc />
    public bool DestroyContext(nint display, nint context) =>
        destroyContext(display, context) != EglConstants.False;

    /// <inheritdoc />
    public bool QuerySurface(nint display, nint surface, int attribute, out int value) {
        int got;
        var ok = querySurface(display, surface, attribute, &got) != EglConstants.False;

        value = ok ? got : 0;
        return ok;
    }

    /// <inheritdoc />
    public bool MakeCurrent(nint display, nint draw, nint read, nint context) =>
        makeCurrent(display, draw, read, context) != EglConstants.False;

    /// <inheritdoc />
    public nint GetCurrentContext() => getCurrentContext();

    /// <inheritdoc />
    public bool SwapBuffers(nint display, nint surface) =>
        swapBuffers(display, surface) != EglConstants.False;

    /// <inheritdoc />
    public bool SwapInterval(nint display, int interval) =>
        swapInterval(display, interval) != EglConstants.False;

    /// <inheritdoc />
    public bool ReleaseThread() => releaseThread() != EglConstants.False;

    /// <inheritdoc />
    public nint GetProcAddress(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // 256 bytes covers every GL and EGL entry point there has ever been. The bound is on the
        // encoded length rather than the character count, so a name that is not ASCII — which no
        // entry point is, and which a caller could still pass — takes the heap rather than
        // overrunning.
        var needed = Encoding.UTF8.GetMaxByteCount(name.Length) + 1;
        Span<byte> utf8 = needed <= 256 ? stackalloc byte[256] : new byte[needed];
        var written = Encoding.UTF8.GetBytes(name, utf8);
        utf8[written] = 0;

        fixed (byte* first = utf8) {
            return getProcAddress(first);
        }
    }

    /// <inheritdoc />
    public nint GetClientProcAddress(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return client != 0 && NativeLibrary.TryGetExport(client, name, out var address) ? address : 0;
    }

    /// <inheritdoc />
    public uint GetError() => getError();

    static void Load() {
        NativeLibraries.Describe(new NativeLibrarySpec("EGL", ["1"], []));
        NativeLibraries.Describe(new NativeLibrarySpec("GLESv2", ["2"], []));

        foreach (var candidate in Candidates("EGL", "1")) {
            if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                continue;
            }

            // The probe is eglGetDisplay rather than a name match, for the reason OpenALLoader gives:
            // a file called libEGL that exports none of EGL is something else that was on the path
            // first, and loading it successfully is not the same as having found EGL.
            if (!NativeLibrary.TryGetExport(handle, "eglGetDisplay", out _)) {
                continue;
            }

            eglHandle = handle;
            ResolvedEglPath = candidate;
            break;
        }

        if (eglHandle == 0) {
            failure = InstallHint();
            return;
        }

        foreach (var candidate in Candidates("GLESv2", "2")) {
            if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                continue;
            }

            clientHandle = handle;
            ResolvedClientPath = candidate;
            break;
        }
    }

    /// <summary>Every path to try for a library, the application's own layout first.</summary>
    /// <remarks>
    ///     The bare name comes last for the reason <c>VulkanLoader</c> states: it is what the dynamic
    ///     linker will search, and letting it answer first would let a machine's system-wide copy beat
    ///     the one the application shipped. On Android the system copy is the only one there is and
    ///     the application's layout is simply empty, so the order costs nothing there.
    /// </remarks>
    static IEnumerable<string> Candidates(string library, string version) {
        foreach (var candidate in NativeLibraries.Candidates(library)) {
            yield return candidate;
        }

        foreach (var name in NativeLibraryNames.For(library, version)) {
            yield return name;
        }
    }

    static nint Export(nint handle, string name) => NativeLibrary.TryGetExport(handle, name, out var address)
        ? address
        : throw new EntryPointNotFoundException($"{name} is not exported by {ResolvedEglPath}.");

    static string InstallHint() {
        if (OperatingSystem.IsAndroid()) {
            return "libEGL could not be loaded, which should not be possible on Android — every "
                + "device ships one in /system/lib. Check that the ABI of this build matches the "
                + "device's.";
        }

        if (OperatingSystem.IsMacOS()) {
            return "libEGL was not found. macOS has no EGL of its own: the GLES profiles need ANGLE "
                + "(`brew install angle`, or a libEGL.dylib shipped in runtimes/<rid>/native). Use "
                + "the Vulkan backend through MoltenVK, or SilkGlApi with a desktop GL context, "
                + "unless the point is to exercise the GLES path specifically.";
        }

        if (OperatingSystem.IsWindows()) {
            return "libEGL.dll was not found. Windows has no EGL of its own: it comes from ANGLE, "
                + "which browsers ship and which can be placed in runtimes/<rid>/native. Desktop GL "
                + "through SilkGlApi needs none of it.";
        }

        return "libEGL was not found. Install the vendor's driver or Mesa's libegl1 (Debian) or "
            + "mesa-libEGL (Fedora); a published build may also carry one in runtimes/<rid>/native.";
    }
}
