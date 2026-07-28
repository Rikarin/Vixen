// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;

namespace Vixen.Net.Tests.Transport;

/// <summary>The in-process transport, held to the contract.</summary>
public sealed class LocalTransportConformanceTests : TransportConformance {
    readonly LocalNetwork network = new();

    /// <inheritdoc />
    protected override ITransport CreateServer() => new LocalTransport(network);

    /// <inheritdoc />
    protected override ITransport CreateClient() => new LocalTransport(network);
}

/// <summary>
///     The in-process transport with the simulation wrapped around it, held to the same contract.
/// </summary>
/// <remarks>
///     With <see cref="NetworkSimulationProfile.Perfect" /> the decorator injects nothing, so it must
///     be invisible: every promise the transport underneath makes, it still makes. A decorator that
///     quietly reordered or swallowed something would fail here rather than in the replication tests
///     six layers up, which is where it would otherwise first be noticed.
/// </remarks>
public sealed class SimulatedTransportConformanceTests : TransportConformance {
    readonly LocalNetwork network = new();
    ulong seed = 1;

    /// <inheritdoc />
    protected override ITransport CreateServer() => Simulate();

    /// <inheritdoc />
    protected override ITransport CreateClient() => Simulate();

    NetworkSimulation Simulate() => new(new LocalTransport(network), NetworkSimulationProfile.Perfect, seed++);
}
