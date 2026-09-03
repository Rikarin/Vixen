// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Generated;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Vixen.Net.Tests.Wire;
using Xunit;

namespace Vixen.Net.Generators.Tests.Wire;

/// <summary>The generated encoders, pinned to committed bytes rather than to each other.</summary>
/// <remarks>
///     <para>
///         <b>What this catches that the rest of the suite does not.</b>
///         <see cref="ReplicationGeneratorTests.TheGeneratedReplicatorWritesExactlyWhatAHandWrittenOneWrites" />
///         is a differential, and a differential is blind to anything that moves both halves at once:
///         both sides call <see cref="BitWriter.WriteQuantized" />, both compute a type id through
///         <see cref="ReplicationRegistry.HashTypeName" />, and both are compiled from this tree in
///         the same build. A change underneath them changes the wire and leaves the comparison green.
///     </para>
///     <para>
///         <b>And the hand-written half only exists for one type.</b>
///         <c>GeneratedPose</c>, <c>GeneratedScore</c> and every RPC in this assembly have no twin at
///         all — what held them was a length (<c>Assert.Equal(10, bits.Length)</c>,
///         <c>Assert.InRange(sent.Length, 1, 9)</c>), which a reordered field list satisfies exactly.
///         ⚠ The RPC manifest hash and the replication manifest hash, which are the two numbers a
///         handshake refuses a peer over, were asserted only to be <i>non-zero</i>.
///     </para>
///     <para>
///         The corpus lives here rather than in <c>Vixen.Net.Tests</c> because this is the only test
///         project the generator runs in — it is referenced as an <c>Analyzer</c> as well as a
///         reference, so the code under test is emitted while this assembly compiles.
///         <c>Vixen.Net.Tests</c>'s <c>components.txt</c> pins two <i>hand-written</i> replicators,
///         which is the specification and not the thing that ships.
///     </para>
///     <para>
///         Regenerate with <c>UPDATE_GOLDEN=1</c> and read the diff: every line of it is a wire
///         format change, and a game already in the field cannot take one.
///     </para>
/// </remarks>
public sealed class GeneratedWireTests {
    /// <summary>Values chosen for the reasons the wire cares about, and stated rather than computed.</summary>
    /// <remarks>
    ///     Negative zero compares equal to zero and is not it; the denormal is where a
    ///     flush-to-zero mode would show; the two out-of-range values are what a
    ///     <see cref="QuantizeRange" /> has to clamp identically on every machine; and nothing here
    ///     came out of a transcendental, because a test whose inputs came from <c>MathF.Cos</c> is a
    ///     test of the platform's libm.
    /// </remarks>
    static readonly (string Name, float Value)[] Interesting = [
        ("zero", 0f),
        ("negative-zero", BitConverter.UInt32BitsToSingle(0x8000_0000)),
        ("smallest-denormal", BitConverter.UInt32BitsToSingle(0x0000_0001)),
        ("exact", 12.5f),
        ("negative-exact", -400.25f),
        ("below-range", -1000.5f),
        ("above-range", 1000.5f),
        ("nan", float.NaN),
        ("infinity", float.PositiveInfinity)
    ];

    /// <summary>Every generated record, byte for byte, for inputs that are written down.</summary>
    [Fact]
    public void AGeneratedRecordIsTheSameBytesEverywhere() {
        var listing = WireGolden.Begin();

        using var world = new World("generated-records");

        foreach (var (name, value) in Interesting) {
            var entity = world.Create(
                new GeneratedTransform {
                    X = value,
                    Y = -value,
                    Yaw = value,
                    Frame = -913,
                    Team = 3,
                    Grounded = true
                }
            );

            listing.Case($"transform/{name}", Encode(Find(typeof(GeneratedTransform)), world, entity));
        }

        // The lanes that are not floats, so a width written one bit wide is in the diff rather than
        // in a match. int.MinValue and byte.MaxValue are the ends of two of them.
        var edges = world.Create(
            new GeneratedTransform {
                X = 0f,
                Y = 0f,
                Yaw = 0f,
                Frame = int.MinValue,
                Team = byte.MaxValue,
                Grounded = false
            }
        );

        listing.Case("transform/edges", Encode(Find(typeof(GeneratedTransform)), world, edges));

        var poses = new (string Name, Vector3 Position, Quaternion Rotation)[] {
            ("identity", Vector3.Zero, Quaternion.Identity),
            ("negated-identity", Vector3.Zero, new(0f, 0f, 0f, -1f)),
            ("tied", new(1.5f, -2.25f, 3.125f), new(0.5f, 0.5f, 0.5f, 0.5f)),
            ("w-largest", new(-999.9f, 0.0625f, 999.9f), new(0.1f, 0.2f, 0.3f, 0.9f)),
            ("degenerate", new(0.03051804f, -0.03051804f, 0.015259f), default)
        };

        foreach (var (name, position, rotation) in poses) {
            var entity = world.Create(new GeneratedPose { Position = position, Rotation = rotation });
            listing.Case($"pose/{name}", Encode(Find(typeof(GeneratedPose)), world, entity));
        }

        foreach (var value in (uint[])[0, 1, 127, 128, uint.MaxValue]) {
            var entity = world.Create(new GeneratedScore { Value = value });
            listing.Case($"score/{value}", Encode(Find(typeof(GeneratedScore)), world, entity));
        }

        listing.Matches("generated-records");
    }

