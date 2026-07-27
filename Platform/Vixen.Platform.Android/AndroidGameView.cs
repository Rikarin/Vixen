// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Android;

/// <summary>The surface Vulkan presents to, and the one the fingers land on.</summary>
/// <remarks>
///     <para>
///         <b>The surface is created and destroyed under a running application, and that is the whole
///         difficulty of this platform.</b> Doc 10 names Android lifecycle as the biggest source of
///         bugs for exactly this reason: <c>surfaceDestroyed</c> arrives when the activity stops, the
///         <c>ANativeWindow</c> behind the swapchain becomes invalid at that moment, and the callback
///         must not return until the renderer has stopped using it. The frame loop is paused before
///         <see cref="PlatformEventKind.Suspending" /> is raised, which is what makes that true.
///     </para>
///     <para>
///         <b><c>ANativeWindow_fromSurface</c> is the only way across.</b> Vulkan's
///         <c>VK_KHR_android_surface</c> takes an <c>ANativeWindow*</c>, and the managed
///         <see cref="Surface" /> is a Java object; the bridge is a C function in <c>libandroid.so</c>
///         that takes the JNI environment and the Java object and returns the native handle. It also
///         takes a reference, which is why the release below is not optional.
///     </para>
/// </remarks>
internal sealed partial class AndroidGameView : SurfaceView, ISurfaceHolderCallback {
    readonly TouchTracker tracker = new();
    readonly PlatformEventBuffer events;

    nint nativeWindow;
    uint windowId;

    internal AndroidGameView(Context context, PlatformEventBuffer events) : base(context) {
        this.events = events;

        Holder?.AddCallback(this);
        Focusable = true;
        FocusableInTouchMode = true;
    }

    /// <summary>The <c>ANativeWindow*</c> a Vulkan surface is created from; zero while there is none.</summary>
    internal nint NativeWindow => nativeWindow;

    /// <summary>The size of the thing being presented to, in physical pixels.</summary>
    internal Int2 PixelSize { get; private set; }

    /// <summary>Whether the surface exists right now.</summary>
    internal bool HasSurface => nativeWindow != 0;

    /// <summary>Which window these events belong to.</summary>
    internal void Attach(uint id) => windowId = id;

    /// <inheritdoc />
    public void SurfaceCreated(ISurfaceHolder holder) {
        ArgumentNullException.ThrowIfNull(holder);
        Acquire(holder);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Called for a rotation and for a resize, and also immediately after creation. The handle is
    ///     re-acquired rather than assumed stable: a configuration change can replace the underlying
    ///     window, and holding the old pointer is a use-after-free that presents to freed memory.
    /// </remarks>
    public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] global::Android.Graphics.Format format, int width, int height) {
        ArgumentNullException.ThrowIfNull(holder);

        Acquire(holder);
        PixelSize = new(width, height);

        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        events.Post(
            PlatformEvent.WindowResized(
                windowId,
                AndroidClock.Now,
                new((int)(width / density), (int)(height / density)),
                PixelSize
            )
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Must not return until nothing is using the surface.</b> Android's contract is explicit:
    ///     after this returns, the window is gone. The platform pauses the frame loop before this is
    ///     reached, so what is left here is releasing the reference
    ///     <c>ANativeWindow_fromSurface</c> took.
    /// </remarks>
    public void SurfaceDestroyed(ISurfaceHolder holder) {
        ArgumentNullException.ThrowIfNull(holder);
        Release();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     One <c>MotionEvent</c> can carry several fingers and a batch of historical positions. The
    ///     history is deliberately not replayed: it is what a drawing application uses to smooth a
    ///     stroke, and reporting it as a run of moves would make the delta of every frame depend on
    ///     how far behind the input thread was.
    /// </remarks>
    public override bool OnTouchEvent(MotionEvent? e) {
        if (e is null) {
            return false;
        }

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                Begin(e, e.ActionIndex);
                break;

            case MotionEventActions.Move:
                // Move carries every finger at once and names none of them, so all are reported.
                for (var index = 0; index < e.PointerCount; index++) {
                    Move(e, index);
                }

                break;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                End(e, e.ActionIndex);
                break;

            case MotionEventActions.Cancel:
                Cancel();
                break;

            default:
                return false;
        }

        return true;
    }

    /// <summary>Drops every finger, as a run of ups.</summary>
    internal void ReleaseAllTouches() => Cancel();

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        Release();
        base.Dispose(disposing);
    }

    void Begin(MotionEvent e, int index) {
        var position = At(e, index);

        if (tracker.TryBegin(e.GetPointerId(index), position, out var finger)) {
            events.Post(
                PlatformEvent.Touch(
                    PlatformEventKind.TouchDown,
                    windowId,
                    AndroidClock.Now,
                    finger,
                    position,
                    default,
                    e.GetPressure(index)
                )
            );
        }
    }

    void Move(MotionEvent e, int index) {
        var position = At(e, index);

        if (tracker.TryMove(e.GetPointerId(index), position, out var finger, out var delta)) {
            events.Post(
                PlatformEvent.Touch(
                    PlatformEventKind.TouchMoved,
                    windowId,
                    AndroidClock.Now,
                    finger,
                    position,
                    delta,
                    e.GetPressure(index)
                )
            );
        }
    }

    void End(MotionEvent e, int index) {
        var position = At(e, index);

        if (tracker.TryEnd(e.GetPointerId(index), out var finger)) {
            events.Post(
                PlatformEvent.Touch(PlatformEventKind.TouchUp, windowId, AndroidClock.Now, finger, position)
            );
        }
    }

    void Cancel() {
        foreach (var finger in tracker.Clear()) {
            events.Post(
                PlatformEvent.Touch(PlatformEventKind.TouchUp, windowId, AndroidClock.Now, finger, default)
            );
        }
    }

    static Vector2 At(MotionEvent e, int index) => new(e.GetX(index), e.GetY(index));

    void Acquire(ISurfaceHolder holder) {
        Release();

        if (holder.Surface is not { IsValid: true } surface) {
            return;
        }

        nativeWindow = ANativeWindow_fromSurface(JNIEnv.Handle, surface.Handle);
    }

    void Release() {
        if (nativeWindow == 0) {
            return;
        }

        ANativeWindow_release(nativeWindow);
        nativeWindow = 0;
    }

    /// <summary>The bridge from a Java <c>Surface</c> to the <c>ANativeWindow*</c> Vulkan wants.</summary>
    /// <remarks>Takes a reference, which is why <c>ANativeWindow_release</c> is not optional.</remarks>
    [LibraryImport("android")]
    private static partial nint ANativeWindow_fromSurface(nint env, nint surface);

    [LibraryImport("android")]
    private static partial void ANativeWindow_release(nint window);
}
