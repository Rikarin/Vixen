// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Xr.Input;

namespace Vixen.Xr;

/// <summary>Where a session is in its lifecycle.</summary>
/// <remarks>
///     <para>
///         <b>This is a state machine and not a flag, because the runtime drives it.</b> A game does
///         not decide that it is visible; the user putting the headset down decides that, and the
///         runtime says so through an event. Modelling it as "is the session running" and finding out
///         later is how a game ends up rendering at full rate into a compositor that is showing
///         somebody the system menu.
///     </para>
///     <para>
///         The states are OpenXR's, minus the ones a game cannot act on. What matters is the
///         boundaries: <see cref="Synchronised" /> is when frames must be submitted and
///         <see cref="Visible" /> is when they are worth drawing, so a session that is synchronised
///         and not visible still calls <c>BeginFrame</c>/<c>EndFrame</c> and skips the rendering
///         between them.
///     </para>
/// </remarks>
public enum XrSessionState : byte {
    /// <summary>Created and doing nothing. No frames.</summary>
    Idle = 0,

    /// <summary>The runtime is ready for the session to begin.</summary>
    Ready = 1,

    /// <summary>Running. Frames must be submitted, whether or not anything is drawn in them.</summary>
    Synchronised = 2,

    /// <summary>Running and on screen, but not receiving input — a system menu is over it.</summary>
    Visible = 3,

    /// <summary>Running, on screen and receiving input. The ordinary state of a game being played.</summary>
    Focused = 4,

    /// <summary>The runtime has asked the session to stop. Stop submitting frames and end it.</summary>
    Stopping = 5,

    /// <summary>The session is over. Nothing more will work.</summary>
    Exiting = 6,

    /// <summary>
    ///     The device has gone — unplugged, crashed, taken over. Everything must be torn down and, if
    ///     the game wants to carry on, created again.
    /// </summary>
    Lost = 7
}

/// <summary>What one frame's timing is.</summary>
/// <param name="PredictedDisplayTime">
///     When the frame will be shown, in the runtime's own clock. Opaque, and the thing to pass back
///     when asking where the head will be — not a number to do arithmetic on.
/// </param>
/// <param name="PredictedDisplayPeriod">How long a frame is expected to last.</param>
/// <param name="ShouldRender">
///     Whether drawing anything is worth it. False while the session is synchronised but not visible:
///     the frame still has to be submitted, with no layers.
/// </param>
public readonly record struct XrFrameState(
    long PredictedDisplayTime,
    TimeSpan PredictedDisplayPeriod,
    bool ShouldRender
);

/// <summary>A rectangle of an eye buffer, in pixels.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">How wide.</param>
/// <param name="Height">How tall.</param>
/// <remarks>
///     Integers, unlike <c>Vixen.Core.Mathematics.Rectangle</c>, which is a float rectangle for UI
///     layout. A swapchain image is a grid of texels and half of one is not a thing a compositor can
///     be told about — the runtime's own type is integral too, and rounding at the boundary is where
///     a one-pixel seam down the middle of a stereo pair comes from.
/// </remarks>
public readonly record struct XrViewport(int X, int Y, int Width, int Height) {
    /// <summary>The whole of a swapchain image.</summary>
    /// <param name="size">Its size.</param>
    /// <returns>The viewport.</returns>
    public static XrViewport Covering(Int2 size) => new(0, 0, size.X, size.Y);
}

/// <summary>One eye's contribution to the composited frame.</summary>
/// <param name="Swapchain">Where it was rendered.</param>
/// <param name="ImageArrayIndex">Which array layer of the acquired image, for a multiview target.</param>
/// <param name="Viewport">Which part of the image, for two eyes rendered side by side into one.</param>
/// <param name="Pose">The pose the eye was actually rendered from.</param>
/// <param name="Fov">The frustum it was actually rendered with.</param>
/// <remarks>
///     <b>The pose and the field of view are submitted, not assumed.</b> The compositor reprojects
///     the image to wherever the head has got to by the time it is displayed, and it can only do that
///     if it is told what the image was drawn for. Submitting last frame's pose, or the pose that was
///     predicted rather than the one used, is a subtle and permanent swim in the image.
/// </remarks>
public readonly record struct XrCompositionView(
    IXrSwapchain Swapchain,
    int ImageArrayIndex,
    XrViewport Viewport,
    XrPose Pose,
    XrFieldOfView Fov
);

/// <summary>How to create a session.</summary>
public readonly record struct XrSessionOptions() {
    /// <summary>What poses are reported relative to.</summary>
    public XrReferenceSpace ReferenceSpace { get; init; } = XrReferenceSpace.Stage;

    /// <summary>Whether the compositor should blend the scene with the real world.</summary>
    /// <remarks>
    ///     False is the opaque case — virtual reality, where the runtime shows nothing of the room.
    ///     True asks for whatever passthrough the device supports, and a device with none simply does
    ///     not offer the blend mode, which the backend reports rather than failing over.
    /// </remarks>
    public bool PreferPassthrough { get; init; }
}