    /// <summary>The numbers a handshake refuses a peer over.</summary>
    /// <remarks>
    ///     A record on the wire names an <i>index</i>, and the index is a position in an ordering
    ///     derived from hashed type names. So the id, the index and the manifest hash are as much a
    ///     part of the format as the bits are: a generator that started emitting a short name instead
    ///     of a full one would renumber every record in the game and break nothing that compiles.
    /// </remarks>
    [Fact]
    public void TheGeneratedRegistryNumbersEveryTypeTheSameEverywhere() {
        var listing = WireGolden.Begin();
        var registry = new ReplicationRegistry();

        ReplicatedComponents.RegisterAll(registry);

        listing.Case("registry/count", (uint)registry.Count);
        listing.Case("registry/manifest-hash", registry.ManifestHash);

        foreach (var component in (Type[])[typeof(GeneratedTransform), typeof(GeneratedPose), typeof(GeneratedScore)]) {
            var id = ReplicationRegistry.HashTypeName(component.FullName!);

            listing.Case($"registry/{component.Name}/type-id", id);
            listing.Case($"registry/{component.Name}/index", (uint)registry.IndexOf(id));

            Assert.True(registry.TryGet(id, out var replicator));

            // The lane widths, which is what the delta codec measures a difference against. A field
            // added, removed or reordered moves this line and nothing else has to notice.
            listing.Case($"registry/{component.Name}/bits", (uint)DeltaCodec.TotalBits(replicator!.Lanes));
            listing.Case($"registry/{component.Name}/lanes", (uint)replicator.Lanes.Length);
            listing.Case($"registry/{component.Name}/channel", (uint)replicator.Channel);
            listing.Case($"registry/{component.Name}/priority", unchecked((uint)replicator.Priority));
        }

        listing.Matches("generated-registry");
    }

    /// <summary>The whole stack, with nothing hand-written in it.</summary>
    /// <remarks>
    ///     The composition the corpus was missing. A snapshot's bytes are the record header, the
    ///     order the registry visits types in, the delta codec's width selectors and the baseline
    ///     bookkeeping — <i>and</i> the generated lanes underneath all of it. Ticks three and four go
    ///     out as differences, so the selectors are in the bytes rather than assumed.
    /// </remarks>
    [Fact]
    public void AWholeSnapshotOfGeneratedComponentsIsTheSameBytesEverywhere() {
        var listing = WireGolden.Begin();

        using var world = new World("generated-snapshot");
        var registry = new ReplicationRegistry();

        ReplicatedComponents.RegisterAll(registry);

        var server = new ReplicationServer(registry);
        var ids = new NetworkIdAllocator();
        var player = new PlayerId(1);
        var buffer = new byte[2048];

        var first = world.Create(
            ids.Next(),
            new GeneratedTransform { X = 0f, Y = -1000f, Yaw = 0.5f, Frame = 0, Team = 1, Grounded = true },
            new GeneratedScore { Value = 0 }
        );

        var second = world.Create(
            ids.Next(),
            new GeneratedPose { Position = new(0.030518f, -0.030518f, 512.25f), Rotation = Quaternion.Identity },
            new GeneratedScore { Value = uint.MaxValue }
        );

        for (var tick = 1u; tick <= 4; tick++) {
            world.AdvanceVersion();

            // Movements that are exactly representable, so what is captured is what is written here
            // rather than what an addition rounded.
            world.Get<GeneratedTransform>(first).X += 0.5f;
            world.Get<GeneratedTransform>(first).Frame += 1;
            world.Get<GeneratedScore>(first).Value += 3;
            world.Get<GeneratedPose>(second).Position += new Vector3(0.25f, 0f, -0.5f);

            server.Capture(world, new(tick));
            Assert.True(server.TryWriteSnapshot(world, player, new(tick), buffer, out var snapshot));
            listing.Case($"snapshot/tick{tick}", snapshot);

            // Acknowledged a tick late, so ticks three and four are differences.
            if (tick > 1) {
                server.Acknowledge(player, new(tick - 1));
            }
        }

        listing.Matches("generated-snapshot");
    }

