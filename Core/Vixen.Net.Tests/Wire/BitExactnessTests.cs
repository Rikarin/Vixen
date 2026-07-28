// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Wire;

/// <summary>Phase 9's last exit criterion: the same bytes on every machine.</summary>
/// <remarks>
///     <para>
///         <b>Two peers that encode the same value differently do not disagree — they desync, and
///         they do it quietly.</b> A snapshot is a difference measured against a capture the receiver
///         also holds, so one machine rounding a quantized level one step differently from another
///         corrupts every difference measured from it afterwards. Nothing throws, nothing is refused,
///         and the object is in the wrong place on one player's screen for the rest of the match.
///         That is the failure this suite exists to make loud, and it is the same gate content
///         determinism gets for the same reason.
///     </para>
///     <para>
///         <b>The gate is the CI matrix, not a job of its own.</b> <c>ci.yml</c> already runs the
///         tests on <c>ubuntu-latest</c>, <c>windows-latest</c> and <c>macos-14</c> — three operating
///         systems and two architectures, since the macOS runner is arm64 — so a suite that asserts
///         against committed bytes is bit-exactness across all three by construction. A separate job
///         would be the same assertion run a fourth time.
///     </para>
///     <para>
///         <b>Every input here is stated rather than computed.</b> The exotic ones are written as
///         their bits: a test whose inputs came out of <c>MathF.Cos</c> would be testing the
///         platform's libm, which is <i>allowed</i> to differ, and would fail on a difference that
///         never reaches a packet. What is under test is the encoder, given a float — so the float is
///         given, exactly.
///     </para>
///     <para>
///         <b>What makes it pass today, so that a failure is legible.</b> Every arithmetic step on
///         the wire path is IEEE-754 and correctly rounded: <c>QuantizeRange</c> does its work in
///         <c>double</c> with nothing but <c>+ - * /</c>, and the two normalisations the rotation
///         codec leans on are <c>1f / MathF.Sqrt(x)</c>, which is two correctly-rounded operations.
///         There is no transcendental, no fused multiply-add — C# never contracts one — and no
///         reciprocal estimate anywhere in it. A red build here almost certainly means one of those
///         four sentences stopped being true.
///     </para>
/// </remarks>
public sealed class BitExactnessTests {
    /// <summary>Floats worth encoding, each one for a reason.</summary>
    /// <remarks>
    ///     The bit patterns are written out because that is what they are. <c>0x80000000</c> is
    ///     negative zero, which compares equal to zero and is not it; the two denormals are the
    ///     smallest things a float can hold and are where a flush-to-zero mode would show; and the
    ///     three values around a level boundary are where a rounding rule that differed would.
    /// </remarks>
    static readonly (string Name, float Value)[] Interesting = [
        ("zero", 0f),
        ("negative-zero", BitConverter.UInt32BitsToSingle(0x8000_0000)),
        ("one", 1f),
        ("minus-one", -1f),
        ("smallest-denormal", BitConverter.UInt32BitsToSingle(0x0000_0001)),
        ("largest-denormal", BitConverter.UInt32BitsToSingle(0x007F_FFFF)),
        ("epsilon", float.Epsilon),
        ("max", float.MaxValue),
        ("min", float.MinValue),
        ("infinity", float.PositiveInfinity),
        ("negative-infinity", float.NegativeInfinity),
        ("nan", float.NaN),
        // A NaN whose payload is not the canonical one. It must not survive into a packet as
        // anything other than what the encoder decided a NaN is.
        ("nan-payload", BitConverter.UInt32BitsToSingle(0x7FC0_1234)),
        ("pi-ish", 3.14159274f),
        ("third", 0.333333343f),
        ("large", 1_000_000.5f),
        ("tiny", 0.000000119209290f)
    ];

