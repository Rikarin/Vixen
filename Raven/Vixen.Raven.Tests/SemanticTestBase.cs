// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

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

    /// <summary>
    ///     <b>Setup.</b> A compilation of a source that is expected to be valid, failing loudly if
    ///     it is not.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named apart from <see cref="AssertNoDiagnostics" /> because the two read identically to
    ///     a grep and mean opposite things to an audit. This one is how a test that is <em>about</em>
    ///     something else — a symbol's kind, an enum's ordinals — gets a compilation to ask its real
    ///     question of; the clean compile is a precondition, and no rule is under test. Counting
    ///     these as negative coverage is how the previous inventory of this suite over-counted
    ///     itself: twenty-three of the thirty-three call sites were preconditions.
    ///     <see cref="AssertNoDiagnostics" /> is the one that is the assertion.
    /// </remarks>
    public static Compilation CompileClean(string source) {
        var (compilation, _, _) = Compile(source);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "Expected no diagnostics, got:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        return compilation;
    }

    /// <summary>
    ///     <b>The assertion.</b> This source is valid and the compiler must say nothing about it.
    /// </summary>
    /// <remarks>
    ///     The whole point of a call site here is the silence, so it returns nothing — a test that
    ///     wants the compilation back wanted <see cref="CompileClean" /> and is not asserting this.
    ///     Broader than a <c>NegativeDiagnosticTests</c> fixture and weaker: that one names the rule
    ///     it is holding to its fire, this one only says no rule fired at all.
    /// </remarks>
    public static void AssertNoDiagnostics(string source) => CompileClean(source);

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
