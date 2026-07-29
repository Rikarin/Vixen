// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Xr.Input;

namespace Vixen.Xr;

/// <summary>A headset that is not there, simulated well enough to develop against.</summary>
/// <remarks>
///     <para>
///         <b>What <c>NullAudioBackend</c> and <c>NullDevice</c> are, for XR.</b> Every layer above
///         this — the frame loop, the stereo views, the action sets, the ECS bridge — is exercised
///         without a runtime, a headset, or a person wearing one. That matters more here than
///         anywhere else in the engine: XR is the one subsystem where the hardware is genuinely
///         unavailable on a CI runner and expensive to keep on a desk.
///     </para>
///     <para>
///         <b>It is a simulation, not a stub.</b> The state machine walks through the same sequence a
///         real runtime does, the two eyes are offset by an interpupillary distance, the poses are
///         whatever the caller says the head is doing, and frames are paced by a counter rather than
///         by a clock — so a test can run four hundred frames in a millisecond and get exactly the
///         timings a headset would have produced.
///     </para>
/// </remarks>
public sealed class NullXrBackend : IXrBackend {
    readonly IGraphicsDevice? device;

    /// <summary>Creates a simulated backend.</summary>
    /// <param name="device">
    ///     The device its swapchains allocate on, or <see langword="null" /> for swapchains whose
    ///     images are null handles — which is enough for anything that is not actually rendering.
    /// </param>
    /// <param name="system">What to claim the headset is, or <see langword="null" /> for a plausible one.</param>
    public NullXrBackend(IGraphicsDevice? device = null, XrSystemInfo? system = null) {
        this.device = device;
        System = system ?? DefaultSystem;
    }

    /// <summary>A two-eyed headset at a resolution nobody will mistake for a real product's.</summary>
    public static XrSystemInfo DefaultSystem => new(
        "Vixen Null Headset",
        2,
        new Int2(1024, 1024),
        new Int2(4096, 4096),
        1,
        HasPositionTracking: true
    );

    /// <summary>What it claims to be.</summary>
    public XrSystemInfo System { get; }

    /// <summary>Whether it should pretend there is no headset attached.</summary>
    /// <remarks>
    ///     The other half of what a null backend is for: a test that asserts a game degrades
    ///     gracefully needs a backend that says no, and the alternative is not constructing one at all
    ///     — which tests a different path.
    /// </remarks>
    public bool PretendUnavailable { get; set; }

    /// <inheritdoc />
    public string Name => "Null";

    /// <inheritdoc />
    public bool IsAvailable => !PretendUnavailable;

    /// <inheritdoc />
    public string UnavailableReason => PretendUnavailable ? "The null backend was asked to pretend it had no device." : "";

    /// <inheritdoc />
    public bool TryGetSystem(out XrSystemInfo system) {
        system = System;

        return IsAvailable;
    }

    /// <inheritdoc />
    public XrVulkanRequirements GetVulkanRequirements() => XrVulkanRequirements.None;

    /// <inheritdoc />
    public nint GetVulkanPhysicalDevice(nint vulkanInstance) => 0;

    /// <inheritdoc />
    public IXrSession CreateSession(
        in XrVulkanBinding binding,
        in XrSessionOptions options,
        IXrImageImporter? importer = null
    ) {
        if (!IsAvailable) {
            throw new InvalidOperationException(UnavailableReason);
        }

        return new NullXrSession(device, System, options);
    }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>A session against a headset that is not there.</summary>
public sealed class NullXrSession : IXrSession {
    readonly IGraphicsDevice? device;
    readonly List<NullXrSwapchain> swapchains = [];
    readonly List<XrActionSet> attached = [];
    readonly XrView[] views;

    bool exitRequested;
    long frameIndex;
    bool frameOpen;

    internal NullXrSession(IGraphicsDevice? device, XrSystemInfo system, in XrSessionOptions options) {
        this.device = device;
        System = system;
        ReferenceSpace = options.ReferenceSpace;
        views = new XrView[Math.Max(1, system.ViewCount)];
    }

    /// <summary>How far apart the two eyes are, in metres.</summary>
    /// <remarks>
    ///     63 mm, which is the adult median and what every headset defaults to. It is settable because
    ///     the thing most worth testing about a stereo path is that the two eyes are actually
    ///     different — a bug that produces identical views is invisible on a monitor and immediately
    ///     obvious in a headset.
    /// </remarks>
    public float InterpupillaryDistance { get; set; } = 0.063f;

