// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CoreAnimation;
using Foundation;
using UIKit;

namespace Vixen.Platform.Ios;

/// <summary>
///     The entry point of an iOS game: owns the platform, translates UIKit's lifecycle, and drives
///     one frame per display refresh.
/// </summary>
/// <remarks>
///     <para>
///         <b>Derive from this and override <see cref="Start" />.</b> A game's iOS head is then two
///         lines — build a <c>VixenApplication</c> on the platform it is handed, return it — and
///         everything else is here. It deliberately knows nothing about <c>Vixen.App</c>: this
///         assembly is in <c>Platform/</c> and the host is in <c>Tools/</c>, so the frame callback is
///         a delegate rather than a reference.
///     </para>
///     <para>
///         <b>Why a display link rather than a loop.</b> <c>UIApplicationMain</c> never returns, so
///         there is nowhere to put a loop, and a background thread is not an option: a UIKit view
///         may only be touched from the main thread, and iOS forbids GPU submission entirely while
///         the application is suspended. <c>CADisplayLink</c> calls back on the main thread in step
///         with the display's refresh, which is what the frame limiter would have been approximating
///         anyway — with the advantage that ProMotion's variable rate is handled by the system.
///     </para>
///     <para>
///         <b>The link is paused across suspension, not merely ignored.</b> A frame that runs after
///         <c>applicationDidEnterBackground</c> is a GPU command submitted while the application is
///         not entitled to submit one, and the penalty is the system killing the process. Pausing is
///         the only correct response, and it is why the lifecycle callbacks below do more than post
///         an event.
///     </para>
/// </remarks>
public abstract class IosApplicationHost : UIApplicationDelegate {
    CADisplayLink? link;
    Action? frame;

    /// <summary>The platform, once the application has finished launching.</summary>
    public IosPlatform Platform { get; private set; } = null!;

    /// <summary>How many times per second to ask for a frame; zero for the display's own rate.</summary>
    /// <remarks>
    ///     Set before <see cref="Start" /> returns. Zero means the display link's default, which is
    ///     the panel's native rate and is what a game should normally want; a lower number is how a
    ///     game trades smoothness for battery and heat, which on this platform is a real decision
    ///     rather than a preference.
    /// </remarks>
    protected int PreferredFramesPerSecond { get; set; }

    /// <summary>
    ///     Builds the game on the platform provided, and returns what to call once per frame.
    /// </summary>
    /// <param name="platform">The platform, already constructed and with no window yet.</param>
    /// <returns>The per-frame callback — in practice <c>application.RunFrame</c>.</returns>
    protected abstract Action Start(IosPlatform platform);

    /// <summary>Called once per display refresh, before the frame.</summary>
    /// <remarks>
    ///     Empty by default. Exists so a subclass can do work that must happen on the main thread
    ///     ahead of the frame without having to replace the whole callback.
    /// </remarks>
    protected virtual void OnBeforeFrame() { }

    /// <inheritdoc />
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) {
        Platform = new();
        frame = Start(Platform);

        Platform.MobileLifecycle.EnterForeground();

        link = CADisplayLink.Create(OnFrame);

        if (PreferredFramesPerSecond > 0) {
            // PreferredFrameRateRange, not the PreferredFramesPerSecond property, which iOS 15
            // deprecated in favour of it. The range form is what ProMotion needs: a device that can
            // vary between 10 and 120 Hz is told a floor, a ceiling and what to aim for, rather than
            // a single number it has to interpret.
            link.PreferredFrameRateRange = new() {
                Minimum = PreferredFramesPerSecond,
                Maximum = PreferredFramesPerSecond,
                Preferred = PreferredFramesPerSecond
            };
        }

        link.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Frontmost again. The link resumes here rather than in <c>WillEnterForeground</c>, so no
    ///     frame runs while the application is still on its way in.
    /// </remarks>
    public override void OnActivated(UIApplication application) {
        Platform.MobileLifecycle.EnterForeground();

        if (link is not null) {
            link.Paused = false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A notification shade, the app switcher, an incoming call. Rendering stops immediately;
    ///     nothing has been destroyed. Touches are released because UIKit does not reliably cancel
    ///     them on the way out, and a finger left down across this is one that is still down when
    ///     the application comes back.
    /// </remarks>
    public override void OnResignActivation(UIApplication application) {
        if (link is not null) {
            link.Paused = true;
        }

        Platform.ReleaseTouches();
        Platform.MobileLifecycle.EnterBackground();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Off screen. From here the GPU may not be touched at all, which is what
    ///     <see cref="PlatformEventKind.Suspending" /> tells the renderer.
    /// </remarks>
    public override void DidEnterBackground(UIApplication application) =>
        Platform.MobileLifecycle.Suspend();

    /// <inheritdoc />
    public override void WillTerminate(UIApplication application) {
        if (link is not null) {
            link.Paused = true;
        }

        Platform.MobileLifecycle.Stopping();

        // The last frame is run deliberately: the loop is what notices IsStopping and calls the
        // game's shutdown, and iOS gives no second chance after this returns.
        RunFrame();

        link?.Invalidate();
        link?.Dispose();
        link = null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Reported as <see cref="MemoryPressure.Warning" />, not Critical. iOS has one warning and
    ///     no severity: the next step after it is termination, with no further notice. Calling it
    ///     Critical would leave nothing for a platform that does distinguish, and would push
    ///     subsystems into their most destructive response on the first nudge.
    /// </remarks>
    public override void ReceiveMemoryWarning(UIApplication application) =>
        Platform.MobileLifecycle.ReportMemoryPressure(MemoryPressure.Warning);

    void OnFrame() {
        OnBeforeFrame();
        RunFrame();
    }

    void RunFrame() {
        // Nothing is caught here. An exception that escapes a frame is the frame loop's business —
        // it logs, tears down and rethrows or returns an exit code by build variant — and catching
        // it in the display-link callback would replace that with a crash inside UIKit's run loop,
        // where the stack says nothing about the game.
        frame?.Invoke();
    }
}
