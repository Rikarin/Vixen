// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;

namespace Vixen.Net.Fuzz.Targets;

/// <summary>The packet reader, driven through every one of its reads.</summary>
/// <remarks>
///     <para>
///         <b>The input is two things, and the split is what makes this work.</b> A quarter of it is
///         a <i>program</i> — a byte per read, saying which read to do and with what cap — and the
///         rest is the packet those reads walk over. Fuzzing the packet alone would only ever
///         exercise the one sequence of reads the harness happened to hard-code, and it is the
///         <i>sequence</i> that finds the bugs: a length read as a blob and then as a string, a raw
///         read of a negative count after a failure has already stuck.
///     </para>
///     <para>
///         <b>The caps are part of what is fuzzed.</b> <c>TryReadBlob</c> and <c>TryReadString</c>
///         take a maximum from the caller precisely because the length on the wire is not to be
///         believed, so the program supplies wild ones — zero, huge, and negative. A negative cap is
///         the interesting one: it converts to a <c>uint</c> larger than any length, so the check
///         passes and the only thing left standing between the packet and an allocation is the
///         bounds check on the buffer. That it is enough is a claim worth testing rather than
///         reading.
///     </para>
/// </remarks>
public sealed class PacketReaderTarget : IFuzzTarget {
    /// <inheritdoc />
    public string Name => "packet";

    /// <inheritdoc />
    public string What => "PacketReader, driven through every read with caps from the input";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     <see cref="PacketReader.TryReadString" /> is the only allocating read there is, and this
    ///     program calls it as often as it can. Each call costs a string header plus at most two
    ///     bytes per byte of packet it consumed, and the packet is finite — so the total is bounded
    ///     by the input, which is the property being asserted. The multiple is what it is because a
    ///     program of nothing but empty-string reads pays the header every time.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 512 + (48L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // A program that reads each kind once, over a packet written by the real writer. Everything
        // the mutator does starts from something that decodes.
        var packet = new byte[256];
        var writer = new PacketWriter(packet);
        writer.WriteByte(7);
        writer.WriteBool(value: true);
        writer.WriteUInt16(0xBEEF);
        writer.WriteUInt32(0xDEADBEEF);
        writer.WriteUInt64(0x0123456789ABCDEF);
        writer.WriteSingle(1.5f);
        writer.WriteTick(new(42));
        writer.WriteVariable(300);
        writer.WriteBlob([1, 2, 3, 4]);
        writer.WriteString("vixen");

        if (writer.TryFinish(out var written)) {
            corpus.Add(Program([0, 1, 2, 3, 5, 6, 7, 8, 0x2B, 0x4D], written));
        }

        // A varint that never terminates, a blob claiming the whole address space, and a string
        // whose length outruns what is left. Three shapes the mutator would eventually find and
        // that cost nothing to state.
        corpus.Add(Program([8], [0x80, 0x80, 0x80, 0x80, 0x80, 0x80]));
        corpus.Add(Program([0x0B], [0xFF, 0xFF, 0xFF, 0xFF, 0x0F]));
        corpus.Add(Program([0x0D], [0xFF, 0x7F, 0x41, 0x42]));
        corpus.Add(Program([0x0C], [0xFF, 0xFF, 0xFF, 0xFF, 0x0F]));
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var split = input.Length / 4;
        var program = input[..split];
        var reader = new PacketReader(input[split..]);
        long signature = 17;

        foreach (var step in program) {
            var argument = step >> 4;
            bool read;

            switch (step & 0x0F) {
                case 0:
                    read = reader.TryReadByte(out _);

                    break;

                case 1:
                    read = reader.TryReadBool(out _);

                    break;

                case 2:
                    read = reader.TryReadUInt16(out _);

                    break;

                case 3:
                    read = reader.TryReadUInt32(out _);

                    break;

                case 4:
                    read = reader.TryReadInt32(out _);

                    break;

                case 5:
                    read = reader.TryReadUInt64(out _);

                    break;

                case 6:
                    read = reader.TryReadSingle(out _);

                    break;

                case 7:
                    read = reader.TryReadTick(out _);

                    break;

                case 8:
                    read = reader.TryReadVariable(out _);

                    break;

                case 9:
                    read = reader.TryReadRaw(argument, out _);

                    break;

                case 10:
                    // A negative count, which the contract says is refused rather than thrown on.
                    read = reader.TryReadRaw(-argument - 1, out _);

                    break;

                case 11:
                    read = reader.TryReadBlob(argument * 8, out _);

                    break;

                case 12:
                    // A cap that is not one: it converts to a uint above every possible length, so
                    // the length check cannot be what stops this.
                    read = reader.TryReadBlob(-1, out _);

                    break;

                case 13:
                    read = reader.TryReadString(argument * 16, out _);

                    break;

                case 14:
                    read = reader.Reject();

                    break;

                default:
                    // Observation only: the properties a decoder checks between reads have to be
                    // safe to ask for at any point, including after a failure has stuck.
                    read = reader.IsComplete || !reader.RemainingBytes.IsEmpty;

                    break;
            }

            signature = (signature * 31) + (read ? 1 : 0);
        }

