// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     <c>groupshared</c> and the barriers: storage one workgroup shares, and the two ways to make
///     a workgroup agree about it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 called this "a storage class the language cannot declare", and
///         docs/plan/22-virtualized-geometry.md § B1 is what wanted it: hierarchical traversal is a
///         workgroup popping a node, testing its children and pushing the survivors, which is a
///         queue with a local head. Without shared memory it is one global atomic per child and a
///         dispatch that spends its life in memory traffic.
///     </para>
///     <para>
///         The three things worth checking are all about it <em>not</em> being something else: it is
///         not a binding, so nothing in the descriptor sets moves; it is not a local, so an atomic
///         may operate on it; and it is not available to a stage with no workgroups, which is where
///         both targets would otherwise be left to say so.
///     </para>
/// </remarks>
public class GroupSharedTests {
    /// <summary>
    ///     A workgroup reduction: the shape everything this feature exists for is made of.
    /// </summary>
    const string Reduce = """
                          package A

                          shader Reduce {
                              const val GroupSize: int = 64

                              groupshared var tile: float[GroupSize]

                              var input: Buffer<float>
                              var output: RWBuffer<float>

                              [ComputeShader(64)]
                              func Main([Semantic("SV_DispatchThreadID")] id: uint3, [Semantic("SV_GroupIndex")] local: uint) {
                                  tile[int(local)] = input[int(id.x)]
                                  barrier()

                                  if (local == 0u) {
                                      var sum = 0f

                                      for (i in 0 .. GroupSize - 1) {
                                          sum = sum + tile[i]
                                      }

                                      output[int(id.x)] = sum
                                  }
                              }
                          }

                          """;

    /// <summary>A workgroup-local allocator: the atomic case, which a local could not serve.</summary>
    const string Allocate = """
                            package A

                            shader Allocate {
                                groupshared var head: uint

                                var output: RWBuffer<uint>

                                [ComputeShader(64)]
                                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                    val slot = atomicAdd(head, 1u)
                                    barrier()
                                    output[int(slot)] = id.x
                                }
                            }

                            """;

    static IReadOnlyList<Diagnostic> Compile(string source) =>
        Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn")).GetDiagnostics();

    /// <summary>Compiles and lowers, returning everything either phase said.</summary>
    static IReadOnlyList<Diagnostic> LowerDiagnostics(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
        var semantic = compilation.GetDiagnostics();

        if (semantic.Any(d => d.IsError)) {
            return semantic;
        }

        var bag = new DiagnosticBag();
        Lowerer.Lower(compilation, bag);
        return [.. semantic, .. bag];
    }

    static IrModule Lower(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        return module;
    }

    /// <summary>A compute shader with a group-shared tile, around whatever body is given.</summary>
    static string Kernel(string body, string members = "") =>
        $$"""
          package A

          shader S {
              groupshared var tile: uint[64]
              groupshared var total: uint

              var output: RWBuffer<uint>
          {{members}}
              [ComputeShader(64)]
              func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                  val i = int(id.x)
          {{body}}
              }
          }

          """;

    // --- The front end -----------------------------------------------------

    [Fact]
    public void A_workgroup_reduction_compiles() {
        Assert.Empty(Compile(Reduce));
    }

    [Fact]
    public void A_workgroup_allocator_compiles() {
        Assert.Empty(Compile(Allocate));
    }

    /// <summary>
    ///     The declaration is not a binding, which is the fact everything downstream rests on.
    /// </summary>
    /// <remarks>
    ///     A group-shared field reaching <c>BindingPlan</c> would take a <c>(set, binding)</c> pair
    ///     from the resources after it and shift every one of them — a shader that renumbers its own
    ///     descriptors by declaring a scratch tile, which nothing would report and a host would
    ///     discover as the wrong texture.
    /// </remarks>
    [Fact]
    public void Group_shared_storage_is_not_a_binding() {
        var shader = Assert.Single(Lower(Reduce).Shaders);

        Assert.DoesNotContain(shader.Bindings, binding => binding.Name == "tile");
        Assert.Equal(["input", "output"], shader.Bindings.Select(b => b.Name).ToArray());

        var shared = Assert.Single(shader.SharedVariables);
        Assert.Equal("tile", shared.Name);
    }

