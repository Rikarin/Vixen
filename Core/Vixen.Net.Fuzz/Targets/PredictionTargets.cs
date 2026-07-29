// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Prediction;

namespace Vixen.Net.Fuzz.Targets;

/// <summary>The input buffer, taking a run of inputs from a client that is not to be trusted.</summary>
/// <remarks>
///     <para>
///         <b>The second thing a client can make a server do work for.</b> An RPC is the first; this
///         is the other, and it arrives every tick rather than when a player does something. The
///         header is a tick and a count, and the count says how many more structures to decode — a
///         length a caller supplies, which is the shape of every packet bug the reader was built to
///         refuse.
///     </para>
///     <para>
///         <b>What is really under test is the memory bound.</b> A client controls the tick it stamps
///         its inputs with, so it can ask the server to hold a second of its future, or an hour of
///         it. <c>Capacity</c> is what turns that into a refusal, and the allowance below is what
///         says so: no input, however constructed, may make this allocate in proportion to the
///         numbers inside it.
///     </para>
/// </remarks>
public sealed class InputBufferTarget : IFuzzTarget {
    readonly InputBuffer<FuzzInput> buffer = new() { Capacity = 32 };

    Counters before;
    uint simulated;

    /// <inheritdoc />
    public string Name => "input";

    /// <inheritdoc />
    public string What => "InputBuffer.TryReceive: a run of predicted inputs from a client";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     The dictionary grows to <c>Capacity</c> and stops, so the steady state allocates nothing at
    ///     all: every case either files into a slot that exists or is refused. The constant covers the
    ///     one growth to that size.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 4096 + (8L * inputLength);

    /// <summary>What the buffer is holding, which must stay inside its capacity.</summary>
    public long Held => buffer.Depth;

    /// <summary>The most it may hold.</summary>
    public long HeldCap => 32;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // Well-formed runs of every length the count byte can honestly describe.
        foreach (var count in (uint[])[0, 1, 2, 4, 8, 32, 64, 255]) {
            corpus.Add(Run(first: 1, count, real: count));
        }

        // A count that promises more than the payload carries, which is the classic.
        corpus.Add(Run(first: 1, count: 255, real: 1));
        corpus.Add(Run(first: 1, count: 8, real: 0));

        // Ticks at both ends of the space, and either side of the wrap — where "is this after that"
        // is a subtraction that means something different from a comparison.
        foreach (var first in (uint[])[0, 1, uint.MaxValue, uint.MaxValue - 3, 0x8000_0000]) {
            corpus.Add(Run(first, count: 4, real: 4));
        }
    }

    /// <inheritdoc />
    public void Maintain() {
        // The simulation moves on, which is what turns a held input into a late one and drains the
        // buffer. Without it every case would be filed and the capacity would be the only thing
        // this ever exercised.
        simulated += 7;
        buffer.TryTake(new(simulated), out _);
        before = Read();
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var accepted = buffer.TryReceive(input, new(simulated));

        // Per-case deltas, not lifetime totals: folding the totals in makes every case look novel
        // and a corpus that keeps everything guides nothing. See IFuzzTarget.Run.
        var after = Read();
        var signature = (accepted ? 1_000_003L : 0)
            + ((after.Filed - before.Filed) * 7919L)
            + ((after.Late - before.Late) * 131L)
            + ((after.Duplicate - before.Duplicate) * 127L)
            + ((after.Refused - before.Refused) * 113L)
            + (after.Malformed - before.Malformed);

        before = after;

        return signature;
    }

    Counters Read() =>
        new(buffer.Depth, buffer.LateCount, buffer.DuplicateCount, buffer.RefusedCount, buffer.MalformedCount);

    static byte[] Run(uint first, uint count, uint real) {
        var bytes = new byte[8 + (real * 3)];
        var writer = new BitWriter(bytes);

        writer.WriteUInt32(first);
        writer.Write(count, 8);

        for (var index = 0u; index < real; index++) {
            new FuzzInput { Bits = (ushort)index }.Write(ref writer);
        }

        writer.TryFinish(out var payload);

        return payload.ToArray();
    }

    readonly record struct Counters(long Filed, long Late, long Duplicate, long Refused, long Malformed);

    /// <summary>Sixteen bits and a flag, which is the shape of a real one.</summary>
    readonly record struct FuzzInput : IPredictedInput<FuzzInput> {
        public ushort Bits { get; init; }

        public void Write(ref BitWriter writer) {
            writer.Write(Bits, 16);
            writer.WriteBool((Bits & 1) != 0);
        }

        public static bool TryRead(ref BitReader reader, out FuzzInput value) {
            value = default;

            if (!reader.TryRead(16, out var bits) || !reader.TryReadBool(out _)) {
                return false;
            }

            value = new() { Bits = (ushort)bits };

            return true;
        }
    }
}
