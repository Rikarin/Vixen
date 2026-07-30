// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>[MaterialIndex]</c> — the per-material block as one record of a buffer, rather than a set
///     bound per draw.
/// </summary>
/// <remarks>
///     <para>
///         A draw that binds a descriptor set per material cannot be merged with a draw that binds a
///         different one, which is the sentence
///         <c>docs/plan/23-bindless-materials.md</c> opens with and the reason compacted draws are blocked.
///         One buffer holding every material in the frame, bound once, and a subscript in the
///         per-draw data makes two materials' draws identical in everything but their data.
///     </para>
///     <para>
///         Set 2 stays set 2. What changes is what is in it — every material at once rather than one
///         — so nothing renumbers and the four-set convention says what it always said.
///     </para>
/// </remarks>
public class MaterialRecordTests {
    const string Indexed = """
                           package A

                           shader S {
                               var tint: float4 = float4(1f, 1f, 1f, 1f)
                               var roughness: float = 0.5f

                               [PerDraw] [MaterialIndex] var materialIndex: uint = 0u

                               [FragmentShader]
                               func Fragment(): float4 {
                                   return tint * roughness
                               }
                           }

                           """;

    /// <summary>The same shader without the marker, which must be untouched.</summary>
    const string Bound = """
                         package A

                         shader S {
                             var tint: float4 = float4(1f, 1f, 1f, 1f)
                             var roughness: float = 0.5f

                             [FragmentShader]
                             func Fragment(): float4 {
                                 return tint * roughness
                             }
                         }

                         """;

    /// <summary>The marker turns the per-material block into a record, at the same binding.</summary>
    [Fact]
    public void The_marker_makes_the_block_a_record() {
        var block = Assert.Single(Plan(Indexed), b => b.IsBlock && b.Set == ResourceSet.PerMaterial);

        Assert.True(block.IsRecord);
        Assert.Equal(0, block.Binding);
        Assert.Equal("materialIndex", block.RecordIndex!.Name);
    }

    /// <summary>Without it, nothing changes at all.</summary>
    /// <remarks>
    ///     The control, and the one that matters most: the non-record path is what runs on GL, on
    ///     WebGL2 and on every device with no bindless at all, so it is not a legacy branch and
    ///     cannot be allowed to drift.
    /// </remarks>
    [Fact]
    public void Without_it_the_block_is_a_block() {
        var block = Assert.Single(Plan(Bound), b => b.IsBlock && b.Set == ResourceSet.PerMaterial);

        Assert.False(block.IsRecord);
        Assert.Null(block.RecordIndex);
    }

    /// <summary>
    ///     The reflection reports a storage buffer, with the offsets it was emitted at.
    /// </summary>
    /// <remarks>
    ///     <strong>The claim a wrong answer would hide.</strong> The host builds a descriptor-set
    ///     layout from this. Reporting a uniform buffer for a shader that reads a <c>BufferBlock</c>
    ///     builds a descriptor of the wrong type — which no API checks, and which reads as a frame lit
    ///     by whatever those bytes happened to mean. The packing is the same shape of mistake one
    ///     level down: a record laid out std140 puts every member at an offset the shader does not
    ///     read it from.
    /// </remarks>
    [Fact]
    public void The_reflection_reports_a_storage_buffer() {
        var record = Assert.Single(
            Reflect(Indexed).Sets.SelectMany(s => s.Bindings),
            b => b.Name == "SPerMaterialUniforms"
        );

        Assert.Equal(DescriptorType.StorageBuffer, record.Type);
        Assert.Equal(0, record.Count);

        // The offsets are the ones both backends laid the record out at, which is the number a host
        // writes bytes to. This record packs identically under either rule — a float4 then a float
        // at 16, rounded to the struct's alignment — so the rule is not observable *here*; it is
        // observable in a record whose members are not already aligned, and the reason to report the
        // rule it was emitted with rather than the one a uniform block usually has.
        Assert.Equal(0, record.Members[0].Offset);
        Assert.Equal(16, record.Members[1].Offset);

        var block = Assert.Single(
            Reflect(Bound).Sets.SelectMany(s => s.Bindings),
            b => b.Name == "SPerMaterialUniforms"
        );

        Assert.Equal(DescriptorType.UniformBuffer, block.Type);
        Assert.Equal(1, block.Count);
    }

    /// <summary>Every read of a per-material value goes through the index.</summary>
    /// <remarks>
    ///     Both of them, because one would pass with a change that indexed the first member and left
    ///     the rest reading a block that no longer exists.
    /// </remarks>
    [Fact]
    public void Every_per_material_read_goes_through_the_index() {
        var glsl = CodeGenTestBase.GenerateOne(Indexed);

        Assert.Contains("struct SPerMaterialUniforms {", glsl, StringComparison.Ordinal);
        Assert.Contains("readonly buffer", glsl, StringComparison.Ordinal);
        Assert.Contains("SPerMaterialUniforms records[];", glsl, StringComparison.Ordinal);

        Assert.Contains(".records[materialIndex].tint", glsl, StringComparison.Ordinal);
        Assert.Contains(".records[materialIndex].roughness", glsl, StringComparison.Ordinal);
    }