    /// <summary>
    ///     An atomic may operate on it, and that is the second half of what B1 asked for.
    /// </summary>
    /// <remarks>
    ///     The rule the atomics live under is not "a writable resource" but "memory more than one
    ///     invocation reaches", and a workgroup is more than one invocation. A local is still
    ///     refused — see <see cref="AtomicTests.AnAtomicOnALocalIsRefused" /> — so this is the rule
    ///     widening to what it always meant rather than being relaxed.
    /// </remarks>
    [Fact]
    public void An_atomic_may_operate_on_group_shared_storage() {
        Assert.Empty(Compile(Allocate));

        var module = Lower(Allocate);
        var atomic = Assert.Single(Flatten(LoweringTestBase.FindFunction(module, "Main").Body)
            .OfType<IrAtomicInstruction>());

        // A place, not a loaded value — the same invariant a resource atomic has, and for the same
        // reason: nothing done to a copy is indivisible.
        Assert.Equal("head", atomic.Place.Root.Name);
        Assert.DoesNotContain(
            Flatten(LoweringTestBase.FindFunction(module, "Main").Body).OfType<IrLoadInstruction>(),
            load => load.Place.Root.Name == "head"
        );
    }

    [Theory]
    [InlineData("groupshared val tile2: uint", "RVN2135")]
    [InlineData("groupshared var tile2: uint = 3u", "RVN2134")]
    [InlineData("groupshared var tile2: Texture2D", "RVN2133")]
    [InlineData("groupshared stream var tile2: float", "RVN2132")]
    [InlineData("groupshared compose val tile2: uint", "RVN2132")]
    [InlineData("groupshared const val tile2: uint = 3u", "RVN2132")]
    public void A_declaration_that_cannot_be_workgroup_storage_is_refused(string declaration, string id) {
        Assert.Contains(
            Compile(Kernel("        output[i] = 0u", $"    {declaration}\n")),
            d => d.Id == id && d.IsError
        );
    }

