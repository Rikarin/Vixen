// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Text;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     Incremental reparse (docs/plan/18 step 7). The property that makes the
///     blender safe: <see cref="SyntaxTree.WithChangedText" /> must produce a tree
///     identical to a from-scratch parse of the new text — reuse may only skip
///     work, never change the result. And the property that makes it worth having:
///     members a change did not touch keep their green nodes, by reference.
/// </summary>
public class IncrementalParseTests {
    const string Source = """
        package A.B

        shader First {
            var tint: float4

            func Untouched(): int {
                return 42
            }

            func Edited(): int {
                return 1
            }
        }

        struct Second {
            var value: float
        }

        """;

    static SyntaxTree Parse(string text) => SyntaxTree.ParseText(text, path: "incremental.rvn");

    static (SyntaxTree Old, SyntaxTree New, SourceText NewText) Reparse(params TextChange[] changes) {
        var oldTree = Parse(Source);
        var newText = oldTree.Text!.WithChanges(changes);
        return (oldTree, oldTree.WithChangedText(newText), newText);
    }

    static void AssertMatchesFullParse(SyntaxTree incremental, SourceText newText) {
        var text = newText.ToString();
        var full = Parse(text);

        Assert.Equal(text, incremental.GetRoot().ToFullString());
        Assert.Equal(SyntaxDumper.Dump(full.GetRoot()), SyntaxDumper.Dump(incremental.GetRoot()));
        Assert.Equal(full.Diagnostics.Count, incremental.Diagnostics.Count);
    }

    static T Member<T>(SyntaxTree tree, string identifier) where T : SyntaxNode {
        var match = Find(tree.GetRoot());
        Assert.NotNull(match);
        return match;

        T? Find(SyntaxNode node) {
            if (node is T candidate && node.ToFullString().Contains(identifier)) {
                foreach (var child in node.ChildNodesAndTokens()) {
                    if (Find(child) is { } deeper) {
                        return deeper;
                    }
                }

                return candidate;
            }

            foreach (var child in node.ChildNodesAndTokens()) {
                if (Find(child) is { } found) {
                    return found;
                }
            }

            return null;
        }
    }

    [Fact]
    public void Editing_one_body_reuses_every_other_member() {
        var edit = TextChange.Insert(Source.IndexOf("return 1", StringComparison.Ordinal), "return 2 //");
        var (oldTree, newTree, newText) = Reparse(edit);

        AssertMatchesFullParse(newTree, newText);

        // The untouched members come back as the same green nodes, shifted.
        Assert.Same(
            Member<MethodDeclarationSyntax>(oldTree, "Untouched").Green,
            Member<MethodDeclarationSyntax>(newTree, "Untouched").Green
        );
        Assert.Same(
            Member<StructDeclarationSyntax>(oldTree, "Second").Green,
            Member<StructDeclarationSyntax>(newTree, "Second").Green
        );

        // The edited one does not.
        Assert.NotSame(
            Member<MethodDeclarationSyntax>(oldTree, "Edited").Green,
            Member<MethodDeclarationSyntax>(newTree, "Edited").Green
        );
    }

    [Fact]
    public void Inserting_a_member_reuses_its_neighbours() {
        var edit = TextChange.Insert(
            Source.IndexOf("    func Edited", StringComparison.Ordinal),
            "    func Added(): int => 7\n\n"
        );
        var (oldTree, newTree, newText) = Reparse(edit);

        AssertMatchesFullParse(newTree, newText);

        Assert.Same(
            Member<MethodDeclarationSyntax>(oldTree, "Untouched").Green,
            Member<MethodDeclarationSyntax>(newTree, "Untouched").Green
        );
        Assert.Same(
            Member<StructDeclarationSyntax>(oldTree, "Second").Green,
            Member<StructDeclarationSyntax>(newTree, "Second").Green
        );
    }

    /// <summary>
    ///     An edit that glues onto a member's boundary must invalidate it — this is
    ///     the adjacency margin. `var tint: float4` gaining ` { get => tint }` right
    ///     at its end becomes a property, which wholesale reuse would have missed.
    /// </summary>
    [Fact]
    public void An_edit_adjacent_to_a_member_boundary_reparses_it() {
        var end = Source.IndexOf("var tint: float4", StringComparison.Ordinal) + "var tint: float4".Length;
        var (_, newTree, newText) = Reparse(TextChange.Insert(end, " => tint"));

        AssertMatchesFullParse(newTree, newText);
        _ = Member<PropertyDeclarationSyntax>(newTree, "tint");
    }

    [Fact]
    public void Deleting_a_member_reuses_the_rest() {
        var start = Source.IndexOf("    func Edited", StringComparison.Ordinal);
        var end = Source.IndexOf("}\n\nstruct", StringComparison.Ordinal) - 4;
        var (oldTree, newTree, newText) = Reparse(TextChange.Delete(TextSpan.FromBounds(start, end)));

        AssertMatchesFullParse(newTree, newText);

        Assert.Same(
            Member<MethodDeclarationSyntax>(oldTree, "Untouched").Green,
            Member<MethodDeclarationSyntax>(newTree, "Untouched").Green
        );
    }

    [Fact]
    public void Unrelated_text_parses_conservatively() {
        var oldTree = Parse(Source);
        var unrelated = SourceText.From("package X\n\nshader Fresh {\n}\n");

        var reparsed = oldTree.WithChangedText(unrelated);
        AssertMatchesFullParse(reparsed, unrelated);
    }

    [Fact]
    public void No_changes_returns_the_same_tree() {
        var oldTree = Parse(Source);
        Assert.Same(oldTree, oldTree.WithChangedText(oldTree.Text!));
    }
}
