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
    static bool ShouldUpdate => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is "1" or "true";

    [Theory]
    [InlineData("lambert")]
    public void Matches_golden(string name) {
        List<string> regenerated = [];

        foreach (var unit in Compile(name)) {
            var goldenPath = FixturePath($"{name}.{StageSuffix(unit)}.glsl");
            var actual = Normalize(unit.Code);

            // Regenerate every stage before failing, so one run refreshes them all.
            if (ShouldUpdate || !File.Exists(goldenPath)) {
                File.WriteAllText(goldenPath, actual);
                regenerated.Add(Path.GetFileName(goldenPath));
                continue;
            }

            var expected = Normalize(File.ReadAllText(goldenPath));

            if (expected != actual) {
                File.WriteAllText(goldenPath + ".actual", actual);
            }

            Assert.Equal(expected, actual);
        }

        Assert.True(
            regenerated.Count == 0,
            $"Goldens were (re)generated: {string.Join(", ", regenerated)}. Review the diff and re-run."
        );
    }

    /// <summary>
    ///     The exit criterion for this phase: real GLSL that a real compiler accepts.
    ///     Runs only when <c>glslc</c> is on PATH — it is not a build dependency, and its
    ///     absence is reported rather than silently ignored.
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
        if (ReferenceCompiler.Glslc is null) {
            output.WriteLine(ReferenceCompiler.HowToInstall);
            return;
        }

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

    static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    // bin/Debug/net10.0 -> Tests project root -> Fixtures
    static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", file);
}
