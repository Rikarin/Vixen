// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Net;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;
using Xunit;

namespace Vixen.Editor.Debugger.Tests;

/// <summary>
///     Doc 20's E4 exit criterion — "a build on a device can be attached to and an entity mutated
///     live" — with the device standing in as a second transport in the same process.
/// </summary>
/// <remarks>
///     ⚠ <b>A loopback transport rather than a mock, and time is a parameter.</b> A round trip here
///     is a loop of <c>Poll</c> calls rather than a wait, so the test observes the same thing every
///     run and takes microseconds. That is <c>ITransport</c>'s own bargain, and it is what makes a
///     protocol testable without a phone on the desk.
/// </remarks>
public sealed class RemoteInspectorTests : IDisposable {
    readonly LocalNetwork network = new();
    readonly LocalTransport build;
    readonly LocalTransport editor;
    readonly RemoteInspectorClient client;
    readonly FakeBuild far;

    public RemoteInspectorTests() {
        build = new(network);
        editor = new(network);

        far = new(build);
        client = new(editor, "Test Editor");

        build.StartServer();
    }

    /// <summary>Runs both ends until something stops changing.</summary>
    /// <remarks>
    ///     Bounded rather than "until attached": a protocol bug that never converges should fail the
    ///     assertion below rather than hang the run.
    /// </remarks>
    void Settle(int rounds = 8) {
        for (var round = 0; round < rounds; round++) {
            client.Poll(TimeSpan.FromMilliseconds(16));
            build.Poll(TimeSpan.FromMilliseconds(16), far);
        }
    }

    [Fact]
    public void AttachingGreetsAndFetchesTheTree() {
        client.Attach();
        Settle();

        Assert.Equal(RemoteState.Attached, client.State);
        Assert.Equal("Test Build", client.BuildName);
        Assert.Equal(InspectorProtocol.Version, client.BuildVersion);
        Assert.Equal(3, client.Entities.Count);
        Assert.False(client.IsFetching);
    }

    /// <summary>The exit criterion: a value written from the editor lands on the far end.</summary>
    [Fact]
    public void AValueWrittenFromTheEditorReachesTheBuild() {
        client.Attach();
        Settle();

        client.SetValue(2, "Transform.Position", "1 2 3");
        Settle();

        Assert.Equal((2ul, "Transform.Position", "1 2 3"), far.LastWrite);
    }

    [Fact]
    public void CountersArriveAndAreKeptByName() {
        client.Attach();
        Settle();

        far.Report("fps", 59.5);
        Settle();

        Assert.Equal(59.5, client.Counters["fps"]);

        far.Report("fps", 61.25);
        Settle();

        Assert.Equal(61.25, client.Counters["fps"]);
    }

