// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Tests.Transport;

namespace Vixen.Net.Transport.WebSocket.Tests;

/// <summary>The WebSocket transport, held to the same contract as the other two.</summary>
/// <remarks>
///     The third transport through the same suite, and the cheapest of the three to get through it —
///     which is the point being made rather than a boast. A WebSocket already delivers reliably, in
///     order, with message boundaries, so most of what the UDP transport had to build is simply the
///     medium here. The suite does not care either way, and neither does anything above it.
/// </remarks>
public sealed class WebSocketTransportConformanceTests : TransportConformance {
    static readonly Uri Address = new("ws://127.0.0.1:45100/");

    readonly WebSocketLoop loop = new();

    /// <inheritdoc />
    protected override ITransport CreateServer() =>
        new WebSocketTransport(loop, new() { ListenAddress = Address, RemoteAddress = Address });

    /// <inheritdoc />
    protected override ITransport CreateClient() =>
        new WebSocketTransport(
            loop,
            new() { ListenAddress = Address, RemoteAddress = Address, ConnectTimeout = TimeSpan.FromMilliseconds(60) }
        );
}
