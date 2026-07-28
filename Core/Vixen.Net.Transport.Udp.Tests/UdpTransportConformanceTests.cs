// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Vixen.Net.Tests.Transport;

namespace Vixen.Net.Transport.Udp.Tests;

/// <summary>
///     The UDP transport, held to the same contract as the in-process one.
/// </summary>
/// <remarks>
///     <para>
///         This is what the conformance suite was written for. Every promise the layers above rely on
///         — that a reliable payload arrives once and in order, that a 64 KiB payload arrives whole,
///         that a disconnection reaches both sides with the right reason — is asserted here against a
///         transport that builds all of it out of datagrams, by exactly the same tests that assert it
///         against a transport that gets it for free.
///     </para>
///     <para>
///         The connect timeout is short because the suite's "nothing is listening" test has to
///         observe a refusal, and on UDP a refusal is a timeout: there is nobody there to say no.
///     </para>
/// </remarks>
public sealed class UdpTransportConformanceTests : TransportConformance {
    readonly DatagramBus bus = new();
    readonly IPEndPoint listenAt = new(IPAddress.Loopback, 45000);

    /// <inheritdoc />
    protected override ITransport CreateServer() =>
        new UdpTransport(bus, new() { ListenEndPoint = listenAt, RemoteEndPoint = listenAt, ConnectTimeout = TimeSpan.FromMilliseconds(60) });

    /// <inheritdoc />
    protected override ITransport CreateClient() =>
        new UdpTransport(bus, new() { ListenEndPoint = new(IPAddress.Loopback, 0), RemoteEndPoint = listenAt, ConnectTimeout = TimeSpan.FromMilliseconds(60) });
}
