// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.CodeGen;
using Vixen.Raven.CodeGen.Spirv;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;
using static Tests.SpirvTestBase;

namespace Tests;

/// <summary>
///     Phase 6: the IR becomes SPIR-V. Every case here is run through
///     <c>spirv-val</c> as well as read, so a passing assertion means a module a
///     driver would actually accept.
/// </summary>
public class SpirvBackendTests {
    [Fact]
    public void The_backend_is_reachable_by_name() {
        Assert.Contains("spirv", TargetBackends.Names);

        var backend = TargetBackends.Create("SPIRV");
        Assert.NotNull(backend);
        Assert.Equal("spirv", backend.Name);
        Assert.Equal(".spv", backend.FileExtension);
    }

    [Fact]
    public void A_unit_carries_the_bytes_and_a_listing_of_the_same_instructions() {
        var unit = One(
            """
            package A

            shader S {
                [FragmentShader]
                func Fragment(): float4 {
                    return float4(1, 1, 1, 1)
                }
            }

            """
        );

        Assert.True(unit.IsBinary);

        // The five-word header: magic, version 1.0, generator, bound, schema.
        var words = Words(unit);
        Assert.Equal(SpirvModule.MagicNumber, words[0]);
        Assert.Equal(0x00010000u, words[1]);
        Assert.Equal(0u, words[4]);

        // The bound has to be past every id the module actually uses.
        Assert.True(words[3] > 1);

        Assert.StartsWith("; SPIR-V", unit.Code);
        Assert.Contains("OpCapability Shader", unit.Code);
        Assert.Contains("OpMemoryModel Logical GLSL450", unit.Code);
        Assert.Contains("OpEntryPoint Fragment", unit.Code);
    }

    [Fact]
    public void Each_entry_point_becomes_its_own_module() {
        var generated = GenerateClean(
            """
            package A

            shader Lit {
                [VertexShader]
                func Vertex(position: float3): float4 {
                    return float4(position, 1)
                }

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(1, 1, 1, 1)
                }
            }

            """,
            "spirv"
        );

        Assert.Equal(["Lit.vert", "Lit.frag"], generated.Select(g => g.Name));
        Assert.All(generated, Validate);
    }

    [Fact]
    public void A_module_only_carries_the_functions_its_stage_reaches() {
        var generated = GenerateClean(
            """
            package A

            shader Lit {
                func OnlyVertex(): float {
                    return 1
                }

                func OnlyFragment(): float {
                    return 2
                }

                [VertexShader]
                func Vertex(): float4 {
                    return float4(OnlyVertex(), 0, 0, 1)
                }

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(OnlyFragment(), 0, 0, 1)
                }
            }

            """,
            "spirv"
        );

        var vertex = generated.Single(g => g.Stage == ShaderStage.Vertex).Code;
        var fragment = generated.Single(g => g.Stage == ShaderStage.Fragment).Code;

        Assert.Contains("\"OnlyVertex\"", vertex);
        Assert.DoesNotContain("\"OnlyFragment\"", vertex);
        Assert.Contains("\"OnlyFragment\"", fragment);
        Assert.DoesNotContain("\"OnlyVertex\"", fragment);
    }

    [Fact]
    public void A_callee_is_defined_before_its_caller() {
        var code = Fragment(
            "        return float4(Helper(), 0, 0, 1)",
            """
                func Helper(): float {
                    return 1
                }

            """
        );

        // SPIR-V is read in one pass, so a call never points forward: Helper,
        // then Fragment, then the main that the pipeline calls.
        var names = Lines(code)
            .Where(line => line.Contains("= OpFunction "))
            .Select(line => line.Split(' ')[0])
            .ToArray();

        Assert.Equal(3, names.Length);
        Assert.Contains($"OpName {names[0]} \"Helper\"", code);
        Assert.Contains($"OpName {names[1]} \"Fragment\"", code);
        Assert.Contains($"OpName {names[2]} \"main\"", code);
    }

    // --- Bindings ----------------------------------------------------------

