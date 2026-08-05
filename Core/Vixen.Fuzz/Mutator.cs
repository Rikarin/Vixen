// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Fuzz;

/// <summary>Turns one input into a slightly different one.</summary>
/// <remarks>
///     <para>
///         AFL's havoc stage, with the operations that matter for a binary protocol and none of the
///         ones that do not. <b>Uniform random bytes are a bad fuzzer for a codec</b>: a snapshot
///         starts with a 32-bit tick and a client drops anything not newer than the last one, so
///         random input is refused at the first field about four billion times out of four billion.
///         Everything interesting is found by taking something that decoded and breaking one thing
///         about it.
///     </para>
///     <para>
///         <b>The interesting bytes are interesting for reasons.</b> <c>0x80</c> is a varint
///         continuation, so a run of them is the encoding that walks a reader forward without ever
///         terminating; <c>0xFF</c> is both the largest length field and a continuation; <c>0x7F</c>
///         is the largest single-byte varint. A length field set to its maximum is the classic way
///         to turn a decoder into an allocator, and it is one byte away from every well-formed
///         input in the corpus.
///     </para>
///     <para>
///         Driven by <see cref="FuzzRandom" />, for the reason stated there: a fuzzer that cannot be
///         replayed has found nothing it can hand you.
///     </para>
/// </remarks>
public sealed class Mutator {
    /// <summary>The byte values a length or a varint is worth being.</summary>
    static ReadOnlySpan<byte> Interesting => [0x00, 0x01, 0x02, 0x0F, 0x7F, 0x80, 0x81, 0xFE, 0xFF];

    /// <summary>The largest input this will produce.</summary>
    /// <remarks>
    ///     A transport's payload cap is around 1200 bytes for UDP and 64 KB for WebSocket, so a
    ///     decoder never sees more than that in the first place. Fuzzing far past it spends the
    ///     budget on lengths that cannot occur.
    /// </remarks>
    public const int MaxLength = 1400;

    readonly FuzzRandom random;
    readonly byte[] scratch = new byte[MaxLength];

    /// <summary>Creates a mutator.</summary>
    /// <param name="seed">What to start the generator at. The same seed replays the same run.</param>
    public Mutator(ulong seed) => random = new(seed);

    /// <summary>Produces a mutant of an input, possibly spliced with another.</summary>
    /// <param name="input">What to start from.</param>
    /// <param name="other">Something else in the corpus, to splice from. May be the same as <paramref name="input" />.</param>
    /// <returns>The mutant. A fresh array, because a target may hold on to what it was given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input" /> or <paramref name="other" /> is null.</exception>
    public byte[] Mutate(byte[] input, byte[] other) {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(other);

        var length = Math.Min(input.Length, MaxLength);
        input.AsSpan(0, length).CopyTo(scratch);

        // Several operations rather than one. A single bit flip explores slowly; a handful at a
        // time is what gets from a well-formed packet to a differently-shaped one within a run.
        var rounds = 1 + (int)(random.Next() % 6);

        for (var i = 0; i < rounds; i++) {
            length = Apply(length, other);
        }

        return scratch.AsSpan(0, length).ToArray();
    }

    /// <summary>Produces an input from nothing, for the cases a corpus would never suggest.</summary>
    /// <returns>Random bytes, of a random length.</returns>
    /// <remarks>
    ///     Kept alongside the corpus-driven mutations rather than replaced by them. A corpus is a
    ///     record of shapes that have been seen, and the empty input, the one-byte input and the
    ///     input that is nothing but continuation bytes are shapes nothing produces by accident.
    /// </remarks>
    public byte[] Fresh() {
        var length = (int)(random.Next() % 96);
        var bytes = new byte[length];

        for (var i = 0; i < length; i++) {
            bytes[i] = (byte)(random.Next() & 0xFF);
        }

        return bytes;
    }

