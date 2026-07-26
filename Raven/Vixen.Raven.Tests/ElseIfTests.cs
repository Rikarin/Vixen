// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     <c>else if</c> chains, which did not parse at all until the corpus caught it.
/// </summary>
/// <remarks>
///     The tree shape always allowed them — <see cref="ElseClauseSyntax.Statement" /> is a
///     <see cref="StatementSyntax" />, not a block — and the binder and lowerer both took
///     whatever statement the clause carried. Only the parser hard-coded a block, so
///     `} else if (…) {` was a syntax error in a C-family language. These tests pin each
///     layer, because the shape being representable is what made the bug invisible.
/// </remarks>
public class ElseIfTests {
    const string Chain = """
                         package A

                         shader S {
                             func Pick(x: int): float {
                                 if (x > 2) {
                                     return 1f
                                 } else if (x > 1) {
                                     return 0.5f
                                 } else if (x > 0) {
                                     return 0.25f
                                 } else {
                                     return 0f
                                 }
                             }

                             [PixelShader]
                             func Pixel(uv: float2): float4 {
                                 return float4(Pick(int(uv.x)))
                             }
                         }

                         """;

    [Fact]
    public void AnElseIfChainParses() {
        var tree = SyntaxTree.ParseText(Chain, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>
    ///     The chain nests rather than flattening: the clause holds the inner <c>if</c>
    ///     directly, with no block between them.
    /// </summary>
    /// <remarks>
    ///     Worth pinning rather than assuming. A block wrapper would compile identically and
    ///     be invisible in the output, but it would cost a scope per <c>else if</c> and would
    ///     put a node in the tree the source never wrote — which the round-trip below would
    ///     then have to invent trivia for.
    /// </remarks>
    [Fact]
    public void TheChainNestsWithoutABlockBetween() {
        var tree = SyntaxTree.ParseText(Chain, path: "Test.rvn");

        var outer = FirstIf(tree.GetRoot());
        var second = Assert.IsType<IfStatementSyntax>(outer.Else?.Statement);
        var third = Assert.IsType<IfStatementSyntax>(second.Else?.Statement);

        // The last `else` is a real block — that is where the chain stops.
        Assert.IsType<BlockSyntax>(third.Else?.Statement);
    }

    /// <summary>A chain round-trips byte-for-byte, trivia included.</summary>
    [Fact]
    public void AnElseIfChainRoundTrips() {
        var tree = SyntaxTree.ParseText(Chain, path: "Test.rvn");
        Assert.Equal(Chain, tree.GetRoot().ToFullString());
    }

    /// <summary>
    ///     And it reaches both backends, so the alternative is really being walked rather
    ///     than parsed and dropped.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void AnElseIfChainReachesTheBackends(string target) {
        var generated = CodeGenTestBase.GenerateClean(Chain, target);
        Assert.Single(generated);
    }

    /// <summary>
    ///     Every arm survives to the output. The IR has no <c>else if</c> — the emitter
    ///     writes a nested block — so what matters is that all four results are still there.
    /// </summary>
    [Fact]
    public void EveryArmOfTheChainIsEmitted() {
        var glsl = CodeGenTestBase.GenerateOne(Chain);

        Assert.Contains("return 1.0;", glsl, StringComparison.Ordinal);
        Assert.Contains("return 0.5;", glsl, StringComparison.Ordinal);
        Assert.Contains("return 0.25;", glsl, StringComparison.Ordinal);
        Assert.Contains("return 0.0;", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A chain with no trailing <c>else</c> is still a chain, and the innermost
    ///     alternative stays absent rather than becoming an empty block.
    /// </summary>
    [Fact]
    public void AChainNeedsNoTrailingElse() {
        const string source = """
                             package A

                             struct S {
                                 func F(x: int): int {
                                     var r = 0
                                     if (x > 1) {
                                         r = 1
                                     } else if (x > 0) {
                                         r = 2
                                     }

                                     return r
                                 }
                             }

                             """;

        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);
        Assert.Equal(source, tree.GetRoot().ToFullString());

        var outer = FirstIf(tree.GetRoot());
        var inner = Assert.IsType<IfStatementSyntax>(outer.Else?.Statement);
        Assert.Null(inner.Else);
    }

    /// <summary>The first <c>if</c> in the tree, in source order.</summary>
    static IfStatementSyntax FirstIf(SyntaxNode root) =>
        Find(root) ?? throw new InvalidOperationException("No if statement in the tree.");

    static IfStatementSyntax? Find(SyntaxNode node) {
        if (node is IfStatementSyntax found) {
            return found;
        }

        foreach (var child in node.ChildNodesAndTokens()) {
            if (Find(child) is { } nested) {
                return nested;
            }
        }

        return null;
    }
}
