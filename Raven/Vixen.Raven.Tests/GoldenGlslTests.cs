// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Vixen.Testing;
using Xunit;

namespace Tests;

/// <summary>
///     Golden-file GLSL tests: each fixture <c>Fixtures/&lt;name&gt;.rvn</c> is
///     compiled all the way through and each generated stage is compared against
///     <c>Fixtures/&lt;name&gt;.&lt;stage&gt;.glsl</c>. This is what makes a change in
///     code generation visible in review.
///     Regenerate with <c>UPDATE_GOLDEN=1</c> and read the diff.
/// </summary>
public class GoldenGlslTests(ITestOutputHelper output) {
    /// <summary>Every generated stage, against the source committed for it.</summary>
    /// <param name="name">The fixture.</param>
    /// <remarks>
    ///     ⚠ A <see cref="GoldenFile.Set" /> rather than a bare loop: the loop that stood here
    ///     passed when <see cref="Compile" /> returned no stages at all, which is the one failure a
    ///     code-generation golden is for. The set still regenerates every stage before it fails, so
    ///     one run refreshes them all.
    /// </remarks>
    [Theory]
    [InlineData("lambert")]
    public void Matches_golden(string name) {
        var goldens = GoldenFile.Batch();

        foreach (var unit in Compile(name)) {
            goldens.Matches(unit.Code, FixturePath($"{name}.{StageSuffix(unit)}.glsl"));
        }

        goldens.Done();
    }

    /// <summary>
    ///     The exit criterion for this phase: real GLSL that a real compiler accepts.
    ///     Runs only when <c>glslc</c> is on PATH — it is not a build dependency, so its absence
    ///     <em>skips</em> this. It used to return quietly and report a pass, which is the same
    ///     thing as not having the check at all; <c>ci.yml</c> installs shaderc on all three legs
    ///     now.
    /// </summary>
    /// <remarks>
    ///     Compiling rather than only validating, because the target is Vulkan GLSL and a
    ///     Vulkan target is what makes <c>layout(set = …)</c> and the separate
    ///     <c>texture2D</c>/<c>sampler</c> types legal in the first place.
    ///     <see cref="SpirvDifferentialTests" /> goes on to diff the result against Raven's own
    ///     SPIR-V; this asserts the weaker half on the golden fixture.
    /// </remarks>
    [Theory]
    [InlineData("lambert")]
    public void A_reference_compiler_accepts_the_golden_glsl(string name) {
        Assert.SkipUnless(ReferenceCompiler.Glslc is not null, ReferenceCompiler.HowToInstall);

        foreach (var unit in Compile(name)) {
            var module = ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage);
            output.WriteLine($"{unit.Name}: {module.Length} bytes of SPIR-V");

            Assert.NotEmpty(module);
        }
    }

    static IReadOnlyList<GeneratedSource> Compile(string name) {
        var source = File.ReadAllText(FixturePath(name + ".rvn"));

        var tree = SyntaxTree.ParseText(source, path: name + ".rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        var generated = TargetBackends.Create("glsl")!.Generate(module, bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return generated;
    }

    static string StageSuffix(GeneratedSource unit) => GlslBackend.StageSuffix(unit.Stage);

    static string FixturePath(string file) => GoldenFile.InProjectDirectory("Fixtures", file);
}
