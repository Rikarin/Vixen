// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     The compute stage: a workgroup size in the language, the dispatch built-ins, and what a
///     compute stage has to be refused because it has no pipeline interface.
/// </summary>
/// <remarks>
///     Both backends reported <c>RVN4002</c> for this stage until now, for one reason: nothing in
///     the language declared a workgroup size. The tests here are grouped around that — what
///     declares it, what happens when it is missing or wrong, and what each target does with it —
///     plus the interface rules, which is where enabling the stage created new ways to
///     mis-emit.
/// </remarks>
public class ComputeTests {
    const string Members = """
                               var scale: float

                           """;

    // --- The workgroup size ------------------------------------------------

    [Fact]
    public void AWorkgroupSizeBindsOntoTheEntryPoint() {
        var compilation = Compile("[ComputeShader(8, 4, 2)]", "func Main()");
        Assert.Empty(compilation.GetDiagnostics());

        var entryPoint = Assert.Single(compilation.GetEntryPoints());
        Assert.Equal(ShaderStage.Compute, entryPoint.Stage);
        Assert.Equal(new WorkgroupSize(8, 4, 2), entryPoint.WorkgroupSize);
    }

    /// <summary>
    ///     One or two dimensions is enough; the rest are 1. A 1-D dispatch is the common case and
    ///     should not have to spell two dimensions it does not use.
    /// </summary>
    [Theory]
    [InlineData("[ComputeShader(64)]", 64, 1, 1)]
    [InlineData("[ComputeShader(16, 16)]", 16, 16, 1)]
    [InlineData("[ComputeShader(4, 4, 4)]", 4, 4, 4)]
    public void OmittedWorkgroupDimensionsAreOne(string attribute, int x, int y, int z) {
        var compilation = Compile(attribute, "func Main()");
        Assert.Empty(compilation.GetDiagnostics());

        var entryPoint = Assert.Single(compilation.GetEntryPoints());
        Assert.Equal(new WorkgroupSize(x, y, z), entryPoint.WorkgroupSize);
    }

