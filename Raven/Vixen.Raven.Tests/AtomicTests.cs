// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The atomics: an indivisible read-modify-write of one integer in a writable resource.
/// </summary>
/// <remarks>
///     <para>
///         What they unlock is the thing a compute shader cannot otherwise do — agree with the other
///         invocations about a shared number. Compaction is the case: every surviving particle takes
///         the next slot by adding one to a counter, and it is <em>the value that comes back</em>
///         that is the slot. Without an atomic, a thousand invocations read the same count and write
///         the same slot.
///     </para>
///     <para>
///         The whole design is in one sentence: the first argument is <b>storage, not a value</b>.
///         Nothing in the signature can say so — <c>inout</c> is copy-in/copy-out, which is exactly
///         what an atomic must not be — so it is a rule about the call, and these tests are mostly
///         about that rule holding at each layer.
///     </para>
/// </remarks>
public class AtomicTests {
    /// <summary>Stream compaction: the reason the whole feature exists.</summary>
    const string Compact = """
                           package A

                           shader Compact {
                               var alive: Buffer<uint>
                               var indices: RWBuffer<uint>
                               var counter: RWBuffer<uint>
                               var count: int

                               [ComputeShader(64)]
                               func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                   val index = int(id.x)

                                   if (index >= count) {
                                       return
                                   }

                                   if (alive[index] == 0u) {
                                       return
                                   }

                                   val slot = atomicAdd(counter[0], 1u)
                                   indices[int(slot)] = uint(index)
                               }
                           }

                           """;

    static IReadOnlyList<Diagnostic> Compile(string source) =>
        Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn")).GetDiagnostics();

    static IrModule Lower(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        return module;
    }

    /// <summary>A compute shader with one atomic in it, around whatever body is given.</summary>
    static string Kernel(string body, string members = "") =>
        $$"""
          package A

          shader S {
              var data: RWBuffer<uint>
              var signed: RWBuffer<int>
              var readOnly: Buffer<uint>
              var scale: uint
          {{members}}
              [ComputeShader(64)]
              func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                  val i = int(id.x)
          {{body}}
              }
          }

          """;

    // --- The front end ----------------------------------------------------

    [Fact]
    public void CompactionCompiles() {
        Assert.Empty(Compile(Compact));
    }

    /// <summary>
    ///     Every atomic there is, on both signednesses, so a new one cannot arrive unbound.
    /// </summary>
    [Theory]
    [InlineData("atomicAdd")]
    [InlineData("atomicMin")]
    [InlineData("atomicMax")]
    [InlineData("atomicAnd")]
    [InlineData("atomicOr")]
    [InlineData("atomicXor")]
    [InlineData("atomicExchange")]
    public void EveryBinaryAtomicBinds(string name) {
        Assert.Empty(Compile(Kernel($"        val old = {name}(data[i], 3u)")));
        Assert.Empty(Compile(Kernel($"        val old = {name}(signed[i], 3)")));
    }

    [Fact]
    public void CompareExchangeBinds() {
        Assert.Empty(Compile(Kernel("        val old = atomicCompareExchange(data[i], 0u, 7u)")));
    }

