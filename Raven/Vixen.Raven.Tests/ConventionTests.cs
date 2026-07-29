// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Reflection;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     The conventions of docs/plan/07 § E, pinned against what the backends actually emit.
/// </summary>
/// <remarks>
///     <para>
///         Get one of these wrong and every shader is subtly incorrect in a way that is painful to
///         find later — a transform that works until the matrix is non-square, a UV that is flipped
///         only on one backend. So these tests read the emitted decorations and opcodes rather than
///         restating the convention in a second place: if the emitter changes its mind, they fail.
///     </para>
///     <para>
///         The rest of § E is not the compiler's to bake in. Reverse-Z lives in the host's projection
///         matrix (Vulkan's depth range is already 0..1), and linear working space, sRGB decode and
///         HDR targets are image-format and shader-library concerns.
///     </para>
/// </remarks>
public class ConventionTests {
    const string Transform = """
                             package A

                             shader S {
                                 var world: mat4

                                 [VertexShader]
                                 [Semantic("SV_Position")]
                                 func Vertex(position: float3): float4 {
                                     return world * float4(position, 1)
                                 }
                             }

                             """;

    // --- Matrices -----------------------------------------------------------

    /// <summary>
    ///     Storage is column-major, spelled out. SPIR-V has no implicit layout, so a matrix that
    ///     did not say this would be read however the driver felt like reading it.
    /// </summary>
    [Fact]
    public void A_matrix_in_a_block_is_column_major_with_an_explicit_stride() {
        var listing = GenerateOne(Transform, "spirv");
        var block = BlockOf(listing);

        Assert.Contains($"OpMemberDecorate {block} 0 ColMajor", listing, StringComparison.Ordinal);
        Assert.Contains($"OpMemberDecorate {block} 0 MatrixStride 16", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("RowMajor", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The reflection has to report the same thing, because the host writes the buffer by these
    ///     numbers rather than by asking the driver.
    /// </summary>
    [Fact]
    public void The_reflection_agrees_about_the_matrix_stride_and_shape() {
        var member = Assert.Single(Describe(Transform).Sets).Bindings[0].Members[0];

        Assert.Equal(16, member.MatrixStride);
        Assert.True(member.Type.IsMatrix);

        // Rows is the length of one column, which is what a column-major matrix is made of.
        Assert.Equal(4, member.Type.Rows);
        Assert.Equal(4, member.Type.Columns);
    }

    /// <summary>
    ///     Matrix on the left: <c>world * position</c>, never <c>position * world</c>. Because
    ///     storage hands the shader the transpose of the host's matrix, this is the spelling that
    ///     means the host's <c>mul(v, M)</c> — see
    ///     <see cref="The_shader_sees_the_transpose_of_the_hosts_matrix_so_m_times_v_is_mul_v_M" />.
    /// </summary>
    [Fact]
    public void A_transform_is_written_matrix_on_the_left() {
        Assert.Contains("OpMatrixTimesVector", GenerateOne(Transform, "spirv"), StringComparison.Ordinal);

        // GLSL loads the uniform into a local first, so the operand order has to be read through
        // that local's declared type rather than off the matrix's name.
        var glsl = GenerateOne(Transform);
        var product = Assert.Single(glsl.Split('\n'), line => line.Contains(" * ", StringComparison.Ordinal));
        var left = product.Split('(')[1].Split(' ')[0];

        Assert.Contains($"mat4 {left} = world;", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>v * m</c> is the other operation, and it is a different one — the untransposed matrix
    ///     applied to a column vector. Legal, and occasionally what someone wants, so it compiles;
    ///     this pins that the two spellings do not collapse into each other.
    /// </summary>
    [Fact]
    public void A_vector_on_the_left_is_a_different_operation_and_still_compiles() {
        var listing = GenerateOne(
            Transform.Replace("world * float4(position, 1)", "float4(position, 1) * world", StringComparison.Ordinal),
            "spirv"
        );

        Assert.Contains("OpVectorTimesMatrix", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpMatrixTimesVector", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A matrix is built from its columns, so <c>mat3(a, b, c, …)</c> fills the column that
    ///     <c>m[0]</c> reads back. Construction and indexing agreeing is the whole point.
    /// </summary>
    [Fact]
    public void A_matrix_is_constructed_from_its_columns() {
        const string Body = "        val m = mat3(1, 2, 3, 4, 5, 6, 7, 8, 9)\n        return float4(m[0], 1)";

        // GLSL's own mat3(…) fills columns, so passing the scalars straight through is what makes
        // the two languages agree without the emitter reordering anything.
        Assert.Contains(
            "mat3(1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0)",
            GeneratePixel(Body),
            StringComparison.Ordinal
        );

        // SPIR-V has no flat form, so it has to group the nine scalars — into three vectors of
        // three, which OpTypeMatrix reads as columns.
        var listing = GeneratePixelSpirv(Body, "    var v: float3\n");
        var matrix = Assert.Single(
            listing.Split('\n'),
            line => line.Contains("OpTypeMatrix", StringComparison.Ordinal)
        );

        var column = matrix.Split(' ')[^2];
        Assert.Equal(3, Occurrences(listing, $"OpCompositeConstruct {column} %"));
        Assert.Contains($"OpCompositeConstruct {matrix.Split(' ')[0]} ", listing, StringComparison.Ordinal);
    }

    // --- The reconciliation with ADR-003 -------------------------------------

    /// <summary>
    ///     ADR-003 says the host stores matrices row-major with the translation in
    ///     <c>M41..M43</c> and multiplies as HLSL's <c>mul(v, M)</c>; the shader decorates its
    ///     matrices <c>ColMajor</c> and writes <c>m * v</c>. Those look contradictory and are not:
    ///     they are the same bytes read two ways, and the two readings compose exactly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This models the GPU's read rather than running one — but it models it from the
    ///         <em>emitted</em> decorations: the stride and the majorness are parsed out of the
    ///         compiled module, so switching the emitter to <c>RowMajor</c> or to a different stride
    ///         breaks this test rather than quietly invalidating the argument in the doc.
    ///     </para>
    ///     <para>
    ///         Numeric agreement on a real device is a separate job — the GPU-readback tests in
    ///         doc 07 § G.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shader_sees_the_transpose_of_the_hosts_matrix_so_m_times_v_is_mul_v_M() {
        var listing = GenerateOne(Transform, "spirv");
        var block = BlockOf(listing);

        // What the module says about how to read the bytes.
        Assert.Contains($"OpMemberDecorate {block} 0 ColMajor", listing, StringComparison.Ordinal);
        var stride = Stride(listing, block);
        Assert.Equal(16, stride);

        // A host matrix, row-major: M[r, c] at float index r * 4 + c, so the translation is the
        // last row — M41..M43, per ADR-003.
        var host = new float[4, 4];
        for (var r = 0; r < 4; r++) {
            for (var c = 0; c < 4; c++) {
                host[r, c] = r * 4 + c + 1;
            }
        }

        var bytes = new float[16];
        for (var r = 0; r < 4; r++) {
            for (var c = 0; c < 4; c++) {
                bytes[r * 4 + c] = host[r, c];
            }
        }

        // The shader's read: column j starts at stride * j, and has one lane per row.
        var lanes = stride / sizeof(float);
        var shader = new float[4, 4];
        for (var j = 0; j < 4; j++) {
            for (var i = 0; i < 4; i++) {
                shader[i, j] = bytes[(j * lanes) + i];
            }
        }

        // Which makes it the transpose, for free — no instruction did this.
        for (var r = 0; r < 4; r++) {
            for (var c = 0; c < 4; c++) {
                Assert.Equal(host[r, c], shader[c, r]);
            }
        }

        float[] v = [2, 3, 5, 1];

        // The shader's `m * v`: matrix times column vector.
        var gpu = new float[4];
        for (var i = 0; i < 4; i++) {
            for (var k = 0; k < 4; k++) {
                gpu[i] += shader[i, k] * v[k];
            }
        }

        // The host's `mul(v, M)`: row vector times matrix.
        var cpu = new float[4];
        for (var c = 0; c < 4; c++) {
            for (var k = 0; k < 4; k++) {
                cpu[c] += v[k] * host[k, c];
            }
        }

        Assert.Equal(cpu, gpu);
    }

    // --- Fragment origin -----------------------------------------------------

    /// <summary>
    ///     UV origin top-left, which for a fragment shader is the <c>gl_FragCoord</c> origin.
    ///     Vulkan only accepts the upper-left one, so this is both the convention and the only
    ///     legal choice — worth pinning because omitting the mode entirely is what a refactor
    ///     would do by accident, and the module would then be invalid rather than flipped.
    /// </summary>
    [Fact]
    public void A_fragment_shader_declares_the_upper_left_origin() {
        var listing = GeneratePixelSpirv("        return float4(1, 0, 0, 1)");

        Assert.Contains("OriginUpperLeft", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginLowerLeft", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Depth stays the host's business: nothing is emitted that would replace or clamp it, so
    ///     Vulkan's native 0..1 range and the host's reverse-Z projection are undisturbed.
    /// </summary>
    [Fact]
    public void Nothing_is_emitted_that_touches_the_depth_range() {
        var listing = GeneratePixelSpirv("        return float4(1, 0, 0, 1)");

        Assert.DoesNotContain("DepthReplacing", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("FragDepth", listing, StringComparison.Ordinal);
    }

    // --- Helpers -------------------------------------------------------------

    static string GeneratePixelSpirv(string body, string members = "") =>
        GenerateOne(
            $$"""
              package A

              shader S {
              {{members}}
                  [FragmentShader]
                  func Fragment(): float4 {
              {{body}}
                  }
              }

              """,
            "spirv"
        );

    static int Occurrences(string text, string needle) {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }

    static RavenReflection Describe(string source) {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(source), "S");
        return ReflectionBuilder.Describe(shader);
    }

    /// <summary>The id of the laid-out block struct.</summary>
    static string BlockOf(string listing) =>
        Assert.Single(listing.Split('\n'), line => line.EndsWith(" Block", StringComparison.Ordinal)).Split(' ')[1];

    /// <summary>The <c>MatrixStride</c> the module declares for member 0 of a block.</summary>
    static int Stride(string listing, string block) {
        var line = Assert.Single(
            listing.Split('\n'),
            l => l.StartsWith($"OpMemberDecorate {block} 0 MatrixStride ", StringComparison.Ordinal)
        );

        return int.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
    }
}