    [Fact]
    public void Uniforms_become_one_explicitly_laid_out_block() {
        var code = Fragment(
            "        return tint",
            "    var scale: float\n    var tint: float4\n"
        );

        var block = BlockStruct(code);

        // Unmarked fields are material parameters: set 2 in the four-set convention.
        Assert.Contains("DescriptorSet 2", code);
        Assert.Contains("Binding 0", code);

        // SPIR-V has no implicit layout: a float then a vec4 puts the vector at
        // 16, because a four-lane vector aligns to four scalars.
        Assert.Contains($"OpMemberDecorate {block} 0 Offset 0", code);
        Assert.Contains($"OpMemberDecorate {block} 1 Offset 16", code);
    }

    [Fact]
    public void A_matrix_member_carries_its_stride_and_ordering() {
        var code = Fragment(
            "        return m * float4(0, 0, 0, 1)",
            "    var m: mat4\n"
        );

        var block = BlockStruct(code);

        // The IR reads a matrix as rows and SPIR-V holds columns, so the memory
        // ordering and the gap between columns both have to be spelled out.
        Assert.Contains($"OpMemberDecorate {block} 0 ColMajor", code);
        Assert.Contains($"OpMemberDecorate {block} 0 MatrixStride 16", code);
        Assert.Contains("OpMatrixTimesVector", code);
    }

    [Fact]
    public void A_texture_and_a_sampler_stay_two_bindings() {
        var code = Fragment(
            "        return albedo.Sample(linear, uv)",
            "    var albedo: Texture2D\n    var linear: Sampler\n",
            "func Fragment(uv: float2): float4"
        );

        // Nothing is folded away: the two meet only at the sample itself. The GLSL
        // backend emits the same shape, which is why their binding indices match.
        Assert.Contains("OpTypeImage", code);
        Assert.Contains("OpTypeSampler\n", code);
        Assert.Contains("OpSampledImage", code);
        Assert.Contains("OpImageSampleImplicitLod", code);

        Assert.Contains("Binding 0", code);
        Assert.Contains("Binding 1", code);
    }

    [Fact]
    public void Sampling_outside_a_fragment_stage_asks_for_an_explicit_level() {
        var unit = One(
            """
            package A

            shader S {
                var albedo: Texture2D
                var linear: Sampler

                [VertexShader]
                func Vertex(uv: float2): float4 {
                    return albedo.Sample(linear, uv)
                }
            }

            """
        );

        // Only a fragment shader has the derivatives an implicit level needs.
        Assert.Contains("OpImageSampleExplicitLod", unit.Code);
        Assert.DoesNotContain("OpImageSampleImplicitLod", unit.Code);
    }

    // --- Stage interface ---------------------------------------------------

    [Fact]
    public void A_vertex_position_becomes_the_Position_built_in() {
        var unit = One(
            """
            package A

            shader S {
                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3): float4 {
                    return float4(position, 1)
                }
            }

            """
        );

        Assert.Contains("BuiltIn Position", unit.Code);
        Assert.Contains("Location 0", unit.Code);
        Assert.Contains("OpEntryPoint Vertex", unit.Code);
    }

    [Fact]
    public void A_fragment_stage_declares_where_its_origin_is() {
        var code = Fragment("        return float4(1, 1, 1, 1)");

        // Vulkan accepts only the upper-left origin, and requires it be stated.
        Assert.Contains("OpExecutionMode", code);
        Assert.Contains("OriginUpperLeft", code);
    }

    [Fact]
    public void Stage_inputs_get_consecutive_locations() {
        var code = Fragment(
            "        return float4(a, b, 0, 1)",
            signature: "func Fragment(a: float, b: float): float4"
        );

        Assert.Contains("Location 0", code);
        Assert.Contains("Location 1", code);
    }

    // --- Types -------------------------------------------------------------