    [Fact]
    public void ACommandCrossesAndItsResultComesBack() {
        client.Attach();
        Settle();

        client.Command("capture");
        Settle();

        Assert.Equal("capture", far.LastCommand);
        Assert.Contains(client.Log, line => line.Contains("capture", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A version mismatch is a state rather than an exception, and it does not half-read the
    ///     far end's messages — an empty entity tree would look exactly like a build with none.
    /// </summary>
    [Fact]
    public void AVersionMismatchIsReportedRatherThanHalfUnderstood() {
        far.Version = (ushort)(InspectorProtocol.Version + 1);

        client.Attach();
        Settle();

        Assert.Equal(RemoteState.Incompatible, client.State);
        Assert.Empty(client.Entities);
    }

    [Fact]
    public void DetachingForgetsWhatTheBuildSaid() {
        client.Attach();
        Settle();

        Assert.NotEmpty(client.Entities);

        client.Detach();

        Assert.Equal(RemoteState.Detached, client.State);
        Assert.Empty(client.Entities);
        Assert.Null(client.BuildName);
    }

    /// <summary>Writing while detached is a no-op, not a crash.</summary>
    [Fact]
    public void NothingIsSentWhileDetached() {
        client.SetValue(1, "Transform.Position", "0 0 0");
        client.Command("capture");
        client.Refresh();

        Settle();

        Assert.Null(far.LastWrite);
        Assert.Null(far.LastCommand);
    }

    /// <summary>
    ///     ⚠ The tree is staged and swapped, so a refresh never leaves the panel showing a half-built
    ///     list — and the count after two refreshes is the tree, not twice the tree.
    /// </summary>
    [Fact]
    public void RefreshingReplacesTheTreeRatherThanAppendingToIt() {
        client.Attach();
        Settle();

        client.Refresh();
        Settle();

        Assert.Equal(3, client.Entities.Count);
    }

    [Fact]
    public void TheLogIsBounded() {
        client.Attach();
        Settle();

        for (var index = 0; index < RemoteInspectorClient.LogCapacity * 2; index++) {
            client.Command("noop");
        }

        Assert.True(client.Log.Count <= RemoteInspectorClient.LogCapacity);
    }

    public void Dispose() {
        client.Dispose();
        editor.Dispose();
        build.Dispose();
    }

    /// <summary>The far end of the protocol: what a player would implement.</summary>
    /// <remarks>
    ///     Written here rather than shipped, because doc 13 owns the runtime half and it is not
    ///     built. Its value as a test double is that it is written against
    ///     <see cref="InspectorProtocol" />'s readers and writers only — so if the editor's half
    ///     drifts from the format, this stops understanding it.
    /// </remarks>
    sealed class FakeBuild(ITransport transport) : ITransportEvents {
        readonly ArrayBufferWriter<byte> outgoing = new(512);

        ConnectionId client;

        public ushort Version { get; set; } = InspectorProtocol.Version;

        public (ulong Entity, string Member, string Value)? LastWrite { get; private set; }

        public string? LastCommand { get; private set; }

        public void OnConnected(TransportRole role, ConnectionId connection) {
            if (role is TransportRole.Server) {
                client = connection;
            }
        }

        public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) { }

        public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
            if (role is not TransportRole.Server || !InspectorProtocol.TryReadKind(payload, out var message)) {
                return;
            }

            switch (message) {
                case InspectorMessage.Hello:
                    Send(writer => WriteWelcome(writer));
                    break;

                case InspectorMessage.RequestTree:
                    Send(writer => InspectorProtocol.WriteEntity(writer, new(1, 0, "Root", ["Transform"])));
                    Send(writer => InspectorProtocol.WriteEntity(writer, new(2, 1, "Camera", ["Transform", "Camera"])));
                    Send(writer => InspectorProtocol.WriteEntity(writer, new(3, 1, "Light", ["Transform", "Light"])));
                    Send(writer => InspectorProtocol.WriteBare(writer, InspectorMessage.TreeComplete));

                    break;

                case InspectorMessage.SetValue:
                    if (InspectorProtocol.TryReadSetValue(payload, out var entity, out var member, out var value)) {
                        LastWrite = (entity, member, value);
                    }

                    break;

                case InspectorMessage.Command:
                    if (InspectorProtocol.TryReadText(payload, out var verb)) {
                        LastCommand = verb;
                        Send(writer => InspectorProtocol.WriteText(writer, InspectorMessage.Result, verb + " done"));
                    }

                    break;

                default:
                    break;
            }
        }

        public void Report(string name, double value) =>
            Send(writer => InspectorProtocol.WriteCounter(writer, new(name, value)));

        /// <summary>
        ///     A greeting that can claim a version this editor does not speak, which is the one field
        ///     the protocol's own writer will not let a caller vary.
        /// </summary>
        void WriteWelcome(ArrayBufferWriter<byte> writer) {
            if (Version == InspectorProtocol.Version) {
                InspectorProtocol.WriteWelcome(writer, "Test Build");
                return;
            }

            var span = writer.GetSpan(3);

            span[0] = (byte)InspectorMessage.Welcome;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[1..], Version);
            writer.Advance(3);

            var name = "Test Build"u8;
            var text = writer.GetSpan(name.Length + 2);

            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(text, (ushort)name.Length);
            name.CopyTo(text[2..]);
            writer.Advance(name.Length + 2);
        }

        void Send(Action<ArrayBufferWriter<byte>> write) {
            outgoing.Clear();
            write(outgoing);

            transport.SendToClient(client, outgoing.WrittenSpan, Channel.Reliable);
        }
    }
}