    [Fact]
    public void PacketPrimitivesAreTheSameBytesEverywhere() {
        var listing = WireGolden.Begin();
        var buffer = new byte[64];

        foreach (var (name, value) in Interesting) {
            var writer = new PacketWriter(buffer);
            writer.WriteSingle(value);
            Assert.True(writer.TryFinish(out var packet));
            listing.Case($"single/{name}", packet);
        }

        foreach (var value in (uint[])[0, 1, 127, 128, 300, 16_383, 16_384, 0x0FFF_FFFF, 0x1000_0000, uint.MaxValue]) {
            var writer = new PacketWriter(buffer);
            writer.WriteVariable(value);
            Assert.True(writer.TryFinish(out var packet));
            listing.Case($"varint/{value}", packet);
        }

        var wide = new PacketWriter(buffer);
        wide.WriteUInt16(0xBEEF);
        wide.WriteUInt32(0xDEAD_BEEF);
        wide.WriteUInt64(0x0123_4567_89AB_CDEF);
        wide.WriteInt32(int.MinValue);
        wide.WriteTick(new(0x8000_0001));
        Assert.True(wide.TryFinish(out var widths));

        // Little-endian everywhere, stated in the codec's own remarks so that this has something to
        // assert. The two big-endian desktop platforms do not exist, and that is not the reason —
        // the reason is that a format which is only accidentally consistent becomes inconsistent.
        listing.Case("widths", widths);

        var text = new PacketWriter(buffer);
        text.WriteString("vixen");
        text.WriteString("naïve · 日本語 · 🦊");
        Assert.True(text.TryFinish(out var strings));

        // UTF-8 without a preamble and without normalisation. A string that arrives re-composed is a
        // different string, and a player name is exactly where that would show.
        listing.Case("strings", strings);

        listing.Matches("primitives");
    }

    [Fact]
    public void BitFieldsAreTheSameBitsEverywhere() {
        var listing = WireGolden.Begin();
        var buffer = new byte[64];

        for (var width = 1; width <= 32; width++) {
            var writer = new BitWriter(buffer);
            writer.Write(0xA5A5_A5A5, width);
            Assert.True(writer.TryFinish(out var packet));
            listing.Case($"field/{width}", packet);
        }

        // Fields that straddle byte boundaries, which is where a shift written the other way round
        // produces the same length and different bytes.
        var straddling = new BitWriter(buffer);
        straddling.WriteBool(value: true);
        straddling.Write(0x2A, 6);
        straddling.Write(0x1FF, 9);
        straddling.WriteBool(value: false);
        straddling.Write(0xFFFF_FFFF, 32);
        Assert.True(straddling.TryFinish(out var straddled));
        listing.Case("straddling", straddled);

        var aligned = new BitWriter(buffer);
        aligned.Write(0x5, 3);
        aligned.Align();
        aligned.WriteBytes([1, 2, 3]);
        Assert.True(aligned.TryFinish(out var alignment));
        listing.Case("aligned", alignment);

        listing.Matches("bits");
    }

    /// <summary>Quantization, which is the only float arithmetic on the wire path.</summary>
    /// <remarks>
    ///     The one worth being thorough about. Everything else here is moving bytes; this decides a
    ///     level from a float, and a level that differs by one between two machines is the desync
    ///     described at the top of this file.
    /// </remarks>
    [Fact]
    public void QuantizationPicksTheSameLevelEverywhere() {
        var listing = WireGolden.Begin();

        var ranges = new (string Name, QuantizeRange Range)[] {
            ("unit8", new(0f, 1f, 8)),
            ("signed16", new(-1000f, 1000f, 16)),
            ("rotation10", MathCodec.RotationRange),
            ("position16", NetworkTransformReplicator.PositionRange),
            ("one-bit", new(0f, 1f, 1)),
            ("full-width", new(-1f, 1f, 32)),
            ("asymmetric", new(-0.125f, 7.375f, 12))
        };

        foreach (var (rangeName, range) in ranges) {
            foreach (var (name, value) in Interesting) {
                listing.Case($"encode/{rangeName}/{name}", range.Encode(value));
            }

            // Exactly on a level, and a hair either side of the midpoint between two. If a rounding
            // rule ever differs between machines, it differs here and nowhere else.
            var span = (double)range.Max - range.Min;

            foreach (var step in (uint[])[0, 1, 2, range.Levels / 2, range.Levels - 1, range.Levels]) {
                var onLevel = (float)(range.Min + (step / (double)range.Levels * span));
                listing.Case($"level/{rangeName}/{step}", range.Encode(onLevel));
                listing.Case($"level/{rangeName}/{step}/below", range.Encode(Below(onLevel)));
                listing.Case($"level/{rangeName}/{step}/above", range.Encode(Above(onLevel)));
                listing.Case($"decode/{rangeName}/{step}", range.Decode(step));
            }
        }

        listing.Matches("quantize");
    }

