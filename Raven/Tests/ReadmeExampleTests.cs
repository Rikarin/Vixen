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
/// The README's language example has to survive the whole pipeline — it is the
/// first thing anyone reads, and it is the exit criterion for both backends:
/// valid GLSL in Phase 4, valid SPIR-V in Phase 6.
/// </summary>
public class ReadmeExampleTests {
    static string ReadExample() {
        var readme = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "README.md"));

        var start = readme.IndexOf("## Language Example", StringComparison.Ordinal);
        var open = readme.IndexOf("```typescript", start, StringComparison.Ordinal) + "```typescript\n".Length;
        var close = readme.IndexOf("```", open, StringComparison.Ordinal);
        return readme[open..close];
    }

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
            "IR did not verify:\n" + string.Join("\n", bag.Select(d => d.ToString())));

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
}
