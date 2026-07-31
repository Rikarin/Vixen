// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Cameras;

/// <summary>The shape of a transition from one shot to another.</summary>
/// <remarks>
///     Five curves rather than an authorable one. A curve asset would be a second thing to load, a
///     second thing to serialise and a second thing to get wrong, and the difference between these
///     and a hand-drawn one is not visible over the half-second a cut usually takes. What is visible
///     is the difference between having eased ends and not having them, which is why
///     <see cref="EaseInOut" /> is the default.
/// </remarks>
public enum CameraBlendStyle {
    /// <summary>No transition. The new shot is simply the one being rendered from the next frame.</summary>
    Cut,

    /// <summary>Constant speed, with a visible start and stop.</summary>
    Linear,

    /// <summary>Slow to leave, arriving at speed.</summary>
    EaseIn,

    /// <summary>Leaving at speed, slow to arrive.</summary>
    EaseOut,

    /// <summary>Slow at both ends. What a camera operated by a person looks like.</summary>
    EaseInOut
}

/// <summary>How long a transition takes and what shape it has.</summary>
[DataContract]
public struct CameraBlend {
    /// <summary>The curve.</summary>
    public CameraBlendStyle Style;

    /// <summary>How long it takes, in seconds. Zero is a cut whatever the style says.</summary>
    public float Duration;

    /// <summary>An eased blend over two seconds — long enough to read as a camera move.</summary>
    public static CameraBlend Default => new() { Style = CameraBlendStyle.EaseInOut, Duration = 2f };

    /// <summary>No blend at all.</summary>
    public static CameraBlend Cut => new() { Style = CameraBlendStyle.Cut, Duration = 0f };

    /// <summary>An eased blend of a given length.</summary>
    /// <param name="seconds">How long.</param>
    /// <returns>The blend.</returns>
    public static CameraBlend Over(float seconds) => new() {
        Style = CameraBlendStyle.EaseInOut,
        Duration = seconds
    };

    /// <summary>Whether this blend does anything at all.</summary>
    public readonly bool IsCut => Style == CameraBlendStyle.Cut || Duration <= 0f;

    /// <summary>The eased progress at a point through the blend.</summary>
    /// <param name="elapsed">How long the blend has been running, in seconds.</param>
    /// <returns>A number from 0 to 1.</returns>
    public readonly float Evaluate(float elapsed) {
        if (IsCut) {
            return 1f;
        }

        var amount = MathUtil.Saturate(elapsed / Duration);

        return Style switch {
            CameraBlendStyle.Linear => amount,
            CameraBlendStyle.EaseIn => amount * amount,
            CameraBlendStyle.EaseOut => amount * (2f - amount),
            CameraBlendStyle.EaseInOut => MathUtil.SmoothStep(amount),
            _ => 1f
        };
    }

    /// <summary>Mixes two composed camera states.</summary>
    /// <param name="fromPosition">Where the outgoing shot is.</param>
    /// <param name="fromRotation">Which way the outgoing shot looks.</param>
    /// <param name="toPosition">Where the incoming shot is.</param>
    /// <param name="toRotation">Which way the incoming shot looks.</param>
    /// <param name="amount">The eased progress.</param>
    /// <param name="position">The mixed position.</param>
    /// <param name="rotation">The mixed rotation.</param>
    /// <remarks>
    ///     <para>
    ///         <b>A straight line, not an arc.</b> Cinemachine interpolates positions linearly too,
    ///         and the alternative — swinging the camera round the point both shots are looking at —
    ///         is a different and much larger feature: it needs the two shots to agree about what
    ///         that point is, and it produces a wild move whenever they do not. A straight line
    ///         through geometry is the known cost, and it is why a blend between two shots on
    ///         opposite sides of a wall should be a cut.
    ///     </para>
    ///     <para>
    ///         The rotation is a slerp, so the camera turns at a constant rate through the blend
    ///         rather than accelerating in the middle the way a normalised lerp does.
    ///     </para>
    /// </remarks>
    public static void Mix(
        Vector3 fromPosition,
        Quaternion fromRotation,
        Vector3 toPosition,
        Quaternion toRotation,
        float amount,
        out Vector3 position,
        out Quaternion rotation
    ) {
        position = Vector3.Lerp(fromPosition, toPosition, amount);
        rotation = Quaternion.Slerp(fromRotation, toRotation, amount);
    }
}

/// <summary>
///     Put beside a <see cref="Camera" />: it chooses which shot that camera is currently taking, and
///     blends when the choice changes.
/// </summary>
/// <remarks>
///     <para>
///         Cinemachine's Brain. It owns the real camera's transform for as long as it is there, and
///         everything it does is decided by two numbers on the shots themselves — which one has the
///         highest <see cref="VirtualCamera.Priority" />, and which was enabled most recently.
///         Nothing anywhere calls "switch to camera B": B is given a higher priority, or A is
///         disabled, and the cut happens because the answer to the question changed.
///     </para>
///     <para>
///         ⚠ <b>The blend in flight is not in this component.</b> What is here is authored settings;
///         which shot is live and how far through a transition it is lives in
///         <see cref="CameraDirectorSystem" />, keyed by the director's entity. A saved game that
///         captured a half-finished blend would reload into the middle of a camera move nobody asked
///         for, and a component that is written sixty times a second would mark its chunk dirty for
///         every change filter in the frame.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct CameraDirector {
    /// <summary>The blend used for any transition a <see cref="CameraBlendTable" /> has no rule for.</summary>
    public CameraBlend DefaultBlend;

    /// <summary>Whether the live shot's lens is copied onto the camera each frame.</summary>
    /// <remarks>
    ///     True is what makes a shot's field of view mean anything. False leaves the
    ///     <see cref="Camera" /> component's optics alone and takes only the position and the
    ///     rotation, for a game that drives its own zoom.
    /// </remarks>
    public bool WriteLens;

    /// <summary>Which shots this director considers. Only those on the same channel.</summary>
    public int Channel;

    /// <summary>A director that blends over two seconds and owns its camera's lens.</summary>
    /// <remarks>
    ///     A property rather than a <c>default</c>, and for the same reason
    ///     <see cref="VirtualCamera.Default" /> is: a zeroed director cuts rather than blends and
    ///     silently declines to write the lens, which are two surprising behaviours dressed up as
    ///     the absence of configuration.
    /// </remarks>
    public static CameraDirector Default => new() {
        DefaultBlend = CameraBlend.Default,
        WriteLens = true,
        Channel = 0
    };
}