    /// <summary>And the unmarked shader still reads them as plain block members.</summary>
    [Fact]
    public void Without_the_marker_a_read_is_a_member() {
        var glsl = CodeGenTestBase.GenerateOne(Bound);

        Assert.Contains("uniform SPerMaterialUniforms {", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("records[", glsl, StringComparison.Ordinal);
    }

    /// <summary>SPIR-V wraps the record in a buffer block with a strided runtime array.</summary>
    /// <remarks>
    ///     The stride is what a host writes records at, and a <c>BufferBlock</c> is what makes the
    ///     variable a storage buffer in the SPIR-V 1.0 form Vulkan 1.1 accepts — the same shape
    ///     <c>EmitStorageBuffer</c> gives every other one, for the same reason: there is no bare
    ///     runtime-array variable.
    /// </remarks>
    [Fact]
    public void The_module_wraps_the_record_in_a_buffer_block() {
        var listing = SpirvTestBase.One(Indexed).Code;

        Assert.Contains("OpTypeRuntimeArray", listing, StringComparison.Ordinal);
        Assert.Contains("BufferBlock", listing, StringComparison.Ordinal);
        Assert.Contains("ArrayStride 32", listing, StringComparison.Ordinal);

        // And the unmarked one is a Block, which is a different decoration and a different object.
        var block = SpirvTestBase.One(Bound).Code;
        Assert.DoesNotContain("BufferBlock", block, StringComparison.Ordinal);
        Assert.DoesNotContain("OpTypeRuntimeArray", block, StringComparison.Ordinal);
    }

    /// <summary>Both targets accept what comes out.</summary>
    /// <remarks>
    ///     <c>spirv-val</c> is the one that can see a malformed access chain — three subscripts where
    ///     the type has two is a module it rejects — and <c>glslc</c> is the one that can see a
    ///     buffer declaration a human could not have written.
    /// </remarks>
    [Fact]
    public void Both_targets_accept_it() {
        SpirvTestBase.Validate(SpirvTestBase.One(Indexed));

        if (ReferenceCompiler.Glslc is not null) {
            var unit = CodeGenTestBase.GenerateClean(Indexed).Single();
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

    /// <summary>The same shader, with the marker made conditional on a permutation.</summary>
    const string Conditional = """
                               package A

                               shader S {
                                   [Permutation] val UseRecords: bool = false

                                   var tint: float4 = float4(1f, 1f, 1f, 1f)

                                   [PerDraw] [MaterialIndex("UseRecords")] var materialIndex: uint = 0u

                                   [FragmentShader]
                                   func Fragment(): float4 {
                                       return tint
                                   }
                               }

                               """;

    /// <summary>
    ///     A conditional marker makes one shader both, chosen per variant.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>What the shipped pass needs.</strong> Records are what a bindless device wants
    ///         and a set per material is what GL, WebGL2 and MoltenVK below argument-buffer tier 2
    ///         need (ADR-011) — so a pass that could only be one of the two would have to be written
    ///         twice, and the forward pass is four hundred lines.
    ///     </para>
    ///     <para>
    ///         Gating on the field being <em>used</em> was the tempting alternative and does not
    ///         work: a binding is a declared field, so it survives its last reader folding away. Both
    ///         variants of a shader written that way report a record, which is what a probe found
    ///         before this test existed.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_conditional_marker_follows_its_permutation(bool enabled) {
        var values = PermutationValues.Create([new("UseRecords", enabled)]);
        var block = Assert.Single(Plan(Conditional, values), b => b.IsBlock && b.Set == ResourceSet.PerMaterial);

        Assert.Equal(enabled, block.IsRecord);
    }

    /// <summary>An unsupplied condition is the fallback, which is the safe half of the pair.</summary>
    [Fact]
    public void An_unsupplied_condition_is_a_block() {
        var block = Assert.Single(
            Plan(Conditional, PermutationValues.Empty),
            b => b.IsBlock && b.Set == ResourceSet.PerMaterial
        );

        Assert.False(block.IsRecord);
    }

    // --- The fixture -------------------------------------------------------

    static IrShader Shader(string source, PermutationValues? values = null) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", values ?? PermutationValues.Empty, [tree]);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return FindShader(module, "S");
    }

    static IReadOnlyList<PlannedBinding> Plan(string source, PermutationValues? values = null) =>
        BindingPlan.Of(Shader(source, values));

    static RavenReflection Reflect(string source) => ReflectionBuilder.Describe(Shader(source), []);
}
