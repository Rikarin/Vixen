// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Shaders.Generators;
using Xunit;

namespace Tests;

/// <summary>
///     What the generator emits from Raven's reflection — docs/plan/07 § Generated C# bindings.
/// </summary>
/// <remarks>
///     <para>
///         The reflection under <c>Fixtures/</c> is Raven's own output for <c>Fixtures/Lighting.rvn</c>,
///         checked in rather than produced during the test run: the generator's contract is with the
///         *schema*, and running the compiler here would make a shader-language change look like a
///         generator failure. <c>Fixtures/README.md</c> has the command that regenerates it.
///     </para>
///     <para>
///         The fixture is shaped to cover every case the layout rules treat differently, because a
///         fixture made of <c>float4</c>s proves nothing: a <c>float3</c> followed by a
///         <c>float</c>, a <c>mat3</c> (the only type whose host bytes differ from the shader's), a
///         <c>mat4</c> (the one that must *not* be rearranged), a <c>bool</c> (four bytes), a scalar
///         array (stride 16, not 4), a struct array, and permutations both used and unused.
///     </para>
/// </remarks>
public class BindingsEmitterTests {
    static string Emit() => BindingsEmitter.Emit("Lighting", Fixture.Reflection, "Lighting.reflect.json");

    // --- Keys ---------------------------------------------------------------