    /// <summary>
    ///     A workgroup belongs to a dispatch, so the declaration belongs to a shader.
    /// </summary>
    [Fact]
    public void Group_shared_storage_outside_a_shader_is_refused() {
        var diagnostics = Compile(
            """
            package A

            struct Tile {
                groupshared var scratch: uint
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2131" && d.IsError);
    }

    // --- The stage rule ----------------------------------------------------

    /// <summary>
    ///     A stage with no workgroups may not reach either the storage or a barrier.
    /// </summary>
    /// <remarks>
    ///     Reported here rather than left to the backends, for the reason <c>RVN3008</c> gives about
    ///     <c>discard</c>: <c>Workgroup</c> storage and a workgroup-scoped <c>OpControlBarrier</c>
    ///     are legal only under the <c>GLCompute</c> execution model, so the alternative is not
    ///     silence — it is <c>spirv-val</c> rejecting a module, with no span to point at.
    /// </remarks>
    [Theory]
    [InlineData("        tile[0] = 1u\n        return float4(0, 0, 0, 1)")]
    [InlineData("        barrier()\n        return float4(0, 0, 0, 1)")]
    public void A_fragment_stage_may_not_reach_workgroup_storage(string body) {
        var diagnostics = LowerDiagnostics(
            $$"""
              package A

              shader S {
                  groupshared var tile: uint[64]

                  [FragmentShader]
                  func Fragment(): float4 {
              {{body}}
                  }
              }

              """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN3012" && d.IsError);
    }

    /// <summary>
    ///     Reachability, not where the call is written: a helper belongs to whichever stages call
    ///     it.
    /// </summary>
    [Fact]
    public void The_stage_rule_follows_the_call_graph() {
        var diagnostics = LowerDiagnostics(
            """
            package A

            shader S {
                func Sync() {
                    barrier()
                }

                [FragmentShader]
                func Fragment(): float4 {
                    Sync()
                    return float4(0, 0, 0, 1)
                }
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN3012" && d.IsError);
    }

    /// <summary>
    ///     Only what the stage actually reaches is declared — workgroup memory is a budget, and a
    ///     device only has to offer 16 KB of it.
    /// </summary>
    [Fact]
    public void A_stage_declares_only_the_shared_storage_it_reaches() {
        var module = Lower(
            """
            package A

            shader S {
                groupshared var used: uint
                groupshared var unused: uint[1024]

                var output: RWBuffer<uint>

                [ComputeShader(64)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    used = id.x
                    output[int(id.x)] = used
                }
            }

            """
        );

        var shader = Assert.Single(module.Shaders);
        Assert.Equal(["used", "unused"], shader.SharedVariables.Select(s => s.Name).ToArray());

        var entryPoint = Assert.Single(shader.EntryPoints);
        Assert.Equal(["used"], entryPoint.SharedVariables.Select(s => s.Name).ToArray());
    }

    // --- The backends ------------------------------------------------------

    [Fact]
    public void Glsl_declares_it_shared_and_spells_the_barrier() {
        var code = CodeGenTestBase.GenerateOne(Reduce);

        Assert.Contains("shared float tile[64];", code, StringComparison.Ordinal);
        Assert.Contains("barrier();", code, StringComparison.Ordinal);

        // Not a binding: nothing about it takes a set, a binding or a block.
        Assert.DoesNotContain("uniform float tile", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Glsl_spells_the_memory_only_barrier_separately() {
        Assert.Contains(
            "memoryBarrierShared();",
            CodeGenTestBase.GenerateOne(Kernel("        tile[i] = 1u\n        memoryBarrierShared()")),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     SPIR-V's variable is in the <c>Workgroup</c> storage class, and its barrier carries the
    ///     scopes and semantics that make it mean anything.
    /// </summary>
    /// <remarks>
    ///     The semantics operand is where this goes quietly wrong. An <c>OpControlBarrier</c> with
    ///     zero semantics is an execution barrier and nothing else: every invocation has arrived,
    ///     and nothing they wrote is guaranteed visible — which is correct on the hardware people
    ///     test on and a race on the hardware they do not.
    /// </remarks>
    [Fact]
    public void Spirv_uses_the_workgroup_storage_class_and_a_release_barrier() {
        var listing = SpirvTestBase.One(Reduce).Code;

        Assert.Contains("OpTypePointer Workgroup", listing, StringComparison.Ordinal);
        Assert.Contains("OpVariable %", listing, StringComparison.Ordinal);

        var line = Assert.Single(
            listing.Split('\n'),
            l => l.Contains("OpControlBarrier", StringComparison.Ordinal)
        );

        // OpControlBarrier %execution %memory %semantics
        var operands = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, ConstantNamed(listing, operands[1]));
        Assert.Equal(2, ConstantNamed(listing, operands[2]));

        // AcquireRelease (0x8) over WorkgroupMemory (0x100), which is what glslang emits for the
        // same GLSL — so docs/plan/07 § C's differential keeps comparing like with like.
        Assert.Equal(0x108, ConstantNamed(listing, operands[3]));
    }

    /// <summary>
    ///     Not in the entry point's interface list, which is a version rule rather than a taste.
    /// </summary>
    /// <remarks>
    ///     Before SPIR-V 1.4 an <c>OpEntryPoint</c> lists <c>Input</c> and <c>Output</c> variables
    ///     and nothing else, and this backend emits 1.0 — so a <c>Workgroup</c> id there is what a
    ///     1.4 module would do and what <c>spirv-val</c> rejects in this one. <c>One()</c> has
    ///     already run the validator by the time this reads the listing; the assertion says what it
    ///     was checking.
    /// </remarks>
    [Fact]
    public void The_shared_variable_stays_out_of_the_entry_point_interface() {
        var listing = SpirvTestBase.One(Reduce).Code;

        var entry = Assert.Single(
            listing.Split('\n'),
            l => l.Contains("OpEntryPoint", StringComparison.Ordinal)
        );

        var shared = Assert.Single(
            listing.Split('\n').Select(l => l.Trim()),
            l => l.Contains("OpVariable", StringComparison.Ordinal)
                && l.EndsWith(" Workgroup", StringComparison.Ordinal)
        );

        Assert.DoesNotContain(shared.Split(' ')[0], entry, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And a real GLSL front end takes what came out. <c>shared</c> outside a compute stage,
    ///     or a <c>barrier()</c> where there is no group, is refused by <c>glslc</c> outright —
    ///     which is what makes this the check on the whole storage-class decision rather than on the
    ///     spelling.
    /// </summary>
    [Fact]
    public void A_real_front_end_accepts_the_emitted_glsl() {
        Assert.SkipUnless(ReferenceCompiler.Available, ReferenceCompiler.HowToInstall);

        foreach (var unit in CodeGenTestBase.GenerateClean(Reduce)) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }

        foreach (var unit in CodeGenTestBase.GenerateClean(Allocate)) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

    // --- The artefact boundary ---------------------------------------------

    /// <summary>
    ///     A function that touches it cannot be exported to a <c>.rvnlib</c>, and says why.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The third refusal in the family, and it earns its own id for the reason the second
    ///         did: what a library cannot carry is anything whose identity the consuming shader
    ///         decides. A binding's is its descriptor, a stream's is its location, and this one's is
    ///         the workgroup — which belongs to the consumer's dispatch.
    ///     </para>
    ///     <para>
    ///         The atomic form is the one worth pinning. An atomic reaches its storage without
    ///         loading it — that is the whole content of the word — so it is not a load and was not
    ///         being looked at: a helper whose <em>only</em> use of shader state was the one use
    ///         that cannot be a copy would have exported cleanly and linked to a variable the
    ///         consumer never declared.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("        total = total + 1u", "RVN5008")]
    [InlineData("        val old = atomicAdd(total, 1u)", "RVN5008")]
    public void A_function_that_touches_it_cannot_be_exported(string body, string id) {
        var compilation = Compilation.Create(
            "Test",
            SyntaxTree.ParseText(
                $$"""
                  package A

                  shader S {
                      groupshared var total: uint

                      func Bump() {
                  {{body}}
                      }
                  }

                  """,
                path: "Test.rvn"
            )
        );

        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        Vixen.Raven.Artefacts.LibraryBuilder.Build(compilation, Lowerer.LowerWithLinks(compilation, bag), bag);

        var error = Assert.Single(bag, d => d.Id == id);
        Assert.Contains("total", error.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>The value an <c>OpConstant</c> with this id was given.</summary>
    static int ConstantNamed(string listing, string id) {
        var line = Assert.Single(
            listing.Split('\n').Select(l => l.Trim()),
            l => l.StartsWith(id + " = OpConstant ", StringComparison.Ordinal)
        );

        return int.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
    }

    static IEnumerable<IrStatement> Flatten(IrStatement statement) {
        yield return statement;

        switch (statement) {
            case IrBlock block:
                foreach (var nested in block.Statements.SelectMany(Flatten)) {
                    yield return nested;
                }

                break;

            case IrIfStatement conditional: {
                foreach (var nested in Flatten(conditional.Then)) {
                    yield return nested;
                }

                if (conditional.Else is { } otherwise) {
                    foreach (var nested in Flatten(otherwise)) {
                        yield return nested;
                    }
                }

                break;
            }

            case IrLoopStatement loop: {
                IrBlock?[] parts = [loop.Condition, loop.Body, loop.Continue];

                foreach (var nested in parts.Where(part => part is not null).SelectMany(part => Flatten(part!))) {
                    yield return nested;
                }

                break;
            }
        }
    }
}
