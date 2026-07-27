// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Foundation;
using ObjCRuntime;
using UIKit;

namespace Vixen.Platform.Ios;

/// <summary>Hosts the Metal view, and answers the questions UIKit asks a full-screen game.</summary>
/// <remarks>
///     A view controller is not optional furniture on iOS: rotation, safe areas, the status bar and
///     the home indicator are all decided by whichever controller owns the screen, and a window with
///     no root controller is a black screen with a runtime complaint. So this exists to say what a
///     game wants for each of them.
/// </remarks>
[Register(nameof(IosViewController))]
internal sealed class IosViewController : UIViewController {
    /// <summary>Creates the controller and the view it hosts.</summary>
    internal IosViewController() {
        MetalView = new(UIScreen.MainScreen.Bounds);
    }

    /// <summary>Required by UIKit's unarchiver.</summary>
    /// <param name="handle">The native object.</param>
    internal IosViewController(NativeHandle handle) : base(handle) {
        MetalView = new(UIScreen.MainScreen.Bounds);
    }

    /// <summary>The view Vulkan presents to.</summary>
    internal IosMetalView MetalView { get; }

    /// <summary>
    ///     Hidden. A game draws to the whole screen; the clock over the top of it is the system's
    ///     idea rather than the application's.
    /// </summary>
    public override bool PrefersStatusBarHidden() => true;

    /// <summary>
    ///     Hidden until idle. The home indicator sits over the bottom of the screen and dims itself
    ///     when nothing is touched; suppressing it entirely is not offered, and pretending it is
    ///     would put a white bar through the bottom of every frame.
    /// </summary>
    public override bool PrefersHomeIndicatorAutoHidden => true;

    /// <summary>
    ///     Every orientation the bundle allows. Which ones those are is an <c>Info.plist</c>
    ///     decision belonging to the application, not to the engine, so this defers rather than
    ///     narrowing it — a game that wants landscape only says so where the App Store also reads it.
    /// </summary>
    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() =>
        UIInterfaceOrientationMask.All;

    /// <inheritdoc />
    public override void LoadView() => View = MetalView;

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (disposing) {
            MetalView.Dispose();
        }

        base.Dispose(disposing);
    }
}