    /// <summary>The generated senders, whose payloads nothing pinned.</summary>
    /// <remarks>
    ///     <para>
    ///         A call's payload is a target id, a type index, a method index and then the arguments —
    ///         and the three indices are positions in an ordering derived from hashed signatures. A
    ///         signature hash that changed, or a table sorted the other way round, dispatches every
    ///         call in the game to the wrong handler on a peer built a day earlier. Nothing throws.
    ///     </para>
    ///     <para>
    ///         The ids are listed as well as the bytes, because the bytes carry the <i>index</i>: two
    ///         builds that agree on three indices and disagree on which method each names produce
    ///         identical packets and different behaviour.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AGeneratedRpcCallIsTheSameBytesEverywhere() {
        var listing = WireGolden.Begin();
        var manifest = new RpcManifest();

        RpcMethods.RegisterAll(manifest);

        listing.Case("manifest/type-count", (uint)manifest.TypeCount);
        listing.Case("manifest/method-count", (uint)manifest.MethodCount);
        listing.Case("manifest/hash", manifest.ManifestHash);

        foreach (var method in GeneratedTurret.RpcMethodTable) {
            listing.Case($"method/{method.Signature}/type-id", method.TypeId);
            listing.Case($"method/{method.Signature}/method-id", method.MethodId);
            listing.Case($"method/{method.Signature}/type-index", unchecked((uint)method.TypeIndex));
            listing.Case($"method/{method.Signature}/method-index", unchecked((uint)method.MethodIndex));
            listing.Case($"method/{method.Signature}/kind", (uint)method.Kind);
            listing.Case($"method/{method.Signature}/channel", (uint)method.Channel);
            listing.Case($"method/{method.Signature}/target", (uint)method.Target);
        }

        var transport = new CapturingTransport();

        // Host, so both a client-bound and a server-bound sender will actually send. The target id is
        // a stated number rather than an allocated one — it is in the first varint of every payload.
        var router = new RpcRouter(manifest, transport, RpcRole.Host);
        var turret = new GeneratedTurret(new(5), router);

        router.Register(new(5), turret);

        foreach (var damage in (int[])[0, 1, -1, int.MinValue, int.MaxValue]) {
            transport.Sent.Clear();
            turret.Rpc.Fire(damage);
            listing.Case($"call/Fire/{damage}", Assert.Single(transport.Sent));
        }

        foreach (var (name, value) in Interesting) {
            transport.Sent.Clear();

            // `at` is a plain float and `intensity` is [Quantize(0f, 1f, 8)], so one lane is the bits
            // it was given and the other is a level. Both from the same input, which is the point.
            turret.Rpc.PlayEffect(value, value);
            listing.Case($"call/PlayEffect/{name}", Assert.Single(transport.Sent));
        }

        transport.Sent.Clear();
        turret.Rpc.Salute();

        // No arguments at all: the header on its own, which is what a mis-sized header shows up in.
        listing.Case("call/Salute", Assert.Single(transport.Sent));

        // A different target id, because the id is a varint and a varint's width depends on it.
        transport.Sent.Clear();
        var far = new GeneratedTurret(new(300_000), router);
        router.Register(new(300_000), far);
        far.Rpc.Fire(7);
        listing.Case("call/Fire/far-target", Assert.Single(transport.Sent));

        listing.Matches("generated-rpc");
    }

    static IComponentReplicator Find(Type component) {
        var registry = new ReplicationRegistry();

        ReplicatedComponents.RegisterAll(registry);
        Assert.True(registry.TryGet(ReplicationRegistry.HashTypeName(component.FullName!), out var replicator));

        return replicator!;
    }

    static byte[] Encode(IComponentReplicator replicator, World world, Core.Entity entity) {
        var writer = new BitWriter(new byte[256]);
        replicator.Write(world, entity, ref writer);

        Assert.True(writer.TryFinish(out var bits));

        return bits.ToArray();
    }

    /// <summary>Keeps what was sent instead of sending it, whoever it was for.</summary>
    sealed class CapturingTransport : IRpcTransport {
        public List<byte[]> Sent { get; } = [];

        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) => Sent.Add(payload.ToArray());

        public void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) =>
            Sent.Add(payload.ToArray());

        public void SendToAll(ReadOnlySpan<byte> payload, Channel channel) => Sent.Add(payload.ToArray());
    }
}
