// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Vixen.Net.Generated;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Generators.Tests;

/// <summary>The RPC generator: the senders it writes, and what it refuses to write them for.</summary>
public sealed class RpcGeneratorTests {
    const string Preamble = """
        using Vixen.Net;
        using Vixen.Net.Replication;
        using Vixen.Net.Rpc;

        namespace Subject;
        """;

    static readonly PlayerId Owner = new(1);
    static readonly PlayerId Stranger = new(2);
    static readonly NetworkId Object = new(5);

    readonly RpcManifest manifest = new();
    readonly RecordingTransport transport = new();
    readonly RpcRouter server;
    readonly GeneratedTurret onServer;

    public RpcGeneratorTests() {
        RpcMethods.RegisterAll(manifest);
        server = new(manifest, transport, RpcRole.Server);
        onServer = new(Object, server);
        server.Register(Object, onServer);
        server.Ownership.SetOwner(Object, Owner);
    }

    [Fact]
    public void EveryCallInThisAssemblyIsInTheManifest() {
        Assert.Equal(1, manifest.TypeCount);
        Assert.Equal(3, manifest.MethodCount);
        Assert.NotEqual(0u, manifest.ManifestHash);
        Assert.NotEqual(-1, manifest.IndexOf(GeneratedTurret.RpcMethodTable[0].TypeId));
    }

    [Fact]
    public void TheTableIsOrderedByIdSoTwoBuildsNumberTheCallsTheSame() {
        var table = GeneratedTurret.RpcMethodTable;

        for (var i = 1; i < table.Length; i++) {
            Assert.True(table[i].MethodId > table[i - 1].MethodId, $"{table[i]} is out of order.");
        }

        for (var i = 0; i < table.Length; i++) {
            Assert.Equal(i, table[i].MethodIndex);
        }
    }

    [Fact]
    public void ASenderSendsAndTheHandlerOnTheOtherSideRuns() {
        var client = new RpcRouter(manifest, transport, RpcRole.Client);
        var onClient = new GeneratedTurret(Object, client);

        onClient.Rpc.Fire(42);

        var sent = Assert.Single(transport.ToServer);

        Assert.True(server.Receive(Owner, sent));
        Assert.Equal([42], onServer.Fired);

        // And it ran on the server, not on the client that asked for it.
        Assert.Empty(onClient.Fired);
    }

    [Fact]
    public void AHandlerIsToldWhoCalledItWithoutTheCallerSayingSo() {
        var client = new RpcRouter(manifest, transport, RpcRole.Client);
        var onClient = new GeneratedTurret(Object, client);

        onClient.Rpc.Salute();

        Assert.True(server.Receive(Stranger, Assert.Single(transport.ToServer)));
        Assert.Equal(Stranger, onServer.LastCaller);
    }

    [Fact]
    public void AQuantizedArgumentArrivesWithinItsStatedError() {
        var client = new RpcRouter(manifest, transport, RpcRole.Client);
        var onClient = new GeneratedTurret(Object, client);
        client.Register(Object, onClient);

        onServer.Rpc.PlayEffect(12.5f, 0.4f);

        var sent = Assert.Single(transport.ToAll);

        Assert.True(client.Receive(PlayerId.None, sent));

        var effect = Assert.Single(onClient.Effects);
        var range = new QuantizeRange(0f, 1f, 8);

        Assert.Equal(12.5f, effect.At);
        Assert.InRange(effect.Intensity, 0.4f - range.MaxError, 0.4f + range.MaxError);

        // Eight bits for the intensity, thirty-two for the position that did not declare a range.
        Assert.InRange(sent.Length, 1, 9);
    }

    [Fact]
    public void ASenderWithNoRouterDoesNothingRatherThanThrowing() {
        // What an object that has been made but not attached to a session looks like. A game that
        // spawned one a frame early should not crash for it.
        var detached = new GeneratedTurret(Object, null);

        detached.Rpc.Fire(1);

        Assert.Empty(transport.ToServer);
    }

    [Fact]
    public void ACallFromSomebodyWhoDoesNotOwnTheObject_IsRefused() {
        var client = new RpcRouter(manifest, transport, RpcRole.Client);
        var onClient = new GeneratedTurret(Object, client);

        onClient.Rpc.Fire(42);

        Assert.False(server.Receive(Stranger, Assert.Single(transport.ToServer)));
        Assert.Empty(onServer.Fired);
        Assert.Equal(1, server.RefusedByOwnershipCount);
    }

    [Fact]
    public void TheGeneratorAndTheRuntimeAgreeAboutIds() {
        var table = GeneratedTurret.RpcMethodTable;

        Assert.Equal(RpcMethod.Hash("Vixen.Net.Generators.Tests.GeneratedTurret"), table[0].TypeId);
        Assert.Equal(
            RpcMethod.Hash($"Vixen.Net.Generators.Tests.GeneratedTurret.{table[0].Signature}"),
            table[0].MethodId
        );
    }

    [Fact]
    public void TheAttributesDefaultsArriveInTheTable() {
        var fire = Find("Fire(int)");
        var effect = Find("PlayEffect(float,float)");
        var salute = Find("Salute(RpcContext)");

        Assert.Equal(RpcKind.Server, fire.Kind);
        Assert.True(fire.RequireOwnership);
        Assert.Equal(Channel.Reliable, fire.Channel);

        Assert.Equal(RpcKind.Client, effect.Kind);
        Assert.Equal(Channel.Unreliable, effect.Channel);
        Assert.Equal(RpcTarget.Observers, effect.Target);

        // Ownership off because it says so, and the channel is the ServerRpc default.
        Assert.False(salute.RequireOwnership);
        Assert.Equal(Channel.Reliable, salute.Channel);
    }