    /// <summary>
    ///     A missing size is an error, not a default of <c>(1, 1, 1)</c>.
    /// </summary>
    /// <remarks>
    ///     The distinction that matters: a default would compile and run and be wrong by whatever
    ///     factor the author assumed, and no later stage could tell a guessed size from a chosen
    ///     one.
    /// </remarks>
    [Fact]
    public void AComputeStageWithoutAWorkgroupSizeIsRefused() {
        var diagnostics = Compile("[ComputeShader]", "func Main()").GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "RVN2104" && d.IsError);
    }

    /// <summary>
    ///     A size that cannot be read is its own error, distinct from an absent one — a
    ///     non-literal, a non-positive value, a named argument, or a fourth dimension.
    /// </summary>
    [Theory]
    [InlineData("[ComputeShader(0)]")]
    [InlineData("[ComputeShader(-4)]")]
    [InlineData("[ComputeShader(8, 0, 1)]")]
    [InlineData("[ComputeShader(1, 1, 1, 1)]")]
    [InlineData("[ComputeShader(y: 8)]")]
    [InlineData("[ComputeShader(\"8\")]")]
    [InlineData("[ComputeShader(8f)]")]
    public void AWorkgroupSizeThatCannotBeReadIsRefused(string attribute) {
        var diagnostics = Compile(attribute, "func Main()").GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "RVN2105" && d.IsError);
    }

    /// <summary>
    ///     A size on a graphics stage warns rather than being ignored: only a compute dispatch has
    ///     workgroups, so the author believes something untrue.
    /// </summary>
    [Fact]
    public void AWorkgroupSizeOnAGraphicsStageWarns() {
        var diagnostics = Compile("[VertexShader(8, 8, 1)]", "func Vertex(): float4").GetDiagnostics();

        var warning = Assert.Single(diagnostics, d => d.Id == "RVN2106");
        Assert.False(warning.IsError);
    }

    // --- The dispatch built-ins -------------------------------------------

    [Theory]
    [InlineData("SV_DispatchThreadID", "uint3")]
    [InlineData("SV_GroupID", "uint3")]
    [InlineData("SV_GroupThreadID", "uint3")]
    [InlineData("SV_GroupIndex", "uint")]
    public void EachDispatchBuiltInBinds(string semantic, string type) {
        var compilation = Compile(
            "[ComputeShader(64)]",
            $"func Main([Semantic(\"{semantic}\")] id: {type})"
        );

        Assert.Empty(compilation.GetDiagnostics());
    }

    /// <summary>
    ///     A dispatch coordinate is unsigned in both targets, so a signed declaration is refused
    ///     rather than silently converted — which would put a conversion nobody wrote between the
    ///     built-in and its first use.
    /// </summary>
    [Theory]
    [InlineData("SV_DispatchThreadID", "int3")]
    [InlineData("SV_DispatchThreadID", "uint")]
    [InlineData("SV_GroupIndex", "uint3")]
    [InlineData("SV_GroupIndex", "int")]
    public void ADispatchBuiltInWithTheWrongTypeIsRefused(string semantic, string type) {
        var diagnostics = Compile(
                "[ComputeShader(64)]",
                $"func Main([Semantic(\"{semantic}\")] id: {type})"
            )
            .GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Id == "RVN2109" && d.IsError);
    }

    [Fact]
    public void ASemanticThatNamesNoBuiltInIsRefused() {
        var diagnostics = Compile(
                "[ComputeShader(64)]",
                "func Main([Semantic(\"SV_Position\")] id: uint3)"
            )
            .GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Id == "RVN2108" && d.IsError);
    }

    // --- What a compute stage has no interface for ------------------------

    /// <summary>
    ///     A compute parameter with no semantic is refused: there are no vertex attributes to feed
    ///     it, so it would be a location nothing binds.
    /// </summary>
    [Fact]
    public void APlainComputeParameterIsRefused() {
        var diagnostics = Compile("[ComputeShader(64)]", "func Main(x: float)").GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "RVN2107" && d.IsError);
    }

    /// <summary>And a return value is refused, because there is no framebuffer to take it.</summary>
    [Fact]
    public void AComputeReturnValueIsRefused() {
        var diagnostics = Compile("[ComputeShader(64)]", "func Main(): float4").GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "RVN2107" && d.IsError);
    }

    /// <summary>
    ///     A stream a compute stage touches is refused at lowering, in either direction.
    /// </summary>
    /// <remarks>
    ///     This is the one that enabling the stage created: a stream is a location in the
    ///     pipeline's interface and a compute dispatch has no pipeline, so the streams were left
    ///     undeclared while the store still emitted — GLSL assigning to an identifier the
    ///     translation unit never declared. <c>glslc</c> caught it; Raven did not.
    /// </remarks>
    [Fact]
    public void AStreamUsedByAComputeStageIsRefused() {
        const string Source = """
                              package A

                              shader S {
                                  stream var v: float3

                                  [ComputeShader(64)]
                                  func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                      v = float3(float(id.x), 0f, 0f)
                                  }
                              }

                              """;

        var tree = SyntaxTree.ParseText(Source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new Vixen.Core.Syntax.Diagnostics.DiagnosticBag();
        Vixen.Raven.Lowering.Lowerer.Lower(compilation, bag);

        Assert.Contains(bag.ToArray(), d => d.Id == "RVN3006" && d.IsError);
    }

    // --- Through the pipeline ---------------------------------------------

    /// <summary>
    ///     The whole way through, on the shipped example, with both reference tools as the
    ///     verdict.
    /// </summary>
    /// <remarks>
    ///     Kept as one test over a realistic shader rather than split per assertion: what is being
    ///     claimed is that a compute shader with permutations, a loop, an <c>else if</c> chain and
    ///     a call graph comes out as something a driver accepts, and no single assertion says
    ///     that.
    /// </remarks>
    [Fact]
    public void AComputeShaderReachesBothBackends() {
        var glsl = Assert.Single(GenerateClean(Example, "glsl"));
        Assert.Equal(ShaderStage.Compute, glsl.Stage);
        Assert.Contains("layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;", glsl.Code, StringComparison.Ordinal);

        var spirv = Assert.Single(GenerateClean(Example, "spirv"));
        Assert.Equal(ShaderStage.Compute, spirv.Stage);
        SpirvTestBase.Validate(spirv);
    }

    /// <summary>
    ///     The reflection reports the stage, so a host knows to bind these descriptors to a
    ///     compute pipeline rather than a graphics one.
    /// </summary>
    [Fact]
    public void TheReflectionReportsTheComputeStage() {
        var tree = SyntaxTree.ParseText(Example, path: "Test.rvn");
        var compilation = Compilation.Create("Test", tree);
        var bag = new Vixen.Core.Syntax.Diagnostics.DiagnosticBag();
        var module = Vixen.Raven.Lowering.Lowerer.Lower(compilation, bag);

        var reflection = ReflectionBuilder.Describe(module.Shaders[0], compilation.UsedPermutationKeys);

        Assert.Equal([ShaderStage.Compute], reflection.Stages);
        Assert.Contains(IrCapability.Compute, reflection.RequiredCapabilities);

        var set = Assert.Single(reflection.Sets);
        Assert.All(set.Bindings, binding => Assert.Equal(ShaderStages.Compute, binding.Stages));

        // No vertex inputs and no outputs: a compute invocation is not fed by a vertex buffer and
        // writes to no render target.
        Assert.Empty(reflection.VertexInputs);
        Assert.Empty(reflection.Outputs);
    }

    /// <summary>
    ///     A compute stage declares no locations at all, in either target — the claim that keeps
    ///     the two decorations from colliding, since a SPIR-V variable cannot be both
    ///     <c>BuiltIn</c> and <c>Location</c>.
    /// </summary>
    [Fact]
    public void AComputeStageDeclaresNoLocations() {
        var glsl = Assert.Single(GenerateClean(Example, "glsl")).Code;
        Assert.DoesNotContain("layout(location", glsl, StringComparison.Ordinal);

        var spirv = Assert.Single(GenerateClean(Example, "spirv")).Code;
        Assert.DoesNotContain("Location", spirv, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The IR verifier is the backstop: a compute entry point that reached lowering without a
    ///     size would have emitted <c>local_size_x = 0</c>, which fails to link rather than
    ///     saying why.
    /// </summary>
    [Fact]
    public void TheVerifierRefusesAComputeEntryPointWithoutASize() {
        var shader = new IrShader("S");
        var function = new IrFunction("Main", IrScalarType.Void);
        shader.Add(function);
        shader.Add(new IrEntryPoint(ShaderStage.Compute, function, [], []));

        var module = new IrModule("Test");
        module.Add(shader);

        var bag = new Vixen.Core.Syntax.Diagnostics.DiagnosticBag();
        Assert.False(IrVerifier.Verify(module, bag));
        Assert.Contains(bag.ToArray(), d => d.Id == "RVN3010");
    }

    static Compilation Compile(string attribute, string signature) {
        var source = $$"""
                       package A

                       shader S {
                       {{Members}}    {{attribute}}
                           {{signature}} {
                           }
                       }

                       """;

        return Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));
    }

    /// <summary>The shipped compute example, so the tests and the file cannot drift.</summary>
    static string Example =>
        File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library", "Example2.rvn")
        );
}
