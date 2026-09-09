// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Raven.Gpu.Tests;

/// <summary>
///     The shipped PRNG, run on a device against a pinned table of bits.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 07 § E's <c>Random.rvn</c> row, in the form that row actually needs — and ⚠ not the
///         form it asks for.</b> The row says the file "must match the CPU implementation
///         bit-for-bit", and there is no CPU implementation of it: <c>Random.Multiplier</c> appears in
///         exactly one file in the tree, which is <c>Random.rvn</c> itself. The pair doc 06's
///         dual-target parity is about is a different one — <c>Vixen.Vfx.VfxRandom</c> against the
///         constants <c>VfxShaderEmitter</c> transcribes into the generated shader — and that pair is
///         still unchecked; see <c>github.com/Rikarin/Vixen/issues/315</c>. Writing a C# port here to
///         compare against would have created the second implementation rather than found one, and
///         nothing in the engine would call it.
///     </para>
///     <para>
///         <b>So what is asserted is what the file's exactness rules actually buy: a golden
///         vector.</b> <c>Random.rvn</c>'s header states three rules — wrapping 32-bit integer
///         arithmetic only, no float anywhere in the state, and a float conversion that is a shift
///         and a multiply by a power of two — and every one of them exists so that a value does not
///         depend on which driver compiled the shader. A table of bits is the only thing that can
///         say whether that held. A tolerance would defeat the entire point of the file.
///     </para>
///     <para>
///         ⚠ <b>The bits are recovered in halves because the readback is floats.</b>
///         <c>float(h &gt;&gt; 16)</c> and <c>float(h &amp; 0xFFFF)</c> are each below 65536 and
///         therefore exactly representable, so the pair reconstructs all thirty-two bits with no
///         rounding anywhere. Writing the hash straight into a float buffer would have lost the low
///         bits — which are the ones an xorshift-multiply gets wrong first.
///     </para>
/// </remarks>
public sealed class RandomGateTests {
    /// <summary>The library files the kernel needs, and no more.</summary>
    /// <remarks>
    ///     <c>Math.rvn</c> is not optional even though nothing here calls it: <c>Random.rvn</c>'s
    ///     disc- and hemisphere-sampling helpers reach <c>Const</c> and <c>Math</c>, and a package is
    ///     bound as a whole.
    /// </remarks>
    static readonly string[] Imports = ["Core/Math.rvn", "Core/Random.rvn"];

    /// <summary>How many seeds the table holds.</summary>
    const int Seeds = 16;

    /// <summary>How many floats each seed writes.</summary>
    const int Stride = 8;

    /// <summary>
    ///     The seeds, spread by an FNV step so consecutive invocations are far apart in the input.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The step is deliberately not <c>0x9E3779B9</c></b>, which is what <c>Combine</c>
    ///     multiplies its second operand by. A seed of <c>i * 0x9E3779B9</c> makes
    ///     <c>Combine(seed, i)</c> hash <c>a ^ (b * 0x9E3779B9)</c> = zero for <em>every</em> i, so
    ///     all sixteen entries would have been the same number and the table would have pinned a
    ///     degenerate case while looking like a sweep. Measured, not guessed.
    /// </remarks>
    static readonly uint[] Seed = [
        0x811C9DC5, 0x821C9F58, 0x831CA0EB, 0x841CA27E,
        0x851CA411, 0x861CA5A4, 0x871CA737, 0x881CA8CA,
        0x891CAA5D, 0x8A1CABF0, 0x8B1CAD83, 0x8C1CAF16,
        0x8D1CB0A9, 0x8E1CB23C, 0x8F1CB3CF, 0x901CB562
    ];

    /// <summary><c>Random.Hash(Seed[i])</c> — PCG's xorshift, odd multiply, xorshift.</summary>
    static readonly uint[] Hash = [
        0x1A0B5E66, 0x70433D57, 0xBE7B80D8, 0x39A288E8,
        0x45AA2C9B, 0xE56F2C77, 0x565CED3B, 0xD132607C,
        0x4C5A6FAF, 0xA2400D9C, 0xCB89FCE2, 0xD7E2BBF0,
        0x32E0F119, 0x64290689, 0x439355BC, 0x03301726
    ];