    /// <summary>The rotation codec, which is the only place a decision is made about a float.</summary>
    /// <remarks>
    ///     Smallest-three picks which component to drop by comparing magnitudes and flips the sign of
    ///     the whole quaternion to make the dropped one positive. Both of those are decisions, and a
    ///     normalisation that came out one ULP different on another machine could make a different
    ///     one — which is a two-bit difference in the packet, not a rounding difference. The
    ///     deliberately ambiguous cases below are the ones where two components are equal.
    /// </remarks>
    [Fact]
    public void TheRotationCodecDropsTheSameComponentEverywhere() {
        var listing = WireGolden.Begin();
        var buffer = new byte[16];

        var rotations = new (string Name, Quaternion Value)[] {
            ("identity", Quaternion.Identity),
            ("negated-identity", new(0f, 0f, 0f, -1f)),
            ("x-largest", new(0.9f, 0.1f, 0.2f, 0.3f)),
            ("y-largest", new(0.1f, 0.9f, 0.2f, 0.3f)),
            ("z-largest", new(0.1f, 0.2f, 0.9f, 0.3f)),
            ("w-largest", new(0.1f, 0.2f, 0.3f, 0.9f)),
            ("negative-largest", new(-0.9f, 0.1f, 0.2f, 0.3f)),
            // Two components of equal magnitude. Which one is "largest" is decided by a strict
            // comparison and the order of the loop, so this pins the tie-break rather than leaving
            // it to be discovered.
            ("tied-xy", new(0.5f, 0.5f, 0.5f, 0.5f)),
            ("tied-negated", new(-0.5f, 0.5f, -0.5f, 0.5f)),
            ("half-turn-x", new(1f, 0f, 0f, 0f)),
            ("not-normalized", new(2f, 4f, 8f, 16f)),
            ("degenerate", default)
        };

        foreach (var (name, rotation) in rotations) {
            var writer = new BitWriter(buffer);
            writer.WriteRotation(rotation);
            Assert.True(writer.TryFinish(out var packet));
            listing.Case($"rotation/{name}", packet);

            // The decode too, because a receiver that reconstructs the dropped component differently
            // is the same desync arriving from the other end.
            var reader = new BitReader(packet);
            Assert.True(reader.TryReadRotation(out var decoded));
            listing.Case($"rotation/{name}/x", decoded.X);
            listing.Case($"rotation/{name}/y", decoded.Y);
            listing.Case($"rotation/{name}/z", decoded.Z);
            listing.Case($"rotation/{name}/w", decoded.W);
        }

        listing.Matches("rotation");
    }

    /// <summary>The whole stack: a world, captured and written the way a server writes it.</summary>
    /// <remarks>
    ///     The other tests pin the pieces; this pins them composed. A snapshot's bytes depend on the
    ///     record header, the order values are visited in, the delta codec's width selectors and the
    ///     baseline bookkeeping, and none of those is covered by encoding one float at a time.
    /// </remarks>
    [Fact]
    public void AWholeSnapshotIsTheSameBytesEverywhere() {
        var listing = WireGolden.Begin();

        using var world = new World("bit-exactness");
        var registry = new ReplicationRegistry();
        registry.Register(new NetworkTransformReplicator());

        var server = new ReplicationServer(registry);
        var ids = new NetworkIdAllocator();
        var player = new PlayerId(1);
        var buffer = new byte[2048];
        var entities = new List<Entity>();

        // Positions and rotations stated rather than generated. A loop with a trigonometric function
        // in it would make this a test of the platform's libm — see the remarks on the class.
        var placements = new (Vector3 Position, Quaternion Rotation)[] {
            (new(0f, 0f, 0f), Quaternion.Identity),
            (new(1.5f, -2.25f, 3.125f), new(0.5f, 0.5f, 0.5f, 0.5f)),
            (new(-999.9f, 0.0625f, 999.9f), new(0.1f, 0.2f, 0.3f, 0.9f)),
            (new(0.03051804f, -0.03051804f, 0.015259f), new(-0.7071068f, 0f, 0f, 0.7071068f))
        };

        foreach (var (position, rotation) in placements) {
            entities.Add(world.Create(ids.Next(), new NetworkTransform { Position = position, Rotation = rotation }));
        }

        for (var tick = 1u; tick <= 4; tick++) {
            world.AdvanceVersion();

            // A movement that is exactly representable, so the value being captured is the value
            // written here rather than one the addition rounded.
            foreach (var entity in entities) {
                ref var transform = ref world.Get<NetworkTransform>(entity);
                transform.Position += new Vector3(0.25f, 0f, -0.5f);
            }

            server.Capture(world, new(tick));
            Assert.True(server.TryWriteSnapshot(world, player, new(tick), buffer, out var snapshot));
            listing.Case($"snapshot/tick{tick}", snapshot);

            // Acknowledged a tick late, so ticks three and four go out as differences and the delta
            // codec's selectors are in the bytes above.
            if (tick > 1) {
                server.Acknowledge(player, new(tick - 1));
            }
        }

        listing.Matches("snapshot");
    }

