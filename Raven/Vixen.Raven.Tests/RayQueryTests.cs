// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     Ray queries — <c>AccelerationStructure</c> and its one method, <c>Trace</c>.
/// </summary>
/// <remarks>
///     <para>
///         The language surface is deliberately just that method: there is no <c>rayQueryEXT</c>
///         value to hold, so each backend synthesizes the whole traversal — GLSL as an injected
///         helper function that owns the query, SPIR-V as an inline structured loop over a
///         <c>Function</c>-storage query variable. These tests pin both spellings and the two
///         things the feature costs a module: <c>GL_EXT_ray_query</c> on one side and a SPIR-V
///         1.4 header with <c>RayQueryKHR</c> on the other.
///     </para>
///     <para>
///         Everything that does not trace has to stay exactly where it was — a 1.0 header, no
///         extension — which is asserted here too, because the version bump is the easiest thing
///         to leak into every module.
///     </para>
/// </remarks>
public class RayQueryTests {
    const string Probe = """
                         package A

                         shader Probe {
                             [PerFrame] var scene: AccelerationStructure

                             [Format("rgba32f")] var target: RWTexture2D<float4>

                             [ComputeShader(8, 8, 1)]
                             func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                 val answer = scene.Trace(float3(0f, 0f, 0f), 0f, float3(0f, 0f, 1f), 100f)
                                 target.Store(int2(int(id.x), int(id.y)), answer)
                             }
                         }

                         """;

    /// <summary>
    ///     The shipped composition's shape — <c>ScreenProbeTrace</c> with <c>RayQueryField</c> in
    ///     its field slot: one kernel tracing rays, reading and writing a storage buffer, and
    ///     storing to a storage image.
    /// </summary>
    /// <remarks>
    ///     The combination is what makes it a test of its own: ray query forces the module to
    ///     SPIR-V 1.4, and 1.4 removed the <c>BufferBlock</c> decoration a 1.0 module spells
    ///     storage buffers with — so the buffer has to come out as <c>Block</c> in
    ///     <c>StorageBuffer</c> storage, or the exact module the device test dispatches fails
    ///     validation.
    /// </remarks>
    const string TracedJobs = """
                              package A

                              shader TracedJobs {
                                  [PerFrame] var scene: AccelerationStructure

                                  var jobs: RWBuffer<float4>

                                  var hits: RWBuffer<int>

                                  [Format("rgba32f")] var target: RWTexture2D<float4>

                                  [ComputeShader(8, 8, 1)]
                                  func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                      val job = jobs[int(id.x)]
                                      val answer = scene.Trace(job.xyz, 0f, float3(0f, 0f, 1f), job.w)
                                      jobs[int(id.x)] = answer

                                      if (answer.w > 0f) {
                                          atomicAdd(hits[0], 1)
                                      }

                                      target.Store(int2(int(id.x), int(id.y)), answer)
                                  }
                              }

                              """;

    /// <summary>A neighbour with no ray query in it, for the nothing-else-moves assertions.</summary>
    const string Plain = """
                         package A

                         shader Plain {
                             [Format("rgba32f")] var target: RWTexture2D<float4>

                             [ComputeShader(8, 8, 1)]
                             func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                 target.Store(int2(int(id.x), int(id.y)), float4(0f, 0f, 0f, 1f))
                             }
                         }

                         """;

