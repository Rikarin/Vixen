// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using Vixen.Core.Mathematics;
using Vixen.Xr.Input;
using NativeAction = Silk.NET.OpenXR.Action;
using NativeActionSet = Silk.NET.OpenXR.ActionSet;
using NativeQuaternion = Vixen.Core.Mathematics.Quaternion;
using NativeVector3 = Vixen.Core.Mathematics.Vector3;

namespace Vixen.Xr.OpenXR;

/// <summary>A running OpenXR session: the frame loop, the poses and the input.</summary>
/// <remarks>
///     <para>
///         <b>The runtime paces the frame, and that is the single most important thing about this
///         class.</b> <see cref="BeginFrame" /> calls <c>xrWaitFrame</c>, which blocks until the
///         compositor wants the next frame — that is how it controls latency and how it throttles an
///         application that is running ahead. A game that renders on its own schedule and submits
///         when it happens to finish gets judder that no frame rate fixes.
///     </para>
///     <para>
///         <b>Every begun frame is ended, including the ones that draw nothing.</b> A runtime waiting
///         for a frame that never arrives stalls the compositor for the whole system, not just for
///         this process.
///     </para>
/// </remarks>
public sealed unsafe class OpenXrSession : IXrSession {
    readonly Dictionary<(XrAction Action, XrHand Hand), Space> actionSpaces = [];
    readonly List<XrActionSet> attached = [];
    readonly XR api;
    readonly OpenXrBackend backend;
    readonly IXrImageImporter? importer;
    readonly List<NativeActionSet> nativeSets = [];
    readonly List<OpenXrSwapchain> swapchains = [];
    readonly XrView[] views;

    EnvironmentBlendMode blendMode = EnvironmentBlendMode.Opaque;
    bool disposed;
    bool frameOpen;
    Space referenceSpace;
    Session session;
    bool sessionRunning;

    internal OpenXrSession(
        OpenXrBackend backend,
        in XrVulkanBinding binding,
        in XrSessionOptions options,
        IXrImageImporter? importer
    ) {
        this.backend = backend;
        this.importer = importer;
        api = backend.Api;
        System = backend.System;
        ReferenceSpace = options.ReferenceSpace;
        views = new XrView[Math.Max(1, System.ViewCount)];

        var graphics = new GraphicsBindingVulkanKHR {
            Type = StructureType.GraphicsBindingVulkanKhr,
            Instance = new VkHandle(binding.Instance),
            PhysicalDevice = new VkHandle(binding.PhysicalDevice),
            Device = new VkHandle(binding.Device),
            QueueFamilyIndex = binding.QueueFamilyIndex,
            QueueIndex = binding.QueueIndex
        };

        var create = new SessionCreateInfo {
            Type = StructureType.SessionCreateInfo,
            Next = &graphics,
            SystemId = backend.SystemId
        };

        Session created;

        OpenXrResult.Check(api.CreateSession(backend.Handle, &create, &created), "xrCreateSession");
        session = created;

        try {
            referenceSpace = CreateReferenceSpace(options.ReferenceSpace);
            blendMode = ChooseBlendMode(options.PreferPassthrough);
        } catch {
            api.DestroySession(session);
            session = default;

            throw;
        }
    }

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

    /// <summary>Whether the compositor is blending the scene with the room.</summary>
    public bool IsPassthrough => blendMode != EnvironmentBlendMode.Opaque;

    internal Session Handle => session;

    internal XR Api => api;

    internal Microsoft.Extensions.Logging.ILogger? BackendLogger => backend.Logger;

    /// <inheritdoc />
    public IXrSwapchain CreateSwapchain(in XrSwapchainDescription description) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var swapchain = new OpenXrSwapchain(this, importer, in description);

        swapchains.Add(swapchain);