    /// <summary><c>Random.Combine(Seed[i], i)</c>.</summary>
    /// <remarks>
    ///     The first entry equals <c>Hash[0]</c>, and that is arithmetic rather than a copy-paste
    ///     slip: <c>Combine(a, 0)</c> is <c>Hash(a ^ 0)</c>.
    /// </remarks>
    static readonly uint[] Forward = [
        0x1A0B5E66, 0x1BE02D32, 0x4A68F54F, 0x020D44F6,
        0x75852E41, 0x251AF0EA, 0x7D89B92C, 0x6BEA642B,
        0x602D2C77, 0x1C5482E2, 0xCEFB35E0, 0x055E16C9,
        0xAD6AF4EA, 0x3EC6A862, 0x1CB331FA, 0x5FBFB702
    ];

    /// <summary><c>Random.Combine(i, Seed[i])</c> — the operands the other way round.</summary>
    static readonly uint[] Reverse = [
        0xF7B23914, 0x2D80D396, 0xCC0E269F, 0x5362D063,
        0xE83D4BF5, 0x6B3D1E0A, 0xBFDED03D, 0x3658CD6E,
        0x4AC08C49, 0x5A084631, 0x30CFF58F, 0xB1E02058,
        0xC00B9718, 0xE8D2BAB4, 0xDE694F5B, 0x49BAD3A3
    ];

    /// <summary>
    ///     Hashes one seed per invocation and writes every result back as two exactly representable
    ///     halves.
    /// </summary>
    /// <remarks>
    ///     The seed is derived inside the shader from the invocation index rather than uploaded, for
    ///     <c>BrdfGateTests</c>' reason: a table of inputs written by one side and read by the other
    ///     would put a layout question inside a numeric test.
    /// </remarks>
    const string Vectors = """
                           package Vixen.Shaders.Gate

                           import Vixen.Shaders.Core

                           shader Gate {
                               [PerFrame] var results: RWBuffer<float>

                               [ComputeShader(16)]
                               func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                   val index = int(id.x)

                                   if (index >= 16) {
                                       return
                                   }

                                   val counter = id.x
                                   val seed = counter * 0x01000193u + 0x811C9DC5u

                                   val hash = Random.Hash(seed)
                                   val forward = Random.Combine(seed, counter)
                                   val reverse = Random.Combine(counter, seed)

                                   val slot = index * 8

                                   results[slot + 0] = float(hash >> 16u)
                                   results[slot + 1] = float(hash & 0xFFFFu)
                                   results[slot + 2] = float(forward >> 16u)
                                   results[slot + 3] = float(forward & 0xFFFFu)
                                   results[slot + 4] = float(reverse >> 16u)
                                   results[slot + 5] = float(reverse & 0xFFFFu)
                                   results[slot + 6] = Random.Float01(seed)
                                   results[slot + 7] = Random.ToFloat01(hash)
                               }
                           }
                           """;

    /// <summary>Every seed hashes to the bits the table says, on the device that ran it.</summary>
    [Fact]
    public void The_device_produces_the_pinned_bits() {
        var run = ShaderRun.Run(Vectors, Imports, Seeds * Stride, groups: 1);

        Assert.NotNull(run);

        for (var index = 0; index < Seeds; index++) {
            var slot = index * Stride;

            // The seed the shader derived, checked before what it hashed to — otherwise a wrong
            // seed reads as a wrong hash and sends the reader into `Random.rvn`.
            Assert.Equal(Seed[index], (uint)((index * 0x01000193) + 0x811C9DC5));

            Bits(Hash[index], run.Values, slot + 0, "Hash", index);
            Bits(Forward[index], run.Values, slot + 2, "Combine(seed, i)", index);
            Bits(Reverse[index], run.Values, slot + 4, "Combine(i, seed)", index);
        }
    }

