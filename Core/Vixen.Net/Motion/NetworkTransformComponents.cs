// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

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