    [Theory]
    [InlineData("bool", "OpTypeBool")]
    [InlineData("int", "OpTypeInt 32 1")]
    [InlineData("uint", "OpTypeInt 32 0")]
    [InlineData("float", "OpTypeFloat 32")]
    [InlineData("double", "OpTypeFloat 64")]
    public void Scalars_map_onto_their_spirv_type(string raven, string expected) {
        var code = Fragment($"        var probe: {raven}\n        return float4(0, 0, 0, 1)");

        Assert.Contains(expected, code);
    }

    [Fact]
    public void A_matrix_becomes_a_repeated_column() {
        // Raven's mat2x3 is 2 rows by 3 columns, so SPIR-V holds 3 columns of 2.
        var code = Fragment("        var probe: mat2x3\n        return float4(0, 0, 0, 1)");

        var matrix = Lines(code).Single(line => line.Contains("OpTypeMatrix")).Split(' ');
        var column = matrix[^2];
        Assert.Equal("3", matrix[^1]);

        // Three columns, each holding two components.
        Assert.Contains(Lines(code), line => line.StartsWith($"{column} = OpTypeVector") && line.EndsWith(" 2"));
    }

    [Fact]
    public void A_double_pulls_in_the_capability_that_allows_it() {
        var code = Fragment("        var probe: double\n        return float4(0, 0, 0, 1)");

        Assert.Contains("OpCapability Float64", code);
    }

    [Fact]
    public void A_struct_keeps_its_field_names_and_is_built_by_composite() {
        var code = One(
                """
                package A

                struct Ray {
                    var origin: float3
                    var direction: float3

                    func At(t: float): float3 => origin + direction * t
                }

                shader S {
                    [FragmentShader]
                    func Fragment(): float4 {
                        var ray: Ray
                        ray.origin = float3(0, 0, 0)
                        ray.direction = float3(0, 0, 1)
                        return float4(ray.At(1f), 1)
                    }
                }

                """
            )
            .Code;

        Assert.Contains("OpName", code);
        Assert.Contains("\"Ray\"", code);
        Assert.Contains("\"origin\"", code);
        Assert.Contains("OpTypeStruct", code);
    }

    // --- Control flow ------------------------------------------------------

    [Fact]
    public void An_if_declares_its_merge_before_it_branches() {
        var code = Fragment(
            """
                    if (level > 1f) {
                        return float4(1, 0, 0, 1)
                    } else {
                        return float4(0, 1, 0, 1)
                    }
            """,
            "    var level: float\n"
        );

        Assert.Contains("OpSelectionMerge", code);
        Assert.Contains("OpBranchConditional", code);

        // Both arms return, so nothing reaches the merge — it still has to exist,
        // and it says so rather than pretending to fall through.
        Assert.Contains("OpUnreachable", code);
    }

    [Fact]
    public void A_loop_puts_its_step_in_the_continue_target() {
        var code = Fragment(
            """
                    var total = 0f
                    for (i in 0 .. 3) {
                        total += 1f
                    }

                    return float4(total, 0, 0, 1)
            """
        );

        // SPIR-V names the continue target, so the step simply goes there — none
        // of the first-iteration flag the GLSL backend has to invent.
        // SPIR-V names the continue target, so the step simply goes there — none
        // of the first-iteration flag the GLSL backend has to invent.
        var header = Lines(code).Single(line => line.Contains("OpLoopMerge")).Split(' ');
        var @continue = header[2];

        Assert.Contains($"OpBranch {@continue}", code);
        Assert.Contains(Lines(code), line => line == $"{@continue} = OpLabel");

        // The step is what that block holds: the loop counter's increment.
        var step = Lines(code).SkipWhile(line => line != $"{@continue} = OpLabel").ToArray();
        Assert.Contains(step.TakeWhile(line => !line.StartsWith("OpBranch")), line => line.Contains("OpIAdd"));
    }

    [Fact]
    public void Break_and_continue_branch_to_the_targets_the_header_declared() {
        var code = Fragment(
            """
                    var total = 0f
                    for (i in 0 .. 8) {
                        if (i == 2) {
                            continue
                        }

                        if (i == 5) {
                            break
                        }

                        total += 1f
                    }

                    return float4(total, 0, 0, 1)
            """
        );

        var loopMerge = code.Split('\n').Single(l => l.Contains("OpLoopMerge"));
        var operands = loopMerge.Split(' ');
        var merge = operands[1];
        var @continue = operands[2];

        // Every branch to those two labels is a break or a continue.
        Assert.Contains($"OpBranch {merge}", code);
        Assert.Contains($"OpBranch {@continue}", code);
    }