    /// <summary>
    ///     ⚠ The float conversion is exact, which is a stronger claim than "close", and the only one
    ///     worth making about this file.
    /// </summary>
    /// <remarks>
    ///     <c>ToFloat01</c> is <c>float(h &gt;&gt; 8) * 2^-24</c>: the shifted value is below 2^24 and
    ///     therefore exactly representable, and 2^-24 is exact, so the product is exact and the same
    ///     bits on any hardware. The comparison is therefore <c>==</c> rather than a tolerance —
    ///     a tolerance here would pass a conversion that had become a division by a non-power of two,
    ///     which is precisely the change the file's header exists to forbid.
    /// </remarks>
    [Fact]
    public void The_float_conversion_is_exact_and_Float01_is_ToFloat01_of_Hash() {
        var run = ShaderRun.Run(Vectors, Imports, Seeds * Stride, groups: 1);

        Assert.NotNull(run);

        for (var index = 0; index < Seeds; index++) {
            var slot = index * Stride;
            var expected = (Hash[index] >> 8) * 5.9604645e-8f;

            Assert.Equal(expected, run.Values[slot + 7]);

            // Float01(seed) is defined as ToFloat01(Hash(seed)), so the two slots are the same
            // number and not merely near one another.
            Assert.Equal(run.Values[slot + 7], run.Values[slot + 6]);

            Assert.InRange(run.Values[slot + 6], 0f, 0.99999995f);
        }
    }

    /// <summary>
    ///     <c>Combine</c> is not commutative, which is the property its odd multiply exists for.
    /// </summary>
    /// <remarks>
    ///     Asserted over the whole table rather than as a remark on the constants, because the
    ///     failure it guards is visible rather than numeric: a commutative combine gives the pixel at
    ///     (3, 7) the same value as the one at (7, 3), which is a diagonal through any noise pattern.
    ///     ⚠ The <c>Assert.Equal</c> on the count is not decoration — a loop that asserts inside
    ///     itself passes on an empty collection, and this one would then be claiming a property of
    ///     nothing.
    /// </remarks>
    [Fact]
    public void Combining_two_seeds_depends_on_their_order() {
        var differ = 0;

        for (var index = 0; index < Seeds; index++) {
            if (Forward[index] != Reverse[index]) {
                differ++;
            }
        }

        Assert.Equal(Seeds, differ);
    }

    /// <summary>
    ///     The gate can see a wrong answer, asserted by giving it one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A golden-vector gate is exactly the kind that passes for the wrong reason</b> — a
    ///     table read out of the device it is meant to check would agree with anything that device
    ///     did. This kernel drops the second xorshift, which is the change most likely to be made by
    ///     someone tidying <c>Hash</c>: it keeps the multiply, keeps the distribution looking
    ///     plausible, and moves every bit of the answer.
    /// </remarks>
    [Fact]
    public void Dropping_the_output_xorshift_is_caught() {
        const string Sabotaged = """
                                 package Vixen.Shaders.Gate

                                 import Vixen.Shaders.Core

                                 shader Gate {
                                     [PerFrame] var results: RWBuffer<float>

                                     [ComputeShader(16)]
                                     func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                         val index = int(id.x)

                                         if (index >= 16) {
                                             return
                                         }

                                         val counter = id.x
                                         val seed = counter * 0x01000193u + 0x811C9DC5u

                                         // The bug: PCG's output stage is xorshift, multiply,
                                         // xorshift, and this stops one step early.
                                         var state = seed
                                         state = state ^ (state >> 16u)
                                         state = state * 0x2C9277B5u

                                         results[index * 2 + 0] = float(state >> 16u)
                                         results[index * 2 + 1] = float(state & 0xFFFFu)
                                     }
                                 }
                                 """;

        var run = ShaderRun.Run(Sabotaged, Imports, Seeds * 2, groups: 1);

        Assert.NotNull(run);

        var moved = 0;

        for (var index = 0; index < Seeds; index++) {
            var value = ((uint)run.Values[(index * 2) + 0] << 16) | (uint)run.Values[(index * 2) + 1];

            if (value != Hash[index]) {
                moved++;
            }
        }

        Assert.Equal(Seeds, moved);
    }

    /// <summary>Reassembles a pinned value from its two halves and says which entry disagreed.</summary>
    static void Bits(uint expected, float[] values, int slot, string what, int index) {
        var high = values[slot];
        var low = values[slot + 1];

        // Whole numbers below 65536, or the halves were not what the shader was asked to write.
        Assert.Equal(high, MathF.Floor(high));
        Assert.Equal(low, MathF.Floor(low));

        var actual = ((uint)high << 16) | (uint)low;

        Assert.True(
            actual == expected,
            $"Seed {index} (0x{Seed[index]:X8}): {what} is pinned at 0x{expected:X8} and the device "
            + $"gave 0x{actual:X8}. `Random.rvn`'s exactness rules say this number cannot depend on "
            + "the driver, so either the file changed or one of those rules stopped holding."
        );
    }
}
