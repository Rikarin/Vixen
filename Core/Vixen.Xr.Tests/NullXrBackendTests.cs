// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Xr.Input;
using Xunit;

namespace Vixen.Xr.Tests;

/// <summary>The session lifecycle and the frame loop, against a headset that is not there.</summary>
public sealed class NullXrBackendTests {
    [Fact]
    public void ABackendWithNoDeviceSaysSoRatherThanThrowing() {
        using var backend = new NullXrBackend { PretendUnavailable = true };

        Assert.False(backend.IsAvailable);
        Assert.False(backend.TryGetSystem(out _));
        Assert.NotEmpty(backend.UnavailableReason);
        Assert.Throws<InvalidOperationException>(
            () => backend.CreateSession(default, new XrSessionOptions())
        );
    }

    [Fact]
    public void ASessionWalksUpToFocusRatherThanStartingThere() {
        // A game that assumes focus on the first frame is caught here rather than on a headset,
        // where the symptom is input being ignored for the first second of every session.
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        var states = new List<XrSessionState> { session.State };

        for (var index = 0; index < 4; index++) {
            session.PollEvents();
            states.Add(session.State);
        }

        Assert.Equal(
            [
                XrSessionState.Idle,
                XrSessionState.Ready,
                XrSessionState.Synchronised,
                XrSessionState.Visible,
                XrSessionState.Focused
            ],
            states
        );
    }

    [Fact]
    public void NoFrameIsBegunBeforeTheSessionIsRunning() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        Assert.False(session.BeginFrame(out _));

        Focus(session);

        Assert.True(session.BeginFrame(out var frame));
        Assert.True(frame.ShouldRender);