    [Fact]
    public void A_repeat_loop_tests_after_its_body() {
        var code = Fragment(
            """
                    var total = 0f
                    repeat {
                        total += 1f
                    } while (total < 4f)

                    return float4(total, 0, 0, 1)
            """
        );

        Assert.Contains("OpLoopMerge", code);

        // The header goes straight to the body; the test sits at the end, in the
        // continue block, and branches back to the header.
        var lines = code.Split('\n');
        var header = Array.FindIndex(lines, l => l.Contains("OpLoopMerge"));
        Assert.Contains("OpBranch", lines[header + 1]);
        Assert.Contains("OpFOrdLessThan", code);
    }

    // --- Instructions ------------------------------------------------------

    [Fact]
    public void A_select_over_vectors_gets_a_condition_per_lane() {
        var code = Fragment(
            "        val picked = level > 1f ? a : b\n        return float4(picked, 1)",
            "    var level: float\n    var a: float3\n    var b: float3\n"
        );

        // Before SPIR-V 1.4 a vector select needs a bvec, so a scalar test is
        // broadcast rather than passed through.
        Assert.Contains("OpTypeVector %2 3", code);
        Assert.Contains("OpSelect", code);
    }

    [Fact]
    public void A_swizzle_read_becomes_a_shuffle_and_one_lane_an_extract() {
        var code = Fragment(
            "        return float4(v.xyz, v.w)",
            "    var v: float4\n"
        );

        // Several lanes are not a location, so they are shuffled out of the
        // loaded value; a single lane is one, so the pointer reaches it directly.
        Assert.Contains("OpVectorShuffle", code);
        Assert.Contains("OpAccessChain", code);
    }

    [Fact]
    public void Writing_some_lanes_reads_the_vector_shuffles_and_writes_it_back() {
        var code = Fragment(
            """
                    var v = float4(0, 0, 0, 1)
                    v.xy = float2(1, 1)
                    return v
            """
        );

        // A vector's lanes are not separately addressable, so a partial write is
        // a read, a shuffle and a whole write.
        Assert.Contains("OpVectorShuffle", code);
        Assert.Contains("OpStore", code);
    }

    [Theory]
    [InlineData("normalize(v)", "Normalize")]
    [InlineData("lerp(v, v, 0.5f)", "FMix")]
    [InlineData("frac(f)", "Fract")]
    [InlineData("rsqrt(f)", "InverseSqrt")]
    [InlineData("atan2(f, f)", "Atan2")]
    [InlineData("max(f, f)", "FMax")]
    [InlineData("length(v)", "Length")]
    [InlineData("reflect(v, v)", "Reflect")]
    public void Intrinsics_reach_the_glsl_extended_instruction_set(string expression, string expected) {
        var code = Fragment(
            $"        val probe = {expression}\n        return float4(0, 0, 0, 1)",
            "    var v: float3\n    var f: float\n"
        );

        Assert.Contains("OpExtInstImport \"GLSL.std.450\"", code);
        Assert.Contains("OpExtInst", code);
        Assert.Contains(((int)Enum.Parse<GlslStd450>(expected)).ToString(), Operands(code, "OpExtInst"));
    }

    [Theory]
    [InlineData("dot(v, v)", "OpDot")]
    [InlineData("transpose(m)", "OpTranspose")]
    [InlineData("all(v < v)", "OpAll")]
    [InlineData("any(v < v)", "OpAny")]
    [InlineData("ddx(f)", "OpDPdx")]
    public void Some_intrinsics_are_core_opcodes(string expression, string expected) {
        var code = Fragment(
            $"        val probe = {expression}\n        return float4(0, 0, 0, 1)",
            "    var v: float3\n    var f: float\n    var m: mat3\n"
        );

        Assert.Contains(expected, code);
    }

