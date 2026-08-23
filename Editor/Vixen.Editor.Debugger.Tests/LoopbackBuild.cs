// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Net;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;

namespace Vixen.Editor.Debugger.Tests;

/// <summary>A build at the far end of a loopback, and the editor's client for it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A real transport rather than a mock, which is <c>RemoteInspectorTests</c>' bargain
///         and the reason it is worth sharing.</b> A round trip is a loop of <c>Poll</c> calls rather
///         than a wait, so a test observes the same thing every run and takes microseconds — and what
///         it exercises is the protocol both ends actually speak.
///     </para>
///     <para>
///         Cut down from <c>RemoteInspectorTests.FakeBuild</c> to what a *panel* test needs: greet,
///         answer with three entities, and report a counter on demand. The write and command paths
///         stay in that file, which is where the protocol is under test.
///     </para>
/// </remarks>
public sealed class LoopbackBuild : IDisposable, ITransportEvents {
    readonly LocalNetwork network = new();
    readonly LocalTransport build;
    readonly LocalTransport editor;
    readonly ArrayBufferWriter<byte> outgoing = new(512);

    ConnectionId connected;

    /// <summary>Starts the build's listener and points a client at it.</summary>
    public LoopbackBuild() {
        build = new(network);
        editor = new(network);

        Client = new(editor, "Test Editor");

        build.StartServer();
    }

    /// <summary>The editor's half.</summary>
    public RemoteInspectorClient Client { get; }

    /// <summary>Runs both ends until the conversation stops moving.</summary>
    /// <param name="rounds">
    ///     How many polls each end gets. Bounded rather than "until attached": a protocol bug that
    ///     never converges should fail an assertion rather than hang the run.
    /// </param>
    public void Settle(int rounds = 8) {
        for (var round = 0; round < rounds; round++) {
            Client.Poll(TimeSpan.FromMilliseconds(16));
            build.Poll(TimeSpan.FromMilliseconds(16), this);
        }
    }

    /// <summary>Sends a live counter, as a running build does every frame.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="value">What it reads.</param>
    public void Report(string name, double value) =>
        Send(writer => InspectorProtocol.WriteCounter(writer, new(name, value)));

    /// <inheritdoc />
    public void OnConnected(TransportRole role, ConnectionId connection) {
        if (role is TransportRole.Server) {
            connected = connection;
        }
    }

    /// <inheritdoc />
    public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) { }

    /// <inheritdoc />
    public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
        if (role is not TransportRole.Server || !InspectorProtocol.TryReadKind(payload, out var message)) {
            return;
        }

        switch (message) {
            case InspectorMessage.Hello:
                Send(writer => InspectorProtocol.WriteWelcome(writer, "Test Build"));
                break;

            case InspectorMessage.RequestTree:
                Send(writer => InspectorProtocol.WriteEntity(writer, new(1, 0, "Root", ["Transform"])));
                Send(writer => InspectorProtocol.WriteEntity(writer, new(2, 1, "Camera", ["Transform", "Camera"])));
                Send(writer => InspectorProtocol.WriteEntity(writer, new(3, 1, "Light", ["Transform", "Light"])));
                Send(writer => InspectorProtocol.WriteBare(writer, InspectorMessage.TreeComplete));

                break;

            default:
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Client.Dispose();

    void Send(Action<ArrayBufferWriter<byte>> write) {
        outgoing.Clear();
        write(outgoing);

        build.SendToClient(connected, outgoing.WrittenSpan, Channel.Reliable);
    }
}
