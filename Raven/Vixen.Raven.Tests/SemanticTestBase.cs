using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;

namespace Tests;

/// <summary>Shared plumbing for the Phase 2 semantic tests.</summary>
public static class SemanticTestBase {
    /// <summary>Parses one source file and wraps it in a compilation.</summary>
    public static (Compilation Compilation, SyntaxTree Tree, SemanticModel Model) Compile(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        return (compilation, tree, compilation.GetSemanticModel(tree));
    }

    /// <summary>Every diagnostic the compilation reports, syntax included.</summary>
    public static IReadOnlyList<Diagnostic> Diagnose(string source) {
        var (compilation, _, _) = Compile(source);
        return compilation.GetDiagnostics();
    }

    /// <summary>Asserts the source compiles with no diagnostics at all.</summary>
    public static Compilation AssertNoDiagnostics(string source) {
        var (compilation, _, _) = Compile(source);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "Expected no diagnostics, got:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        return compilation;
    }

    /// <summary>Asserts exactly the given diagnostic ids are reported, in order.</summary>
    public static IReadOnlyList<Diagnostic> AssertDiagnostics(string source, params string[] expectedIds) {
        var diagnostics = Diagnose(source);
        var actual = diagnostics.Select(d => d.Id).ToArray();

        Assert.True(
            expectedIds.SequenceEqual(actual),
            $"Expected [{string.Join(", ", expectedIds)}] but got:\n"
            + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        return diagnostics;
    }

    /// <summary>Finds the single type with this name.</summary>
    public static NamedTypeSymbol FindType(Compilation compilation, string name) =>
        Assert.Single(compilation.GetAllTypes(), t => t.Name == name);

    /// <summary>Finds the single member with this name on a type.</summary>
    public static T GetMember<T>(NamedTypeSymbol type, string name) where T : Symbol =>
        Assert.Single(type.GetMembers(name).OfType<T>());

    /// <summary>The first node of the given kind in the tree, in source order.</summary>
    public static T FindNode<T>(SyntaxTree tree, Func<T, bool>? predicate = null) where T : SyntaxNode {
        foreach (var node in Descend(tree.GetRoot())) {
            if (node is T typed && (predicate is null || predicate(typed))) {
                return typed;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} found.");
    }

    static IEnumerable<SyntaxNode> Descend(SyntaxNode node) {
        yield return node;
        foreach (var child in node.ChildNodesAndTokens()) {
            foreach (var descendant in Descend(child)) {
                yield return descendant;
            }
        }
    }
}
