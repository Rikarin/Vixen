// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Reflection;
using Xunit;

namespace Vixen.Raven.Gpu.Tests;

/// <summary>
///     What the reflection says a uniform block's members are at, against where a device actually
///     reads them.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 07's per-backend layout gate, and the failure it exists for has happened
///         twice.</b> The golden images caught a composed material parameter whose qualified name
///         depended on lowering order, and one Raven struct used in both a uniform block and a
///         storage buffer collapsing to a single MSL type — a padded <c>float3</c> won and the
///         fragment stage read a light four bytes late. Both produced valid SPIR-V, no validation
///         message, and a wrong picture. Neither is findable by compiling.
///     </para>
///     <para>
///         <b>The method is the whole of it: the host writes bytes at the offsets the reflection
///         reports, and the shader reads the members by name and writes back what it got.</b> A
///         member that agrees round-trips its own value. A member whose reflected offset is not the
///         offset the compiler gave the module comes back holding a neighbour's value — or padding —
///         and says so by name. Nothing here asserts what the offsets *should* be, because that is
///         std140's business and hard-coding a table would be a second implementation of the rules;
///         what is asserted is that the two halves of the engine agree, which is the only thing that
///         can silently stop being true.
///     </para>
///     <para>
///         ⚠ <b>The block deliberately contains the types std140 is awkward about.</b> A
///         <c>float3</c> occupies twelve bytes and aligns to sixteen, so a scalar after one either
///         fills the gap or starts a new slot depending on which rule you believe; an array's stride
///         is rounded up to sixteen whatever its element is; and a matrix is an array of column
///         vectors with the same rule again. Every one of those is a place two implementations can
///         differ while both looking reasonable.
///     </para>
/// </remarks>
public sealed class LayoutGateTests {
    /// <summary>
    ///     A block with every std140 hazard in it, read back member by member.
    /// </summary>
    /// <remarks>
    ///     Each member is written out as a float — an integer converted, a vector's components
    ///     spread — because one output type keeps the readback a plain array and the comparison a
    ///     plain loop. What is under test is where the bytes were, not what type they were.
    /// </remarks>
    const string Block = """
                         package Vixen.Shaders.Gate

                         shader Gate {
                             [PerFrame] var results: RWBuffer<float>

                             /// A leading scalar, so nothing starts conveniently at zero.
                             [PerFrame] var alpha: float

                             /// Twelve bytes aligned to sixteen: the classic.
                             [PerFrame] var direction: float3

                             /// The scalar that either fills `direction`'s tail or does not.
                             [PerFrame] var beta: float

                             /// A full slot, for a baseline that cannot be got wrong.
                             [PerFrame] var tint: float4

                             /// An array, whose stride std140 rounds up to sixteen.
                             [PerFrame] var weights: float[4]

                             /// And a matrix, which is that rule applied to four columns.
                             [PerFrame] var transform: mat4

                             [ComputeShader(1)]
                             func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                 results[0] = alpha
                                 results[1] = direction.x
                                 results[2] = direction.y
                                 results[3] = direction.z
                                 results[4] = beta
                                 results[5] = tint.x
                                 results[6] = tint.y
                                 results[7] = tint.z
                                 results[8] = tint.w
                                 results[9] = weights[0]
                                 results[10] = weights[1]
                                 results[11] = weights[2]
                                 results[12] = weights[3]
                                 results[13] = transform[0][0]
                                 results[14] = transform[1][1]
                                 results[15] = transform[2][2]
                                 results[16] = transform[3][3]
                             }
                         }
                         """;

    /// <summary>Every member arrives holding the value the host put at its reflected offset.</summary>
    [Fact]
    public void A_device_reads_every_member_where_the_reflection_says_it_is() {
        var (_, reflection) = ShaderRun.Compile(Block, []);

        var block = Assert.Single(
            reflection.Sets.SelectMany(set => set.Bindings),
            binding => binding.Type == DescriptorType.UniformBuffer
        );

        Assert.NotEmpty(block.Members);

        // ⚠ **The hazards are asserted to be present, because a gate is only a gate for what it can
        // observe.** If this block came back as six members on tidy sixteen-byte boundaries, every
        // assertion below would still pass and none of them would be about std140 at all. Measured:
        // alpha@0, direction@16, beta@28, tint@32, weights@48 stride 16, transform@112 stride 16.
        var members = block.Members.ToDictionary(member => member.Name, StringComparer.Ordinal);

        Assert.True(
            members["beta"].Offset == members["direction"].Offset + 12,
            $"`beta` is at {members["beta"].Offset} and `direction` at {members["direction"].Offset}, so the "
            + "scalar no longer packs into the vector's tail — which is the hazard this block exists to put "
            + "in front of a device."
        );

        Assert.Equal(16, members["weights"].ArrayStride);
        Assert.Equal(16, members["transform"].MatrixStride);

        var bytes = new byte[block.Size];

        // ⚠ A distinct, recognisable value per member, and none of them zero. A member read from the
        // wrong offset in a buffer of zeroes comes back zero and so does one read correctly from a
        // member that happens to be zero — so "it was not zero" has to distinguish them, and the
        // values are chosen far enough apart that a neighbour cannot be mistaken for the right one.
        var expected = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var next = 1f;

        foreach (var member in block.Members) {
            var components = Components(member);
            var values = new float[components];

            for (var index = 0; index < components; index++) {
                values[index] = next;
                next += 1f;
            }

            expected[member.Name] = values;
            Write(bytes, member, values);
        }

        var run = ShaderRun.Run(Block, [], 17, groups: 1, bytes);

        Assert.NotNull(run);

        Check(run.Values, 0, expected["alpha"][0], "alpha");
        Check(run.Values, 1, expected["direction"][0], "direction.x");
        Check(run.Values, 2, expected["direction"][1], "direction.y");
        Check(run.Values, 3, expected["direction"][2], "direction.z");
        Check(run.Values, 4, expected["beta"][0], "beta");
        Check(run.Values, 5, expected["tint"][0], "tint.x");
        Check(run.Values, 8, expected["tint"][3], "tint.w");
        Check(run.Values, 9, expected["weights"][0], "weights[0]");
        Check(run.Values, 12, expected["weights"][3], "weights[3]");
        Check(run.Values, 13, expected["transform"][0], "transform[0][0]");
        Check(run.Values, 16, expected["transform"][15], "transform[3][3]");
    }

