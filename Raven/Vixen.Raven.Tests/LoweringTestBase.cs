using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;

namespace Tests;

/// <summary>Shared plumbing for the Phase 3 lowering tests.</summary>
public static class LoweringTestBase {
    /// <summary>
    ///     Compiles, asserts the program bound cleanly, then lowers and verifies it.
    ///     Lowering only makes sense on a compilation the binder accepted.
    /// </summary>
    public static IrModule Lower(string source) {
        var module = LowerWithDiagnostics(source, out var diagnostics);

        Assert.True(
            diagnostics.Count == 0,
            "Expected no lowering diagnostics, got:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        return module;
    }

    /// <summary>Lowers and verifies, handing back whatever was reported.</summary>
    public static IrModule LowerWithDiagnostics(string source, out IReadOnlyList<Diagnostic> diagnostics) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        var semantic = compilation.GetDiagnostics();

        Assert.True(
            semantic.Count == 0,
            "Expected no semantic diagnostics, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        diagnostics = bag.ToArray();
        return module;
    }

    /// <summary>Lowers without verifying, for tests about lowering diagnostics alone.</summary>
    public static IReadOnlyList<Diagnostic> LoweringDiagnosticsOf(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        var bag = new DiagnosticBag();
        Lowerer.Lower(compilation, bag);
        return bag.ToArray();
    }

    /// <summary>The printed body of one function, for readable assertions.</summary>
    public static string PrintFunction(IrModule module, string name) => IrPrinter.Print(FindFunction(module, name));

    public static IrFunction FindFunction(IrModule module, string name) =>
        Assert.Single(module.AllFunctions, f => f.Name == name);

    public static IrShader FindShader(IrModule module, string name) =>
        Assert.Single(module.Shaders, s => s.Name == name);
}