    /// <summary>
    ///     A float has no atomic in either target, so the overloads simply are not there.
    /// </summary>
    /// <remarks>
    ///     Refused by overload resolution rather than by a rule of its own: GLSL 4.5 core has no
    ///     atomic on a float and SPIR-V's needs a capability, so declaring the signature and then
    ///     reporting at the backend would be a promise made in the language and broken in the
    ///     emitter.
    /// </remarks>
    [Fact]
    public void AnAtomicOnAFloatHasNoOverload() {
        var diagnostics = Compile(
            Kernel("        val old = atomicAdd(numbers[i], 1f)", "    var numbers: RWBuffer<float>\n")
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2031" && d.IsError);
    }

    // --- The rule that the target is storage -------------------------------

    /// <summary>
    ///     An expression is not storage, so there is nothing for the operation to be atomic about.
    /// </summary>
    [Theory]
    [InlineData("data[i] + 1u")]
    [InlineData("3u")]
    [InlineData("scale")]
    public void AnAtomicOnAValueIsRefused(string target) {
        var diagnostics = Compile(Kernel($"        val old = atomicAdd({target}, 1u)"));

        // `scale` is a uniform, which is storage — but host storage, so it is the writability rule
        // that catches it rather than this one. Either way the call is refused.
        Assert.Contains(diagnostics, d => d.Id is "RVN2130" or "RVN2119" && d.IsError);
    }

    /// <summary>A read-modify-write is a write, so a read-only buffer is refused as one.</summary>
    /// <remarks>
    ///     The same <c>RVN2119</c> a store would give, with the same one-character fix in its
    ///     message. Reusing it rather than inventing a second id is the point: the rule is not
    ///     "atomics are special", it is "this is a write".
    /// </remarks>
    [Fact]
    public void AnAtomicOnAReadOnlyBufferIsRefused() {
        var error = Assert.Single(
            Compile(Kernel("        val old = atomicAdd(readOnly[i], 1u)")),
            d => d.Id == "RVN2119"
        );

        Assert.Contains("readOnly", error.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("RWBuffer", error.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A local is storage and is still refused, because it is not storage anyone else can reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The rule the first draft had was "the argument must be a place", which admitted this
    ///         and read as the more general rule. It is not: GLSL says so outright — <i>"only
    ///         l-values corresponding to shader block storage or shared variables can be used with
    ///         atomic memory functions"</i> — so a local target binds, verifies, emits, and is then
    ///         rejected by the GLSL front end, which is the one failure mode this whole language is
    ///         arranged to catch earlier.
    ///     </para>
    ///     <para>
    ///         It is also right on the merits: an atomic on memory one invocation owns has nothing to
    ///         be indivisible against, so the operation is an ordinary add wearing a fence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnAtomicOnALocalIsRefused() {
        var error = Assert.Single(
            Compile(Kernel("        var total = 0u\n        val old = atomicAdd(total, 1u)")),
            d => d.Id == "RVN2130"
        );

        Assert.Contains("one invocation", error.GetMessage(), StringComparison.Ordinal);
    }

    // --- Lowering ---------------------------------------------------------

    /// <summary>
    ///     The IR carries a <em>place</em>, and that is the whole correctness argument.
    /// </summary>
    /// <remarks>
    ///     A load followed by an intrinsic would compile, validate and run, and would be an ordinary
    ///     add on a copy — a race with no symptom but a wrong answer under contention. So the check
    ///     that matters is that nothing loaded the target.
    /// </remarks>
    [Fact]
    public void TheTargetIsAPlaceAndIsNeverLoaded() {
        var module = Lower(Compact);
        var body = LoweringTestBase.FindFunction(module, "Main").Body;
        var atomic = Assert.Single(Flatten(body).OfType<IrAtomicInstruction>());

        Assert.Equal(IrAtomicOp.Add, atomic.Op);
        Assert.Equal("counter", atomic.Place.Root.Name);
        Assert.Null(atomic.Comparand);

        Assert.DoesNotContain(
            Flatten(body).OfType<IrLoadInstruction>(),
            load => load.Place.Root.Name == "counter"
        );

        Assert.Contains("atomic.add @counter", IrPrinter.Print(module), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The comparand is the middle argument here and the last operand in SPIR-V, so the IR has to
    ///     say which is which rather than leave it to position.
    /// </summary>
    [Fact]
    public void CompareExchangeKeepsItsComparandApartFromItsValue() {
        var module = Lower(Kernel("        val old = atomicCompareExchange(data[i], 4u, 9u)"));
        var atomic = Assert.Single(Flatten(LoweringTestBase.FindFunction(module, "Main").Body).OfType<IrAtomicInstruction>());

        Assert.Equal(IrAtomicOp.CompareExchange, atomic.Op);
        Assert.NotNull(atomic.Comparand);

        var printed = IrPrinter.Print(module);
        Assert.Contains("atomic.compareExchange", printed, StringComparison.Ordinal);
    }

    // --- The backends ------------------------------------------------------

    [Fact]
    public void GlslEmitsAnAtomicOnTheLValue() {
        var code = CodeGenTestBase.GenerateOne(Compact);

        // The block member, not a temporary: GLSL's atomics take an l-value, and a generated
        // `uint _7 = counter[0]; atomicAdd(_7, 1u)` would compile and be a plain add.
        Assert.Contains("atomicAdd(counter[0], 1u)", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("atomicMin(data[i], 3u)", "atomicMin(data[")]
    [InlineData("atomicExchange(data[i], 3u)", "atomicExchange(data[")]
    [InlineData("atomicCompareExchange(data[i], 3u, 4u)", "atomicCompSwap(data[")]
    public void GlslNamesEachAtomicItsOwnWay(string call, string expected) {
        Assert.Contains(expected, CodeGenTestBase.GenerateOne(Kernel($"        val old = {call}")), StringComparison.Ordinal);
    }

    /// <summary>
    ///     SPIR-V's atomic takes two constants the author never writes, and they are the two that
    ///     decide whether it is atomic across the dispatch or only across a workgroup.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Device scope (1) and relaxed semantics (0), which is what glslang emits for the same
    ///         GLSL. Getting the scope wrong is the failure that does not show up in a test: a
    ///         workgroup-scoped atomic on a storage buffer is correct for every dispatch small
    ///         enough to be one workgroup, and wrong for every one that is not.
    ///     </para>
    ///     <para>
    ///         Read out of the listing by resolving the operand ids rather than matched as text,
    ///         since ids are assigned as the module is built and the next emitter change would shift
    ///         them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SpirvAsksForDeviceScopeAndRelaxedSemantics() {
        var listing = SpirvTestBase.One(Compact).Code;

        var line = Assert.Single(
            listing.Split('\n'),
            l => l.Contains("OpAtomicIAdd", StringComparison.Ordinal)
        );

        // %result = OpAtomicIAdd %type %pointer %scope %semantics %value
        var operands = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1, ConstantNamed(listing, operands[5]));
        Assert.Equal(0, ConstantNamed(listing, operands[6]));

        // The pointer is an access chain into the block, not the block variable itself.
        Assert.Contains("OpAccessChain", listing, StringComparison.Ordinal);
    }

    /// <summary>The value an <c>OpConstant</c> with this id was given.</summary>
    static int ConstantNamed(string listing, string id) {
        var line = Assert.Single(
            listing.Split('\n').Select(l => l.Trim()),
            l => l.StartsWith(id + " = OpConstant ", StringComparison.Ordinal)
        );

        return int.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Signedness splits in SPIR-V where it does not in GLSL, and the place's type is what says
    ///     which.
    /// </summary>
    [Theory]
    [InlineData("atomicMin(data[i], 3u)", "OpAtomicUMin")]
    [InlineData("atomicMin(signed[i], 3)", "OpAtomicSMin")]
    [InlineData("atomicMax(data[i], 3u)", "OpAtomicUMax")]
    [InlineData("atomicMax(signed[i], 3)", "OpAtomicSMax")]
    [InlineData("atomicCompareExchange(data[i], 3u, 4u)", "OpAtomicCompareExchange")]
    public void SpirvPicksTheOpcodeTheOperandsAskFor(string call, string expected) {
        Assert.Contains(expected, SpirvTestBase.One(Kernel($"        val old = {call}")).Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And a real GLSL front end takes what came out — which is the check that matters most
    ///     here.
    /// </summary>
    /// <remarks>
    ///     An atomic is the one construct whose whole meaning is in <em>which</em> operand it got:
    ///     <c>atomicAdd(x, 1u)</c> on a temporary compiles, validates and runs, and is a plain add.
    ///     Handing the emitted GLSL to <c>glslc</c> is what says the argument really is the block
    ///     member, since a temporary would be a non-l-value and refused outright.
    /// </remarks>
    [Fact]
    public void ARealFrontEndAcceptsTheEmittedGlsl() {
        Assert.SkipUnless(ReferenceCompiler.Available, ReferenceCompiler.HowToInstall);

        foreach (var unit in CodeGenTestBase.GenerateClean(Compact)) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
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