    /// <summary>Where the simulated head is. Move it to simulate a player moving.</summary>
    public XrPose HeadPose { get; set; } = XrPose.Identity;

    /// <summary>What both eyes' frustums are.</summary>
    public XrFieldOfView FieldOfView { get; set; } =
        XrFieldOfView.Symmetric(MathUtil.DegreesToRadians(100f), MathUtil.DegreesToRadians(96f));

    /// <summary>How long a simulated frame lasts.</summary>
    public TimeSpan FramePeriod { get; set; } = TimeSpan.FromSeconds(1d / 90);

    /// <summary>How many frames have been begun.</summary>
    public long FrameCount => frameIndex;

    /// <summary>How many frames have been submitted with at least one layer.</summary>
    public long RenderedFrames { get; private set; }

    /// <summary>The sets a game attached.</summary>
    public IReadOnlyList<XrActionSet> AttachedActionSets => attached;

    /// <summary>Every haptic pulse asked for, in order, so a test can assert one happened.</summary>
    public List<(XrAction Action, XrHand Hand, XrHapticPulse Pulse)> Haptics { get; } = [];

    /// <inheritdoc />
    public XrSessionState State { get; private set; } = XrSessionState.Idle;

    /// <inheritdoc />
    public int ViewCount => views.Length;

    /// <inheritdoc />
    public XrSystemInfo System { get; }

    /// <inheritdoc />
    public XrReferenceSpace ReferenceSpace { get; }

    /// <inheritdoc />
    public ReadOnlySpan<XrView> Views => views;