    [Fact]
    public void ATypeThatIsNotPartial_IsAnError() {
        var (diagnostics, sources) = GeneratorHarness.RunRpc(
            $$"""
            {{Preamble}}

            public sealed class Sealed : IRpcObject {
                public NetworkId NetworkId => default;
                public RpcRouter? RpcRouter => null;

                [ServerRpc]
                void Go() { }
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET2002");
        Assert.Empty(sources);
    }

    [Fact]
    public void ATypeThatDoesNotSayWhatItsCallsAreAbout_IsAnError() {
        var (diagnostics, _) = GeneratorHarness.RunRpc(
            $$"""
            {{Preamble}}

            public sealed partial class Loose {
                [ServerRpc]
                void Go() { }
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET2003");
    }

    [Fact]
    public void TheSameComplaintIsMadeOncePerTypeRatherThanOncePerCall() {
        var (diagnostics, _) = GeneratorHarness.RunRpc(
            $$"""
            {{Preamble}}

            public sealed partial class Loose {
                [ServerRpc]
                void One() { }

                [ServerRpc]
                void Two() { }

                [ClientRpc]
                void Three() { }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXNET2003");
    }

    [Fact]
    public void AnArgumentThatCannotBeSent_IsAnError() {
        var (diagnostics, _) = GeneratorHarness.RunRpc(
            $$"""
            {{Preamble}}

            public sealed partial class Talker : IRpcObject {
                public NetworkId NetworkId => default;
                public RpcRouter? RpcRouter => null;

                [ServerRpc]
                void Say(string words) { }
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET2001");
    }

    [Fact]
    public void ACallThatReturnsSomething_IsAnError() {
        var (diagnostics, _) = GeneratorHarness.RunRpc(
            $$"""
            {{Preamble}}

            public sealed partial class Asker : IRpcObject {
                public NetworkId NetworkId => default;
                public RpcRouter? RpcRouter => null;

                [ServerRpc]
                int Ask() => 1;
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET2004");
    }

    [Fact]
    public void AHandlerMarkedBothWays_IsAnError() {
        var (diagnostics, _) = GeneratorHarness.RunRpc(
            $$"""
            {{Preamble}}

            public sealed partial class Confused : IRpcObject {
                public NetworkId NetworkId => default;
                public RpcRouter? RpcRouter => null;

                [ServerRpc]
                [ClientRpc]
                void Go() { }
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET2005");
    }

    [Fact]
    public void ANestedType_IsAnError() {
        var (diagnostics, _) = GeneratorHarness.RunRpc(
            $$"""
            {{Preamble}}

            public sealed class Outer {
                public sealed partial class Inner : IRpcObject {
                    public NetworkId NetworkId => default;
                    public RpcRouter? RpcRouter => null;

                    [ServerRpc]
                    void Go() { }
                }
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET2006");
    }

    [Fact]
    public void TheGeneratedCodeCompiles() {
        var diagnostics = GeneratorHarness.CompileWithGeneratedCode(
            $$"""
            {{Preamble}}

            public sealed partial class Everything : IRpcObject {
                public NetworkId NetworkId => default;
                public RpcRouter? RpcRouter => null;

                [ServerRpc(RequireOwnership = false, Channel = Channel.ReliableUnordered)]
                void Numbers(int a, uint b, short c, ushort d, byte e, sbyte f, bool g) { }

                [ClientRpc(Target = RpcTarget.Owner, Channel = Channel.Sequenced)]
                void Floats(float exact, [Quantize(-1f, 1f, 12)] float packed) { }

                [ServerRpc]
                void WithContext(in RpcContext context, int value) { }
            }
            """,
            rpc: true
        );

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void EditingSomethingElseReRunsNothing() {
        var reasons = GeneratorHarness.ReasonsOnSecondRun(
            $$"""
            {{Preamble}}

            public sealed partial class Watched : IRpcObject {
                public NetworkId NetworkId => default;
                public RpcRouter? RpcRouter => null;

                [ServerRpc]
                void Go(int value) { }
            }
            """,
            rpc: true
        );

        Assert.NotEmpty(reasons);
        Assert.All(
            reasons,
            reason => Assert.True(
                reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"An unrelated edit re-ran the step: {reason}."
            )
        );
    }

    static RpcMethod Find(string signature) {
        foreach (var method in GeneratedTurret.RpcMethodTable) {
            if (method.Signature == signature) {
                return method;
            }
        }

        throw new InvalidOperationException(signature);
    }

    /// <summary>Records what was sent instead of sending it.</summary>
    sealed class RecordingTransport : IRpcTransport {
        public List<byte[]> ToServer { get; } = [];

        public List<byte[]> ToAll { get; } = [];

        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) => ToServer.Add(payload.ToArray());

        public void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) =>
            ToAll.Add(payload.ToArray());

        public void SendToAll(ReadOnlySpan<byte> payload, Channel channel) => ToAll.Add(payload.ToArray());
    }
}
