// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>How bad the network is pretending to be.</summary>
/// <remarks>
///     <para>
///         The delays and losses apply to what the endpoint holding the profile <i>sends</i>, so
///         <see cref="Latency" /> is one way. Wrapping both ends of a connection — the usual case in
///         a test — gives a round trip of twice it.
///     </para>
///     <para>
///         Loss and duplication only ever touch channels whose contract permits them
///         (<see cref="ChannelExtensions.MayDrop" />, <see cref="ChannelExtensions.MayDuplicate" />).
///         A simulation that dropped a <see cref="Channel.Reliable" /> payload would not be
///         simulating a bad network, it would be simulating a broken transport, and the layer above
///         is entitled to assume that never happens.
///     </para>
/// </remarks>
public sealed record NetworkSimulationProfile {
    /// <summary>A perfect wire. What the simulation does when it is switched on but told to do nothing.</summary>
    public static NetworkSimulationProfile Perfect { get; } = new();

    /// <summary>Two players on one switch.</summary>
    public static NetworkSimulationProfile Lan { get; } = new() {
        Latency = TimeSpan.FromMilliseconds(2),
        Jitter = TimeSpan.FromMilliseconds(1),
        LossChance = 0.0001
    };

    /// <summary>
    ///     Ordinary home broadband to a regional server. The profile a development build should run
    ///     with by default: netcode written on localhost is netcode that breaks on release day.
    /// </summary>
    public static NetworkSimulationProfile Broadband { get; } = new() {
        Latency = TimeSpan.FromMilliseconds(35),
        Jitter = TimeSpan.FromMilliseconds(8),
        LossChance = 0.005
    };

    /// <summary>A phone on a good day.</summary>
    public static NetworkSimulationProfile Mobile { get; } = new() {
        Latency = TimeSpan.FromMilliseconds(90),
        Jitter = TimeSpan.FromMilliseconds(40),
        LossChance = 0.02,
        DuplicateChance = 0.002
    };

    /// <summary>A phone on a train. Not a plausible target — a deliberate stress case.</summary>
    public static NetworkSimulationProfile Awful { get; } = new() {
        Latency = TimeSpan.FromMilliseconds(200),
        Jitter = TimeSpan.FromMilliseconds(100),
        LossChance = 0.2,
        DuplicateChance = 0.01
    };

    /// <summary>The one-way delay added to everything sent.</summary>
    public TimeSpan Latency { get; init; }

    /// <summary>
    ///     How far either side of <see cref="Latency" /> a delay may land, drawn uniformly. This is
    ///     also where reordering comes from: two unordered payloads with different draws arrive in
    ///     whichever order their draws put them, which is what really happens and is not worth
    ///     modelling separately.
    /// </summary>
    public TimeSpan Jitter { get; init; }

    /// <summary>
    ///     The chance, from 0 to 1, that a droppable payload is thrown away instead of sent.
    /// </summary>
    public double LossChance { get; init; }

    /// <summary>
    ///     The chance, from 0 to 1, that a duplicable payload is sent twice, the copy drawing its own
    ///     jitter.
    /// </summary>
    public double DuplicateChance { get; init; }
}