    [Fact]
    public void GLSL_declares_the_extension_and_traces_through_the_injected_helper() {
        var unit = Assert.Single(GenerateClean(Probe));

        // glslang only admits rayQueryEXT from 4.60, so a tracing unit states at least that.
        Assert.Contains("#version 460", unit.Code, StringComparison.Ordinal);
        Assert.Contains("#extension GL_EXT_ray_query : require", unit.Code, StringComparison.Ordinal);
        Assert.Contains("accelerationStructureEXT", unit.Code, StringComparison.Ordinal);
        Assert.Contains("rayQueryInitializeEXT", unit.Code, StringComparison.Ordinal);

        // The helper owns the query object, is defined once, and every Trace goes through it.
        Assert.Contains("vec4 vx_traceRayQuery(accelerationStructureEXT", unit.Code, StringComparison.Ordinal);
        Assert.Contains("vx_traceRayQuery(scene,", unit.Code, StringComparison.Ordinal);

        // The contract's constants, spelled in the helper: opaque geometry, every instance.
        Assert.Contains("gl_RayFlagsOpaqueEXT, 0xffu", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_front_end_accepts_the_traced_glsl() {
        Assert.SkipUnless(ReferenceCompiler.Available, ReferenceCompiler.HowToInstall);

        foreach (var unit in GenerateClean(Probe)) {
            // GlslToSpirv targets Vulkan 1.2 for a unit that declares GL_EXT_ray_query.
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

    [Fact]
    public void SPIR_V_declares_the_capability_and_moves_the_header_to_1_4() {
        // SpirvTestBase.One validates through spirv-val, which picks the 1.4 target
        // environment off the header word.
        var unit = SpirvTestBase.One(Probe);

        Assert.Contains("OpCapability RayQueryKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpExtension \"SPV_KHR_ray_query\"", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpTypeAccelerationStructureKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpTypeRayQueryKHR", unit.Code, StringComparison.Ordinal);

        // The synthesized traversal: initialize, the proceed loop, the committed read, and the
        // phi that joins hit with miss.
        Assert.Contains("OpRayQueryInitializeKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpRayQueryProceedKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpRayQueryGetIntersectionTypeKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpRayQueryGetIntersectionTKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpRayQueryGetIntersectionPrimitiveIndexKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpRayQueryGetIntersectionInstanceIdKHR", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpPhi", unit.Code, StringComparison.Ordinal);

        // SPV_KHR_ray_query requires SPIR-V 1.4, and only the modules that use it move.
        Assert.NotNull(unit.Binary);
        Assert.Equal(0x00010400u, BitConverter.ToUInt32(unit.Binary!, 4));
    }

    [Fact]
    public void A_traced_storage_buffer_becomes_a_Block_in_StorageBuffer_storage() {
        // One() validates through spirv-val at vulkan1.1spv1.4, picked off the header — the
        // referee that rejects BufferBlock in a 1.4 module.
        var unit = SpirvTestBase.One(TracedJobs);

        Assert.Contains("OpCapability RayQueryKHR", unit.Code, StringComparison.Ordinal);
        Assert.NotNull(unit.Binary);
        Assert.Equal(0x00010400u, BitConverter.ToUInt32(unit.Binary!, 4));

        // The 1.3+ spelling, forced by the version ray query forces.
        Assert.DoesNotContain("BufferBlock", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpTypePointer StorageBuffer", unit.Code, StringComparison.Ordinal);

        // The storage image is untouched by the version split, and an atomic through the
        // relocated storage class is still an atomic — the pointer's class rides
        // SpirvGlobal.Storage, so nothing about the operation had to know.
        Assert.Contains("OpImageWrite", unit.Code, StringComparison.Ordinal);
        Assert.Contains("OpAtomicIAdd", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_front_end_accepts_the_traced_buffer_glsl() {
        Assert.SkipUnless(ReferenceCompiler.Available, ReferenceCompiler.HowToInstall);

        foreach (var unit in GenerateClean(TracedJobs)) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

    [Fact]
    public void A_shader_without_ray_query_keeps_the_1_0_header() {
        var unit = SpirvTestBase.One(Plain);

        Assert.NotNull(unit.Binary);
        Assert.Equal(0x00010000u, BitConverter.ToUInt32(unit.Binary!, 4));
        Assert.DoesNotContain("RayQueryKHR", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_binding_reflects_as_its_own_descriptor_type_and_reports_the_capability() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Probe), "Probe");

        var binding = Assert.Single(shader.Bindings, b => b.Kind == IrBindingKind.AccelerationStructure);
        Assert.IsType<IrAccelerationStructureType>(binding.Type);
        Assert.False(binding.IsWritable);

        var reflection = ReflectionBuilder.Describe(shader);
        Assert.Contains(
            reflection.Sets.SelectMany(s => s.Bindings),
            b => b.Type == DescriptorType.AccelerationStructure && b.Name == "scene"
        );

        // The host gates the pipeline on this: ray query is optional hardware.
        Assert.Contains(IrCapability.RayQuery, reflection.RequiredCapabilities);
    }
}