        return (signature * 31)
            + (reader.Position * 7L)
            + (reader.Failed ? 1_000_003L : 0)
            + (reader.IsComplete ? 7L : 0);
    }

    static byte[] Program(byte[] steps, ReadOnlySpan<byte> packet) {
        // The split is a quarter, so the program has to be padded to hold its ground when the rest
        // is long. Padding with a read that observes rather than consumes keeps the seed's meaning.
        var wanted = Math.Max(steps.Length, (steps.Length + packet.Length) / 4);
        var input = new byte[wanted + packet.Length];

        input.AsSpan(0, wanted).Fill(0x0F);
        steps.CopyTo(input, 0);
        packet.CopyTo(input.AsSpan(wanted));

        return input;
    }
}

/// <summary>The bit reader, driven the same way.</summary>
/// <remarks>
///     <para>
///         Same shape as <see cref="PacketReaderTarget" /> and for the same reason, over the reader
///         that everything above the session actually uses: snapshots, remote-call arguments and
///         sync lists are all bit-packed, so this is the reader on the hot untrusted path.
///     </para>
///     <para>
///         <b>Two arguments are clamped rather than fuzzed, and that is not a gap.</b>
///         <c>TryRead</c> throws outside 1–32 bits and <c>Rewind</c> throws past where the reader
///         has been, and both are documented to. Those are contracts with the <i>caller</i> — the
///         decoder, which is our code — not with the packet, and a fuzzer that reported them would
///         be reporting that a method does what its own documentation says. What is under test is
///         what the <i>packet</i> can do, and a packet cannot choose either number.
///     </para>
/// </remarks>
public sealed class BitReaderTarget : IFuzzTarget {
    readonly byte[] copyBuffer = new byte[Mutator.MaxLength];

    /// <inheritdoc />
    public string Name => "bits";

    /// <inheritdoc />
    public string What => "BitReader, driven through every read including the copying ones";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     Nothing here allocates at all — the reader is a <c>ref struct</c> over a caller's span
    ///     and every read returns by value or by slice. The allowance is a constant because zero is
    ///     the honest number and a small constant is what survives the runtime deciding to grow
    ///     something on a thread's behalf.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 1024;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        var packet = new byte[128];
        var writer = new BitWriter(packet);
        writer.WriteBool(value: true);
        writer.Write(0x2A, 6);
        writer.WriteVariable(70_000);
        writer.WriteQuantized(0.25f, Range);
        writer.WriteSingle(-3.5f);
        writer.WriteUInt32(0xFFFFFFFF);
        writer.Align();
        writer.WriteBytes([9, 8, 7]);

        if (writer.TryFinish(out var written)) {
            corpus.Add(Program([1, 0x50, 6, 5, 4, 2, 0x0A, 0x39], written));
        }

        corpus.Add(Program([6], [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80]));
        corpus.Add(Program([0x08, 0x0D], [0xFF, 0xFF, 0xFF, 0xFF]));
        corpus.Add(Program([0xF9, 0xF9, 0xF9], [0x01, 0x02]));
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var split = input.Length / 4;
        var program = input[..split];
        var reader = new BitReader(input[split..]);
        var copy = new BitWriter(copyBuffer);
        long signature = 17;

        foreach (var step in program) {
            var argument = step >> 4;
            bool read;

            switch (step & 0x0F) {
                case 0:
                    read = reader.TryRead(1 + (argument * 2), out _);

                    break;

                case 1:
                    read = reader.TryReadBool(out _);

                    break;

                case 2:
                    read = reader.TryReadUInt32(out _);

                    break;

                case 3:
                    read = reader.TryReadInt32(out _);

                    break;

                case 4:
                    read = reader.TryReadSingle(out _);

                    break;

                case 5:
                    read = reader.TryReadQuantized(Range, out _);

                    break;

                case 6:
                    read = reader.TryReadVariable(out _);

                    break;

                case 7:
                    read = reader.TryReadBitsOver(argument * 4);

                    break;

                case 8:
                    read = reader.TryReadBitsOver(-argument - 1);

                    break;

                case 9:
                    read = reader.TryReadBytes(argument, out _);

                    break;

                case 10:
                    read = reader.Align();

                    break;

                case 11:
                    // Clamped: rewinding past where the reader has been is a caller's bug and
                    // throws by contract. A packet cannot choose this number.
                    reader.Rewind(Math.Min(argument * 3, reader.BitsRead));
                    read = true;

                    break;

                case 12:
                    read = reader.TryCopyTo(ref copy, argument * 3);

                    break;

                case 13:
                    read = reader.TryCopyTo(ref copy, -argument - 1);

                    break;

                case 14:
                    read = reader.TryReadBytes(-argument - 1, out _);

                    break;

                default:
                    read = reader.TryRead(32, out _);

                    break;
            }

            signature = (signature * 31) + (read ? 1 : 0);
        }

        return (signature * 31)
            + (reader.BitsRead * 7L)
            + (copy.BitsWritten * 3L)
            + (reader.Failed ? 1_000_003L : 0)
            + (copy.Overflowed ? 31L : 0);
    }

    static QuantizeRange Range => new(-100f, 100f, 12);

    static byte[] Program(byte[] steps, ReadOnlySpan<byte> packet) {
        var wanted = Math.Max(steps.Length, (steps.Length + packet.Length) / 4);
        var input = new byte[wanted + packet.Length];

        input.AsSpan(0, wanted).Fill(0x0A);
        steps.CopyTo(input, 0);
        packet.CopyTo(input.AsSpan(wanted));

        return input;
    }
}
