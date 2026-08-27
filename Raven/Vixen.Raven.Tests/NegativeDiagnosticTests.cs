// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The other half of a diagnostic: the shader that comes within one predicate of triggering it
///     and must not.
/// </summary>
/// <remarks>
///     <para>
///         A rule with only a positive test tells you it fires. It does not tell you it fires
///         <em>only</em> when it should, and an over-firing rule is the worse of the two failures:
///         a missing rule lets a mistake through, an over-firing one refuses correct work and
///         cannot be argued with.
///     </para>
///     <para>
///         ⚠ Every fixture here is a <em>near miss</em> rather than an unrelated valid shader. For
///         a rule "X may not appear under Y" the fixture is Y with something that looks like X, or
///         X under the Y′ that is allowed — it shares the shape of the positive test and differs by
///         the one fact the rule turns on. A fixture with nothing in common with the trigger proves
///         nothing, so each one below names the positive test it is the mirror of.
///     </para>
///     <para>
///         <see cref="Silent" /> is why none of these can pass vacuously. It asserts the source
///         parses, that the named id is absent, <em>and</em> that nothing else errored — so a
///         fixture broken by a typo fails on the typo rather than quietly reporting that the rule
///         it was written to guard did not fire on a program that never compiled.
///     </para>
/// </remarks>
public class NegativeDiagnosticTests {
    // --- Plumbing ----------------------------------------------------------