    /// <summary>
    ///     ⚠ <b>The gate can see a wrong offset, asserted by giving it one.</b>
    /// </summary>
    /// <remarks>
    ///     The whole risk with a layout gate is that it passes because the host and the shader are
    ///     reading one table — which they are, and deliberately. What makes it a gate is that the
    ///     bytes travel through a real device in between. So this writes the block with one member's
    ///     offset shifted by four bytes, exactly as a std140 rule applied one way rather than the
    ///     other would, and requires the comparison to notice.
    /// </remarks>
    [Fact]
    public void A_member_written_four_bytes_off_is_caught() {
        var (_, reflection) = ShaderRun.Compile(Block, []);

        var block = Assert.Single(
            reflection.Sets.SelectMany(set => set.Bindings),
            binding => binding.Type == DescriptorType.UniformBuffer
        );

        var tint = block.Members.Single(member => member.Name == "tint");
        var bytes = new byte[block.Size];

        // Four bytes late: what a `float3` treated as twelve bytes rather than as a sixteen-byte
        // slot does to everything after it.
        Write(bytes, tint with { Offset = tint.Offset + 4 }, [101f, 102f, 103f, 104f]);

        var run = ShaderRun.Run(Block, [], 17, groups: 1, bytes);

        Assert.NotNull(run);

        Assert.False(
            run.Values[5] == 101f && run.Values[8] == 104f,
            "The shader read `tint` correctly from a block written four bytes off, so this comparison "
            + "cannot tell a right offset from a wrong one."
        );
    }

    /// <summary>How many floats a member occupies, for the purpose of filling it.</summary>
    static int Components(MemberInfo member) =>
        member.Type.IsMatrix ? member.Type.Rows * member.Type.Columns
        : member.Type.IsArray ? member.Type.ArrayLength!.Value * member.Type.Rows
        : member.Type.Rows;

    /// <summary>
    ///     Writes a member's floats at its reflected offset, honouring the reflected strides.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The strides come from the reflection too</b>, which is the point: an array whose
    ///     stride the host assumed was its element size would write four floats into the first slot
    ///     and the shader would read three of them as padding. That is the fault, and writing it the
    ///     shader's way would hide it.
    /// </remarks>
    static void Write(byte[] bytes, MemberInfo member, float[] values) {
        if (member.Type.IsMatrix) {
            for (var column = 0; column < member.Type.Columns; column++) {
                for (var row = 0; row < member.Type.Rows; row++) {
                    Put(bytes, member.Offset + (column * member.MatrixStride) + (row * sizeof(float)),
                        values[(column * member.Type.Rows) + row]);
                }
            }

            return;
        }

        if (member.Type.IsArray) {
            for (var element = 0; element < member.Type.ArrayLength!.Value; element++) {
                for (var component = 0; component < member.Type.Rows; component++) {
                    Put(bytes, member.Offset + (element * member.ArrayStride) + (component * sizeof(float)),
                        values[(element * member.Type.Rows) + component]);
                }
            }

            return;
        }

        for (var component = 0; component < member.Type.Rows; component++) {
            Put(bytes, member.Offset + (component * sizeof(float)), values[component]);
        }
    }

    static void Put(byte[] bytes, int offset, float value) =>
        BitConverter.TryWriteBytes(bytes.AsSpan(offset), value);

    static void Check(float[] values, int index, float expected, string what) =>
        Assert.True(
            values[index] == expected,
            $"`{what}` came back as {values[index]} and the host put {expected} at the offset the "
            + "reflection reported for it. The compiler and the reflection disagree about where that member "
            + "is, which is a wrong picture with valid SPIR-V and nothing in the validation layer."
        );
}