    [Fact]
    public void A_permutation_becomes_a_typed_key_carrying_the_shaders_own_default() {
        var source = Emit();

        Assert.Contains(
            "public static readonly PermutationKey<bool> UseShadows = "
            + "ParameterKeys.NewPermutation<bool>(false, \"Lighting.UseShadows\");",
            source,
            StringComparison.Ordinal
        );

        Assert.Contains(
            "public static readonly PermutationKey<int> MaxLights = "
            + "ParameterKeys.NewPermutation<int>(4, \"Lighting.MaxLights\");",
            source,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     A permutation that changed no code is emitted and marked, not dropped.
    /// </summary>
    /// <remarks>
    ///     This is what <c>UsedPermutationKeys</c> is for, and it is the difference between a
    ///     tractable shader cache and 2ⁿ entries where a handful are distinct: a material may still
    ///     set <c>Unused</c>, and knowing that doing so produces the same shader is what lets the
    ///     effect system collapse the two into one compilation.
    /// </remarks>
    [Fact]
    public void An_unused_permutation_is_emitted_but_excluded_from_the_cache_key() {
        var source = Emit();

        Assert.Contains("PermutationKey<bool> Unused", source, StringComparison.Ordinal);
        Assert.Contains("Declared but unused: setting it produces the same shader.", source, StringComparison.Ordinal);

        var used = source[source.IndexOf("UsedPermutationKeys = [", StringComparison.Ordinal)..];
        used = used[..used.IndexOf("];", StringComparison.Ordinal)];

        Assert.Contains("UseShadows", used, StringComparison.Ordinal);
        Assert.Contains("MaxLights", used, StringComparison.Ordinal);
        Assert.DoesNotContain("Unused", used, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resource_becomes_a_key_of_the_handle_type_the_RHI_binds() {
        var source = Emit();

        Assert.Contains("ParameterKey<global::Vixen.Graphics.TextureViewHandle> Albedo", source, StringComparison.Ordinal);
        Assert.Contains("ParameterKey<global::Vixen.Graphics.SamplerHandle> Linear", source, StringComparison.Ordinal);
        Assert.Contains("ParameterKey<global::Vixen.Graphics.BufferHandle> Overflow", source, StringComparison.Ordinal);
    }

    // --- The constant block -------------------------------------------------

    /// <summary>
    ///     Every offset is Raven's, copied rather than recomputed — which is the whole point.
    /// </summary>
    /// <remarks>
    ///     The numbers below come from the same <c>ShaderLayout</c> pass that told the GLSL and
    ///     SPIR-V emitters where to put things, so host and shader cannot disagree about padding.
    ///     Recomputing them here would be the second implementation that eventually differs, and it
    ///     would differ silently: every byte still lands inside the buffer.
    /// </remarks>
    [Theory]
    [InlineData("worldViewProjection", 0)]
    [InlineData("normalMatrix", 64)]
    [InlineData("ambient", 112)]
    [InlineData("exposure", 124)]
    [InlineData("lightCount", 128)]
    [InlineData("enabled", 132)]
    [InlineData("weights", 144)]
    public void Each_value_is_stored_at_the_offset_the_shader_reported(string name, int offset) {
        Assert.Contains($"buffer, {offset},", Emit(), StringComparison.Ordinal);
        Assert.Contains($"// {name}", Emit(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A <c>mat3</c> is written column by column; a <c>mat4</c> is written whole.
    /// </summary>
    /// <remarks>
    ///     The asymmetry is the convention paying off rather than an inconsistency. A
    ///     <c>Matrix4x4</c>'s sixty-four bytes are already what the shader wants — read as
    ///     <c>ColMajor</c> they are its transpose, which is exactly what <c>mul(v, M)</c> needs — so
    ///     rearranging them would compute the wrong transform more expensively. A <c>Matrix3x3</c>
    ///     is nine floats end to end and std140 wants three columns of sixteen bytes, so that one
    ///     genuinely has to be taken apart.
    /// </remarks>
    [Fact]
    public void A_mat4_is_a_blit_and_a_mat3_is_not() {
        var source = Emit();

        Assert.Contains("ShaderConstants.Write(buffer, 0, in WorldViewProjection);", source, StringComparison.Ordinal);
        Assert.Contains("ShaderConstants.Write(buffer, 64, in NormalMatrix, 16);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scalar_array_carries_its_std140_stride_rather_than_its_element_size() {
        // A float[4] occupies 64 bytes, not 16: std140 rounds an array's element stride up to a
        // sixteen-byte boundary whatever the element is.
        Assert.Contains(
            "ShaderConstants.WriteArray<float>(buffer, 144, 16, 4, Weights);",
            Emit(),
            StringComparison.Ordinal
        );
    }

    // --- Struct arrays ------------------------------------------------------

    /// <summary>
    ///     A struct array becomes one element type with an indexed writer.
    /// </summary>
    /// <remarks>
    ///     The reflection delivers it as four independent leaves — <c>lights[].position</c> and so
    ///     on — and generating four parallel arrays from that would be honest to the layout and
    ///     awful to use. One struct plus the loop the caller was going to write anyway is the same
    ///     bytes.
    /// </remarks>
    [Fact]
    public void A_struct_array_becomes_an_element_type_the_caller_fills_per_slot() {
        var source = Emit();

        Assert.Contains("public struct LightingLightsElement {", source, StringComparison.Ordinal);
        Assert.Contains("public global::Vixen.Core.Mathematics.Vector3 Position;", source, StringComparison.Ordinal);
        Assert.Contains("public float Intensity;", source, StringComparison.Ordinal);

        // Element zero begins at 208, elements are 32 bytes apart, and the shader made room for 4.
        Assert.Contains("public const int BaseOffset = 208;", source, StringComparison.Ordinal);
        Assert.Contains("public const int Stride = 32;", source, StringComparison.Ordinal);
        Assert.Contains("public const int Count = 4;", source, StringComparison.Ordinal);

        // Field offsets are relative to the element, so slot i is BaseOffset + i * Stride + those.
        Assert.Contains("ShaderConstants.Write(buffer, at + 16, in Color);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_element_leaves_do_not_also_appear_as_block_fields() {
        var source = Emit();
        var constants = source[source.IndexOf("public struct LightingConstants", StringComparison.Ordinal)..];

        Assert.DoesNotContain("LightsColor", constants, StringComparison.Ordinal);
        Assert.DoesNotContain("LightsPosition", constants, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A storage buffer's members belong to the storage buffer, not to the uniform block.
    /// </summary>
    /// <remarks>
    ///     Both are bindings in the same descriptor set with offsets starting at zero, so filtering
    ///     on the binding index alone put the storage buffer's fields into the uniform block's
    ///     writer — at offsets that meant something else entirely. Found here, which is why the
    ///     fixture has both.
    /// </remarks>
    [Fact]
    public void A_storage_buffers_members_stay_out_of_the_uniform_block() {
        var constants = Emit();
        constants = constants[constants.IndexOf("public struct LightingConstants", StringComparison.Ordinal)..];

        Assert.DoesNotContain("Overflow", constants, StringComparison.Ordinal);
    }

    // --- Degenerate input ---------------------------------------------------

    [Fact]
    public void A_document_that_is_not_reflection_is_refused_rather_than_emitted_as_nothing() {
        var error = Assert.Throws<InvalidOperationException>(() => ReflectionReader.Read("""{"hello":1}"""));
        Assert.Contains("Raven reflection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shader_with_no_uniform_block_emits_keys_and_no_constants() {
        var source = BindingsEmitter.Emit(
            "Bare",
            ReflectionReader.Read("""{"Sets":[],"Parameters":[],"Permutations":[],"UsedPermutationKeys":[]}"""),
            "Bare.reflect.json"
        );

        Assert.Contains("public static class BareKeys {", source, StringComparison.Ordinal);
        Assert.DoesNotContain("struct BareConstants", source, StringComparison.Ordinal);
    }

    // --- Vertex attributes --------------------------------------------------

    /// <summary>
    ///     An attribute location is the shader's decision, so the host is told it rather than
    ///     writing it down.
    /// </summary>
    /// <remarks>
    ///     <c>Lighting</c> is the case that makes the point without being contrived: it declares one
    ///     stream, and Raven locates a stage's own parameters <em>after</em> a shader's streams — so
    ///     <c>position</c> is at 1 and <c>texcoord</c> at 2, not 0 and 1. A host that wrote the
    ///     obvious numbers would bind nothing to either, which is not a validation error but a stage
    ///     reading whatever the driver left there.
    /// </remarks>
    [Fact]
    public void A_vertex_attribute_reports_the_location_the_shader_reads_it_from() {
        var source = Emit();

        Assert.Contains("public const uint PositionLocation = 1;", source, StringComparison.Ordinal);
        Assert.Contains("public const uint TexcoordLocation = 2;", source, StringComparison.Ordinal);
        Assert.Contains("public const int VertexAttributeCount = 2;", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Nothing is invented for a shader with no vertex stage — a compute or post-process
    ///     reflection has an empty list, and an empty list is the honest answer.
    /// </summary>
    [Fact]
    public void A_shader_with_no_attributes_emits_no_locations() {
        var source = BindingsEmitter.Emit(
            "Bare",
            ReflectionReader.Read("""{"Sets":[],"Parameters":[],"Permutations":[],"UsedPermutationKeys":[]}"""),
            "Bare.reflect.json"
        );

        Assert.DoesNotContain("VertexAttributeCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Location =", source, StringComparison.Ordinal);
    }

    // --- A block per set ----------------------------------------------------

    /// <summary>A shader that marks its sets gets a key for every block, not for the first.</summary>
    /// <remarks>
    ///     <para>
    ///         Raven gathers a shader's loose uniforms into one block <em>per set</em>, and a shader
    ///         that marks none of its bindings has one set — which is why "the uniform block" was a
    ///         well-formed phrase for as long as it was. A pass that says where each binding belongs
    ///         has up to four, and generating for the first left three sets' worth of values
    ///         reachable only by spelling the name out, which is the thing generated bindings exist
    ///         to avoid.
    ///     </para>
    ///     <para>
    ///         The names say which set, because <c>PerDrawBlockSize</c> is a fact about the shader
    ///         and <c>Set3BlockSize</c> is a fact about where it happens to sit.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_shader_that_marks_its_sets_gets_a_block_for_each() {
        var source = BindingsEmitter.Emit("Pass", TwoBlocks, "Pass.reflect.json");

        // A key per block, both by their own names.
        Assert.Contains("ParameterKeys.New<float>(\"Pass.ambient\"", source, StringComparison.Ordinal);
        Assert.Contains("ParameterKeys.New<int>(\"Pass.lightCount\"", source, StringComparison.Ordinal);

        // A size and a binding per set, named for what the set is.
        Assert.Contains("public const int PerFrameBlockSize = 16;", source, StringComparison.Ordinal);
        Assert.Contains("public const int PerDrawBlockSize = 32;", source, StringComparison.Ordinal);
        Assert.Contains("public const uint PerDrawBlockBinding = 0;", source, StringComparison.Ordinal);

        // And a writer per block, qualified — because "the constant buffer" stops meaning anything
        // the moment there is more than one.
        Assert.Contains("public struct PassPerFrameConstants {", source, StringComparison.Ordinal);
        Assert.Contains("public struct PassPerDrawConstants {", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public struct PassConstants {", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A shader with one block is unchanged, which is every shader that marks no sets.
    /// </summary>
    /// <remarks>
    ///     The compatibility half, asserted rather than assumed: <c>ConstantBufferSize</c> and
    ///     <c>&lt;Shader&gt;Constants</c> are what every post-process pass and every existing host
    ///     names, and a change to the shape of those would be a change to code nobody was editing.
    /// </remarks>
    [Fact]
    public void A_shader_with_one_block_keeps_the_unadorned_names() {
        var source = Emit();

        Assert.Contains("public const int ConstantBufferSize = 336;", source, StringComparison.Ordinal);
        Assert.Contains("public struct LightingConstants {", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockSize", source, StringComparison.Ordinal);
    }

    /// <summary>A pass with a per-frame and a per-draw block, which is what marking sets produces.</summary>
    static ShaderReflection TwoBlocks =>
        ReflectionReader.Read(
            """
            {
              "Sets": [
                {
                  "Set": 0,
                  "Bindings": [
                    {
                      "Binding": 0, "Name": "PassPerFrameUniforms", "Type": "UniformBuffer", "Count": 1, "Size": 16,
                      "Members": [
                        { "Name": "ambient", "Type": { "Scalar": "Float", "Rows": 1, "Columns": 1 }, "Offset": 0, "Size": 4, "ArrayStride": 0, "MatrixStride": 0 }
                      ]
                    }
                  ]
                },
                {
                  "Set": 3,
                  "Bindings": [
                    {
                      "Binding": 0, "Name": "PassPerDrawUniforms", "Type": "UniformBuffer", "Count": 1, "Size": 32,
                      "Members": [
                        { "Name": "lightCount", "Type": { "Scalar": "Int", "Rows": 1, "Columns": 1 }, "Offset": 0, "Size": 4, "ArrayStride": 0, "MatrixStride": 0 }
                      ]
                    }
                  ]
                }
              ],
              "Parameters": [
                { "Name": "ambient", "Type": { "Scalar": "Float", "Rows": 1, "Columns": 1 }, "Set": 0, "Binding": 0, "Offset": 0, "Size": 4, "ArrayStride": 0, "MatrixStride": 0 },
                { "Name": "lightCount", "Type": { "Scalar": "Int", "Rows": 1, "Columns": 1 }, "Set": 3, "Binding": 0, "Offset": 0, "Size": 4, "ArrayStride": 0, "MatrixStride": 0 }
              ],
              "Permutations": [],
              "UsedPermutationKeys": []
            }
            """
        );
}
