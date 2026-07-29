// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Net.Messaging;

/// <summary>Vectors and rotations, packed the way a network wants them rather than the way memory does.</summary>
public static class MathCodec {
    /// <summary>
    ///     How many bits each of the three sent components of a rotation costs.
    /// </summary>
    /// <remarks>
    ///     Ten gives about a tenth of a degree of error, which is below what anybody can see on a
    ///     turning object and well below what the interpolation between two of them contributes. The
    ///     whole rotation is then 32 bits against the 128 a quaternion occupies in memory.
    /// </remarks>
    public const int RotationBits = 10;

    /// <summary>
    ///     The range the three sent components of a rotation live in.
    /// </summary>
    /// <remarks>
    ///     Not a coincidence and not a tuning value: if one component of a unit quaternion is the
    ///     largest, the other three cannot exceed 1/√2, or the four would not square to one. That is
    ///     what makes smallest-three exact rather than approximate — the range is a fact about unit
    ///     quaternions, so no precision is being thrown away to get it.
    /// </remarks>
    public static QuantizeRange RotationRange { get; } = new(-0.70710678f, 0.70710678f, RotationBits);

    /// <summary>Writes a position, each axis quantized into the same range.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The vector.</param>
    /// <param name="range">What its components are components of.</param>
    public static void WriteVector3(this ref BitWriter writer, in Vector3 value, in QuantizeRange range) {
        writer.WriteQuantized(value.X, range);
        writer.WriteQuantized(value.Y, range);
        writer.WriteQuantized(value.Z, range);
    }

    /// <summary>Reads a position.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="range">The same range it was written with.</param>
    /// <param name="value">The vector, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public static bool TryReadVector3(this ref BitReader reader, in QuantizeRange range, out Vector3 value) {
        value = default;

        if (!reader.TryReadQuantized(range, out var x)
            || !reader.TryReadQuantized(range, out var y)
            || !reader.TryReadQuantized(range, out var z)) {
            return false;
        }

        value = new(x, y, z);

        return true;
    }

    /// <summary>Writes a vector as three whole floats, for one with no declared range.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The vector.</param>
    public static void WriteVector3(this ref BitWriter writer, in Vector3 value) {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    /// <summary>Reads a vector written as three whole floats.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="value">The vector, or zero.</param>
    /// <returns>Whether it was there.</returns>
    public static bool TryReadVector3(this ref BitReader reader, out Vector3 value) {
        value = default;

        if (!reader.TryReadSingle(out var x) || !reader.TryReadSingle(out var y) || !reader.TryReadSingle(out var z)) {
            return false;
        }

        value = new(x, y, z);

        return true;
    }

    /// <summary>
    ///     Writes a rotation in 32 bits, by not sending the component it can work out.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The rotation. Normalized first — a quaternion that is not a rotation cannot
    /// be sent as one.</param>
    /// <remarks>
    ///     <para>
    ///         Smallest-three: a unit quaternion's largest component can always be recovered from the
    ///         other three, because they square to one together. So two bits say which one was left
    ///         out and three quantized components follow.
    ///     </para>
    ///     <para>
    ///         The sign of the missing component is the reason the largest is the one dropped rather
    ///         than a fixed one: <c>q</c> and <c>-q</c> are the same rotation, so the sender flips the
    ///         whole quaternion to make the dropped component positive and the receiver can take the
    ///         positive square root. Dropping a fixed component would mean sending its sign, and
    ///         dropping a small one would mean the square root amplifying its neighbours' error.
    ///     </para>
    /// </remarks>
    public static void WriteRotation(this ref BitWriter writer, in Quaternion value) =>
        writer.Write(PackRotation(value), 32);

    /// <summary>The same 32 bits, as a value rather than as a write.</summary>
    /// <param name="value">The rotation.</param>
    /// <returns>The packed rotation.</returns>
    /// <remarks>
    ///     <para>
    ///         For the things that hold a rotation in its packed form rather than encoding one on the
    ///         way out — a replicated pose keeps a bone this way so that a bone which did not move is
    ///         <i>bit-identical</i> to last tick and costs the delta codec one bit. Storing the
    ///         quaternion and packing per connection would also be four times the bytes in a chunk.
    ///     </para>
    ///     <para>
    ///         <b>Identical to what <see cref="WriteRotation" /> emits</b>, which is not a coincidence
    ///         to be maintained by hand: that method is written in terms of this one, and the wire
    ///         golden pins the result. <see cref="BitWriter" /> packs least-significant-bit first, so
    ///         one 32-bit field and the four narrower fields it is assembled from are the same bits in
    ///         the same order.
    ///     </para>
    /// </remarks>
    public static uint PackRotation(in Quaternion value) {
        var quaternion = Quaternion.Normalize(value);
        Span<float> components = [quaternion.X, quaternion.Y, quaternion.Z, quaternion.W];

        var largest = 0;

        for (var i = 1; i < 4; i++) {
            if (Math.Abs(components[i]) > Math.Abs(components[largest])) {
                largest = i;
            }
        }

        // q and -q are the same rotation, so the sender picks the one whose dropped component is
        // positive and the receiver never has to be told the sign.
        var flip = components[largest] < 0f ? -1f : 1f;
        var packed = (uint)largest;
        var offset = 2;

        for (var i = 0; i < 4; i++) {
            if (i == largest) {
                continue;
            }

            packed |= RotationRange.Encode(components[i] * flip) << offset;
            offset += RotationBits;
        }

        return packed;
    }

    /// <summary>Turns a packed rotation back into one.</summary>
    /// <param name="packed">The packed rotation.</param>
    /// <returns>The rotation.</returns>
    /// <remarks>
    ///     Every 32-bit pattern decodes to <i>a</i> rotation, so there is nothing to reject: the two
    ///     bits always name a component and the three fields are always inside the range they are read
    ///     from. That is why this returns a value where the reader returns a bool — the reader can run
    ///     out of bits and this cannot be given a wrong answer.
    /// </remarks>
    public static Quaternion UnpackRotation(uint packed) {
        var largest = (int)(packed & 3);
        var offset = 2;
        Span<float> components = [0f, 0f, 0f, 0f];
        var squares = 0f;

        for (var i = 0; i < 4; i++) {
            if (i == largest) {
                continue;
            }

            var component = RotationRange.Decode((packed >> offset) & ((1u << RotationBits) - 1));
            offset += RotationBits;
            components[i] = component;
            squares += component * component;
        }

        // Clamped at zero: the three components come back a fraction larger than they went out, and
        // a negative under the root would be a NaN rotation propagating into a scene.
        components[largest] = MathF.Sqrt(Math.Max(0f, 1f - squares));

        return Quaternion.Normalize(new(components[0], components[1], components[2], components[3]));
    }

    /// <summary>Reads a rotation written by <see cref="WriteRotation" />.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="value">The rotation, or the identity.</param>
    /// <returns>Whether it was there.</returns>
    public static bool TryReadRotation(this ref BitReader reader, out Quaternion value) {
        value = Quaternion.Identity;

        if (!reader.TryRead(32, out var packed)) {
            return false;
        }

        value = UnpackRotation(packed);

        return true;
    }
}