        return swapchain;
    }

    /// <inheritdoc />
    public bool PollEvents() {
        ObjectDisposedException.ThrowIf(disposed, this);

        var buffer = new EventDataBuffer { Type = StructureType.EventDataBuffer };

        while (true) {
            buffer.Type = StructureType.EventDataBuffer;
            buffer.Next = null;

            var result = api.PollEvent(backend.Handle, &buffer);

            if (result == Result.EventUnavailable) {
                return State is not (XrSessionState.Exiting or XrSessionState.Lost);
            }

            OpenXrResult.Check(result, "xrPollEvent");

            switch (buffer.Type) {
                case StructureType.EventDataSessionStateChanged: {
                    var changed = *(EventDataSessionStateChanged*)&buffer;

                    OnStateChanged(changed.State);

                    break;
                }

                case StructureType.EventDataInstanceLossPending:
                    if (backend.Logger is { } lossLogger) {
                        OpenXrLog.InstanceLossPending(lossLogger);
                    }

                    State = XrSessionState.Lost;

                    return false;

                case StructureType.EventDataEventsLost: {
                    var lost = *(EventDataEventsLost*)&buffer;

                    if (backend.Logger is { } lostLogger) {
                        OpenXrLog.EventsLost(lostLogger, (int)lost.LostEventCount);
                    }

                    break;
                }

                case StructureType.EventDataInteractionProfileChanged:
                    if (backend.Logger is { } profileLogger) {
                        OpenXrLog.InteractionProfileChanged(profileLogger);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <inheritdoc />
    public bool BeginFrame(out XrFrameState frame) {
        ObjectDisposedException.ThrowIf(disposed, this);

        frame = default;

        if (!sessionRunning) {
            return false;
        }

        if (frameOpen) {
            throw new InvalidOperationException(
                "A frame was begun while one was still open. Every BeginFrame must be closed by an "
                + "EndFrame, including the ones that draw nothing."
            );
        }

        var wait = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        var state = new FrameState { Type = StructureType.FrameState };

        OpenXrResult.Check(api.WaitFrame(session, &wait, &state), "xrWaitFrame");

        var begin = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };

        OpenXrResult.Check(api.BeginFrame(session, &begin), "xrBeginFrame");
        frameOpen = true;

        // Kept because a pose action has to be located at a time, and the only time that means
        // anything is the one this frame will be displayed at. See ReadPose.
        LastDisplayTime = state.PredictedDisplayTime;

        frame = new XrFrameState(
            state.PredictedDisplayTime,
            TimeSpan.FromTicks(state.PredictedDisplayPeriod / 100),
            state.ShouldRender != 0
        );

        return true;
    }

    /// <inheritdoc />
    public ReadOnlySpan<XrView> LocateViews(in XrFrameState frame) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var locate = new ViewLocateInfo {
            Type = StructureType.ViewLocateInfo,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo,
            DisplayTime = frame.PredictedDisplayTime,
            Space = referenceSpace
        };

        var state = new ViewState { Type = StructureType.ViewState };
        var located = new View[views.Length];

        for (var index = 0; index < located.Length; index++) {
            located[index].Type = StructureType.View;
        }

        var count = (uint)located.Length;

        fixed (View* first = located) {
            OpenXrResult.Check(
                api.LocateView(session, &locate, &state, count, &count, first),
                "xrLocateViews"
            );
        }

        // Both flags, not either: a pose whose orientation is valid and whose position is not would
        // render the world rotating about the wrong point, which is worse than holding still.
        var valid = (state.ViewStateFlags & ViewStateFlags.PositionValidBit) != 0
            && (state.ViewStateFlags & ViewStateFlags.OrientationValidBit) != 0;

        if (!valid) {
            return views;
        }

        for (var index = 0; index < views.Length && index < count; index++) {
            views[index] = new XrView(ToPose(located[index].Pose), ToFov(located[index].Fov));
        }

        return views;
    }

    /// <inheritdoc />
    public bool LocateSpace(XrReferenceSpace space, in XrFrameState frame, out XrPose pose) {
        ObjectDisposedException.ThrowIf(disposed, this);

        pose = XrPose.Identity;

        if (space == ReferenceSpace) {
            return true;
        }

        var handle = CreateReferenceSpace(space);

        try {
            var location = new SpaceLocation { Type = StructureType.SpaceLocation };

            OpenXrResult.Check(
                api.LocateSpace(handle, referenceSpace, frame.PredictedDisplayTime, &location),
                "xrLocateSpace"
            );

            var tracked = (location.LocationFlags & SpaceLocationFlags.PositionValidBit) != 0
                && (location.LocationFlags & SpaceLocationFlags.OrientationValidBit) != 0;

            if (tracked) {
                pose = ToPose(location.Pose);
            }

            return tracked;
        } finally {
            api.DestroySpace(handle);
        }
    }

    /// <inheritdoc />
    public void EndFrame(in XrFrameState frame, ReadOnlySpan<XrCompositionView> views) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!frameOpen) {
            throw new InvalidOperationException("A frame was ended that had not been begun.");
        }

        frameOpen = false;

        var projectionViews = stackalloc CompositionLayerProjectionView[Math.Max(1, views.Length)];

        for (var index = 0; index < views.Length; index++) {
            var view = views[index];

            projectionViews[index] = new CompositionLayerProjectionView {
                Type = StructureType.CompositionLayerProjectionView,
                Pose = FromPose(view.Pose),
                Fov = FromFov(view.Fov),
                SubImage = new SwapchainSubImage {
                    Swapchain = ((OpenXrSwapchain)view.Swapchain).Handle,
                    ImageArrayIndex = (uint)view.ImageArrayIndex,
                    ImageRect = new Rect2Di {
                        Offset = new Offset2Di { X = view.Viewport.X, Y = view.Viewport.Y },
                        Extent = new Extent2Di { Width = view.Viewport.Width, Height = view.Viewport.Height }
                    }
                }
            };
        }

        var layer = new CompositionLayerProjection {
            Type = StructureType.CompositionLayerProjection,
            Space = referenceSpace,
            ViewCount = (uint)views.Length,
            Views = projectionViews
        };

        var layers = stackalloc CompositionLayerBaseHeader*[1];

        layers[0] = (CompositionLayerBaseHeader*)&layer;

        var end = new FrameEndInfo {
            Type = StructureType.FrameEndInfo,
            DisplayTime = frame.PredictedDisplayTime,
            EnvironmentBlendMode = blendMode,

            // Zero layers is not an error and not a mistake: it is what a session that is running and
            // not visible submits, and the frame still has to be submitted.
            LayerCount = views.IsEmpty ? 0u : 1u,
            Layers = views.IsEmpty ? null : layers
        };

        OpenXrResult.Check(api.EndFrame(session, &end), "xrEndFrame");
    }

    /// <inheritdoc />
    public void AttachActionSets(ReadOnlySpan<XrActionSet> sets) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (attached.Count > 0) {
            throw new InvalidOperationException("Action sets have already been attached to this session.");
        }

        foreach (var set in sets) {
            CreateNativeSet(set);
        }

        SuggestBindings(sets);

        var handles = stackalloc NativeActionSet[Math.Max(1, nativeSets.Count)];

        for (var index = 0; index < nativeSets.Count; index++) {
            handles[index] = nativeSets[index];
        }

        var attachInfo = new SessionActionSetsAttachInfo {
            Type = StructureType.SessionActionSetsAttachInfo,
            CountActionSets = (uint)nativeSets.Count,
            ActionSets = handles
        };

        OpenXrResult.Check(api.AttachSessionActionSets(session, &attachInfo), "xrAttachSessionActionSets");

        foreach (var set in sets) {
            set.MarkAttached();
            attached.Add(set);
        }

        CreateActionSpaces();
    }

    /// <inheritdoc />
    public void SyncActions() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (State != XrSessionState.Focused) {
            // The runtime would report every action inactive anyway; not calling saves the round
            // trip and makes the rule visible rather than implied.
            foreach (var set in attached) {
                foreach (var action in set.Actions) {
                    action.Deactivate();
                }
            }

            return;
        }

        var active = stackalloc ActiveActionSet[Math.Max(1, attached.Count)];
        var count = 0;

        for (var index = 0; index < attached.Count; index++) {
            if (!attached[index].IsActive) {
                continue;
            }

            active[count++] = new ActiveActionSet {
                ActionSet = nativeSets[index],
                SubactionPath = 0
            };
        }

        var sync = new ActionsSyncInfo {
            Type = StructureType.ActionsSyncInfo,
            CountActiveActionSets = (uint)count,
            ActiveActionSets = active
        };

        OpenXrResult.Check(api.SyncAction(session, &sync), "xrSyncActions");

        foreach (var set in attached) {
            foreach (var action in set.Actions) {
                if (!set.IsActive || action.Type == XrActionType.Haptic) {
                    action.Deactivate();

                    continue;
                }

                action.Publish(XrHand.Left, Read(action, XrHand.Left));
                action.Publish(XrHand.Right, Read(action, XrHand.Right));
            }
        }
    }

    /// <inheritdoc />
    public void ApplyHaptics(XrAction action, XrHand hand, in XrHapticPulse request) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(action);

        if (action.BackendHandle is not NativeAction native) {
            return;
        }

        var vibration = new HapticVibration {
            Type = StructureType.HapticVibration,

            // Nanoseconds, and a negative one is the specification's "as short as you can": zero
            // would be no pulse at all, which is not what a caller asking for the minimum means.
            Duration = request.Duration <= TimeSpan.Zero ? -1 : request.Duration.Ticks * 100,
            Frequency = request.Frequency,
            Amplitude = Math.Clamp(request.Amplitude, 0f, 1f)
        };

        var info = new HapticActionInfo {
            Type = StructureType.HapticActionInfo,
            Action = native,
            SubactionPath = PathOf(hand)
        };

        OpenXrResult.Check(
            api.ApplyHapticFeedback(session, &info, (HapticBaseHeader*)&vibration),
            "xrApplyHapticFeedback"
        );
    }

    /// <inheritdoc />
    public void RequestExit() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (sessionRunning) {
            OpenXrResult.Check(api.RequestExitSession(session), "xrRequestExitSession");
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var swapchain in swapchains) {
            swapchain.Dispose();
        }

        swapchains.Clear();

        foreach (var space in actionSpaces.Values) {
            api.DestroySpace(space);
        }

        actionSpaces.Clear();

        foreach (var set in nativeSets) {
            api.DestroyActionSet(set);
        }

        nativeSets.Clear();

        if (referenceSpace.Handle != 0) {
            api.DestroySpace(referenceSpace);
            referenceSpace = default;
        }

        if (session.Handle != 0) {
            api.DestroySession(session);
            session = default;
        }
    }

    internal static XrPose ToPose(in Posef pose) => new(
        new NativeVector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
        new NativeQuaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W)
    );

    internal static Posef FromPose(in XrPose pose) => new() {
        Position = new Vector3f(pose.Position.X, pose.Position.Y, pose.Position.Z),
        Orientation = new Quaternionf(
            pose.Orientation.X,
            pose.Orientation.Y,
            pose.Orientation.Z,
            pose.Orientation.W
        )
    };

    static XrFieldOfView ToFov(in Fovf fov) => new(fov.AngleLeft, fov.AngleRight, fov.AngleUp, fov.AngleDown);

    static Fovf FromFov(in XrFieldOfView fov) => new() {
        AngleLeft = fov.AngleLeft,
        AngleRight = fov.AngleRight,
        AngleUp = fov.AngleUp,
        AngleDown = fov.AngleDown
    };

    static ActionType ToActionType(XrActionType type) => type switch {
        XrActionType.Boolean => ActionType.BooleanInput,
        XrActionType.Float => ActionType.FloatInput,
        XrActionType.Vector2 => ActionType.Vector2fInput,
        XrActionType.Pose => ActionType.PoseInput,
        _ => ActionType.VibrationOutput
    };

    static void WriteName(byte* destination, int capacity, string value) {
        var bytes = Encoding.UTF8.GetBytes(value);
        var count = Math.Min(bytes.Length, capacity - 1);

        for (var index = 0; index < count; index++) {
            destination[index] = bytes[index];
        }

        destination[count] = 0;
    }

    /// <summary>Walks the session state machine the runtime drives, and begins or ends it.</summary>
    void OnStateChanged(SessionState next) {
        switch (next) {
            case SessionState.Ready: {
                var begin = new SessionBeginInfo {
                    Type = StructureType.SessionBeginInfo,
                    PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo
                };

                OpenXrResult.Check(api.BeginSession(session, &begin), "xrBeginSession");
                sessionRunning = true;
                State = XrSessionState.Ready;

                break;
            }

            case SessionState.Synchronized:
                State = XrSessionState.Synchronised;

                break;

            case SessionState.Visible:
                State = XrSessionState.Visible;

                break;

            case SessionState.Focused:
                State = XrSessionState.Focused;

                break;

            case SessionState.Stopping:
                OpenXrResult.Check(api.EndSession(session), "xrEndSession");
                sessionRunning = false;
                State = XrSessionState.Stopping;

                break;

            case SessionState.LossPending:
                State = XrSessionState.Lost;
                sessionRunning = false;

                break;

            case SessionState.Exiting:
                State = XrSessionState.Exiting;
                sessionRunning = false;

                break;

            default:
                break;
        }

        // Logged after the mapping rather than before it, so the line says the state the engine is
        // in rather than the runtime's spelling of it — and so the argument is an enum the generated
        // call site formats only if somebody is listening.
        if (backend.Logger is { } logger) {
            OpenXrLog.StateChanged(logger, State);
        }
    }

    Space CreateReferenceSpace(XrReferenceSpace space) {
        var create = new ReferenceSpaceCreateInfo {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = space switch {
                XrReferenceSpace.Stage => ReferenceSpaceType.Stage,
                XrReferenceSpace.View => ReferenceSpaceType.View,
                _ => ReferenceSpaceType.Local
            },
            PoseInReferenceSpace = FromPose(XrPose.Identity)
        };

        Space created;
        var result = api.CreateReferenceSpace(session, &create, &created);

        // A seated runtime, or one whose guardian has not been set up, has no stage space. Falling
        // back to local is what every player does and is far better than refusing to start.
        if (result == Result.ErrorReferenceSpaceUnsupported && space == XrReferenceSpace.Stage) {
            create.ReferenceSpaceType = ReferenceSpaceType.Local;
            result = api.CreateReferenceSpace(session, &create, &created);
        }

        OpenXrResult.Check(result, "xrCreateReferenceSpace");

        return created;
    }

    EnvironmentBlendMode ChooseBlendMode(bool preferPassthrough) {
        var count = 0u;

        OpenXrResult.Check(
            api.EnumerateEnvironmentBlendModes(
                backend.Handle,
                backend.SystemId,
                ViewConfigurationType.PrimaryStereo,
                0,
                &count,
                null
            ),
            "xrEnumerateEnvironmentBlendModes"
        );

        if (count == 0) {
            return EnvironmentBlendMode.Opaque;
        }

        var modes = stackalloc EnvironmentBlendMode[(int)count];

        OpenXrResult.Check(
            api.EnumerateEnvironmentBlendModes(
                backend.Handle,
                backend.SystemId,
                ViewConfigurationType.PrimaryStereo,
                count,
                &count,
                modes
            ),
            "xrEnumerateEnvironmentBlendModes"
        );

        if (preferPassthrough) {
            for (var index = 0; index < count; index++) {
                if (modes[index] is EnvironmentBlendMode.AlphaBlend or EnvironmentBlendMode.Additive) {
                    return modes[index];
                }
            }
        }

        // The runtime's own first choice, which for a headset is opaque and for a passthrough device
        // may not be. Asking for opaque on a device that has none is a refused frame.
        return modes[0];
    }

    void CreateNativeSet(XrActionSet set) {
        var create = new ActionSetCreateInfo {
            Type = StructureType.ActionSetCreateInfo,
            Priority = (uint)Math.Max(0, set.Priority)
        };

        WriteName(create.ActionSetName, 64, set.Name);
        WriteName(create.LocalizedActionSetName, 128, set.LocalisedName);

        NativeActionSet native;

        OpenXrResult.Check(api.CreateActionSet(backend.Handle, &create, &native), "xrCreateActionSet");

        set.BackendHandle = native;
        nativeSets.Add(native);

        var hands = stackalloc ulong[2];

        hands[0] = PathOf(XrHand.Left);
        hands[1] = PathOf(XrHand.Right);

        foreach (var action in set.Actions) {
            var info = new ActionCreateInfo {
                Type = StructureType.ActionCreateInfo,
                ActionType = ToActionType(action.Type),

                // Every action is declared for both hands, which is what makes "either hand can pick
                // things up" one action rather than two. A profile that binds only one simply
                // reports the other inactive.
                CountSubactionPaths = 2,
                SubactionPaths = hands
            };

            WriteName(info.ActionName, 64, action.Name);
            WriteName(info.LocalizedActionName, 128, action.LocalisedName);

            NativeAction created;

            OpenXrResult.Check(api.CreateAction(native, &info, &created), "xrCreateAction");
            action.BackendHandle = created;
        }
    }

    void SuggestBindings(ReadOnlySpan<XrActionSet> sets) {
        var byProfile = new Dictionary<string, List<ActionSuggestedBinding>>(StringComparer.Ordinal);

        foreach (var set in sets) {
            foreach (var binding in set.Bindings) {
                if (binding.Action.BackendHandle is not NativeAction native) {
                    continue;
                }

                if (!byProfile.TryGetValue(binding.InteractionProfile, out var list)) {
                    list = [];
                    byProfile[binding.InteractionProfile] = list;
                }

                list.Add(new ActionSuggestedBinding {
                    Action = native,
                    Binding = StringToPath(binding.BindingPath)
                });
            }
        }

        foreach (var (profile, bindings) in byProfile) {
            var array = bindings.ToArray();

            fixed (ActionSuggestedBinding* first = array) {
                var suggestion = new InteractionProfileSuggestedBinding {
                    Type = StructureType.InteractionProfileSuggestedBinding,
                    InteractionProfile = StringToPath(profile),
                    CountSuggestedBindings = (uint)array.Length,
                    SuggestedBindings = first
                };

                // A profile the runtime has never heard of is refused, and that is not fatal: a game
                // suggesting bindings for five controllers on a runtime that knows three should keep
                // the three. The specification's own guidance.
                var result = api.SuggestInteractionProfileBinding(backend.Handle, &suggestion);

                if (result is not (Result.ErrorPathUnsupported or Result.ErrorValidationFailure)) {
                    OpenXrResult.Check(result, "xrSuggestInteractionProfileBindings");
                }
            }
        }
    }

    void CreateActionSpaces() {
        foreach (var set in attached) {
            foreach (var action in set.Actions) {
                if (action.Type != XrActionType.Pose || action.BackendHandle is not NativeAction native) {
                    continue;
                }

                foreach (var hand in (ReadOnlySpan<XrHand>)[XrHand.Left, XrHand.Right]) {
                    var create = new ActionSpaceCreateInfo {
                        Type = StructureType.ActionSpaceCreateInfo,
                        Action = native,
                        SubactionPath = PathOf(hand),
                        PoseInActionSpace = FromPose(XrPose.Identity)
                    };

                    Space space;

                    OpenXrResult.Check(
                        api.CreateActionSpace(session, &create, &space),
                        "xrCreateActionSpace"
                    );

                    actionSpaces[(action, hand)] = space;
                }
            }
        }
    }

    XrActionState Read(XrAction action, XrHand hand) {
        if (action.BackendHandle is not NativeAction native) {
            return default;
        }

        var info = new ActionStateGetInfo {
            Type = StructureType.ActionStateGetInfo,
            Action = native,
            SubactionPath = PathOf(hand)
        };

        switch (action.Type) {
            case XrActionType.Boolean: {
                var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };

                OpenXrResult.Check(api.GetActionStateBoolean(session, &info, &state), "xrGetActionStateBoolean");

                return new XrActionState(
                    state.IsActive != 0,
                    state.ChangedSinceLastSync != 0,
                    state.CurrentState != 0
                );
            }

            case XrActionType.Float: {
                var state = new ActionStateFloat { Type = StructureType.ActionStateFloat };

                OpenXrResult.Check(api.GetActionStateFloat(session, &info, &state), "xrGetActionStateFloat");

                return new XrActionState(
                    state.IsActive != 0,
                    state.ChangedSinceLastSync != 0,
                    Float: state.CurrentState
                );
            }

            case XrActionType.Vector2: {
                var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };

                OpenXrResult.Check(api.GetActionStateVector2(session, &info, &state), "xrGetActionStateVector2f");

                return new XrActionState(
                    state.IsActive != 0,
                    state.ChangedSinceLastSync != 0,
                    Vector: new Vector2(state.CurrentState.X, state.CurrentState.Y)
                );
            }

            case XrActionType.Pose:
                return ReadPose(action, hand, in info);

            default:
                return default;
        }
    }

    XrActionState ReadPose(XrAction action, XrHand hand, in ActionStateGetInfo info) {
        var state = new ActionStatePose { Type = StructureType.ActionStatePose };

        fixed (ActionStateGetInfo* pointer = &info) {
            OpenXrResult.Check(api.GetActionStatePose(session, pointer, &state), "xrGetActionStatePose");
        }

        if (state.IsActive == 0 || !actionSpaces.TryGetValue((action, hand), out var space)) {
            return default;
        }

        // A pose action's *value* is not in its state — the state only says whether it is active, and
        // where it is has to be located in a space, at a time. That is why pose actions get an action
        // space at attach time and why locating them needs the frame's display time.
        var location = new SpaceLocation { Type = StructureType.SpaceLocation };

        OpenXrResult.Check(
            api.LocateSpace(space, referenceSpace, LastDisplayTime, &location),
            "xrLocateSpace"
        );

        var tracked = (location.LocationFlags & SpaceLocationFlags.PositionValidBit) != 0
            && (location.LocationFlags & SpaceLocationFlags.OrientationValidBit) != 0;

        return new XrActionState(
            IsActive: true,
            Pose: tracked ? ToPose(location.Pose) : default,
            IsTracked: tracked
        );
    }

    /// <summary>The display time of the last frame begun, which is what poses are located at.</summary>
    long LastDisplayTime { get; set; }

    ulong PathOf(XrHand hand) =>
        StringToPath(hand == XrHand.Left ? XrPaths.LeftHand : XrPaths.RightHand);

    ulong StringToPath(string value) {
        ulong path;

        OpenXrResult.Check(api.StringToPath(backend.Handle, value, &path), "xrStringToPath");

        return path;
    }
}