/// <summary>A running connection to a headset: frames, poses and input.</summary>
/// <remarks>
///     <para>
///         <b>The frame loop is the runtime's, not the game's.</b> <see cref="BeginFrame" /> blocks
///         until the runtime says it is time to start the next frame — that is how a compositor
///         paces an application to the display and how it inserts the latency it wants — so an XR
///         game's outer loop is driven from here rather than from a timer. A game that renders on its
///         own schedule and submits whenever it finishes gets judder that no amount of frame rate
///         fixes.
///     </para>
///     <para>
///         <b>Poses are predicted for the display time.</b> <see cref="LocateViews" /> is asked for a
///         moment in the future, and the answer is where the runtime thinks the head will be then.
///         Locating with the wrong time — this instant, or last frame's — is a constant lag that
///         looks exactly like a slow tracker.
///     </para>
///     <para>
///         <b>Every frame between <see cref="BeginFrame" /> and <see cref="EndFrame" /> must be
///         closed.</b> Including the ones where nothing is drawn, and including the ones where the
///         game threw: a runtime that is waiting for a frame that never ends stops the compositor for
///         everybody.
///     </para>
/// </remarks>
public interface IXrSession : IDisposable {
    /// <summary>Where the session is in its lifecycle.</summary>
    XrSessionState State { get; }

    /// <summary>Whether frames should be submitted at all.</summary>
    bool IsRunning => State is XrSessionState.Synchronised or XrSessionState.Visible or XrSessionState.Focused;

    /// <summary>Whether the game should be reading input and simulating.</summary>
    bool HasFocus => State == XrSessionState.Focused;

    /// <summary>How many views a frame has.</summary>
    int ViewCount { get; }

    /// <summary>What the runtime said about the device.</summary>
    XrSystemInfo System { get; }

    /// <summary>What poses are reported relative to.</summary>
    XrReferenceSpace ReferenceSpace { get; }

    /// <summary>The views located for the current frame, one per eye.</summary>
    /// <remarks>Valid between <see cref="LocateViews" /> and the next <see cref="BeginFrame" />.</remarks>
    ReadOnlySpan<XrView> Views { get; }

    /// <summary>Creates a swapchain for eye buffers.</summary>
    /// <param name="description">What to create.</param>
    /// <returns>The swapchain.</returns>
    IXrSwapchain CreateSwapchain(in XrSwapchainDescription description);

    /// <summary>Drains the runtime's event queue, moving <see cref="State" /> on.</summary>
    /// <returns>Whether the session is still usable.</returns>
    /// <remarks>
    ///     Called once a frame, before anything else. It is what turns "the user took the headset
    ///     off" into a state a game can read, and it is the only thing that ever changes
    ///     <see cref="State" />.
    /// </remarks>
    bool PollEvents();

    /// <summary>Waits for the runtime to want the next frame, and begins it.</summary>
    /// <param name="frame">Its timing, and whether drawing is worth it.</param>
    /// <returns>Whether a frame was begun. False when the session is not running.</returns>
    bool BeginFrame(out XrFrameState frame);

    /// <summary>Asks where the eyes will be when the frame is displayed.</summary>
    /// <param name="frame">The frame, for its display time.</param>
    /// <returns>One view per eye, also available afterwards from <see cref="Views" />.</returns>
    ReadOnlySpan<XrView> LocateViews(in XrFrameState frame);

    /// <summary>Locates something the runtime tracks, in the session's reference space.</summary>
    /// <param name="space">What to locate.</param>
    /// <param name="frame">The frame, for its display time.</param>
    /// <param name="pose">Where it is.</param>
    /// <returns>Whether the pose is tracked. False means <paramref name="pose" /> is stale or unknown.</returns>
    bool LocateSpace(XrReferenceSpace space, in XrFrameState frame, out XrPose pose);

    /// <summary>Submits the frame.</summary>
    /// <param name="frame">The frame begun.</param>
    /// <param name="views">
    ///     One per eye, or empty for a frame with nothing to show — which is what a session that is
    ///     running but not visible submits.
    /// </param>
    void EndFrame(in XrFrameState frame, ReadOnlySpan<XrCompositionView> views);

    /// <summary>Attaches the action sets, after which they cannot be changed.</summary>
    /// <param name="sets">Every set the game will ever use.</param>
    /// <remarks>
    ///     Once, and before the first <see cref="SyncActions" />. OpenXR makes this permanent for the
    ///     life of the session because the runtime binds actions to physical inputs at this point —
    ///     which is also when the user's own rebinding, if the runtime offers it, is applied.
    /// </remarks>
    void AttachActionSets(ReadOnlySpan<XrActionSet> sets);

    /// <summary>Updates every attached action's state for this frame.</summary>
    /// <remarks>
    ///     Does nothing unless the session has focus: an unfocused session's input belongs to whatever
    ///     took the focus, and a runtime reports every action inactive rather than leaking it.
    /// </remarks>
    void SyncActions();

    /// <summary>Plays a haptic pulse.</summary>
    /// <param name="action">The haptic action to play it on.</param>
    /// <param name="hand">Which hand's binding of it.</param>
    /// <param name="request">What to play.</param>
    void ApplyHaptics(XrAction action, XrHand hand, in XrHapticPulse request);

    /// <summary>Asks the runtime to end the session.</summary>
    /// <remarks>
    ///     A request rather than a teardown: the runtime replies with the state changes that walk the
    ///     session down to <see cref="XrSessionState.Exiting" />, and disposing before then is what
    ///     leaves a compositor waiting.
    /// </remarks>
    void RequestExit();
}
