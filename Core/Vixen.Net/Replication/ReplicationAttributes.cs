// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Replication;

/// <summary>Marks a component as one the server sends to clients.</summary>
/// <remarks>
///     <para>
///         Read by <c>Vixen.Net.Generators</c>, which emits an <see cref="IComponentReplicator" /> for
///         the type and registers it. Nothing reads it at run time — by the time the game is running,
///         the decision it records has become code.
///     </para>
///     <para>
///         The defaults are the ones most components want: unreliable, because state that supersedes
///         itself should not stall a channel waiting for a retransmit of a value that is already
///         stale, and every tick, because a component nobody set does not cost anything to consider.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class ReplicatedAttribute : Attribute {
    /// <summary>How to send it. Unreliable by default — the next value makes this one irrelevant.</summary>
    public Channel Channel { get; set; } = Channel.Unreliable;

    /// <summary>
    ///     How many times a second to send it at most, or zero for every tick. A component that
    ///     changes every tick but only matters ten times a second says so here.
    /// </summary>
    public int SendRate { get; set; }

    /// <summary>
    ///     What to shed last when the bandwidth budget runs out. Higher goes first; the default of
    ///     zero means "with everything else".
    /// </summary>
    public int Priority { get; set; }
}

/// <summary>Declares what a float is a float <i>of</i>, so it can be sent in fewer bits.</summary>
/// <remarks>
///     Read by <c>Vixen.Net.Generators</c>, which turns it into a <see cref="Messaging.QuantizeRange" />
///     in the emitted replicator. Putting it on the field rather than in the sending code is the
///     point: the precision a packet costs is then declared where the field is declared, and reviewed
///     when the field is.
/// </remarks>
/// <param name="min">The smallest value that will be sent exactly.</param>
/// <param name="max">The largest.</param>
/// <param name="bits">How many bits to spend, from 1 to 32.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class QuantizeAttribute(float min, float max, int bits) : Attribute {
    /// <summary>The smallest value that will be sent exactly.</summary>
    public float Min => min;

    /// <summary>The largest.</summary>
    public float Max => max;

    /// <summary>How many bits to spend.</summary>
    public int Bits => bits;
}
