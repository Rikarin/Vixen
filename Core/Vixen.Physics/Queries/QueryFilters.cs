// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;
using Vixen.Physics.Bodies;

namespace Vixen.Physics.Queries;

/// <summary>Passes bodies whose object layer is in a mask.</summary>
/// <remarks>
///     <para>
///         Jolt calls this from inside the broad-phase walk, once per candidate, so the test has to
///         be a mask-and and nothing else. Filtering afterwards is not equivalent: a closest-hit
///         query that considered a body on a layer the caller excluded returns that body and hides
///         the one that was wanted.
///     </para>
///     <para>
///         One instance per world, with the mask written just before the query. Allocating a filter
///         per cast would put a native object construction — and its finalisable managed wrapper — in
///         front of every raycast a game does, which for a shooter is several a frame per character.
///         The cost of reuse is that a query is not reentrant, which matches
///         <see cref="PhysicsWorld" /> not being thread-safe.
///     </para>
/// </remarks>
sealed class LayerMaskFilter : ObjectLayerFilter {
    /// <summary>Which layers pass.</summary>
    public PhysicsLayerMask Mask { get; set; } = PhysicsLayerMask.All;

    /// <inheritdoc />
    protected override bool ShouldCollide(ObjectLayer layer) => (Mask.Bits & (1u << (int)layer.Value)) != 0;
}

/// <summary>Rejects one body, and optionally every sensor.</summary>
/// <remarks>
///     The two are one filter because Jolt takes one body filter per query and both are per-body
///     decisions. Splitting them would mean the sensor test had to live in the layer filter, where it
///     does not belong, or in a post-pass, where it is wrong for the reason
///     <see cref="LayerMaskFilter" /> gives.
/// </remarks>
sealed class IgnoreBodyFilter : BodyFilter {
    /// <summary>The body to skip, or <see cref="BodyHandle.None" />.</summary>
    public BodyHandle Ignore { get; set; } = BodyHandle.None;

    /// <summary>Whether sensors are hits.</summary>
    public bool IncludeSensors { get; set; }

    /// <inheritdoc />
    protected override bool ShouldCollide(BodyID bodyID) => Ignore.IsNone || bodyID.ID != Ignore.Value;

    /// <inheritdoc />
    /// <remarks>
    ///     The locked overload is the only one that can see whether a body is a sensor — the id alone
    ///     does not carry it, and reading the world's side table from a Jolt job thread would be a
    ///     race against whatever the main thread is doing to it.
    /// </remarks>
    protected override bool ShouldCollideLocked(Body body) =>
        (IncludeSensors || !body.IsSensor) && (Ignore.IsNone || body.ID.ID != Ignore.Value);
}
