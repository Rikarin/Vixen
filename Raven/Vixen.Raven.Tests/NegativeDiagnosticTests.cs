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
}