    int Apply(int length, byte[] other) {
        switch (random.Next() % 10) {
            case 0:
                return FlipBit(length);

            case 1:
                return SetInteresting(length);

            case 2:
                return Nudge(length);

            case 3:
                return Splice(length, other);

            case 4:
                return Duplicate(length);

            case 5:
                return Delete(length);

            case 6:
                return Truncate(length);

            case 7:
                return Extend(length);

            case 8:
                return FillRun(length);

            default:
                return Randomise(length);
        }
    }

    int FlipBit(int length) {
        if (length == 0) {
            return length;
        }

        var at = Index(length);
        scratch[at] ^= (byte)(1 << (int)(random.Next() & 7));

        return length;
    }

    int SetInteresting(int length) {
        if (length == 0) {
            return length;
        }

        scratch[Index(length)] = Interesting[(int)(random.Next() % (ulong)Interesting.Length)];

        return length;
    }

    int Nudge(int length) {
        if (length == 0) {
            return length;
        }

        // Off-by-one on a length field is the bug this looks for, and it is the mutation a bit flip
        // almost never produces: 4 and 5 differ in one bit, but 7 and 8 differ in four.
        var at = Index(length);
        scratch[at] = (byte)(scratch[at] + (byte)(random.Next() % 5) - 2);

        return length;
    }

    int Splice(int length, byte[] other) {
        if (length == 0 || other.Length == 0) {
            return length;
        }

        var take = 1 + (int)(random.Next() % (ulong)Math.Min(other.Length, 64));
        var from = (int)(random.Next() % (ulong)(other.Length - take + 1));
        var to = Index(length);
        var fits = Math.Min(take, length - to);

        other.AsSpan(from, fits).CopyTo(scratch.AsSpan(to));

        return length;
    }

    int Duplicate(int length) {
        if (length == 0) {
            return length;
        }

        var take = 1 + (int)(random.Next() % (ulong)Math.Min(length, 64));
        var from = (int)(random.Next() % (ulong)(length - take + 1));
        var to = Index(length);
        var room = Math.Min(take, MaxLength - length);

        if (room <= 0) {
            return length;
        }

        // Shift the tail out of the way, so this inserts rather than overwrites — a record
        // duplicated inside a snapshot is a different input from a record overwritten by one.
        scratch.AsSpan(to, length - to).CopyTo(scratch.AsSpan(to + room));
        scratch.AsSpan(from < to ? from : from + room, room).CopyTo(scratch.AsSpan(to));

        return length + room;
    }

    int Delete(int length) {
        if (length < 2) {
            return length;
        }

        var take = 1 + (int)(random.Next() % (ulong)Math.Min(length - 1, 64));
        var at = (int)(random.Next() % (ulong)(length - take));

        scratch.AsSpan(at + take, length - at - take).CopyTo(scratch.AsSpan(at));

        return length - take;
    }

    int Truncate(int length) =>
        // Every prefix of a well-formed packet is a truncated packet, and truncation is the failure
        // a real network produces most often. Cheap to generate and the case a decoder is most
        // likely to have got subtly wrong.
        length == 0 ? 0 : (int)(random.Next() % (ulong)length);

    int Extend(int length) {
        var add = 1 + (int)(random.Next() % 32);
        var room = Math.Min(add, MaxLength - length);

        for (var i = 0; i < room; i++) {
            scratch[length + i] = (byte)(random.Next() & 0xFF);
        }

        return length + room;
    }

    int FillRun(int length) {
        if (length == 0) {
            return length;
        }

        // A run of one value, which is how a varint is made to never terminate and how a length
        // field is made as large as its encoding allows.
        var value = Interesting[(int)(random.Next() % (ulong)Interesting.Length)];
        var at = Index(length);
        var run = Math.Min(1 + (int)(random.Next() % 16), length - at);

        scratch.AsSpan(at, run).Fill(value);

        return length;
    }

    int Randomise(int length) {
        if (length == 0) {
            return length;
        }

        var at = Index(length);
        var run = Math.Min(1 + (int)(random.Next() % 8), length - at);

        for (var i = 0; i < run; i++) {
            scratch[at + i] = (byte)(random.Next() & 0xFF);
        }

        return length;
    }

    int Index(int length) => (int)(random.Next() % (ulong)length);
}
