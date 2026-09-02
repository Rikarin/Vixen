// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Net.Replication;

namespace Vixen.Net.Motion;

/// <summary>Where a networked object is, as the network sends it.</summary>
/// <remarks>
///     <para>
///         Deliberately not the engine's transform. That one is a hierarchy with parents, scales and
///         matrices; this is the two fields a network actually carries, quantized to what a network
///         can afford. A system copies between them, and the two are free to disagree about
///         precision — which they must, because the engine's is exact and this one is 96 bits.
///     </para>
///     <para>
///         The range is ±1000 metres at 16 bits a component, which is three centimetres. A game whose
///         world is larger or whose precision matters more declares its own component with its own
///         <c>[Quantize]</c> and its own replicator; that is a decision, and the shipped default is
///         one too rather than an accident.
///     </para>
/// </remarks>
[Component]
public struct NetworkTransform {
    /// <summary>Where it is.</summary>
    public Vector3 Position;

    /// <summary>Which way it faces.</summary>
    public Quaternion Rotation;

    /// <summary>
    ///     Counts up every time the object is put somewhere rather than moved there.
    /// </summary>
    /// <remarks>
    ///     A respawn far enough away is caught by the snapshot buffer's snap distance on its own, but
    ///     a teleport of two metres is not — and a two-metre slide is exactly what a door should not
    ///     look like. This says so out loud, costs eight bits, and wraps without meaning anything by
    ///     it: the receiver compares it with the last one it saw and only asks whether it changed.
    /// </remarks>
    public byte TeleportCount;
}

/// <summary>Which frame a <see cref="NetworkTransform" /> is expressed in.</summary>
/// <remarks>
///     <para>
///         <b>A component of its own rather than a field on <see cref="NetworkTransform" />, and that
///         is the whole design.</b> Almost nothing in a world has a parent, and a field would put
///         thirty-two bits on every transform in the game so that the handful of riders and turrets
///         could have theirs. As a separate replicated component an unparented entity pays nothing
///         at all — no record, no lane, no mask bit — and the transform stays the 88 bits doc 16
///         costed it at.
///     </para>
///     <para>
///         <b>It is a <see cref="NetworkId" /> and not an <c>Entity</c></b>, for the reason
///         <see cref="NetworkId" /> exists: the same vehicle is a different handle on every peer.
///         Zero means world space, which is also what a default-initialised one means, so an entity
///         that never had a parent and one that has just lost it are the same value.
///     </para>
///     <para>
///         <b>Its arrival is not ordered against the parent's.</b> A rider can be told which vehicle
///         it is in several ticks before the vehicle itself is spawned — interest resolved them in
///         one order and the budget shed them in another. What the receiving side does about that is
///         <c>NetworkTransformApplySystem</c>'s, and the answer is that the rider does not move until
///         the frame it is quoted in exists. Applying the numbers as world coordinates meanwhile is
///         the one thing that must never happen: it puts the rider at the vehicle's seat offset from
///         the world origin and then corrects itself, which reads as the netcode teleporting people
///         into the ground.
///     </para>
/// </remarks>
[Component]
public struct NetworkParent {
    /// <summary>The <see cref="NetworkId" /> the position and rotation are relative to. Zero is the world.</summary>
    public uint Value;
}
