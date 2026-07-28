// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Xr.Ecs;

/// <summary>Which tracked thing an entity follows.</summary>
public enum XrTrackedDevice : byte {
    /// <summary>The headset. Where the camera goes.</summary>
    Head = 0,

    /// <summary>The left controller.</summary>
    LeftHand = 1,

    /// <summary>The right controller.</summary>
    RightHand = 2
}

/// <summary>The entity the play space is anchored to.</summary>
/// <remarks>
///     <para>
///         <b>The rig, and the reason it is an entity rather than a global.</b> Tracked poses arrive
///         in the runtime's reference space, whose origin is the middle of the room; the game's world
///         has the player somewhere else entirely and moving. This component marks the entity whose
///         transform that reference space is nailed to, so teleporting the player is moving one
///         entity and everything tracked follows.
///     </para>
///     <para>
///         <b>Exactly one per world.</b> Two would mean two answers to "where is the player", and the
///         system takes the first it finds and says so in a diagnostic rather than picking silently.
///     </para>
/// </remarks>
public struct XrOrigin {
    /// <summary>
    ///     How many world units a metre is. One unless the game works in something other than metres.
    /// </summary>
    /// <remarks>
    ///     Applied to tracked positions and to nothing else. A game in centimetres sets 100 here and
    ///     leaves every other part of its content alone — which is the only place the conversion can
    ///     live without being done twice somewhere.
    /// </remarks>
    public float UnitsPerMetre;

    /// <summary>A rig in metres, which is what a game should be in.</summary>
    /// <returns>The component.</returns>
    public static XrOrigin Default() => new() { UnitsPerMetre = 1f };
}

/// <summary>An entity that follows a tracked device.</summary>
/// <remarks>
///     Adding this is what makes an entity follow the headset or a controller, and removing it stops
///     it — there is no register call, which is the same bargain every other ECS bridge in the engine
///     makes.
/// </remarks>
public struct XrTrackedPose {
    /// <summary>What it follows.</summary>
    public XrTrackedDevice Device;

    /// <summary>Whether to write the tracked position.</summary>
    /// <remarks>
    ///     Off is a real case: a seated game that wants the head's rotation and its own idea of where
    ///     the player is standing, and a hand model pinned to a UI panel that should still rotate.
    /// </remarks>
    public bool ApplyPosition;

    /// <summary>Whether to write the tracked rotation.</summary>
    public bool ApplyRotation;

    /// <summary>Whether the device was actually tracked this frame. Written by the system.</summary>
    /// <remarks>
    ///     A controller that is put down stops being tracked and keeps its last pose. Game code that
    ///     wants to hide the model rather than leave it floating reads this; the transform is left
    ///     alone either way, because snapping a held object to the origin is worse than leaving it
    ///     where it was last seen.
    /// </remarks>
    public bool IsTracked;

    /// <summary>The raw pose, in the reference space, before the rig was applied. Written by the system.</summary>
    public XrPose Pose;

    /// <summary>Following a device with both position and rotation.</summary>
    /// <param name="device">What to follow.</param>
    /// <returns>The component.</returns>
    public static XrTrackedPose Following(XrTrackedDevice device) => new() {
        Device = device,
        ApplyPosition = true,
        ApplyRotation = true
    };
}
