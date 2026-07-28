// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>
///     A unit quaternion in eight bytes instead of sixteen, by not storing the component that can be
///     worked out.
/// </summary>
/// <remarks>
///     <para>
///         <b>Smallest three.</b> A unit quaternion satisfies <c>x² + y² + z² + w² = 1</c>, so any one
///         component is recoverable from the other three up to a sign — and <c>q</c> and <c>−q</c> are
///         the same rotation, so the sign can be chosen rather than stored. Drop the largest
///         component (the one whose recovery is least sensitive to error), negate the quaternion if
///         it was negative, and the three that remain are bounded by <c>1/√2</c> because the dropped
///         one was the largest.
///     </para>
///     <para>
///         Twenty bits each over <c>[−1/√2, 1/√2]</c>, plus two bits naming the dropped component:
///         sixty-two bits, in a <see cref="ulong" />. The quantisation step is 1.35 × 10⁻⁶, which is
///         an angular error under 3 × 10⁻⁶ radians — five orders of magnitude below anything a
///         blend of two poses in <c>float</c> will preserve, and four below what a person could see
///         at the end of a two-metre limb.
///     </para>
///     <para>
///         <b>Why the runtime clip and not the asset.</b> <c>AnimationClipData</c> is the contract a
///         content build writes and the object database stores; changing its storage is a re-import
///         of every animation ever built. A runtime clip is baked at load from that data, so its
///         storage is nobody's contract — which makes it the one place a representation change costs
///         nothing but the code that does it.
///     </para>
/// </remarks>
public readonly record struct PackedQuaternion(ulong Bits) {
    const int Bits20 = 20;
    const uint Mask = (1u << Bits20) - 1;
    const float Range = 0.70710678f;

    /// <summary>The identity rotation, packed.</summary>
    public static PackedQuaternion Identity => Pack(Quaternion.Identity);

    /// <summary>Packs a rotation.</summary>
    /// <param name="value">The rotation. Normalised on the way in.</param>
    /// <returns>The packed form.</returns>
    public static PackedQuaternion Pack(Quaternion value) {
        var quaternion = Quaternion.Normalize(value);

        Span<float> components = [quaternion.X, quaternion.Y, quaternion.Z, quaternion.W];
        var largest = 0;

        for (var index = 1; index < 4; index++) {
            if (MathF.Abs(components[index]) > MathF.Abs(components[largest])) {
                largest = index;
            }
        }

        // q and −q are the same rotation, so the dropped component is made positive and its sign
        // does not have to be stored. This is the whole reason three components are enough.
        var sign = components[largest] < 0f ? -1f : 1f;
        var bits = (ulong)largest;
        var slot = 0;

        for (var index = 0; index < 4; index++) {
            if (index == largest) {
                continue;
            }

            bits |= (ulong)Quantise(components[index] * sign) << (2 + (slot * Bits20));
            slot++;
        }

        return new(bits);
    }

    /// <summary>Unpacks a rotation.</summary>
    /// <returns>The rotation, to within the quantisation step.</returns>
    public Quaternion Unpack() {
        var largest = (int)(Bits & 0x3);
        Span<float> components = stackalloc float[4];
        var sum = 0f;
        var slot = 0;

        for (var index = 0; index < 4; index++) {
            if (index == largest) {
                continue;
            }

            var value = Dequantise((uint)(Bits >> (2 + (slot * Bits20))) & Mask);
            components[index] = value;
            sum += value * value;
            slot++;
        }

        components[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sum));

        return new(components[0], components[1], components[2], components[3]);
    }

    /// <summary>Packs a whole track.</summary>
    /// <param name="values">The rotations.</param>
    /// <returns>The packed rotations.</returns>
    public static PackedQuaternion[] Pack(ReadOnlySpan<Quaternion> values) {
        var packed = new PackedQuaternion[values.Length];

        for (var index = 0; index < values.Length; index++) {
            packed[index] = Pack(values[index]);
        }

        return packed;
    }

    static uint Quantise(float value) {
        var normalised = ((MathUtil.Clamp(value, -Range, Range) / Range) + 1f) * 0.5f;
        return (uint)MathF.Round(normalised * Mask);
    }

    static float Dequantise(uint value) => (((value / (float)Mask) * 2f) - 1f) * Range;
}