    /// <inheritdoc />
    public IXrSwapchain CreateSwapchain(in XrSwapchainDescription description) {
        var swapchain = new NullXrSwapchain(device, in description);

        swapchains.Add(swapchain);

        return swapchain;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Walks one step of the same sequence a runtime produces — idle, ready, synchronised,
    ///     visible, focused — so that a game which waits for focus before starting actually waits, and
    ///     a game which assumes focus on the first frame is caught here rather than on a headset.
    /// </remarks>
    public bool PollEvents() {
        if (exitRequested) {
            State = State switch {
                XrSessionState.Exiting => XrSessionState.Exiting,
                XrSessionState.Stopping => XrSessionState.Exiting,
                _ => XrSessionState.Stopping
            };

            return State != XrSessionState.Exiting;
        }

        State = State switch {
            XrSessionState.Idle => XrSessionState.Ready,
            XrSessionState.Ready => XrSessionState.Synchronised,
            XrSessionState.Synchronised => XrSessionState.Visible,
            XrSessionState.Visible => XrSessionState.Focused,
            _ => State
        };

        return true;
    }

    /// <inheritdoc />
    public bool BeginFrame(out XrFrameState frame) {
        frame = default;

        if (!((IXrSession)this).IsRunning) {
            return false;
        }

        if (frameOpen) {
            throw new InvalidOperationException(
                "A frame was begun while one was still open. Every BeginFrame must be closed by an "
                + "EndFrame, including the ones that draw nothing."
            );
        }

        frameOpen = true;

        frame = new XrFrameState(
            frameIndex * FramePeriod.Ticks * 100,
            FramePeriod,
            State is XrSessionState.Visible or XrSessionState.Focused
        );

        frameIndex++;

        return true;
    }

    /// <inheritdoc />
    public ReadOnlySpan<XrView> LocateViews(in XrFrameState frame) {
        var offset = InterpupillaryDistance * 0.5f;

        for (var index = 0; index < views.Length; index++) {
            var sign = index == (int)XrEye.Left ? -1f : 1f;
            var local = new Vector3(sign * offset, 0f, 0f);

            views[index] = new XrView(
                new XrPose(
                    HeadPose.Position + Quaternion.Transform(local, HeadPose.Orientation),
                    HeadPose.Orientation
                ),
                FieldOfView
            );
        }

        return views;
    }

    /// <inheritdoc />
    public bool LocateSpace(XrReferenceSpace space, in XrFrameState frame, out XrPose pose) {
        pose = space == XrReferenceSpace.View ? HeadPose : XrPose.Identity;

        return true;
    }

    /// <inheritdoc />
    public void EndFrame(in XrFrameState frame, ReadOnlySpan<XrCompositionView> views) {
        if (!frameOpen) {
            throw new InvalidOperationException("A frame was ended that had not been begun.");
        }

        frameOpen = false;

        if (!views.IsEmpty) {
            RenderedFrames++;
        }
    }

    /// <inheritdoc />
    public void AttachActionSets(ReadOnlySpan<XrActionSet> sets) {
        if (attached.Count > 0) {
            throw new InvalidOperationException("Action sets have already been attached to this session.");
        }

        foreach (var set in sets) {
            set.MarkAttached();
            attached.Add(set);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Publishes nothing but activity: there is no hardware to read. What it does do is honour the
    ///     rule that matters — an unfocused session's actions are inactive — so that a game which
    ///     reads input while a system menu is up is caught without a headset.
    /// </remarks>
    public void SyncActions() {
        foreach (var set in attached) {
            foreach (var action in set.Actions) {
                if (!HasFocusInternal || !set.IsActive || action.Type == XrActionType.Haptic) {
                    action.Deactivate();

                    continue;
                }

                var state = action.Type == XrActionType.Pose
                    ? new XrActionState(IsActive: true, Pose: HeadPose, IsTracked: true)
                    : new XrActionState(IsActive: true);

                action.Publish(XrHand.Left, in state);
                action.Publish(XrHand.Right, in state);
            }
        }
    }

    /// <inheritdoc />
    public void ApplyHaptics(XrAction action, XrHand hand, in XrHapticPulse request) =>
        Haptics.Add((action, hand, request));

    /// <inheritdoc />
    public void RequestExit() => exitRequested = true;

    /// <inheritdoc />
    public void Dispose() {
        foreach (var swapchain in swapchains) {
            swapchain.Dispose();
        }

        swapchains.Clear();
        State = XrSessionState.Exiting;
    }

    bool HasFocusInternal => State == XrSessionState.Focused;
}

/// <summary>Eye buffers with no runtime behind them.</summary>
sealed class NullXrSwapchain : IXrSwapchain {
    readonly IGraphicsDevice? device;
    readonly TextureHandle[] images;
    readonly TextureViewHandle[] views;

    internal NullXrSwapchain(IGraphicsDevice? device, in XrSwapchainDescription description) {
        this.device = device;
        Size = description.Size;
        Format = description.Format;
        ArrayLayers = Math.Max(1, description.ArrayLayers);

        // Three, which is what every runtime this engine has met hands out, so that a game whose
        // logic depends on the count meets the same number here as on hardware.
        images = new TextureHandle[3];
        views = new TextureViewHandle[3];

        if (device is null) {
            return;
        }

        for (var index = 0; index < images.Length; index++) {
            images[index] = device.CreateTexture(
                new TextureDescription(
                    description.Format,
                    description.Size.X,
                    description.Size.Y,
                    description.Usage,
                    ArrayLayers: ArrayLayers,
                    SampleCount: Math.Max(1, description.SampleCount),
                    Name: $"{description.Name} image {index}"
                )
            );

            views[index] = device.CreateTextureView(images[index]);
        }
    }

    public Int2 Size { get; }

    public PixelFormat Format { get; }

    public int ArrayLayers { get; }

    public int ImageCount => images.Length;

    public int AcquiredIndex { get; private set; } = -1;

    public TextureHandle Image(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, images.Length);

        return images[index];
    }

    public TextureViewHandle View(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, views.Length);

        return views[index];
    }

    public int AcquireImage() {
        if (AcquiredIndex >= 0) {
            throw new InvalidOperationException("An image is already acquired from this swapchain.");
        }

        AcquiredIndex = (int)(Acquisitions++ % images.Length);

        return AcquiredIndex;
    }

    public void ReleaseImage() {
        if (AcquiredIndex < 0) {
            throw new InvalidOperationException("No image is acquired from this swapchain.");
        }

        AcquiredIndex = -1;
    }

    public void Dispose() {
        if (device is null) {
            return;
        }

        for (var index = 0; index < images.Length; index++) {
            if (views[index].IsValid) {
                device.Destroy(views[index]);
            }

            if (images[index].IsValid) {
                device.Destroy(images[index]);
            }
        }
    }

    long Acquisitions { get; set; }
}
