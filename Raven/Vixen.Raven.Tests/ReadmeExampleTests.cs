// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;

namespace Tests;

/// <summary>
///     The README's language example has to survive the whole pipeline — it is the
///     first thing anyone reads, and it is the exit criterion for both backends:
///     valid GLSL in Phase 4, valid SPIR-V in Phase 6.
/// </summary>
public class ReadmeExampleTests {
    [Fact]
    public void The_readme_language_example_compiles_cleanly() {
        var tree = SyntaxTree.ParseText(ReadExample(), path: "README.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void The_readme_language_example_reaches_glsl() {
        var tree = SyntaxTree.ParseText(ReadExample(), path: "README.rvn");
        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.True(
            IrVerifier.Verify(module, bag),
            "IR did not verify:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        var generated = TargetBackends.Create("glsl")!.Generate(module, bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        // One unit per stage, each a complete GLSL translation unit.
        Assert.Equal([ShaderStage.Vertex, ShaderStage.Pixel], generated.Select(g => g.Stage));
        Assert.All(generated, unit => Assert.StartsWith("#version 450", unit.Code));
        Assert.All(generated, unit => Assert.Contains("void main() {", unit.Code));
    }

    [Fact]
    public void The_readme_language_example_reaches_valid_spirv() {
        var tree = SyntaxTree.ParseText(ReadExample(), path: "README.rvn");
        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.True(IrVerifier.Verify(module, bag));

        var generated = TargetBackends.Create("spirv")!.Generate(module, bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        Assert.Equal([ShaderStage.Vertex, ShaderStage.Pixel], generated.Select(g => g.Stage));

        // The verdict that matters is the reference validator's.
        Assert.All(generated, SpirvTestBase.Validate);
    }

    /// <summary>
    ///     The streams example compiles, and its interstage locations line up.
    /// </summary>
    /// <remarks>
    ///     Held to the same standard as the main example, and the location claim is the part worth
    ///     pinning: the README says the writing stage and the reading stage agree on a stream's
    ///     location without either knowing about the other, and that is checkable.
    /// </remarks>
    [Fact]
    public void The_readme_stream_example_compiles_and_the_stages_agree() {
        var tree = SyntaxTree.ParseText("package Vixen.Shaders\n\n" + ReadExample("### Streams"), path: "README.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.True(
            IrVerifier.Verify(module, bag),
            "IR did not verify:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        var generated = TargetBackends.Create("glsl")!.Generate(module, bag);
        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        var vertex = Assert.Single(generated, unit => unit.Stage == ShaderStage.Vertex).Code;
        var pixel = Assert.Single(generated, unit => unit.Stage == ShaderStage.Pixel).Code;

        // Declaration order gives normalWS 0 and uv 1, in both directions, so the pipeline links.
        Assert.Contains("layout(location = 0) out vec3 out_normalWS", vertex, StringComparison.Ordinal);
        Assert.Contains("layout(location = 1) out vec2 out_uv", vertex, StringComparison.Ordinal);
        Assert.Contains("layout(location = 0) in vec3 in_normalWS", pixel, StringComparison.Ordinal);
        Assert.Contains("layout(location = 1) in vec2 in_uv", pixel, StringComparison.Ordinal);

        // And the vertex attributes sit after them, which is the stated consequence.
        Assert.Contains("layout(location = 2) in vec3 in_position", vertex, StringComparison.Ordinal);
    }

    static string ReadExample(string heading = "## Language Example") {
        var readme = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "README.md"));

        var start = readme.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"README has no '{heading}' section.");

        var open = readme.IndexOf("```typescript", start, StringComparison.Ordinal) + "```typescript\n".Length;
        var close = readme.IndexOf("```", open, StringComparison.Ordinal);
        return readme[open..close];
    }
}