        session.EndFrame(in frame, []);
    }

    [Fact]
    public void ASynchronisedSessionSubmitsFramesItDoesNotDraw() {
        // The state that catches people out: running, so frames are compulsory, but not visible, so
        // drawing them is wasted. A game that skips the whole loop here stalls the compositor.
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        session.PollEvents();
        session.PollEvents();

        Assert.Equal(XrSessionState.Synchronised, session.State);
        Assert.True(session.IsRunning);
        Assert.True(session.BeginFrame(out var frame));
        Assert.False(frame.ShouldRender);

        session.EndFrame(in frame, []);
    }

    [Fact]
    public void AFrameLeftOpenIsAnError() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        Focus(session);
        session.BeginFrame(out _);

        Assert.Throws<InvalidOperationException>(() => session.BeginFrame(out _));
    }

    [Fact]
    public void EndingAFrameThatWasNeverBegunIsAnError() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        Focus(session);

        Assert.Throws<InvalidOperationException>(() => session.EndFrame(default, []));
    }

    [Fact]
    public void TheTwoEyesAreActuallyDifferent() {
        // A stereo bug that produces two identical views is invisible on a monitor and immediately
        // obvious in a headset. Asserted here so it is neither.
        using var backend = new NullXrBackend();
        using var session = (NullXrSession)backend.CreateSession(default, new XrSessionOptions());

        Focus(session);
        session.BeginFrame(out var frame);

        var views = session.LocateViews(in frame);

        Assert.Equal(2, views.Length);
        Assert.Equal(
            session.InterpupillaryDistance,
            views[(int)XrEye.Right].Pose.Position.X - views[(int)XrEye.Left].Pose.Position.X,
            4
        );

        session.EndFrame(in frame, []);
    }

    [Fact]
    public void TheEyesFollowTheHeadThroughItsOwnRotation() {
        using var backend = new NullXrBackend();
        using var session = (NullXrSession)backend.CreateSession(default, new XrSessionOptions());

        Focus(session);

        // Turned a quarter turn to the left about Y: the eye separation, which was along X, is now
        // along Z. A rig that added the offset without rotating it would still have it on X.
        session.HeadPose = new XrPose(
            new Vector3(1f, 1.7f, -2f),
            Quaternion.FromAxisAngle(new Vector3(0f, 1f, 0f), MathF.PI / 2f)
        );

        session.BeginFrame(out var frame);

        var views = session.LocateViews(in frame);

        Assert.Equal(1f, views[0].Pose.Position.X, 3);
        Assert.NotEqual(views[0].Pose.Position.Z, views[1].Pose.Position.Z, 3);

        session.EndFrame(in frame, []);
    }

    [Fact]
    public void ASwapchainCyclesThroughItsImages() {
        using var device = new NullDevice();
        using var backend = new NullXrBackend(device);
        using var session = backend.CreateSession(default, new XrSessionOptions());

        using var swapchain = session.CreateSwapchain(
            new XrSwapchainDescription(new Int2(512, 512), Name: "eye")
        );

        Assert.True(swapchain.ImageCount > 1);
        Assert.Equal(-1, swapchain.AcquiredIndex);

        var seen = new List<int>();

        for (var index = 0; index < swapchain.ImageCount + 1; index++) {
            var acquired = swapchain.AcquireImage();

            Assert.True(swapchain.Image(acquired).IsValid);
            seen.Add(acquired);
            swapchain.ReleaseImage();
        }

        Assert.Equal(swapchain.ImageCount, seen.Distinct().Count());
    }

    [Fact]
    public void AnImageCannotBeAcquiredTwiceOrReleasedTwice() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        using var swapchain = session.CreateSwapchain(new XrSwapchainDescription(new Int2(64, 64)));

        swapchain.AcquireImage();

        Assert.Throws<InvalidOperationException>(() => swapchain.AcquireImage());

        swapchain.ReleaseImage();

        Assert.Throws<InvalidOperationException>(swapchain.ReleaseImage);
    }

    [Fact]
    public void SubmittedFramesAreCountedAndEmptyOnesAreNot() {
        using var device = new NullDevice();
        using var backend = new NullXrBackend(device);
        using var session = (NullXrSession)backend.CreateSession(default, new XrSessionOptions());

        Focus(session);

        using var swapchain = session.CreateSwapchain(new XrSwapchainDescription(new Int2(64, 64)));

        session.BeginFrame(out var first);
        session.EndFrame(in first, []);

        session.BeginFrame(out var second);
        var views = session.LocateViews(in second);

        session.EndFrame(
            in second,
            [
                new XrCompositionView(swapchain, 0, XrViewport.Covering(swapchain.Size), views[0].Pose, views[0].Fov),
                new XrCompositionView(swapchain, 0, XrViewport.Covering(swapchain.Size), views[1].Pose, views[1].Fov)
            ]
        );

        Assert.Equal(2, session.FrameCount);
        Assert.Equal(1, session.RenderedFrames);
    }

    [Fact]
    public void AnUnfocusedSessionsActionsAreInactive() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        var set = new XrActionSet("gameplay");
        var fire = set.CreateAction("fire", XrActionType.Boolean);

        session.AttachActionSets([set]);

        session.PollEvents();
        session.PollEvents();
        session.PollEvents();

        Assert.Equal(XrSessionState.Visible, session.State);

        session.SyncActions();

        Assert.False(fire.State(XrHand.Right).IsActive);

        session.PollEvents();
        session.SyncActions();

        Assert.True(fire.State(XrHand.Right).IsActive);
    }

    [Fact]
    public void AnInactiveSetIsNotSynced() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        var set = new XrActionSet("menu") { IsActive = false };
        var confirm = set.CreateAction("confirm", XrActionType.Boolean);

        session.AttachActionSets([set]);
        Focus(session);
        session.SyncActions();

        Assert.False(confirm.State(XrHand.Left).IsActive);
    }

    [Fact]
    public void ActionSetsAreAttachedOnceAndFrozenAfterwards() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        var set = new XrActionSet("gameplay");

        set.CreateAction("fire", XrActionType.Boolean);
        session.AttachActionSets([set]);

        Assert.True(set.IsAttached);
        Assert.Throws<InvalidOperationException>(() => set.CreateAction("late", XrActionType.Boolean));
        Assert.Throws<InvalidOperationException>(() => session.AttachActionSets([set]));
    }

    [Fact]
    public void HapticsAreRecordedRatherThanIgnored() {
        using var backend = new NullXrBackend();
        using var session = (NullXrSession)backend.CreateSession(default, new XrSessionOptions());

        var set = new XrActionSet("gameplay");
        var rumble = set.CreateAction("rumble", XrActionType.Haptic);

        session.AttachActionSets([set]);
        Focus(session);
        session.ApplyHaptics(rumble, XrHand.Left, XrHapticPulse.Click);

        Assert.Single(session.Haptics);
        Assert.Equal(XrHand.Left, session.Haptics[0].Hand);
    }

    [Fact]
    public void RequestingAnExitWalksTheSessionDownRatherThanStoppingDead() {
        using var backend = new NullXrBackend();
        using var session = backend.CreateSession(default, new XrSessionOptions());

        Focus(session);
        session.RequestExit();

        Assert.True(session.PollEvents());
        Assert.Equal(XrSessionState.Stopping, session.State);
        Assert.False(session.PollEvents());
        Assert.Equal(XrSessionState.Exiting, session.State);
        Assert.False(session.IsRunning);
    }

    static void Focus(IXrSession session) {
        for (var index = 0; index < 8 && session.State != XrSessionState.Focused; index++) {
            session.PollEvents();
        }
    }
}
