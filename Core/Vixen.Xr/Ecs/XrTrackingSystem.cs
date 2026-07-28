// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Xr.Input;

namespace Vixen.Xr.Ecs;

/// <summary>Puts the headset and the controllers where the runtime says they are.</summary>
/// <remarks>
///     <para>
///         <b>It reads what the host has already done and writes transforms.</b> The frame loop —
///         poll, begin, locate, sync — belongs to whatever owns the session, because it is the loop
///         the runtime paces. By the time this runs, the views have been located for this frame's
///         display time and the actions have been synced, so the system's whole job is to compose
///         those poses with the rig and put the result on the entities.
///     </para>
///     <para>
///         <b>The head pose is the midpoint of the eyes, not a third located space.</b> Locating the
///         view space separately would be a second prediction of the same thing, and the two would
///         disagree by however much the runtime's two answers differ. A camera at the midpoint is
///         also the right place for anything that is not being rendered in stereo — a shadow view, an
///         audio listener, a culling frustum.
///     </para>
///     <para>
///         <b>It writes <see cref="LocalTransform" />, so a tracked entity should be a root.</b> The
///         same constraint <c>NavigationSystem</c> has, for the same reason: this composes the rig in
///         itself, and a parented entity would have the result composed with its parent a second
///         time.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
public sealed class XrTrackingSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription origins = new QueryDescription().WithAll<XrOrigin, LocalTransform>();
    readonly QueryDescription tracked = new QueryDescription().WithAll<XrTrackedPose, LocalTransform>();

    /// <summary>Creates the system over a session.</summary>
    /// <param name="session">The session whose poses it publishes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session" /> is null.</exception>
    public XrTrackingSystem(IXrSession session) {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
    }

    /// <summary>The session being read.</summary>
    public IXrSession Session { get; }

    /// <summary>The pose action the left controller's transform comes from.</summary>
    /// <remarks>
    ///     Null and controllers do not move, which is the correct behaviour for a game that has
    ///     declared no input: OpenXR has no way to ask where a controller is except through an action,
    ///     precisely so that the user's rebinding applies to poses as well.
    /// </remarks>
    public XrAction? HandPoseAction { get; set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>NavigationSystem</c> gives: naming a
    ///     component in a generic call is what assigns it an id, and an attribute can only look one
    ///     up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<XrOrigin>()
        .Write<XrTrackedPose>()
        .Write<LocalTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Publish(context.World);

        return dependency;
    }

    /// <summary>Writes this frame's poses onto a world's entities.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or a tool can publish poses without standing up a runner.</remarks>
    public void Publish(World world) {
        ArgumentNullException.ThrowIfNull(world);

        var (origin, scale) = FindOrigin(world);
        var head = HeadPose();

        foreach (var chunk in world.Chunks(tracked)) {
            var poses = chunk.Values<XrTrackedPose>();
            var transforms = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                ref var pose = ref poses[index];

                var (local, isTracked) = pose.Device switch {
                    XrTrackedDevice.Head => (head, Session.IsRunning),
                    XrTrackedDevice.LeftHand => HandPose(XrHand.Left),
                    _ => HandPose(XrHand.Right)
                };

                pose.IsTracked = isTracked;

                if (!isTracked) {
                    // Left where it was last seen. Snapping a put-down controller to the rig's origin
                    // is worse than a hand that has stopped moving, and it is what a naive "write the
                    // identity when untracked" does.
                    continue;
                }

                pose.Pose = local;

                // The rig's own composition, with the metre-to-unit scale applied to the offset and
                // not to the rig's position: the rig is already in world units and the tracked pose
                // is in metres, so scaling the sum would move the whole play space.
                if (pose.ApplyPosition) {
                    transforms[index].Position = origin.Position
                        + (Quaternion.Transform(local.Position, origin.Orientation) * scale);
                }

                if (pose.ApplyRotation) {
                    transforms[index].Rotation = origin.Orientation * local.Orientation;
                }
            }
        }
    }

    /// <summary>Where the head is, as the midpoint of the located eyes.</summary>
    XrPose HeadPose() {
        var views = Session.Views;

        if (views.IsEmpty) {
            return XrPose.Identity;
        }

        if (views.Length == 1) {
            return views[0].Pose;
        }

        return new XrPose(
            (views[0].Pose.Position + views[1].Pose.Position) * 0.5f,

            // The two eyes share an orientation on every headset that exists, so taking the left
            // one's is exact rather than an approximation — and slerping two identical quaternions
            // twice a frame would be arithmetic in place of a fact.
            views[0].Pose.Orientation
        );
    }

    (XrPose Pose, bool IsTracked) HandPose(XrHand hand) {
        if (HandPoseAction is not { } action) {
            return (XrPose.Identity, false);
        }

        var state = action.State(hand);

        return (state.Pose, state is { IsActive: true, IsTracked: true });
    }

    /// <summary>The rig's transform, or the identity if the world has no rig.</summary>
    (XrPose Origin, float Scale) FindOrigin(World world) {
        foreach (var chunk in world.Chunks(origins)) {
            if (chunk.Count == 0) {
                continue;
            }

            var rigs = chunk.ReadValues<XrOrigin>();
            var transforms = chunk.ReadValues<LocalTransform>();

            return (
                new XrPose(transforms[0].Position, transforms[0].Rotation),
                rigs[0].UnitsPerMetre > 0f ? rigs[0].UnitsPerMetre : 1f
            );
        }

        // No rig is a legal world — a game that has not built one yet, or a test — and the reference
        // space's own origin is then the world's.
        return (XrPose.Identity, 1f);
    }
}
