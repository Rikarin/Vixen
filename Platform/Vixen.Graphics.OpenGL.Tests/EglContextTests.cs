// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>Bringing a GLES context up, and taking it down again.</summary>
/// <remarks>
///     <para>
///         The GLES profiles have been modelled since the backend was written; what they lacked was
///         a context. What can be wrong about creating one is the sequence rather than the calls —
///         which attribute list, in what order, and what happens when a driver says no — and none of
///         that needs an EGL to decide, which is fortunate, because the machines this is developed
///         on have none.
///     </para>
///     <para>
///         The version ladder gets the most attention here because it is the one place the backend
///         chooses a <see cref="GlProfile" /> rather than being handed one, and every capability gate
///         in <see cref="GlProfiles" /> hangs off that choice.
///     </para>
/// </remarks>
public sealed class EglContextTests {
    /// <summary>The whole sequence, in the order EGL requires it.</summary>
    [Fact]
    public void BringsUpAContextInOrder() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, Windowed(egl));

        Assert.Equal(
            [
                "GetDisplay",
                "Initialise",
                "BindApi",
                "ChooseConfig",
                "GetConfigAttrib",
                "CreateContext",
                "CreateWindowSurface",
                "MakeCurrent",
                "SwapInterval"
            ],
            egl.Names
        );

        Assert.Equal(egl.Context, context.Handle);
        Assert.Equal(egl.Display, context.Display);
        Assert.Equal(egl.Surface, context.Surface);
        Assert.Equal((1, 5), context.EglVersion);
    }

    /// <summary>The client API is bound before anything is created, not after.</summary>
    /// <remarks>
    ///     EGL's current API is per-thread state, and its default only holds for a thread that has
    ///     not already used EGL for something else. Binding after a config has been chosen is binding
    ///     after the decision it affects.
    /// </remarks>
    [Fact]
    public void BindsTheClientApiBeforeChoosingAnything() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, Windowed(egl));

        Assert.True(egl.Precedes("BindApi", "ChooseConfig"));
        Assert.Equal(EglConstants.OpenGlEsApi, egl.Single("BindApi").Arguments[0]);
    }

    /// <summary>A driver with GLES 3.2 gives GLES 3.2, asked for first.</summary>
    [Fact]
    public void TakesTheHighestProfileOnOffer() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, Windowed(egl));

        Assert.Equal(GlProfile.Es32, context.Profile);
        Assert.Equal(1, egl.Count("CreateContext"));
        Assert.Equal(2, MinorOf(egl.Single("CreateContext")));
    }

    /// <summary>A driver that refuses 3.2 gets asked for 3.0, and the device is a 3.0 device.</summary>
    /// <remarks>
    ///     The common Android phone, and the reason the ladder exists. Everything that follows from
    ///     it — no compute, no storage buffers, no indirect draws — is <see cref="GlProfiles" />
    ///     reading the profile this test asserts.
    /// </remarks>
    [Fact]
    public void FallsBackToEs30WhenThirtyTwoIsRefused() {
        var egl = new RecordingEglApi { RefusedMinorVersions = { 2 } };
        using var context = new EglContext(egl, Windowed(egl));

        Assert.Equal(GlProfile.Es30, context.Profile);
        Assert.Equal(2, egl.Count("CreateContext"));
        Assert.Equal([2, 0], egl.Named("CreateContext").Select(MinorOf).ToArray());
        Assert.False(context.Profile.HasCompute());
    }

    /// <summary>The refusal is drained before the next rung is tried.</summary>
    /// <remarks>
    ///     EGL keeps one error per thread until something reads it. Left there, the refusal of 3.2
    ///     would be the reason reported for whatever failed next — a surface, a window — and the
    ///     message would name the wrong problem.
    /// </remarks>
    [Fact]
    public void DrainsTheErrorBetweenAttempts() {
        var egl = new RecordingEglApi { RefusedMinorVersions = { 2 } };
        using var context = new EglContext(egl, Windowed(egl));

        var names = egl.Names.ToList();
        var first = names.IndexOf("CreateContext");
        var second = names.LastIndexOf("CreateContext");

        Assert.Equal("GetError", names[first + 1]);
        Assert.True(first + 1 < second);
    }

    /// <summary>A profile asked for by name is asked for once and not silently downgraded.</summary>
    [Fact]
    public void AsksOnceWhenAProfileIsNamed() {
        var egl = new RecordingEglApi { RefusedMinorVersions = { 2 } };

        var failure = Assert.Throws<InvalidOperationException>(
            () => new EglContext(egl, Windowed(egl, GlProfile.Es32))
        );

        Assert.Equal(1, egl.Count("CreateContext"));
        Assert.Contains("3.2", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A profile EGL cannot create at all is refused before anything is opened.</summary>
    [Theory]
    [InlineData(GlProfile.Core45)]
    [InlineData(GlProfile.WebGl2)]
    public void RefusesAProfileThatIsNotGles(GlProfile profile) {
        var egl = new RecordingEglApi();

        Assert.Throws<ArgumentOutOfRangeException>(() => new EglContext(egl, Windowed(egl, profile)));
        Assert.Empty(egl.Calls);
    }

    /// <summary>No window means an offscreen surface, sized by the options.</summary>
    [Fact]
    public void MakesAPbufferWhenThereIsNoWindow() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, new(0, new Int2(320, 240)));

        Assert.Equal(0, egl.Count("CreateWindowSurface"));

        var attributes = (int[])egl.Single("CreatePbufferSurface").Arguments[2]!;
        Assert.Equal(
            [EglConstants.Width, 320, EglConstants.Height, 240, EglConstants.None],
            attributes
        );
    }

    /// <summary>An offscreen device asks for no swap interval, because a pbuffer has none.</summary>
    /// <remarks><c>eglSwapInterval</c> on a pbuffer is defined to fail; asking would be noise.</remarks>
    [Fact]
    public void DoesNotSetTheSwapIntervalOffscreen() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, new(0, new Int2(64, 64)));

        Assert.Equal(0, egl.Count("SwapInterval"));
    }

    /// <summary>A config that matches nothing fails before a context is created.</summary>
    [Fact]
    public void StopsWhenNoConfigMatches() {
        var egl = new RecordingEglApi { ConfigCount = 0 };

        var failure = Assert.Throws<InvalidOperationException>(() => new EglContext(egl, Windowed(egl)));

        Assert.Equal(0, egl.Count("CreateContext"));
        Assert.Contains("eglChooseConfig", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A failure part-way through takes down what had been built.</summary>
    /// <remarks>
    ///     EGL leaks quietly. A display that was initialised and a context that was created outlive a
    ///     constructor that threw between them, and on Android that is a process that cannot bring up
    ///     graphics again after one bad start.
    /// </remarks>
    [Fact]
    public void UnwindsWhatItBuiltWhenAStepFails() {
        var egl = new RecordingEglApi { MakesCurrent = false };

        Assert.Throws<InvalidOperationException>(() => new EglContext(egl, Windowed(egl)));

        Assert.Equal(1, egl.Count("DestroySurface"));
        Assert.Equal(1, egl.Count("DestroyContext"));
        Assert.Equal(1, egl.Count("Terminate"));
        Assert.True(egl.Precedes("DestroySurface", "DestroyContext"));
        Assert.True(egl.Precedes("DestroyContext", "Terminate"));
    }

    /// <summary>A failure before the display exists destroys nothing.</summary>
    [Fact]
    public void DestroysNothingWhenThereIsNoDisplay() {
        var egl = new RecordingEglApi { Display = EglConstants.NoDisplay };

        Assert.Throws<InvalidOperationException>(() => new EglContext(egl, Windowed(egl)));
        Assert.Equal(["GetDisplay", "GetError"], egl.Names);
    }

    /// <summary>Dispose is the construction sequence backwards.</summary>
    /// <remarks>
    ///     Unbound first: destroying a current context only flags it for deletion, and a driver that
    ///     then hands the same handle out again turns that into a leak that reads like a driver bug.
    /// </remarks>
    [Fact]
    public void TearsDownInReverse() {
        var egl = new RecordingEglApi();
        var context = new EglContext(egl, Windowed(egl));

        context.Dispose();

        var unbind = egl.Named("MakeCurrent")[^1];
        Assert.Equal(EglConstants.NoContext, unbind.Arguments[3]);
        Assert.True(egl.Precedes("DestroySurface", "DestroyContext"));
        Assert.True(egl.Precedes("DestroyContext", "Terminate"));
        Assert.True(egl.Precedes("Terminate", "ReleaseThread"));
    }

    /// <summary>Disposing twice does it once.</summary>
    [Fact]
    public void DisposesOnce() {
        var egl = new RecordingEglApi();
        var context = new EglContext(egl, Windowed(egl));

        context.Dispose();
        context.Dispose();

        Assert.Equal(1, egl.Count("Terminate"));
    }

    /// <summary>An entry point is looked for in the client library before EGL is asked.</summary>
    /// <remarks>
    ///     Before EGL 1.5 only extension entry points had to come back from
    ///     <c>eglGetProcAddress</c> — a core function like <c>glDrawArrays</c> was allowed to return
    ///     null, and on several drivers it does. Asking <c>libGLESv2</c> first is what makes the
    ///     backend load on those.
    /// </remarks>
    [Fact]
    public void AsksTheClientLibraryFirst() {
        var egl = new RecordingEglApi { ClientSymbols = { ["glDrawArrays"] = 0x1234 } };
        using var context = new EglContext(egl, Windowed(egl));

        Assert.Equal(0x1234, (int)context.GetProcAddress("glDrawArrays"));
        Assert.Equal(1, egl.Count("GetClientProcAddress"));
        Assert.Equal(0, egl.Count("GetProcAddress"));
    }

    /// <summary>…and EGL covers what the client library does not export, which is the extensions.</summary>
    [Fact]
    public void FallsBackToEglForExtensions() {
        var egl = new RecordingEglApi { EglSymbols = { ["glDiscardFramebufferEXT"] = 0x5678 } };
        using var context = new EglContext(egl, Windowed(egl));

        Assert.True(context.TryGetProcAddress("glDiscardFramebufferEXT", out var address));
        Assert.Equal(0x5678, (int)address);
        Assert.False(context.TryGetProcAddress("glNotAThing", out _));
    }

    /// <summary>The size is asked for, not remembered.</summary>
    /// <remarks>
    ///     A window surface follows its window, so a rotation changes this without anything here
    ///     being told. A cached size is a swapchain that resizes to what the window used to be.
    /// </remarks>
    [Fact]
    public void QueriesTheSizeEveryTime() {
        var egl = new RecordingEglApi { SurfaceSize = (800, 600) };
        using var context = new EglContext(egl, Windowed(egl));

        Assert.Equal(new Int2(800, 600), context.Size);

        egl.SurfaceSize = (600, 800);
        Assert.Equal(new Int2(600, 800), context.Size);
    }

    /// <summary>Presenting is one call, and a failed one is reported rather than swallowed.</summary>
    [Fact]
    public void PresentsAndReportsAFailedPresent() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, Windowed(egl));

        context.SwapBuffers();
        Assert.Equal([egl.Display, egl.Surface], egl.Single("SwapBuffers").Arguments);

        egl.Swaps = false;
        Assert.Throws<InvalidOperationException>(context.SwapBuffers);
    }

    /// <summary>A disposed context refuses to be used rather than calling into freed handles.</summary>
    [Fact]
    public void RefusesUseAfterDispose() {
        var egl = new RecordingEglApi();
        var context = new EglContext(egl, Windowed(egl));

        context.Dispose();

        Assert.Throws<ObjectDisposedException>(context.SwapBuffers);
        Assert.Throws<ObjectDisposedException>(context.MakeCurrent);
        Assert.Throws<ObjectDisposedException>(() => _ = context.Size);
    }

    /// <summary>Whether a context is current is EGL's answer, not a remembered flag.</summary>
    [Fact]
    public void AsksEglWhatIsCurrent() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, Windowed(egl));

        Assert.True(context.IsCurrent);

        context.Clear();
        Assert.False(context.IsCurrent);
    }

    /// <summary>The config's native visual is read, and it is read before the surface is made.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing read it, and every windowed test in this file passed anyway.</b> A config
    ///     that <c>eglChooseConfig</c> matched is not yet a config a window will accept: an
    ///     <c>ANativeWindow</c> carries a buffer format of its own, and
    ///     <c>eglCreateWindowSurface</c> answers <c>EGL_BAD_MATCH</c> when it disagrees with
    ///     <c>EGL_NATIVE_VISUAL_ID</c>. The recorder now refuses the way the driver does, which is
    ///     what turns "the sequence compiles" into "the sequence is one a driver accepts".
    /// </remarks>
    [Fact]
    public void ReadsTheNativeVisualBeforeMakingAWindowSurface() {
        var egl = new RecordingEglApi();

        egl.ConfigAttributes[EglConstants.NativeVisualId] = 4;

        using var context = new EglContext(egl, Windowed(egl));

        Assert.Equal(4, context.NativeVisualId);
        Assert.True(egl.Precedes("ChooseConfig", "GetConfigAttrib"));
        Assert.True(egl.Precedes("GetConfigAttrib", "CreateWindowSurface"));

        var read = egl.Single("GetConfigAttrib");

        Assert.Equal(egl.Config, read.Arguments[1]);
        Assert.Equal(EglConstants.NativeVisualId, read.Arguments[2]);
    }

    /// <summary>The window is prepared with that visual, between the context and the surface.</summary>
    /// <remarks>
    ///     A callback rather than a call, because the call is
    ///     <c>ANativeWindow_setBuffersGeometry</c> in <c>libandroid.so</c> — Android's, not EGL's,
    ///     and this assembly's business to ask for rather than to make.
    /// </remarks>
    [Fact]
    public void PreparesTheNativeWindowWithTheVisualItWillNeed() {
        var egl = new RecordingEglApi();
        var prepared = new List<(nint Window, int Visual)>();

        egl.ConfigAttributes[EglConstants.NativeVisualId] = 4;

        using var context = new EglContext(
            egl,
            new(
                0x900,
                PrepareNativeWindow: (window, visual) => {
                    prepared.Add((window, visual));
                    egl.WindowFormat = visual;
                }
            )
        );

        Assert.Equal([((nint)0x900, 4)], prepared);
    }

    /// <summary>A window nobody prepared is refused, and the message says what to do about it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the case the old recorder could not express.</b> It returned a surface for
    ///     any window, so an Android head that never set the buffer geometry would have gone green
    ///     here and <c>EGL_BAD_MATCH</c> on the device.
    /// </remarks>
    [Fact]
    public void RefusesAWindowWhoseFormatDoesNotMatchTheConfig() {
        var egl = new RecordingEglApi();

        egl.ConfigAttributes[EglConstants.NativeVisualId] = 4;
        egl.WindowFormat = 1;

        var failure = Assert.Throws<InvalidOperationException>(() => new EglContext(egl, new(0x900)));

        Assert.Contains("eglCreateWindowSurface", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PrepareNativeWindow", failure.Message, StringComparison.Ordinal);
        Assert.Contains("EGL_BAD_MATCH", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An offscreen context asks for no visual, because a pbuffer has no window to match.</summary>
    [Fact]
    public void AnOffscreenContextReadsNoNativeVisual() {
        var egl = new RecordingEglApi();
        using var context = new EglContext(egl, new(0, new Int2(64, 64)));

        Assert.Equal(0, egl.Count("GetConfigAttrib"));
        Assert.Equal(0, context.NativeVisualId);
    }

    /// <summary>A driver with no native visual to report is not a failure.</summary>
    /// <remarks>
    ///     ⚠ Zero is an answer here, not an error — desktop EGL implementations routinely have no
    ///     visual for a config, and refusing one would refuse every ANGLE and Mesa context on the
    ///     strength of an attribute only Android needs.
    /// </remarks>
    [Fact]
    public void ADriverWithNoNativeVisualStillBringsUpAContext() {
        var egl = new RecordingEglApi { ReportsConfigAttributes = false, EnforcesWindowFormat = false };
        using var context = new EglContext(egl, new(0x900));

        Assert.Equal(0, context.NativeVisualId);
        Assert.Equal(egl.Surface, context.Surface);
    }

    /// <summary>Options for a window surface, with the preparation a driver insists on.</summary>
    /// <remarks>
    ///     ⚠ <b>Every windowed test here needs this now, and that is the finding rather than an
    ///     inconvenience.</b> What it stands in for is the Android platform's
    ///     <c>ANativeWindow_setBuffersGeometry</c>; a test that omits it is a head that omits it,
    ///     and the recorder refuses both.
    /// </remarks>
    static EglContextOptions Windowed(RecordingEglApi egl, GlProfile? profile = null) =>
        new(0x900, Profile: profile, PrepareNativeWindow: (_, visual) => egl.WindowFormat = visual);

    /// <summary>The GLES minor version a recorded <c>eglCreateContext</c> asked for.</summary>
    static int MinorOf(EglCall call) {
        var attributes = (int[])call.Arguments[3]!;

        for (var index = 0; index + 1 < attributes.Length; index += 2) {
            if (attributes[index] == EglConstants.ContextMinorVersion) {
                return attributes[index + 1];
            }
        }

        return 0;
    }
}
