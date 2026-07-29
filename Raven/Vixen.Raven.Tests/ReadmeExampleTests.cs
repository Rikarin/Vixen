// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

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
        Assert.Equal([ShaderStage.Vertex, ShaderStage.Fragment], generated.Select(g => g.Stage));
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

        Assert.Equal([ShaderStage.Vertex, ShaderStage.Fragment], generated.Select(g => g.Stage));

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
        var fragment = Assert.Single(generated, unit => unit.Stage == ShaderStage.Fragment).Code;

        // Declaration order gives normalWS 0 and uv 1, in both directions, so the pipeline links.
        Assert.Contains("layout(location = 0) out vec3 out_normalWS", vertex, StringComparison.Ordinal);
        Assert.Contains("layout(location = 1) out vec2 out_uv", vertex, StringComparison.Ordinal);
        Assert.Contains("layout(location = 0) in vec3 in_normalWS", fragment, StringComparison.Ordinal);
        Assert.Contains("layout(location = 1) in vec2 in_uv", fragment, StringComparison.Ordinal);

        // And the vertex attributes sit after them, which is the stated consequence.
        Assert.Contains("layout(location = 2) in vec3 in_position", vertex, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The compute example compiles and declares the workgroup size the README says it does.
    /// </summary>
    /// <remarks>
    ///     Held to the same standard as the other two. The size is the part worth pinning: the
    ///     README claims the dimensions are read off the stage attribute positionally, and a
    ///     transposed or dropped dimension would be a shader that reads out of bounds rather than
    ///     one that fails to compile.
    /// </remarks>
    [Fact]
    public void The_readme_compute_example_compiles_with_the_size_it_declares() {
        var source = "package Vixen.Shaders\n\n" + ReadExample("### Compute");

        var tree = SyntaxTree.ParseText(source, path: "README.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var glsl = Assert.Single(CodeGenTestBase.GenerateClean(source));
        Assert.Equal(ShaderStage.Compute, glsl.Stage);
        Assert.Contains(
            "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;",
            glsl.Code,
            StringComparison.Ordinal
        );

        // And the dispatch built-ins the table promises, threaded straight into the entry point.
        Assert.Contains("gl_GlobalInvocationID", glsl.Code, StringComparison.Ordinal);
        Assert.Contains("gl_LocalInvocationIndex", glsl.Code, StringComparison.Ordinal);

        Assert.All(CodeGenTestBase.GenerateClean(source, "spirv"), SpirvTestBase.Validate);
    }

    /// <summary>And the compaction example, which is the one the atomics exist for.</summary>
    /// <remarks>
    ///     Worth its own test rather than folded into the compute one: it is the README's claim that
    ///     the value an atomic returns is a usable slot index, and that claim is only true if the
    ///     call reached the block member rather than a copy of it. The emitted GLSL says which.
    /// </remarks>
    [Fact]
    public void The_readme_atomic_example_compacts_through_the_counter_itself() {
        var source = "package Vixen.Shaders\n\n" + ReadExample("### Atomics");

        var tree = SyntaxTree.ParseText(source, path: "README.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var glsl = Assert.Single(CodeGenTestBase.GenerateClean(source));
        Assert.Contains("atomicAdd(counter[0], 1u)", glsl.Code, StringComparison.Ordinal);

        Assert.All(CodeGenTestBase.GenerateClean(source, "spirv"), SpirvTestBase.Validate);
    }

    /// <summary>And the reduction, which is what workgroup-shared memory is for.</summary>
    /// <remarks>
    ///     The README's claim is that the storage is the workgroup's rather than the invocation's
    ///     and that it costs nothing in the descriptor sets. The emitted GLSL says both: <c>shared</c>
    ///     rather than a local, and no <c>set</c> or <c>binding</c> anywhere near it.
    /// </remarks>
    [Fact]
    public void The_readme_group_shared_example_reduces_through_one_tile() {
        var source = "package Vixen.Shaders\n\n" + ReadExample("### Workgroup-shared memory");

        var tree = SyntaxTree.ParseText(source, path: "README.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var glsl = Assert.Single(CodeGenTestBase.GenerateClean(source));
        Assert.Contains("shared float tile[64];", glsl.Code, StringComparison.Ordinal);
        Assert.Contains("barrier();", glsl.Code, StringComparison.Ordinal);

        Assert.All(CodeGenTestBase.GenerateClean(source, "spirv"), SpirvTestBase.Validate);
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
