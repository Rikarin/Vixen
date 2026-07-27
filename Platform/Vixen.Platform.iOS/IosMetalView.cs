// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using CoreAnimation;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace Vixen.Platform.Ios;

/// <summary>The view Vulkan presents to, and the one the fingers land on.</summary>
/// <remarks>
///     <para>
///         <b>Its backing layer is a <see cref="CAMetalLayer" />, and that is the whole trick.</b>
///         MoltenVK's <c>VK_EXT_metal_surface</c> takes a <c>CAMetalLayer</c> and nothing else, and
///         a <c>UIView</c> cannot be given one after the fact — the layer class is decided once, by
///         the class itself, before any instance exists. Overriding
///         <see cref="LayerClass" /> is how UIKit asks that question, so this view exists to answer
///         it.
///     </para>
///     <para>
///         <b>The drawable size is set here rather than left to UIKit.</b> A
///         <c>CAMetalLayer</c> sizes its drawables from <c>bounds × contentsScale</c>, and both
///         change — on rotation, and when a window moves between a 2× and a 3× screen. Getting it
///         wrong by a pixel is a swapchain that fails validation, so it is recomputed whenever the
///         layout changes and the size is reported in the same event the rest of the engine already
///         listens for.
///     </para>
///     <para>
///         <b>Touches are handled here because this is where UIKit delivers them.</b> The
///         translation to <see cref="PlatformEvent" /> is a straight one; the bookkeeping that makes
///         a <c>UITouch</c> into a small stable finger id lives in <see cref="TouchTracker" />,
///         shared with Android.
///     </para>
/// </remarks>
[Register(nameof(IosMetalView))]
internal sealed class IosMetalView : UIView {
    readonly TouchTracker tracker = new();

    PlatformEventBuffer? events;
    uint windowId;

    /// <summary>Creates the view.</summary>
    /// <param name="frame">Its initial bounds, in points.</param>
    internal IosMetalView(CGRect frame) : base(frame) {
        Opaque = true;
        MultipleTouchEnabled = true;
        ContentScaleFactor = UIScreen.MainScreen.Scale;
    }

    /// <summary>Required by UIKit's unarchiver.</summary>
    /// <param name="handle">The native object.</param>
    internal IosMetalView(NativeHandle handle) : base(handle) { }

    /// <summary>Tells UIKit to back this view with a Metal layer.</summary>
    /// <remarks>
    ///     Answered per class, not per instance, and answered before any instance exists — which is
    ///     why this type exists at all rather than a plain <c>UIView</c> being configured.
    /// </remarks>
    [Export("layerClass")]
    internal static Class LayerClass => new(typeof(CAMetalLayer));

    /// <summary>The layer a Vulkan surface is created from.</summary>
    internal CAMetalLayer MetalLayer => (CAMetalLayer)Layer;

    /// <summary>The size of the thing being presented to, in physical pixels.</summary>
    internal Int2 DrawableSize {
        get {
            var size = MetalLayer.DrawableSize;
            return new((int)size.Width, (int)size.Height);
        }
    }

    /// <summary>Where events go once this view is attached to a window.</summary>
    /// <param name="buffer">The platform's event buffer.</param>
    /// <param name="id">The window these events belong to.</param>
    internal void Attach(PlatformEventBuffer buffer, uint id) {
        events = buffer;
        windowId = id;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Rotation, split view, and a move between screens of different scale all arrive here and
    ///     nowhere else. The drawable is resized first and the event raised second, so a handler
    ///     that rebuilds a swapchain reads a layer that already agrees with the number it was given.
    /// </remarks>
    public override void LayoutSubviews() {
        base.LayoutSubviews();

        var scale = Window?.Screen.Scale ?? UIScreen.MainScreen.Scale;
        ContentScaleFactor = scale;
        MetalLayer.ContentsScale = scale;

        var bounds = Bounds;
        var pixels = new Int2((int)(bounds.Width * scale), (int)(bounds.Height * scale));

        if (pixels.X <= 0 || pixels.Y <= 0) {
            return;
        }

        MetalLayer.DrawableSize = new(pixels.X, pixels.Y);

        events?.Post(
            PlatformEvent.WindowResized(
                windowId,
                IosClock.Now,
                new((int)bounds.Width, (int)bounds.Height),
                pixels
            )
        );
    }

    /// <inheritdoc />
    public override void TouchesBegan(NSSet touches, UIEvent? evt) {
        foreach (var touch in Enumerate(touches)) {
            var position = At(touch);

            if (tracker.TryBegin(touch.Handle.Handle, position, out var finger)) {
                Post(PlatformEventKind.TouchDown, finger, position, default, touch);
            }
        }
    }

    /// <inheritdoc />
    public override void TouchesMoved(NSSet touches, UIEvent? evt) {
        foreach (var touch in Enumerate(touches)) {
            var position = At(touch);

            if (tracker.TryMove(touch.Handle.Handle, position, out var finger, out var delta)) {
                Post(PlatformEventKind.TouchMoved, finger, position, delta, touch);
            }
        }
    }

    /// <inheritdoc />
    public override void TouchesEnded(NSSet touches, UIEvent? evt) => End(touches);

    /// <inheritdoc />
    /// <remarks>
    ///     Cancellation is not a rare path. An incoming call, a system edge gesture, or the control
    ///     centre all take a touch sequence away mid-gesture, and an application that is never told
    ///     the finger lifted keeps drawing the line it was dragging. Reported as an ordinary
    ///     <see cref="PlatformEventKind.TouchUp" />, because from the application's side that is
    ///     exactly what happened.
    /// </remarks>
    public override void TouchesCancelled(NSSet touches, UIEvent? evt) => End(touches);

    void End(NSSet touches) {
        foreach (var touch in Enumerate(touches)) {
            var position = At(touch);

            if (tracker.TryEnd(touch.Handle.Handle, out var finger)) {
                Post(PlatformEventKind.TouchUp, finger, position, default, touch);
            }
        }
    }

    /// <summary>Drops every finger, as a run of ups.</summary>
    /// <remarks>
    ///     What the view controller calls when the application leaves the foreground: UIKit does not
    ///     reliably cancel touches on the way out, and a finger left down across a suspend is one
    ///     that is still down when the application comes back minutes later.
    /// </remarks>
    internal void ReleaseAllTouches() {
        foreach (var finger in tracker.Clear()) {
            events?.Post(
                PlatformEvent.Touch(PlatformEventKind.TouchUp, windowId, IosClock.Now, finger, default)
            );
        }
    }

    void Post(PlatformEventKind kind, int finger, Vector2 position, Vector2 delta, UITouch touch) =>
        events?.Post(
            PlatformEvent.Touch(kind, windowId, IosClock.Now, finger, position, delta, Pressure(touch))
        );

    Vector2 At(UITouch touch) {
        var point = touch.LocationInView(this);
        return new((float)point.X, (float)point.Y);
    }

    /// <summary>
    ///     How hard the finger is pressing, as a fraction of "normal".
    /// </summary>
    /// <remarks>
    ///     <c>MaximumPossibleForce</c> is zero on a device without a pressure-sensitive screen, and
    ///     dividing by it would report every touch as <c>NaN</c> — which propagates into whatever
    ///     the pressure scales and is very hard to trace back here. Those devices report full
    ///     pressure instead, which is what a binary touch means.
    /// </remarks>
    static float Pressure(UITouch touch) =>
        touch.MaximumPossibleForce > 0 ? (float)(touch.Force / touch.MaximumPossibleForce) : 1f;

    static IEnumerable<UITouch> Enumerate(NSSet touchSet) => touchSet.OfType<UITouch>();
}