    static IReadOnlyList<Diagnostic> Semantic(string source, PermutationValues? values = null) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        return Compilation.Create("Test", values ?? PermutationValues.Empty, [tree]).GetDiagnostics();
    }

    static IReadOnlyList<Diagnostic> Lowered(string source, ComposeBindings? compose = null) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create(
            "Test",
            PermutationValues.Empty,
            compose ?? ComposeBindings.Empty,
            [tree]
        );

        var bag = new DiagnosticBag();
        Lowerer.Lower(compilation, bag);

        return [.. compilation.GetDiagnostics(), .. bag];
    }

    /// <summary>
    ///     Lowering plus <see cref="IrVerifier" />, for the rules that live on the module rather
    ///     than on any one declaration.
    /// </summary>
    static IReadOnlyList<Diagnostic> Verified(string source, ComposeBindings? compose = null) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create(
            "Test",
            PermutationValues.Empty,
            compose ?? ComposeBindings.Empty,
            [tree]
        );

        var bag = new DiagnosticBag();
        IrVerifier.Verify(Lowerer.Lower(compilation, bag), bag);

        return [.. compilation.GetDiagnostics(), .. bag];
    }

    static IReadOnlyList<Diagnostic> Exported(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Lib.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Lib", tree);
        var bag = new DiagnosticBag();
        var lowered = Lowerer.LowerWithLinks(compilation, bag);
        LibraryBuilder.Build(compilation, lowered, bag);

        return [.. compilation.GetDiagnostics(), .. bag];
    }

    /// <summary>
    ///     Asserts this source does not trigger <paramref name="id" /> — and that it compiled, so
    ///     the absence means the rule held its fire rather than that nothing got far enough to ask.
    /// </summary>
    static void Silent(string id, IReadOnlyList<Diagnostic> diagnostics) {
        Assert.DoesNotContain(diagnostics, d => d.Id == id);

        Assert.True(
            diagnostics.All(d => !d.IsError),
            $"The fixture guarding {id} did not compile, so its silence proves nothing:\n"
            + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );
    }

    // --- RVN3012: workgroup storage outside a compute stage -----------------

    /// <summary>
    ///     A shader with a compute stage <em>and</em> a graphics stage: the group-shared storage
    ///     belongs to the first and the second never reaches it.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>GroupSharedTests.A_fragment_stage_may_not_reach_workgroup_storage</c>,
    ///     which is this shader with the two bodies swapped. A rule written per <em>shader</em>
    ///     rather than per stage would refuse this, and every shader that reduces in compute and
    ///     draws the result would go with it.
    /// </remarks>
    [Fact]
    public void A_compute_stage_beside_a_graphics_stage_may_use_workgroup_storage() =>
        Silent(
            "RVN3012",
            Lowered(
                """
                package A

                shader S {
                    groupshared var tile: uint[64]

                    var output: RWBuffer<uint>

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        tile[int(id.x)] = id.x
                        barrier()
                        output[int(id.x)] = tile[0]
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(1f, 1f, 1f, 1f)
                    }
                }

                """
            )
        );

    /// <summary>
    ///     A helper that barriers, called only from the compute stage of a shader that also has a
    ///     fragment stage.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>GroupSharedTests.The_stage_rule_follows_the_call_graph</c>. That test
    ///     proves the rule follows calls rather than syntax; this one proves it follows them in the
    ///     other direction too — a helper is not condemned by the mere existence of a fragment
    ///     stage that does not call it.
    /// </remarks>
    [Fact]
    public void A_barrier_in_a_helper_only_compute_calls_is_allowed() =>
        Silent(
            "RVN3012",
            Lowered(
                """
                package A

                shader S {
                    groupshared var total: uint

                    var output: RWBuffer<uint>

                    func Sync() {
                        barrier()
                    }

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        Sync()
                        output[int(id.x)] = total
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(0f, 0f, 0f, 1f)
                    }
                }

                """
            )
        );

    // --- RVN3008: discard outside a fragment stage --------------------------

    /// <summary>
    ///     A helper that discards, reached from the fragment stage of a shader that also has a
    ///     vertex stage.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>DiscardTests.A_discard_reachable_from_a_vertex_stage_is_refused</c>,
    ///     which is this shader with one extra call. Alpha cutout is written exactly like this —
    ///     the vertex stage is right there in the file — so a rule that asked "does this shader
    ///     have a non-fragment stage" instead of "does one reach the discard" would refuse the
    ///     commonest use the feature has.
    /// </remarks>
    [Fact]
    public void A_discard_reached_only_from_the_fragment_stage_is_allowed() =>
        Silent(
            "RVN3008",
            Lowered(
                """
                package A

                shader S {
                    var cutoff: float = 0.5f

                    stream var uv: float2

                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex(position: float3): float4 {
                        uv = float2(position.x, position.y)
                        return float4(position, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        Cut(uv.x)
                        return float4(1f, 1f, 1f, 1f)
                    }

                    func Cut(v: float) {
                        if (v < cutoff) {
                            discard
                        }
                    }
                }

                """
            )
        );

    // --- RVN3005: a stream nothing reads ------------------------------------

    /// <summary>
    ///     A stream written by the vertex stage and read by the fragment stage — including one read
    ///     only inside a helper.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>StreamTests.AStreamWrittenByTheFragmentStageIsReported</c>. The rule is
    ///     "written by a stage nothing reads it from", and the consuming read is what it has to
    ///     find; a search that only looked at entry-point bodies would report both streams here,
    ///     which is the ordinary way every lit shader passes its interpolants.
    /// </remarks>
    [Fact]
    public void A_stream_the_next_stage_reads_is_not_reported() =>
        Silent(
            "RVN3005",
            Lowered(
                """
                package A

                shader Lit {
                    stream var normalWS: float3
                    stream var uv: float2

                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex(position: float3): float4 {
                        normalWS = float3(0f, 1f, 0f)
                        uv = float2(position.x, position.y)
                        return float4(position, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Shade(): float4 {
                        val n = normalize(normalWS)
                        return float4(n.x, n.y, Tint(), 1f)
                    }

                    func Tint(): float {
                        return uv.x
                    }
                }

                """
            )
        );

    // --- RVN2053: a resource outside a shader -------------------------------

    /// <summary>
    ///     The descriptors on the shader and a plain struct beside them, whose fields are the
    ///     texture's element type rather than the texture.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>ShaderSemanticsTests</c>'s <c>RVN2053</c> case, which is this file with
    ///     the <c>Texture2D</c> moved into the struct. A rule that tested "this type is used by a
    ///     struct" rather than "this field's type is a descriptor" would refuse every G-buffer
    ///     record in the shipped library.
    /// </remarks>
    [Fact]
    public void A_struct_of_values_beside_a_shaders_descriptors_is_allowed() =>
        Silent(
            "RVN2053",
            Semantic(
                """
                package A

                struct Sample {
                    var color: float4
                    var uv: float2
                    var taps: float[4]
                }

                shader S {
                    var albedo: Texture2D
                    var albedoSampler: Sampler

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        var s: Sample
                        s.uv = float2(0f, 0f)
                        s.taps = [0f, 0f, 0f, 0f]
                        s.color = albedo.Sample(albedoSampler, s.uv)

                        return s.color + float4(s.taps[0], 0f, 0f, 0f)
                    }
                }

                """
            )
        );

    // --- RVN2091: a descriptor-set marker on a non-binding ------------------

    /// <summary>
    ///     The same markers on the fields that <em>do</em> become bindings: a uniform, a texture, a
    ///     sampler and a buffer.
    /// </summary>
    /// <remarks>
    ///     The mirror of
    ///     <c>DescriptorSetTests.A_marker_on_something_that_never_becomes_a_binding_is_a_warning</c>,
    ///     which is <c>[PerFrame] const val</c> — one keyword away from the first line here. The
    ///     rule turns on the field's resource kind, and marking a binding with its set is the
    ///     feature, so an over-fire would warn on every correctly annotated shader.
    /// </remarks>
    [Fact]
    public void A_marker_on_a_field_that_does_become_a_binding_is_silent() =>
        Silent(
            "RVN2091",
            Semantic(
                """
                package A

                shader S {
                    [PerFrame] var time: float
                    [PerFrame] var albedo: Texture2D
                    [PerFrame] var albedoSampler: Sampler
                    [PerDraw] var tint: float4
                    [PerDraw] var vertices: Buffer<float4>

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return albedo.Sample(albedoSampler, float2(time, 0f)) * tint + vertices[0]
                    }
                }

                """
            )
        );

    // --- RVN2125: a format on something with no texels ----------------------

    /// <summary>Every storage image shape, each with the format its element agrees with.</summary>
    /// <remarks>
    ///     The mirror of <c>StorageImageTests.A_format_on_something_with_no_texels_says_nothing</c>,
    ///     which is <c>[Format("rgba16f")]</c> on a <c>Texture2D</c>. The rule turns on the field's
    ///     type being a storage image, and a <c>[Format]</c> is <em>required</em> on one
    ///     (<c>RVN2123</c>) — so an over-fire would make the two rules contradict each other and
    ///     leave no way to declare a storage image at all.
    /// </remarks>
    [Fact]
    public void A_format_on_a_storage_image_is_silent() {
        var diagnostics = Semantic(
            """
            package A

            shader S {
                [Format("r32f")] var height: RWTexture2D<float4>
                [Format("rgba32f")] var volume: RWTexture3D<float4>
                [Format("rgba8ui")] var mask: RWTexture2D<uint4>
                [Format("r32i")] var counter: RWTexture2D<int4>

                [ComputeShader(8, 8, 1)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    val xy = int2(int(id.x), int(id.y))
                    height.Store(xy, float4(1f, 0f, 0f, 1f))
                    volume.Store(int3(xy.x, xy.y, 0), float4(1f, 0f, 0f, 1f))
                    mask.Store(xy, uint4(1u, 2u, 3u, 4u))
                    counter.Store(xy, counter.Load(xy) + int4(1, 0, 0, 0))
                }
            }

            """
        );

        Silent("RVN2125", diagnostics);

        // And the format agreeing with the element is the other half of the same declaration.
        Silent("RVN2124", diagnostics);
    }

    // --- RVN2065 / RVN2084 / RVN2077: assignment to a compile-time slot -----

    /// <summary>
    ///     Reading a permutation key, in every position a value can appear in.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>PermutationTests.Assigning_to_a_permutation_is_rejected_with_a_reason</c>,
    ///     which is this shader with <c>Flag = true</c>. Reading the key is the entire point of
    ///     having one, so a rule that fired on any mention rather than on an assignment target
    ///     would refuse every shader in the library that branches on a permutation.
    /// </remarks>
    [Fact]
    public void Reading_a_permutation_key_is_not_assigning_to_it() =>
        Silent(
            "RVN2065",
            Semantic(
                """
                package A

                shader S {
                    [Permutation] val Flag: bool = false

                    func Probe(): float {
                        var total = 0f

                        if (Flag) {
                            total = total + 1f
                        }

                        val doubled = Flag && !Flag
                        total = total + Pick(Flag)

                        return doubled ? total : total * 2f
                    }

                    func Pick(on: bool): float {
                        return on ? 1f : 0f
                    }
                }

                """
            )
        );

    /// <summary>Reading a value parameter, and using it as an array length.</summary>
    /// <remarks>
    ///     The mirror of <c>ValueParameterTests</c>'s <c>RVN2084</c> case. A value parameter folds
    ///     at every use — the uses are what it is for — and one of them is a constant array size,
    ///     which is a position an over-wide rule would be most likely to mistake for a write
    ///     because the declaration it appears in is itself storage.
    /// </remarks>
    [Fact]
    public void Reading_a_value_parameter_is_not_assigning_to_it() =>
        Silent(
            "RVN2084",
            Semantic(
                """
                package A

                shader Blur<val TapCount: int> {
                    var source: float4

                    func Filter(): float4 {
                        var taps: float[TapCount]
                        var total = source

                        for (i in 0 .. TapCount - 1) {
                            taps[i] = 1f
                            total = total + source * taps[i]
                        }

                        return total
                    }
                }

                """,
                PermutationValues.Parse(["TapCount=4"])
            )
        );

    /// <summary>Calling through a compose slot, and passing it along.</summary>
    /// <remarks>
    ///     The mirror of <c>ComposeTests.Assigning_to_a_slot_is_rejected_with_a_reason</c>, which is
    ///     this shader with <c>thing = thing</c>. Invoking the slot is the feature; a rule that
    ///     tested "the slot appears on the left of something" rather than "the slot is an
    ///     assignment target" would refuse every composed material.
    /// </remarks>
    [Fact]
    public void Calling_through_a_compose_slot_is_not_assigning_to_it() =>
        Silent(
            "RVN2077",
            Lowered(
                """
                package A

                protocol IThing {
                    func Do(): int
                }

                shader Impl : IThing {
                    func Do(): int {
                        return 1
                    }
                }

                shader S {
                    compose val thing: IThing

                    func Probe(): int {
                        var total = thing.Do()
                        total = total + thing.Do()

                        return total
                    }
                }

                """,
                ComposeBindings.Create([new("thing", "Impl")])
            )
        );

    // --- RVN2110: an inout argument that is not storage ---------------------

    /// <summary>Every place an <c>inout</c> argument may legally be.</summary>
    /// <remarks>
    ///     The mirror of <c>InOutTests.ALiteralArgumentIsRefused</c>. <c>MaterialSurface</c>'s whole
    ///     contract is a feature accumulating into a surface the caller declared, so a rule that
    ///     admitted only a bare local — the easy case to write — would refuse the array element,
    ///     the nested struct field and the vector lane that the accumulation is actually made of.
    /// </remarks>
    [Fact]
    public void An_inout_argument_may_be_any_place() =>
        Silent(
            "RVN2110",
            Semantic(
                """
                package A

                struct Surface {
                    var color: float3
                    var roughness: float
                }

                struct Feature {
                    static func Take(inout x: float) {
                        x = 1f
                    }

                    static func Uses(): float {
                        var bare = 0f
                        var surface: Surface
                        surface.color = float3(0f, 0f, 0f)
                        surface.roughness = 0f
                        var taps: float[4]
                        taps[0] = 0f

                        Take(bare)
                        Take(surface.roughness)
                        Take(surface.color.x)
                        Take(taps[0])

                        return bare + surface.roughness + surface.color.x + taps[0]
                    }
                }

                """
            )
        );

    // --- RVN2115 / RVN2116: an array size that is not a positive constant ---

    /// <summary>An array length that is a folded constant expression rather than a literal.</summary>
    /// <remarks>
    ///     The mirror of <c>SizedArrayTests</c>'s <c>RVN2115</c> case. The rule is "the size folds
    ///     to a constant", not "the size is a literal", and the shipped reductions are written
    ///     <c>float[GroupSize]</c> — so a rule one step narrower would refuse every workgroup tile
    ///     whose size is named.
    /// </remarks>
    [Fact]
    public void An_array_size_that_folds_to_a_constant_is_allowed() {
        var diagnostics = Semantic(
            """
            package A

            shader S {
                const val GroupSize: int = 64

                groupshared var tile: float[GroupSize]
                groupshared var pairs: float[GroupSize * 2 + 1]

                var output: RWBuffer<float>

                [ComputeShader(64)]
                func Main([Semantic("SV_GroupIndex")] local: uint) {
                    tile[int(local)] = 1f
                    pairs[int(local)] = 2f
                    barrier()
                    output[int(local)] = tile[0] + pairs[0]
                }
            }

            """
        );

        Silent("RVN2115", diagnostics);
        Silent("RVN2116", diagnostics);
    }

    // --- RVN2118: a buffer element with no memory layout --------------------

    /// <summary>A buffer of a struct: vectors, a nested struct and a fixed array.</summary>
    /// <remarks>
    ///     The mirror of <c>WritableResourceTests.AnElementWithNoLayoutIsRefused</c>, which is
    ///     <c>Buffer&lt;Texture2D&gt;</c>. The rule is about descriptors having no bytes, and a
    ///     check that recursed into the struct's fields looking for anything unexpected — rather
    ///     than asking whether each has a layout — would refuse the vertex buffer every shader in
    ///     the geometry library reads.
    /// </remarks>
    [Fact]
    public void A_buffer_of_a_struct_that_lays_out_is_allowed() =>
        Silent(
            "RVN2118",
            Semantic(
                """
                package A

                struct Bounds {
                    var centre: float3
                    var radius: float
                }

                struct Vertex {
                    var position: float3
                    var uv: float2
                    var bounds: Bounds
                    var weights: float[4]
                }

                shader S {
                    var vertices: Buffer<Vertex>
                    var visible: RWBuffer<Vertex>

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        visible[int(id.x)] = vertices[int(id.x)]
                    }
                }

                """
            )
        );

    // --- RVN2104 / RVN2106 / RVN2107: the compute stage rules ---------------

    /// <summary>
    ///     One shader with all three stages: the workgroup size on the compute entry point and
    ///     nowhere else.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>ComputeTests</c>'s <c>RVN2104</c> and <c>RVN2106</c> cases, which are a
    ///     compute entry with no size and a graphics entry with one. All three rules read the same
    ///     pair of facts — this stage, this attribute — so a check that read either from the shader
    ///     rather than from the entry point would fire on all three of these at once. A cull pass
    ///     and the draw it feeds live in one file, which is why this shape is worth pinning.
    /// </remarks>
    [Fact]
    public void A_shader_with_a_compute_and_two_graphics_stages_is_silent() {
        var diagnostics = Semantic(
            """
            package A

            shader S {
                var output: RWBuffer<float>

                stream var uv: float2

                [ComputeShader(8, 8, 1)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    output[int(id.x)] = float(id.y)
                }

                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3): float4 {
                    uv = float2(position.x, position.y)
                    return float4(position, 1f)
                }

                [FragmentShader]
                [Semantic("SV_Target")]
                func Fragment(): float4 {
                    return float4(uv.x, uv.y, 0f, 1f)
                }
            }

            """
        );

        Silent("RVN2104", diagnostics);
        Silent("RVN2106", diagnostics);
        Silent("RVN2107", diagnostics);
    }

    // --- RVN2130: an atomic on something that is not shared storage ---------

    /// <summary>
    ///     Atomics on the two roots that qualify, reached through an index and through a field.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>AtomicTests.AnAtomicOnALocalIsRefused</c>. The rule is "the storage is
    ///     reachable by more than one invocation", and it has to see through indexing and member
    ///     access to find the root — a check that only recognised a bare group-shared name would
    ///     refuse the workgroup histogram and the allocator, which are the two things the feature
    ///     exists for.
    /// </remarks>
    [Fact]
    public void An_atomic_on_shared_storage_reached_through_a_place_is_allowed() {
        var diagnostics = Semantic(
            """
            package A

            struct Counters {
                var used: uint
                var slots: uint[4]
            }

            shader S {
                groupshared var tile: uint[64]
                groupshared var total: uint
                groupshared var counters: Counters

                var output: RWBuffer<uint>

                [ComputeShader(64)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    val i = int(id.x)

                    val a = atomicAdd(tile[i], 1u)
                    val b = atomicAdd(total, 1u)
                    val c = atomicAdd(counters.used, 1u)
                    val d = atomicAdd(counters.slots[0], 1u)
                    val e = atomicAdd(output[i], 1u)

                    output[i] = a + b + c + d + e
                }
            }

            """
        );

        Silent("RVN2130", diagnostics);

        // The writability half of the same call: these targets are all writable.
        Silent("RVN2119", diagnostics);
    }

    // --- RVN2136: a sampled texture's element -------------------------------

    /// <summary>The integer texels a fetch-only texture may be declared with.</summary>
    /// <remarks>
    ///     The mirror of <c>SampledTextureTests.The_element_has_to_be_an_integer_texel</c>, whose
    ///     rejected list includes the bare <c>Texture2D</c>'s <c>float4</c>. The bare name is the
    ///     float texture and has to stay silent alongside them, which is the confusion an over-wide
    ///     rule would make: one type spelled two ways.
    /// </remarks>
    [Fact]
    public void An_integer_texel_and_the_bare_texture_are_both_allowed() =>
        Silent(
            "RVN2136",
            Semantic(
                """
                package A

                shader S {
                    var identities: Texture2D<uint4>
                    var stencil: Texture2D<int4>
                    var albedo: Texture2D
                    var albedoSampler: Sampler

                    var flags: RWBuffer<uint>

                    [ComputeShader(8, 8, 1)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        val at = int3(int(id.x), int(id.y), 0)
                        flags[int(id.x)] = identities.Load(at).x + uint(stencil.Load(at).x)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return albedo.Sample(albedoSampler, float2(0f, 0f))
                    }
                }

                """
            )
        );

    // --- RVN2140: an empty collection literal -------------------------------

    /// <summary>A collection literal with elements, in the position the empty one survived in.</summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests.An_empty_collection_literal_is_reported</c>.
    ///     That one is <c>[]</c> as an expression statement — the position where nothing asks what
    ///     the literal is — so the rule has to turn on the element count rather than on the
    ///     position, and this is the same position with one element in it.
    /// </remarks>
    [Fact]
    public void A_collection_literal_with_elements_is_not_reported() =>
        Silent(
            "RVN2140",
            Semantic(
                """
                package A

                shader S {
                    func Probe(): float {
                        var taps: float[3] = [1f, 2f, 3f]
                        var one: float[1] = [4f]

                        return taps[0] + one[0]
                    }
                }

                """
            )
        );

    // --- RVN5001 / RVN5007 / RVN5008: what a library may export -------------

    /// <summary>
    ///     A function on a shader that <em>has</em> a binding, a stream and group-shared storage,
    ///     which takes each of their values as a parameter instead of reading them.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>CompiledLibraryTests.RefusesToExportABodyThatReadsABinding</c>, which is
    ///     this shader with <c>return density</c>. The three refusals are about a body naming
    ///     storage the consumer never declared, so the fix they ask for is exactly this: take it as
    ///     an argument. A rule scoped to the declaring shader rather than to the body would refuse
    ///     the fixed version too, and leave the author nowhere to go.
    /// </remarks>
    [Fact]
    public void A_body_that_takes_the_value_rather_than_reading_it_still_exports() {
        var diagnostics = Exported(
            """
            package Lib

            shader Fog {
                var density: float

                stream var uv: float2

                groupshared var tile: float[64]

                func Apply(d: float, v: float): float {
                    return v * d
                }

                func Blend(a: float, b: float): float {
                    return Apply(a, b) * 0.5f
                }
            }

            """
        );

        Silent("RVN5001", diagnostics);
        Silent("RVN5007", diagnostics);
        Silent("RVN5008", diagnostics);
    }

    // =======================================================================
    //  The second tier, ranked by what an over-fire would cost rather than by
    //  id. A rule scoped to a whole shader or a whole module is the expensive
    //  one to get wrong: it does not refuse a line, it refuses a file, and the
    //  shipped library's files each hold several entry points and several
    //  features that were written separately.
    // =======================================================================

    // --- RVN2050: two entry points for one stage ----------------------------

    /// <summary>One shader carrying a vertex, a fragment <em>and</em> a compute entry point.</summary>
    /// <remarks>
    ///     The mirror of <c>ShaderSemanticsTests</c>'s <c>RVN2050</c> case, which is two
    ///     <c>[FragmentShader]</c>s on one shader. The rule is keyed on the stage, and it has to be:
    ///     a rule that asked "does this shader already have an entry point" would refuse every
    ///     graphics shader ever written, since a vertex stage and the fragment stage it feeds are
    ///     one file by construction. Three stages rather than two because the check is a
    ///     <c>TryAdd</c> into a set, and a set keyed on the wrong thing would still let the second
    ///     one through.
    /// </remarks>
    [Fact]
    public void Three_different_stages_on_one_shader_are_allowed() =>
        Silent(
            "RVN2050",
            Semantic(
                """
                package A

                shader S {
                    var counts: RWBuffer<uint>

                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex(position: float3): float4 {
                        return float4(position, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(1f, 1f, 1f, 1f)
                    }

                    [ComputeShader(64)]
                    func Prepare([Semantic("SV_DispatchThreadID")] id: uint3) {
                        counts[int(id.x)] = id.x
                    }
                }

                """
            )
        );

    // --- RVN3006: a stream a compute stage touches --------------------------

    /// <summary>
    ///     A compute stage in the same shader as the vertex/fragment pair that owns the streams,
    ///     reaching none of them — including through a helper of its own.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>ComputeTests.AStreamUsedByAComputeStageIsRefused</c>, which is a compute
    ///     stage that writes the stream directly. This is the stage-reachability family again, and
    ///     the one where an over-fire costs most: a dispatch that prepares the draw that follows it
    ///     lives in the same file as that draw, which is how <c>Culling.rvn</c> and
    ///     <c>ClusterRaster.rvn</c> are written. A rule that asked "does this shader declare a
    ///     stream" rather than "does this entry point reach one" would refuse the pattern outright.
    /// </remarks>
    [Fact]
    public void A_compute_stage_beside_the_streams_it_never_touches_is_allowed() =>
        Silent(
            "RVN3006",
            Lowered(
                """
                package A

                shader S {
                    var counts: RWBuffer<uint>

                    stream var normalWS: float3
                    stream var uv: float2

                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Vertex(position: float3): float4 {
                        normalWS = float3(0f, 1f, 0f)
                        uv = float2(position.x, position.y)
                        return float4(position, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(Interpolated(), normalWS.y, 0f, 1f)
                    }

                    func Interpolated(): float {
                        return uv.x
                    }

                    [ComputeShader(64)]
                    func Prepare([Semantic("SV_DispatchThreadID")] id: uint3) {
                        counts[int(id.x)] = Widen(id.x)
                    }

                    func Widen(v: uint): uint {
                        return v * 2u
                    }
                }

                """
            )
        );

    // --- RVN3011: two declarations of one shared binding --------------------

    /// <summary>
    ///     Two features that declare the same shared table and agree about it, beside two that
    ///     declare the same <em>unshared</em> name in different sets.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>SharedBindingTests.Declarations_that_disagree_are_refused</c>. The rule
    ///     groups by name, so the second half is the fixture's whole point: two features that happen
    ///     to call a uniform <c>strength</c> get one contribution each and are meant to, and a rule
    ///     that grouped every binding by name rather than only the shared ones would call that a
    ///     disagreement and refuse the composition. <c>CompositeSurface</c> chains up to eight
    ///     features, so the odds of two of them picking one word are not small.
    /// </remarks>
    [Fact]
    public void Shared_declarations_that_agree_beside_unshared_ones_that_do_not_are_allowed() =>
        Silent(
            "RVN3011",
            Verified(
                """
                package A

                protocol ISurface {
                    func Compute(inout value: float4)
                }

                shader BaseColor : ISurface {
                    [PerFrame] [Shared] var textures: Texture2D[]
                    [PerFrame] var strength: float
                    var linear: Sampler

                    func Compute(inout value: float4) {
                        value = textures[0].Sample(linear, float2(0f, 0f)) * strength
                    }
                }

                shader NormalMap : ISurface {
                    [PerFrame] [Shared] var textures: Texture2D[]
                    [PerDraw] var strength: float
                    var linear: Sampler

                    func Compute(inout value: float4) {
                        value += textures[1].Sample(linear, float2(0f, 0f)) * strength
                    }
                }

                shader Composite : ISurface {
                    compose val first: ISurface
                    compose val second: ISurface

                    func Compute(inout value: float4) {
                        first.Compute(value)
                        second.Compute(value)
                    }
                }

                shader Pass {
                    compose val surface: ISurface

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        var value = float4(0f, 0f, 0f, 1f)
                        surface.Compute(value)
                        return value
                    }
                }

                """,
                ComposeBindings.Create(
                    [
                        new("Pass.surface", "Composite"),
                        new("Composite.first", "BaseColor"),
                        new("Composite.second", "NormalMap")
                    ]
                )
            )
        );

    // --- RVN2090: two set markers on one binding ----------------------------

    /// <summary>
    ///     One set marker each, on four bindings, with a second attribute standing beside two of
    ///     them.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>DescriptorSetTests</c>'s <c>RVN2090</c> case, which is two markers on
    ///     one field. The rule counts <em>resource-set</em> markers on a single field, and both
    ///     halves matter: a rule that counted markers across the shader would refuse any shader that
    ///     used more than one set — which is every shader in the pipeline — and a rule that counted
    ///     attributes rather than set markers would refuse the <c>[Format]</c> and the
    ///     <c>[Semantic]</c> that legitimately share a declaration with one.
    /// </remarks>
    [Fact]
    public void One_set_marker_per_binding_across_several_sets_is_allowed() =>
        Silent(
            "RVN2090",
            Semantic(
                """
                package A

                shader S {
                    [PerFrame] var time: float
                    [PerView] var viewProjection: mat4
                    [PerDraw] var world: mat4
                    [PerMaterial] [Format("rgba16f")] var target: RWTexture2D<float4>

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment([Semantic("TEXCOORD0")] uv: float2): float4 {
                        target.Store(int2(0, 0), float4(time, uv.x, 0f, 1f))

                        return viewProjection[0] + world[0]
                    }
                }

                """
            )
        );

    // --- RVN2139: a body that reaches itself --------------------------------

    /// <summary>
    ///     A call graph that reconverges, an overload that calls its sibling, and one name declared
    ///     on two types whose bodies call each other's.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests</c>'s <c>RVN2139</c> case, which is a body that
    ///     really does reach itself. The graph is over <em>symbols</em>, and each of these three is
    ///     a way of looking like a cycle without being one: the diamond reaches <c>Leaf</c> twice by
    ///     two routes, <c>Fit</c> resolves to a different overload than the one it is written in,
    ///     and <c>Warp.Apply</c> and <c>Fold.Apply</c> share nothing but a word. A check keyed on
    ///     the name rather than the symbol would report all three, and shading code is written
    ///     almost entirely out of small overloaded helpers.
    /// </remarks>
    [Fact]
    public void A_reconverging_graph_an_overload_and_a_shared_name_are_not_recursion() =>
        Silent(
            "RVN2139",
            Semantic(
                """
                package A

                struct Warp {
                    var k: float

                    func Apply(v: float): float {
                        return v * k
                    }
                }

                struct Fold {
                    var w: Warp

                    func Apply(v: float): float {
                        return w.Apply(v) + 1f
                    }
                }

                shader S {
                    var fold: Fold

                    func Leaf(v: float): float {
                        return v * 2f
                    }

                    func Left(v: float): float {
                        return Leaf(v)
                    }

                    func Right(v: float): float {
                        return Leaf(v) + Leaf(v)
                    }

                    func Diamond(v: float): float {
                        return Left(v) + Right(v)
                    }

                    func Fit(v: float): float {
                        return v
                    }

                    func Fit(v: float, scale: float): float {
                        return Fit(v) * scale
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(Diamond(1f), Fit(1f, 2f), fold.Apply(1f), 1f)
                    }
                }

                """
            )
        );

    // --- RVN2008: a struct whose storage reaches itself ---------------------

    /// <summary>
    ///     One struct reached twice by different routes, and a generic whose parameter is closed by
    ///     a scalar rather than by the struct that holds it.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests</c>'s <c>RVN2008</c> cases. The walk is over
    ///     the <em>constructed</em> members and compares on the original definition, which is what
    ///     makes <c>Box&lt;float&gt;</c> different from the <c>Box&lt;Holder&gt;</c> that would
    ///     close — and a walk that stopped at "this definition was seen before" rather than "this
    ///     definition is the one being laid out" would call <c>Pair</c> recursive for being used
    ///     twice, which is what a vertex record does with a wrapper.
    /// </remarks>
    [Fact]
    public void A_struct_reached_twice_and_a_generic_closed_by_a_scalar_are_not_recursive() =>
        Silent(
            "RVN2008",
            Semantic(
                """
                package A

                struct Pair {
                    var a: float
                    var b: float
                }

                struct Box<T> {
                    var item: T
                }

                struct Holder {
                    var first: Pair
                    var second: Pair
                    var nested: Box<float>
                    var boxedPair: Box<Pair>
                    var many: Pair[4]
                }

                shader S {
                    func Probe(h: Holder): float {
                        return h.first.a + h.second.b + h.nested.item + h.boxedPair.item.a + h.many[0].a
                    }
                }

                """
            )
        );

    // --- RVN2132: group-shared storage that is also something else ----------

    /// <summary>
    ///     Group-shared storage beside a permutation key, a compose slot, a stream and a constant —
    ///     four separate declarations rather than four modifiers on one.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>GroupSharedTests</c>'s three <c>RVN2132</c> rows, each of which is
    ///     <c>groupshared</c> and one other thing on the <em>same</em> field. The rule is about one
    ///     declaration claiming to be two kinds of storage, so a check that asked what the shader
    ///     contains rather than what the field is would refuse every compute shader that also has a
    ///     permutation — which is how a reduction picks its group size.
    /// </remarks>
    [Fact]
    public void Group_shared_storage_beside_other_kinds_of_field_is_allowed() =>
        Silent(
            "RVN2132",
            Lowered(
                """
                package A

                protocol IWeight {
                    func Weight(): float
                }

                shader Half : IWeight {
                    func Weight(): float {
                        return 0.5f
                    }
                }

                shader S {
                    groupshared var tile: float[64]

                    [Permutation]
                    val UseWide: bool = true

                    compose val weight: IWeight

                    stream var uv: float2

                    const val Taps = 4

                    var output: RWBuffer<float>

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        tile[int(id.x)] = float(Taps) * weight.Weight()
                        barrier()
                        output[int(id.x)] = tile[0]
                    }
                }

                """,
                ComposeBindings.Create([new("S.weight", "Half")])
            )
        );

    // --- RVN2112: an inout parameter on an entry point ----------------------

    /// <summary>
    ///     An <c>inout</c> helper called from the fragment stage of a shader whose entry points take
    ///     ordinary parameters.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>InOutTests</c>'s <c>RVN2112</c> case, which is the <c>inout</c> on the
    ///     entry point itself. The rule turns on the method's stage, and it must: <c>inout</c> is
    ///     how every composed feature in the library is written — <c>func Compute(inout value:
    ///     float4)</c> is the shape of <c>ISurface</c> — so a rule that refused <c>inout</c>
    ///     anywhere a stage could reach it would refuse the protocol the material system is built
    ///     out of.
    /// </remarks>
    [Fact]
    public void An_inout_helper_reached_from_an_entry_point_is_allowed() =>
        Silent(
            "RVN2112",
            Semantic(
                """
                package A

                shader S {
                    func Tint(inout value: float4, k: float) {
                        value *= k
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment([Semantic("TEXCOORD0")] uv: float2): float4 {
                        var value = float4(uv.x, uv.y, 0f, 1f)
                        Tint(value, 0.5f)

                        return value
                    }
                }

                """
            )
        );

    // --- RVN2137: a boolean binding -----------------------------------------

    /// <summary>
    ///     Booleans in every place a shader may put one: a local, a struct field, a parameter, a
    ///     return type and a <c>[Permutation]</c> key.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests.A_boolean_binding_is_refused</c>. The rule is
    ///     about the host's memory — there is no portable byte for a <c>bool</c> in a uniform block
    ///     — so it has to turn on the field being a binding rather than on the type being seen. A
    ///     permutation key is the case worth pinning: it is a <c>bool</c> field on a shader that is
    ///     folded to a constant before any layout happens, and it is how nearly every variant in the
    ///     library is spelled.
    /// </remarks>
    [Fact]
    public void Booleans_that_are_not_bindings_including_a_permutation_key_are_allowed() =>
        Silent(
            "RVN2137",
            Semantic(
                """
                package A

                struct Flags {
                    var lit: bool
                    var shadowed: bool
                }

                shader S {
                    [Permutation]
                    val UseSoftKnee: bool = true

                    func Decide(f: Flags, t: float): bool {
                        return f.lit && !f.shadowed && t > 0f
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        var f: Flags
                        f.lit = UseSoftKnee
                        f.shadowed = false
                        val on = Decide(f, 1f)

                        return on ? float4(1f, 1f, 1f, 1f) : float4(0f, 0f, 0f, 1f)
                    }
                }

                """
            )
        );

    // --- RVN2126: an array with no length -----------------------------------

    /// <summary>
    ///     The one unsized array both targets can express — a descriptor array of textures — beside
    ///     sized arrays of everything else.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>SizedArrayTests</c>'s <c>RVN2126</c> case. This is the exception the
    ///     rule was written around rather than a loosening of it: a bindless table is an array of
    ///     descriptors, which are not laid out at all, so the length it does not have is the point.
    ///     A rule that asked only "does this rank carry a size" would refuse
    ///     <c>docs/plan/23-bindless-materials.md</c> in its entirety.
    /// </remarks>
    [Fact]
    public void An_unsized_texture_array_beside_sized_arrays_is_allowed() =>
        Silent(
            "RVN2126",
            Semantic(
                """
                package A

                shader S {
                    var textures: Texture2D[]
                    var linear: Sampler
                    var weights: float[4]
                    var grid: float[2][3]
                    var indices: Buffer<uint>

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        val slot = int(indices[0])

                        return textures[slot].Sample(linear, float2(0f, 0f)) * weights[0] * grid[0][0]
                    }
                }

                """
            )
        );

    // --- RVN5002: an entry point a library does not carry -------------------

    /// <summary>A library of ordinary functions on a shader that has no entry point at all.</summary>
    /// <remarks>
    ///     The mirror of <c>CompiledLibraryTests.SaysThatAnEntryPointIsNotExported</c>, which is
    ///     this file with a <c>[FragmentShader]</c> on one of the functions. The notice is about the
    ///     stage, not about the shader being unexportable — everything else here does travel — so a
    ///     rule that reported once per shader rather than once per staged method would put a line of
    ///     noise against every file in a library that is nothing but helpers.
    /// </remarks>
    [Fact]
    public void A_library_of_helpers_with_no_entry_point_reports_nothing() =>
        Silent(
            "RVN5002",
            Exported(
                """
                package Lib

                shader Curves {
                    func Smooth(t: float): float {
                        return t * t * (3f - 2f * t)
                    }

                    func Remap(v: float, lo: float, hi: float): float {
                        return Smooth((v - lo) / (hi - lo))
                    }
                }

                """
            )
        );

    // --- RVN2129 / RVN2128 / RVN2127: the flow analysis ---------------------
    //
    // The rules in this file are predicates over a declaration; these three are an
    // *approximation*, which is a different kind of thing to get wrong. Every other rule
    // over-fires only by being written down wrong. An analysis over-fires by being one
    // lattice step too coarse, which is what it is designed to be everywhere else, so the
    // question "does it fire only when it should" is a real question here rather than a
    // formality — and it reaches every function in the language rather than one construct.
    //
    // ⚠ It is also the one place in the ids still owed a negative where accept coverage
    // already exists: FlowAnalysisTests.What_the_analysis_accepts pins seven shapes with
    // Assert.Empty. None is id-named and none was proved by widening, which is what these
    // add; the shapes below are the ones that theory does not hold.

    /// <summary>
    ///     Three ways out of a value-returning function that are not a trailing <c>return</c>: both
    ///     arms of an <c>if</c>, every section of a <c>switch</c>, and a <c>discard</c>.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>FlowAnalysisTests.A_value_returning_function_that_can_reach_its_end_is_refused</c>,
    ///     which is a body with a path that does fall out. ⚠ <c>discard</c> is the one that carries
    ///     this test: it is a separate <c>Exit</c> from <c>Returns</c> — kept apart only so a
    ///     message can name what made the code after it dead — and reading the analysis as "the body
    ///     must end in a return" rather than "the end must not be reachable" refuses every fragment
    ///     stage that finishes by throwing the fragment away.
    /// </remarks>
    [Fact]
    public void A_body_that_cannot_reach_its_end_need_not_end_in_a_return() =>
        Silent(
            "RVN2129",
            Semantic(
                """
                package A

                shader S {
                    var mode: int
                    var tint: float4

                    func ByArms(): float4 {
                        if (mode > 0) {
                            return tint
                        } else {
                            return tint * 2f
                        }
                    }

                    func BySections(): float4 {
                        switch (mode) {
                            case 0:
                                return tint
                            case 1:
                                return tint * 2f
                            default:
                                return tint * 3f
                        }
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        if (mode > 0) {
                            return ByArms() + BySections()
                        }

                        discard
                    }
                }

                """
            )
        );

    /// <summary>
    ///     Statements after an <c>if</c> only one arm leaves by, after a loop whose body
    ///     <c>break</c>s, and after a <c>switch</c> with no <c>default</c> every section returns
    ///     from.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>FlowAnalysisTests.A_statement_after_a_jump_is_reported</c> and
    ///     <c>Unreachable_code_inside_a_loop_after_a_break_is_reported</c>, which are these three
    ///     shapes with the other path removed. Each is a jump followed by code, and in each the code
    ///     is reached — by the arm that did not jump, by the iteration that did not break, by the
    ///     value that matched no label. An analysis that let a nested jump escape its statement
    ///     would put a warning on the ordinary early return.
    /// </remarks>
    [Fact]
    public void Code_after_a_jump_that_only_one_path_takes_is_reachable() =>
        Silent(
            "RVN2128",
            Semantic(
                """
                package A

                shader S {
                    var mode: int
                    var tint: float4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        if (mode > 0) {
                            return tint
                        }

                        var total = 0f

                        for (i in 0 .. 4) {
                            if (i > mode) {
                                break
                            }

                            total = total + 1f
                        }

                        switch (mode) {
                            case 1:
                                return tint * total
                            case 2:
                                return tint * 2f
                        }

                        return float4(total, 0f, 0f, 1f)
                    }
                }

                """
            )
        );

    /// <summary>
    ///     Locals every path does assign: through an array index, through a struct field, by an
    ///     <c>inout</c> argument, and in every section of a <c>switch</c> that has a
    ///     <c>default</c>.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>FlowAnalysisTests.A_local_assigned_in_only_one_arm_is_not_assigned_after</c>
    ///     and <c>A_switch_with_no_default_is_one_more_path</c>. The rule is sound and deliberately
    ///     incomplete, so its cost is entirely in the other direction: a definite-assignment pass
    ///     that intersects one state too few refuses code that is correct, and on a GPU the author
    ///     cannot even reproduce the complaint by running it. ⚠ The <c>switch</c> is the sharp
    ///     corner — a section that <em>returns</em> contributes no state to intersect and must also
    ///     take none away, or a shader that handles two modes and returns early from a third loses
    ///     the local both surviving paths assigned.
    /// </remarks>
    [Fact]
    public void Locals_every_path_assigns_are_assigned() =>
        Silent(
            "RVN2127",
            Semantic(
                """
                package A

                shader S {
                    var mode: int
                    var tint: float4

                    func Fill(inout v: float) {
                        v = 1f
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        var data: float[4]
                        data[0] = 1f

                        var uv: float2
                        uv.x = 0f
                        uv.y = 1f

                        var filled: float
                        Fill(filled)

                        var picked: float

                        switch (mode) {
                            case 0:
                                picked = 1f
                            case 1:
                                return tint
                            default:
                                picked = 2f
                        }

                        return tint * (data[0] + uv.x + filled + picked)
                    }
                }

                """
            )
        );

    // --- RVN2054: a member written straight into a file ---------------------

    /// <summary>
    ///     All four things a file <em>may</em> hold at package level — an enum, a protocol, a
    ///     struct and a shader — each holding the members that would be reported one level out.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests.A_member_at_package_level_is_reported</c>,
    ///     which is these same members with their type taken off. The rule turns on being a member
    ///     of a compilation unit rather than of a type, and not on which type: narrow it to the
    ///     shader — the only one a stage, a binding or a key can live in, and the one every other
    ///     "must be a shader field" rule names — and every <c>struct</c>, <c>protocol</c> and
    ///     <c>enum</c> in <c>Raven/Library</c> is refused, which is the vocabulary the shaders are
    ///     written in.
    /// </remarks>
    [Fact]
    public void The_four_type_declarations_a_file_may_hold_are_allowed() =>
        Silent(
            "RVN2054",
            Semantic(
                """
                package A

                enum Mode {
                    Flat,
                    Lit
                }

                protocol IFeature {
                    func F(): float
                }

                struct Vertex {
                    var uv: float2

                    init(at: float2) {
                        uv = at
                    }
                }

                shader Lit: IFeature {
                    const val Bias = 0.5f

                    var tint: float4
                    var mode: int

                    var exposure: float {
                        get => tint.a
                    }

                    func F(): float {
                        return Bias * exposure
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        val vertex = Vertex(float2(0f, 0f))

                        return mode == int(Mode.Lit) ? tint * F() : float4(vertex.uv.x, 0f, 0f, 1f)
                    }
                }

                """
            )
        );

    // --- RVN2060: where a permutation key may be declared -------------------

    /// <summary>
    ///     A key on a <em>feature</em> — a shader with no entry point, implementing a protocol,
    ///     reached only through the slot another shader composes it into.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>PermutationTests.A_permutation_outside_a_shader_is_rejected</c>, which is
    ///     this key on a <c>struct</c>. The rule turns on the declaring type being a
    ///     <c>shader</c> and on nothing further: add "and one that is compiled as a pipeline" — take
    ///     the entry points into the predicate — and every feature in <c>Raven/Library</c> loses its
    ///     keys, which is the half of the library that has them.
    /// </remarks>
    [Fact]
    public void A_key_on_a_feature_shader_with_no_entry_point_is_allowed() =>
        Silent(
            "RVN2060",
            Semantic(
                """
                package A

                protocol IFeature {
                    func F(): float
                }

                shader Grain: IFeature {
                    [Permutation] val Coarse: bool = false

                    func F(): float {
                        return Coarse ? 4f : 1f
                    }
                }

                shader Lit {
                    compose val feature: IFeature = Grain

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(feature.F(), 0f, 0f, 1f)
                    }
                }

                """
            )
        );

    // --- RVN2061: a key that cannot be reassigned ---------------------------

    /// <summary>
    ///     Keys declared <c>val</c> rather than <c>const val</c>, beside the mutable binding the
    ///     same shader has.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The mirror of <c>PermutationTests.A_mutable_permutation_is_rejected</c>, which is
    ///         this shader with <c>var</c> in place of <c>val</c>. <c>const</c> is what the rule
    ///         must not demand: a key <em>is</em> a compile-time constant, so asking for the keyword
    ///         reads like the same claim and is not — every <c>[Permutation] val</c> in
    ///         <c>Raven/Library</c> is written without it, and a key is not <c>const</c> precisely
    ///         because its value comes from outside the source.
    ///     </para>
    ///     <para>
    ///         ⚠ Proving that took two attempts, and the first is the interesting one. Adding
    ///         <c>|| !field.IsConst</c> to the rule left this test <em>green</em>, because
    ///         <c>SourceFieldSymbol.IsConst</c> is <c>IsPermutation || const</c> — the marker
    ///         already forces it, exactly as it forces <c>IsReadOnly</c>, which is why the rule
    ///         reads <c>IsDeclaredReadOnly</c> in the first place. A widening that cannot change the
    ///         answer proves as little as one that will not compile. What did prove it was refusing
    ///         the <c>val</c> keyword itself.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_key_declared_val_rather_than_const_val_is_allowed() =>
        Silent(
            "RVN2061",
            Semantic(
                """
                package A

                shader Lit {
                    [Permutation] val Fancy: bool = false
                    [Permutation] val Taps: int = 4

                    var tint: float4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return Fancy ? tint * float(Taps) : tint
                    }
                }

                """
            )
        );

    // --- RVN2062: what a key's type may be ----------------------------------

    /// <summary>A key of each type a define can carry — <c>bool</c>, <c>int</c> and <c>uint</c>.</summary>
    /// <remarks>
    ///     The mirror of <c>PermutationTests.A_permutation_of_an_unsupported_type_is_rejected</c>,
    ///     whose cases are the same declarations with a type outside that set. <c>uint</c> is the
    ///     one worth pinning: it is the member of the set with no user in <c>Raven/Library</c>, so
    ///     dropping it costs nothing that any shipped file would notice and it is exactly what
    ///     <c>RVN2137</c>'s message tells an author to reach for — "declare it <c>uint</c>".
    /// </remarks>
    [Fact]
    public void Keys_of_every_supported_type_are_allowed() =>
        Silent(
            "RVN2062",
            Semantic(
                """
                package A

                shader Lit {
                    [Permutation] val Fancy: bool = false
                    [Permutation] val Taps: int = 4
                    [Permutation] val Slots: uint = 8u

                    var factor: float

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(factor * float(Taps) * float(Slots), 0f, 0f, Fancy ? 1f : 0f)
                    }
                }

                """
            )
        );

    // --- RVN2063: a key's default -------------------------------------------

    /// <summary>A literal default on a key of each supported type.</summary>
    /// <remarks>
    ///     The mirror of <c>PermutationTests.A_permutation_without_a_default_is_rejected</c>, which
    ///     is the <c>bool</c> key here with its <c>= false</c> taken off. The default is what a
    ///     variant compiles with when nothing supplies a value, so the rule has to accept one for
    ///     every type the previous rule admits: read the literal as well as look for it — accept a
    ///     flag's default but not a count's — and <c>Bloom</c>, <c>Smaa</c>, <c>VolumetricFog</c>
    ///     and every other <c>[Permutation] val … : int</c> in the library is refused.
    /// </remarks>
    [Fact]
    public void A_literal_default_of_every_supported_type_is_allowed() =>
        Silent(
            "RVN2063",
            Semantic(
                """
                package A

                shader Lit {
                    [Permutation] val Fancy: bool = false
                    [Permutation] val Taps: int = 4
                    [Permutation] val Slots: uint = 8u

                    var factor: float

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(factor * float(Taps) * float(Slots), 0f, 0f, Fancy ? 1f : 0f)
                    }
                }

                """
            )
        );

    // --- RVN2064: a supplied value against the declared type ----------------

    /// <summary>
    ///     Values supplied the way a build supplies them — through
    ///     <see cref="PermutationValues.Parse" />, from <c>-D</c> text — for a key of each supported
    ///     type, beside a define for a name no shader declares.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The mirror of <c>PermutationTests.A_supplied_value_of_the_wrong_type_is_rejected</c>,
    ///         which supplies <c>true</c> for an <c>int</c> key. Two facts have to hold and neither
    ///         is the obvious one. A define for a key nothing declares is not this rule's business —
    ///         <c>A_key_no_shader_declares_is_ignored</c> is the positive side of that — so the walk
    ///         is over the declared keys and not over the supplied values. And the comparison is
    ///         against the <em>declared</em> type rather than against the CLR type the text parsed
    ///         to.
    ///     </para>
    ///     <para>
    ///         ⚠ This one needed no widening: it was <em>red on the rule as shipped</em>, which is
    ///         the first over-fire three batches of these have turned up.
    ///         <see cref="PermutationValues.TryParse" /> tries bool, then int, then uint, so
    ///         <c>Slots=16</c> arrives as an <c>int</c> however the key is declared and the
    ///         <c>uint</c> branch is reached only above <c>int.MaxValue</c>. Comparing CLR types
    ///         therefore rejected every value a build could supply for a <c>uint</c> key: the define
    ///         reported <c>RVN2064</c>, the key kept its declared default, and the variant compiled
    ///         as though nothing had been asked for. <c>SuppliedValue.TryCoerce</c> is the fix, and
    ///         <c>PermutationTests.A_uint_key_takes_the_value_a_define_supplies</c> is its positive.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Values_parsed_from_defines_match_the_keys_they_are_supplied_for() =>
        Silent(
            "RVN2064",
            Semantic(
                """
                package A

                shader Lit {
                    [Permutation] val Fancy: bool = false
                    [Permutation] val Taps: int = 4
                    [Permutation] val Slots: uint = 8u

                    var factor: float

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return float4(factor * float(Taps) * float(Slots), 0f, 0f, Fancy ? 1f : 0f)
                    }
                }

                """,
                PermutationValues.Parse(["Fancy=true", "Taps=6", "Slots=16", "NotAKeyHere=3"])
            )
        );

    // --- RVN2100: where a stream may be declared ----------------------------

    /// <summary>
    ///     A shader carrying a stream, beside a <c>struct</c> whose ordinary field has that same
    ///     name and that same type.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>StreamTests.AStreamOutsideAShaderIsReported</c>, which is this file with
    ///     <c>stream</c> written on the struct's field. The rule turns on the modifier <em>and</em>
    ///     on the kind of the type the field is declared in, and drop either half and this goes: a
    ///     walk that checks every field of a non-shader type — rather than every <c>stream</c> field
    ///     of one — reports both of <c>Vertex</c>'s, which is to say it refuses the ordinary practice
    ///     of naming a struct's field after the varying it is packed from.
    /// </remarks>
    [Fact]
    public void An_ordinary_field_named_like_a_stream_is_not_a_stream() =>
        Silent(
            "RVN2100",
            Semantic(
                """
                package A

                struct Vertex {
                    var normalWS: float3
                    var uv: float2
                }

                shader Lit {
                    stream var normalWS: float3
                    stream var uv: float2

                    [VertexShader]
                    func Vertex([Semantic("POSITION")] position: float3): float4 {
                        normalWS = float3(0f, 1f, 0f)
                        uv = float2(position.x, position.y)
                        return float4(position.x, position.y, position.z, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Shade(): float4 {
                        return float4(normalWS.x, uv.x, 0f, 1f)
                    }
                }

                """
            )
        );

    // --- RVN2101: a stream that is also something else ----------------------

    /// <summary>
    ///     One shader holding a stream, a <c>const</c>, a <c>[Permutation]</c> key and a
    ///     <c>compose</c> slot — four fields, one modifier each.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>StreamTests.AStreamThatIsAlsoAConstantIsReported</c>, whose three cases
    ///     are exactly these pairs collapsed onto a single field. The rule is about one declaration
    ///     wearing two hats, so it has to be asked per field: asked per shader — "does this shader
    ///     have both a stream and a permutation" — it refuses nearly every shader in
    ///     <c>Raven/Library</c>, where a feature flag beside a varying is the normal shape.
    /// </remarks>
    [Fact]
    public void A_stream_beside_a_constant_a_key_and_a_slot_is_allowed() =>
        Silent(
            "RVN2101",
            Semantic(
                """
                package A

                protocol IFeature {
                    func F(): float
                }

                shader Plain: IFeature {
                    func F(): float {
                        return 1f
                    }
                }

                shader Lit: IFeature {
                    stream var uv: float2

                    const val Bias = 0.5f
                    [Permutation] val Fancy: bool = false
                    compose val feature: IFeature = Plain

                    func F(): float {
                        return Bias
                    }

                    [VertexShader]
                    func Vertex([Semantic("POSITION")] position: float3): float4 {
                        uv = float2(position.x, position.y)
                        return float4(position.x, position.y, position.z, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Shade(): float4 {
                        return float4(uv.x, uv.y, Bias * feature.F(), 1f)
                    }
                }

                """
            )
        );

    // --- RVN2102: where a stream's value comes from -------------------------

    /// <summary>
    ///     A stream with no initializer that the vertex stage assigns the very literal the trigger
    ///     puts on the declaration.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>StreamTests.AStreamWithAnInitializerIsReported</c>, which is this shader
    ///     with <c>= float2(0f, 0f)</c> moved from the body up to the declaration. Being written by
    ///     a stage is what a stream is <em>for</em>, and the <c>const</c> beside it is the second
    ///     half: ask the shader whether any field has an initializer rather than asking this field,
    ///     and every graphics shader that keeps a constant next to its varyings is refused.
    /// </remarks>
    [Fact]
    public void A_stream_the_vertex_stage_assigns_has_no_initializer() =>
        Silent(
            "RVN2102",
            Semantic(
                """
                package A

                shader Lit {
                    stream var uv: float2

                    const val Bias = 0.5f

                    [VertexShader]
                    func Vertex([Semantic("POSITION")] position: float3): float4 {
                        uv = float2(0f, 0f)
                        return float4(position.x, position.y, position.z, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Shade(): float4 {
                        return float4(uv.x, uv.y, Bias, 1f)
                    }
                }

                """
            )
        );

    // --- RVN2103: what a stage interface can carry --------------------------

    /// <summary>
    ///     A stream of every type a stage interface does carry — the scalars and the vectors —
    ///     beside a <c>bool</c> and a <c>mat4</c> that are not streams.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>StreamTests.AStreamOfAnUncarryableTypeIsReported</c>, whose three cases
    ///     are <c>bool</c>, <c>mat4</c> and <c>Texture2D</c> written with <c>stream</c> on them. Two
    ///     of those three are here without it, so the rule is pinned to the modifier as well as to
    ///     the type — and the vector cases are the ones that matter: drop <c>Vector</c> from the
    ///     set a stage interface can carry and every varying normal, tangent and UV in
    ///     <c>Raven/Library</c> goes with it.
    /// </remarks>
    [Fact]
    public void Streams_of_every_carryable_type_are_allowed() =>
        Silent(
            "RVN2103",
            Semantic(
                """
                package A

                shader Lit {
                    stream var depth: float
                    stream var uv: float2
                    stream var normalWS: float3
                    stream var colour: float4
                    stream var material: int
                    stream var packed: uint
                    stream var cell: int2

                    const val Flag = true
                    var world: mat4

                    [VertexShader]
                    func Vertex([Semantic("POSITION")] position: float3): float4 {
                        depth = position.z
                        uv = float2(position.x, position.y)
                        normalWS = float3(0f, 1f, 0f)
                        colour = float4(1f, 1f, 1f, 1f)
                        material = 3
                        packed = 7u
                        cell = int2(1, 2)
                        return world * float4(position.x, position.y, position.z, 1f)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Shade(): float4 {
                        val lit = Flag ? colour : float4(0f, 0f, 0f, 1f)
                        return lit * (depth + uv.x + normalWS.y + float(material) + float(packed) + float(cell.x))
                    }
                }

                """
            )
        );

    // --- RVN2120 / RVN2121: what a push constant is and is not --------------

    /// <summary>
    ///     Push constants of the value types, in a shader that also holds the descriptors — a
    ///     texture and a sampler — that may not be pushed.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>PushConstantTests.A_descriptor_cannot_be_pushed</c>, which is this
    ///     shader with <c>[PushConstant]</c> moved onto the texture. The rule is about the marked
    ///     field's own kind: asked about the shader — "does anything here resolve to a handle" — it
    ///     refuses every material shader there is, because a push constant beside a bound texture is
    ///     the reason push constants exist.
    /// </remarks>
    [Fact]
    public void A_push_constant_beside_a_texture_that_is_not_pushed_is_allowed() =>
        Silent(
            "RVN2120",
            Semantic(
                """
                package A

                shader S {
                    [PushConstant] var offset: float2
                    [PushConstant] var world: mat4

                    var albedo: Texture2D
                    var linear: Sampler
                    var tint: float4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return albedo.Sample(linear, offset) * tint * world[0]
                    }
                }

                """
            )
        );

    /// <summary>
    ///     A push constant with no marker on it, beside the set-marked bindings the same shader
    ///     does have.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>PushConstantTests.A_set_marker_on_a_push_constant_says_something_untrue</c>,
    ///     which is this shader with <c>[PerFrame]</c> moved onto the push constant. The notice is
    ///     about the two markers meeting on one declaration, so a rule that asked whether the
    ///     shader has any set marker at all would warn on every shader that pushes a per-draw
    ///     transform and binds a per-frame camera — which is what the markers are for.
    /// </remarks>
    [Fact]
    public void A_push_constant_beside_set_marked_bindings_is_not_marked_itself() =>
        Silent(
            "RVN2121",
            Semantic(
                """
                package A

                shader S {
                    [PushConstant] var offset: float2

                    [PerFrame] var view: mat4
                    [PerDraw] var world: mat4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return (view * world)[0] + float4(offset.x, offset.y, 0f, 0f)
                    }
                }

                """
            )
        );

    // --- RVN2082 / RVN2083: a value supplied for a `val` parameter ----------

    /// <summary>
    ///     One <c>val</c> parameter supplied by its bare name and another by its qualified one, in
    ///     one compilation that holds both shaders.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>ValueParameterTests.A_value_parameter_with_no_value_is_rejected</c>.
    ///     ⚠ A key has two written forms and the rule accepts either — <c>Sharpen.Taps</c> first,
    ///     then <c>Taps</c> — so a lookup narrowed to the qualified form alone refuses every build
    ///     that supplies one value for every shader that reads it, which is what a bare <c>-D</c> on
    ///     a command line is. <c>ValueParameterTests.A_qualified_value_wins_over_a_bare_one</c> is
    ///     the positive side of the precedence; this is the side that says the bare form still
    ///     arrives.
    /// </remarks>
    [Fact]
    public void A_value_parameter_may_be_supplied_by_either_of_its_names() =>
        Silent(
            "RVN2082",
            Semantic(
                """
                package A

                shader Blur<val Taps: int> {
                    var source: float4

                    func Filter(): float4 {
                        return source * float(Taps)
                    }
                }

                shader Sharpen<val Taps: int> {
                    var source: float4

                    func Filter(): float4 {
                        return source / float(Taps)
                    }
                }

                """,
                PermutationValues.Parse(["Taps=4", "Sharpen.Taps=8"])
            )
        );

    /// <summary>
    ///     Values supplied the way a build supplies them — through
    ///     <see cref="PermutationValues.Parse" />, from <c>-D</c> text — for a <c>val</c> parameter
    ///     of each supported type.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>ValueParameterTests.A_value_of_the_wrong_type_is_rejected</c>, which
    ///     supplies <c>true</c> for an <c>int</c> parameter. ⚠ This is <c>RVN2064</c>'s sibling and
    ///     shares its code: <c>SuppliedValue.TryCoerce</c> is what both call, so the over-fire that
    ///     made <c>uint</c> permutation keys unusable made <c>uint</c> <c>val</c> parameters
    ///     unusable at the same time and by the same reasoning — <c>Slots=16</c> parses as an
    ///     <c>int</c> whatever it is meant for, and comparing CLR types rejected it. Pinned apart
    ///     from <c>RVN2064</c> because the two have separate raise sites and only one of them was
    ///     ever red.
    /// </remarks>
    [Fact]
    public void Values_parsed_from_defines_match_the_val_parameters_they_are_supplied_for() =>
        Silent(
            "RVN2083",
            Semantic(
                """
                package A

                shader Blur<val Taps: int, val Slots: uint, val Fancy: bool> {
                    var source: float4

                    func Filter(): float4 {
                        return source * float(Taps) * float(Slots) * (Fancy ? 1f : 0f)
                    }
                }

                """,
                PermutationValues.Parse(["Taps=6", "Slots=16", "Fancy=true"])
            )
        );

    // --- RVN2108 / RVN2109: a stage's built-in table ------------------------

    /// <summary>
    ///     A vertex stage whose parameters mix a recognised vertex built-in, at its declared type,
    ///     with the vertex attributes a host feeds — beside a compute stage using the closed
    ///     compute table correctly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The mirror of <c>ComputeTests.ASemanticThatNamesNoBuiltInIsRefused</c>, which is
    ///         <c>SV_Position</c> on a compute parameter. ⚠ The asymmetry is the whole rule: the
    ///         compute table is closed because a dispatch has no attributes, and the graphics table
    ///         is open because a vertex parameter list is mostly attributes. A rule that closed the
    ///         table everywhere — the easy way to write "an unknown semantic is a typo" — refuses
    ///         <c>POSITION</c> and <c>TEXCOORD0</c>, and with them every vertex shader that has an
    ///         input.
    ///     </para>
    ///     <para>
    ///         Both stages are in one shader so the closed table is present in the same file rather
    ///         than merely absent, which is what makes this a near miss instead of an unrelated
    ///         graphics shader.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_unrecognised_semantic_on_a_graphics_stage_is_an_attribute() =>
        Silent(
            "RVN2108",
            Semantic(
                """
                package A

                shader S {
                    var output: RWBuffer<float4>

                    [VertexShader]
                    func Vertex(
                        [Semantic("SV_VertexID")] id: int,
                        [Semantic("POSITION")] position: float3,
                        [Semantic("TEXCOORD0")] uv: float2
                    ): float4 {
                        return float4(position.x + uv.x, position.y + uv.y, float(id), 1f)
                    }

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] tid: uint3) {
                        output[int(tid.x)] = float4(0f, 0f, 0f, 0f)
                    }
                }

                """
            )
        );

    /// <summary>The same parameter list, held to the rule about a built-in's <em>type</em>.</summary>
    /// <remarks>
    ///     The mirror of <c>StageBuiltInTests.A_built_in_declared_at_the_wrong_type_is_refused</c>,
    ///     which is <c>SV_VertexID</c> declared <c>uint</c>. The rule compares a declared type
    ///     against the one entry in a table, so it has an answer only where the table has an entry:
    ///     a check that reached for a type whenever a parameter carried a <c>[Semantic]</c> at all
    ///     would demand one of <c>POSITION</c> and refuse whatever the host actually feeds it.
    /// </remarks>
    [Fact]
    public void A_semantic_with_no_built_in_behind_it_has_no_declared_type_to_disagree_with() =>
        Silent(
            "RVN2109",
            Semantic(
                """
                package A

                shader S {
                    [VertexShader]
                    func Vertex(
                        [Semantic("SV_VertexID")] id: int,
                        [Semantic("SV_InstanceID")] instance: int,
                        [Semantic("POSITION")] position: float3,
                        [Semantic("TEXCOORD0")] uv: float2
                    ): float4 {
                        return float4(position.x + uv.x, position.y + uv.y, float(id + instance), 1f)
                    }
                }

                """
            )
        );

    // --- RVN2138: the attribute name, as written and as read ----------------

    /// <summary>Every recognised attribute written in its <c>Attribute</c>-suffixed form.</summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests.An_unrecognised_attribute_is_reported</c>,
    ///     whose fixture is <c>[Permuation]</c> — one letter from a name that is read.
    ///     ⚠ This is the shape the whole file hunts for: a <em>written</em> name compared against a
    ///     <em>declared</em> set, with a normalisation in between.
    ///     <c>DeclarationFacts.GetAttributeName</c> strips the suffix before the lookup, so
    ///     <c>[PermutationAttribute]</c> is the same name as <c>[Permutation]</c> everywhere in the
    ///     compiler — including where the stage is read off the method. ⚠ Widening it to the raw
    ///     token warns about all five and then reports <c>RVN2137</c> as well, because <c>Flag</c>
    ///     stops being a permutation key and becomes a boolean uniform — which is the damage the
    ///     notice is really guarding, and it is silent damage: the shader still compiles, at the
    ///     wrong meaning.
    /// </remarks>
    [Fact]
    public void The_suffixed_spelling_of_a_recognised_attribute_is_the_same_name() =>
        Silent(
            "RVN2138",
            Semantic(
                """
                package A

                shader S {
                    [PermutationAttribute] val Flag: bool = true
                    [PushConstantAttribute] var offset: float4

                    [FragmentShaderAttribute]
                    [SemanticAttribute("SV_Target")]
                    func Fragment([SemanticAttribute("TEXCOORD0")] uv: float2): float4 {
                        return float4(uv.x, uv.y, offset.x, Flag ? 1f : 0f)
                    }
                }

                """
            )
        );

    // --- RVN2001: two declarations of one name ------------------------------

    /// <summary>
    ///     An overload set, a name reused in two disjoint blocks, and a parameter that shadows a
    ///     field.
    /// </summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests.Duplicate_method_signatures_are_reported_but_overloads_are_not</c>
    ///     and <c>Duplicate_locals_are_reported</c>. The rule is a collision of <em>signatures</em>
    ///     within one scope, not of names: keyed on the name alone it refuses every overload set in
    ///     the shipped library — <c>float4</c>'s constructors, every <c>Sample</c> — and keyed on
    ///     the enclosing function rather than on the block it refuses the second <c>val</c> in a
    ///     body that branches.
    /// </remarks>
    [Fact]
    public void Overloads_and_disjoint_scopes_are_not_collisions() =>
        Silent(
            "RVN2001",
            Semantic(
                """
                package A

                shader S {
                    var value: float4

                    func Take(v: int): float => float(v)

                    func Take(v: float): float => v

                    func Take(a: int, b: int): float => float(a + b)

                    func Probe(value: int): float {
                        if (value > 0) {
                            val scratch = 1
                            return Take(scratch)
                        }

                        val scratch = 2f
                        return Take(scratch) + Take(value, 1)
                    }
                }

                """
            )
        );

    // --- RVN2011: a name after a dot that is not a declared member ----------

    /// <summary>Swizzles, in both letter sets, and a matrix row's components.</summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests.Unknown_member_names_the_receiver_type</c>,
    ///     which is <c>v.missing</c> on a <c>float3</c>. ⚠ A swizzle is not a declared member and
    ///     never could be — a <c>float4</c> has hundreds — so a lookup answered from the member list
    ///     alone refuses <c>tint.rgb</c>, which is most of what a shader writes after a dot.
    /// </remarks>
    [Fact]
    public void A_swizzle_is_not_a_missing_member() =>
        Silent(
            "RVN2011",
            Semantic(
                """
                package A

                struct Surface {
                    var color: float3
                    var roughness: float
                }

                shader S {
                    var tint: float4
                    var world: mat4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        var surface: Surface
                        surface.color = tint.rgb
                        surface.roughness = tint.a
                        val lane = tint.wzyx.xy

                        return float4(surface.color.xy, lane.y, world[0].w) * surface.roughness
                    }
                }

                """
            )
        );

    // --- RVN2033: the arguments written against the parameters declared -----

    /// <summary>A call that stops at the first default, and one that fills them all.</summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests</c>'s <c>RVN2033</c> case, which calls a
    ///     one-parameter function with none. The rule compares what was written against what the
    ///     signature <em>requires</em>, and a comparison against <c>Parameters.Count</c> — the
    ///     obvious one to write — refuses every call that leaves a defaulted parameter out, which is
    ///     the only reason a default exists. ⚠ Widening <c>MinimumArgumentCount</c> alone leaves
    ///     this green and proves nothing: applicability fills the defaults in <c>TryMapArguments</c>
    ///     without consulting it, so the call resolves and the arity message is never reached. What
    ///     makes it red is <c>SourceParameterSymbol.HasDefaultValue</c>, which both of them read.
    /// </remarks>
    [Fact]
    public void A_call_may_leave_a_defaulted_parameter_out() =>
        Silent(
            "RVN2033",
            Semantic(
                """
                package A

                shader S {
                    var tint: float4

                    func Blend(color: float4, scale: float = 1f, bias: float = 0f): float4 {
                        return color * scale + float4(bias, bias, bias, bias)
                    }

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return Blend(tint) + Blend(tint, 2f) + Blend(tint, 2f, 0.5f)
                    }
                }

                """
            )
        );

    // --- RVN2044: indexing something that has elements ----------------------

    /// <summary>Every receiver an index may be written on: array, vector, matrix, buffer.</summary>
    /// <remarks>
    ///     The mirror of <c>SemanticDiagnosticsTests</c>'s <c>flag[0]</c> on a <c>bool</c>. The rule
    ///     is about a receiver with no elements at all, and a check that admitted only arrays — the
    ///     one shape whose type is written with brackets — refuses the matrix row, the vector lane
    ///     and the structured buffer read that every lighting shader in the library is made of.
    /// </remarks>
    [Fact]
    public void An_index_may_be_written_on_anything_with_elements() =>
        Silent(
            "RVN2044",
            Semantic(
                """
                package A

                shader S {
                    var world: mat4
                    var tint: float4
                    var samples: Buffer<float4>
                    var output: RWBuffer<float>

                    [ComputeShader(64)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        var taps: float[4]
                        taps[0] = tint[1]

                        output[int(id.x)] = taps[0] + world[2][3] + samples[int(id.x)].w
                    }
                }

                """
            )
        );

    // --- RVN2051: what "generic" is being said about ------------------------

    /// <summary>An entry point on a shader that is itself parameterised.</summary>
    /// <remarks>
    ///     The mirror of <c>ShaderSemanticsTests.A_generic_entry_point_is_rejected</c>, which is
    ///     <c>func Vertex&lt;T&gt;</c>. ⚠ The rule is about the <em>method's</em> type parameters,
    ///     and what parameterises a shader sits on the shader instead — here a <c>val</c> parameter,
    ///     which is written in the same angle brackets and is not a type parameter at all. Asked of
    ///     the containing shader, the rule refuses every parameterised effect there is, which is the
    ///     only kind whose entry point is worth varying. ⚠ The two representations disagree by
    ///     design and that is what makes this reachable: <c>Blur</c>'s
    ///     <c>Declaration.TypeParameterList</c> holds one entry and its <c>TypeParameters</c> is
    ///     empty, so a check written off the syntax and a check written off the symbol give
    ///     opposite answers about the same shader.
    /// </remarks>
    [Fact]
    public void An_entry_point_on_a_parameterised_shader_is_not_a_generic_entry_point() =>
        Silent(
            "RVN2051",
            Semantic(
                """
                package A

                shader Blur<val Taps: int> {
                    var source: float4

                    [FragmentShader]
                    [Semantic("SV_Target")]
                    func Fragment(): float4 {
                        return source * float(Taps)
                    }
                }

                """,
                PermutationValues.Parse(["Taps=4"])
            )
        );

    // --- RVN2073: a slot that was filled, and one that did not need it ------

    /// <summary>One compose slot bound from outside, one carrying its own default.</summary>
    /// <remarks>
    ///     The mirror of <c>ComposeTests.An_unfilled_slot_is_rejected</c>, which is this shader's
    ///     second slot with nothing supplied. A default is the slot saying what it is when nobody
    ///     asks, so a rule that asked only whether a binding was <em>supplied</em> refuses every
    ///     shader that ships a sensible default — and a library's slots are defaulted precisely so a
    ///     material does not have to name all of them.
    /// </remarks>
    [Fact]
    public void A_compose_slot_with_a_default_is_bound() =>
        Silent(
            "RVN2073",
            Lowered(
                """
                package A

                protocol IDiffuseModel {
                    func Diffuse(tint: float4): float4
                }

                shader Lambert : IDiffuseModel {
                    func Diffuse(tint: float4): float4 {
                        return tint * 0.5f
                    }
                }

                shader Half : IDiffuseModel {
                    func Diffuse(tint: float4): float4 {
                        return tint * 0.25f
                    }
                }

                shader Lit {
                    compose val diffuse: IDiffuseModel = Lambert
                    compose val rim: IDiffuseModel

                    var tint: float4

                    func Shade(): float4 {
                        return diffuse.Diffuse(tint) + rim.Diffuse(tint)
                    }
                }

                """,
                ComposeBindings.Parse(["rim=Half"])
            )
        );

    // --- RVN2133: what group-shared storage may hold ------------------------

    /// <summary>The four shapes a workgroup tile is actually written as.</summary>
    /// <remarks>
    ///     The mirror of <c>GroupSharedTests</c>'s <c>groupshared var tile2: Texture2D</c>. The rule
    ///     is "a descriptor is not memory", asked as <c>ResourceKind</c>; asked instead as "is it a
    ///     scalar" — which is what the storage class looks like in the one-line examples — it
    ///     refuses the array, the vector, the matrix and the struct, and a workgroup tile that is a
    ///     single scalar is not a reduction.
    /// </remarks>
    [Fact]
    public void Group_shared_storage_may_be_an_array_a_vector_a_matrix_or_a_struct() =>
        Silent(
            "RVN2133",
            Semantic(
                """
                package A

                struct Accum {
                    var total: float3
                    var weight: float
                }

                shader S {
                    groupshared var tile: float[64]
                    groupshared var sums: float3
                    groupshared var basis: mat4
                    groupshared var block: Accum

                    var output: RWBuffer<float>

                    [ComputeShader(64)]
                    func Main([Semantic("SV_GroupIndex")] local: uint) {
                        tile[int(local)] = 1f
                        sums = float3(0f, 0f, 0f)
                        block.total = sums
                        block.weight = tile[0]
                        barrier()

                        output[int(local)] = block.weight + block.total.x + basis[0].x
                    }
                }

                """
            )
        );

    // --- RVN2141: an expression statement that cannot do anything -----------

    /// <summary>Every form that is allowed to stand alone as a statement, in one body.</summary>
    /// <remarks>
    ///     <para>
    ///         The mirror of
    ///         <c>SemanticDiagnosticsTests.An_expression_statement_that_does_nothing_is_reported</c>,
    ///         which is this body with the effects taken out — <c>v</c>, <c>v + 1f</c>, <c>-v</c>.
    ///         The rule turns on the <em>root</em> node of the statement's expression and on
    ///         nothing else, so the near miss is each root that does something: an assignment, a
    ///         compound assignment, an increment, a call.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Pure(scale)</c> is the fixture's whole point.</b> Its result is discarded as
    ///         completely as <c>v + 1f</c>'s is, and it must stay legal: a callee may write a
    ///         resource or an <c>inout</c> argument, and the statement cannot see which from here —
    ///         <c>Bump(c)</c> is the same shape and does. A rule that asked "is the value used"
    ///         rather than "can evaluating this do anything" refuses both, and with them every
    ///         <c>Store</c>, every atomic and every barrier in the library.
    ///     </para>
    ///     <para>
    ///         ⚠ And it is deliberately not "does the subtree contain a call": the statement this
    ///         rule was written for is
    ///         <c>+ float3(Morph.Low(first), …) * weight.x</c>, which contains three.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_assignment_an_increment_and_a_call_may_stand_alone_as_statements() =>
        Silent(
            "RVN2141",
            Semantic(
                """
                package A

                struct Counter {
                    var value: int
                }

                shader S {
                    var output: RWBuffer<float>
                    var scale: float

                    func Bump(inout c: Counter) {
                        c.value = c.value + 1
                    }

                    func Pure(v: float): float => v * 2f

                    [ComputeShader(64)]
                    func Main([Semantic("SV_GroupIndex")] local: uint) {
                        var c: Counter
                        c.value = 0

                        var total = 0f

                        total = scale
                        total += scale
                        total++
                        Bump(c)
                        Pure(scale)
                        barrier()

                        output[int(local)] = total + float(c.value)
                    }
                }

                """
            )
        );
}