    /// <summary>A game's own components, rather than the one the engine ships.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="NetworkTransform" /> is hand-written and unusual — smallest-three rotations,
    ///         a teleport counter, a bespoke lane layout. What a game actually replicates is a struct
    ///         with <c>[Quantize]</c> on some fields and plain integers on others, and its record
    ///         header, its lane widths and the order two component types are visited in are all part
    ///         of the wire and none of them are covered above.
    ///     </para>
    ///     <para>
    ///         Two types, so the registry's ordering is in the bytes. Types are ordered by hashed id
    ///         rather than by registration order — deliberately, so two builds agree without agreeing
    ///         on start-up order — which means the index a record names is a function of the type
    ///         <i>names</i>, and renaming a replicated component is a wire break. That is worth
    ///         having pinned somewhere it will be noticed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AGamesOwnComponentsAreTheSameBytesEverywhere() {
        var listing = WireGolden.Begin();

        using var world = new World("bit-exactness-components");
        var registry = new ReplicationRegistry();
        registry.Register(new Replication.PositionReplicator());
        registry.Register(new Replication.HealthReplicator());

        var server = new ReplicationServer(registry);
        var ids = new NetworkIdAllocator();
        var player = new PlayerId(1);
        var buffer = new byte[1024];

        listing.Case("registry/position-index", (uint)registry.IndexOf(new Replication.PositionReplicator().TypeId));
        listing.Case("registry/health-index", (uint)registry.IndexOf(new Replication.HealthReplicator().TypeId));

        var first = world.Create(
            ids.Next(),
            new Replication.ReplicatedPosition { X = 0f, Y = -1000f, Z = 1000f },
            new Replication.ReplicatedHealth { Value = 100 }
        );

        var second = world.Create(
            ids.Next(),
            new Replication.ReplicatedPosition { X = 0.030518f, Y = -0.030518f, Z = 512.25f },
            new Replication.ReplicatedHealth { Value = int.MinValue }
        );

        for (var tick = 1u; tick <= 3; tick++) {
            world.AdvanceVersion();

            world.Get<Replication.ReplicatedPosition>(first).X += 0.5f;
            world.Get<Replication.ReplicatedHealth>(second).Value += 1;

            server.Capture(world, new(tick));
            Assert.True(server.TryWriteSnapshot(world, player, new(tick), buffer, out var snapshot));
            listing.Case($"components/tick{tick}", snapshot);

            if (tick > 1) {
                server.Acknowledge(player, new(tick - 1));
            }
        }

        listing.Matches("components");
    }

    /// <summary>The next float below a value, as a bit pattern rather than a subtraction.</summary>
    /// <remarks>
    ///     <c>MathF.BitDecrement</c> would do, and writing it out makes the point that these inputs
    ///     are chosen bit patterns: "a hair below" has to mean one ULP, not one of whatever
    ///     <c>0.0001f</c> works out to at this magnitude.
    /// </remarks>
    static float Below(float value) => MathF.BitDecrement(value);

    static float Above(float value) => MathF.BitIncrement(value);
}