    [Fact]
    public void Saturate_becomes_a_clamp_against_constants_it_has_to_build() {
        var code = Fragment(
            "        val probe = saturate(v)\n        return float4(probe, 1)",
            "    var v: float3\n"
        );

        // There is no saturate instruction, and the bounds have to match the
        // argument's shape, so two vector constants are constructed first.
        Assert.Contains(((int)GlslStd450.FClamp).ToString(), Operands(code, "OpExtInst"));
        Assert.Contains("OpCompositeConstruct", code);
    }

    [Theory]
    [InlineData("a + b", "OpIAdd")]
    [InlineData("a / b", "OpSDiv")]
    [InlineData("a % b", "OpSRem")]
    [InlineData("a << b", "OpShiftLeftLogical")]
    [InlineData("a >> b", "OpShiftRightArithmetic")]
    [InlineData("a & b", "OpBitwiseAnd")]
    [InlineData("a ^ b", "OpBitwiseXor")]
    public void Integer_operators_pick_the_signed_instruction(string expression, string expected) {
        var code = Fragment(
            $"        val probe = {expression}\n        return float4(0, 0, 0, 1)",
            "    var a: int\n    var b: int\n"
        );

        Assert.Contains(expected, code);
    }

    [Fact]
    public void An_unsigned_divide_is_a_different_instruction_from_a_signed_one() {
        var code = Fragment(
            "        val probe = a / b\n        return float4(0, 0, 0, 1)",
            "    var a: uint\n    var b: uint\n"
        );

        Assert.Contains("OpUDiv", code);
        Assert.DoesNotContain("OpSDiv", code);
    }

    [Fact]
    public void A_vector_times_a_scalar_is_its_own_instruction() {
        var code = Fragment(
            "        return float4(v * f, 1)",
            "    var v: float3\n    var f: float\n"
        );

        // The IR splats the scalar first, so this is a plain componentwise
        // multiply — the shaped instruction is there for the forms that reach it.
        Assert.Contains("OpFMul", code);
    }

    [Fact]
    public void A_conversion_names_the_direction_it_goes() {
        var code = Fragment(
            "        val probe: float = i\n        return float4(probe, 0, 0, 1)",
            "    var i: int\n"
        );

        Assert.Contains("OpConvertSToF", code);
    }

    // --- What SPIR-V will not take -----------------------------------------

    /// <summary>
    ///     An unsized array is still refused here, though nothing written in Raven reaches it.
    /// </summary>
    /// <remarks>
    ///     <c>RVN2126</c> now catches the declaration, which is where the fix is. This stays as a
    ///     backstop for the one route that skips the binder — an unsized array decoded out of a
    ///     <c>.rvnlib</c> — and is built from the IR directly, because there is no longer any source
    ///     that produces one.
    /// </remarks>
    [Fact]
    public void An_unsized_array_is_rejected_rather_than_emitted() {
        Assert.Contains(
            UnsizedArrayDiagnostics("spirv"),
            d => d.Id == "RVN4001" && d.IsError
        );
    }

    /// <summary>
    ///     A compute entry point is a <c>GLCompute</c> module with a <c>LocalSize</c> execution
    ///     mode, which <c>spirv-val</c> requires — a module without one is rejected outright.
    /// </summary>
    [Fact]
    public void A_compute_entry_point_declares_GLCompute_and_LocalSize() {
        var unit = One(
            """
            package A

            shader S {
                [ComputeShader(8, 4, 2)]
                func Main() { }
            }

            """
        );

        Assert.Contains("OpEntryPoint GLCompute", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpExecutionMode %", unit.Code, StringComparison.Ordinal);
        Assert.Contains("LocalSize 8 4 2", unit.Code, StringComparison.Ordinal);

        // OriginUpperLeft belongs to a fragment stage; a compute module must not claim it.
        Assert.DoesNotContain("OriginUpperLeft", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Each dispatch built-in becomes a <c>BuiltIn</c>-decorated <c>Input</c>, never a
    ///     located one — the two decorations are mutually exclusive, and a compute stage has no
    ///     located interface at all.
    /// </summary>
    [Theory]
    [InlineData("SV_DispatchThreadID", "uint3", "GlobalInvocationId")]
    [InlineData("SV_GroupID", "uint3", "WorkgroupId")]
    [InlineData("SV_GroupThreadID", "uint3", "LocalInvocationId")]
    [InlineData("SV_GroupIndex", "uint", "LocalInvocationIndex")]
    public void EachDispatchBuiltInIsDecoratedAsOne(string semantic, string type, string expected) {
        var unit = One(
            $$"""
              package A

              shader S {
                  [ComputeShader(64)]
                  func Main([Semantic("{{semantic}}")] id: {{type}}) { }
              }

              """
        );

        Assert.Contains($"BuiltIn {expected}", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("Location", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_boolean_stage_input_is_rejected_because_vulkan_has_no_such_interface() {
        Generate(
            """
            package A

            shader S {
                [FragmentShader]
                func Fragment(flag: bool): float4 {
                    return float4(1, 1, 1, 1)
                }
            }

            """,
            out var diagnostics,
            "spirv"
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN4001" && d.IsError);
    }

    [Fact]
    public void Dropping_a_binding_default_is_reported_once_rather_than_per_stage() {
        Generate(
            """
            package A

            shader S {
                var tint: float4 = float4(1, 1, 1, 1)

                [VertexShader]
                func Vertex(): float4 {
                    return tint
                }

                [FragmentShader]
                func Fragment(): float4 {
                    return tint
                }
            }

            """,
            out var diagnostics,
            "spirv"
        );

        var dropped = Assert.Single(diagnostics, d => d.Id == "RVN4003");
        Assert.False(dropped.IsError);
    }

    /// <summary>
    ///     A non-square matrix is the case that catches a wrong indexing convention: for a
    ///     <c>mat2x3</c> a column has 2 lanes and a row has 3, so getting it backwards is a type
    ///     error rather than a silently wrong value. The emitter used to refuse this outright.
    /// </summary>
    [Fact]
    public void Indexing_a_matrix_yields_a_column() {
        var unit = One(
            """
            package A

            shader S {
                var m: mat2x3

                [FragmentShader]
                func Fragment(): float4 {
                    val column = m[0]
                    return float4(column, 0, 1)
                }
            }

            """
        );

        // An access chain, exactly as for an array element — no gather, no transpose.
        Assert.Contains("OpAccessChain", unit.Code);

        // `One` puts it through spirv-val, which is what proves the access chain's result
        // type matches its base.
        Assert.Contains("OpTypeMatrix", unit.Code);
    }

    [Fact]
    public void The_validator_is_installed_so_these_tests_mean_something() {
        // A silent skip would make every case above vacuous, so the absence of
        // the validator is itself a visible failure.
        Assert.True(
            ValidatorAvailable,
            "spirv-val was not found. Install SPIR-V Tools (brew install spirv-tools) — without it "
            + "the SPIR-V tests only check the listing, not whether the module is valid."
        );
    }

    // --- Helpers -----------------------------------------------------------

    static uint[] Words(GeneratedSource unit) {
        var binary = unit.Binary!;
        var words = new uint[binary.Length / 4];

        for (var i = 0; i < words.Length; i++) {
            words[i] = BitConverter.ToUInt32(binary, i * 4);
        }

        return words;
    }

    static string[] Lines(string listing) => listing.Split('\n');

    /// <summary>The id of the uniform block struct, which ids alone would not pin down.</summary>
    static string BlockStruct(string listing) => Lines(listing).Single(line => line.EndsWith(" Block")).Split(' ')[1];

    /// <summary>Everything after the opcode, for every instruction using it.</summary>
    static string Operands(string listing, string op) =>
        string.Join(
            "\n",
            listing.Split('\n').Where(line => line.Contains(op + " ", StringComparison.Ordinal))
        );
}
