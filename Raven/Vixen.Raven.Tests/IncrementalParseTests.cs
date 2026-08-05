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

    /// <summary>
    ///     A file that already has errors keeps reporting them after an edit somewhere else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reuse takes a member's nodes and leaves its diagnostics behind</b>, because those
    ///         were produced by the parse that is not being run again. Every candidate was offered
    ///         regardless of what its parse reported, so an author editing one function watched the
    ///         errors in the rest of the file disappear — and a hot reload, which is what calls
    ///         <see cref="SyntaxTree.WithChangedText" />, bound a tree with fabricated tokens in it
    ///         while reporting nothing to explain them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every test above asserts the diagnostic counts match and none of them caught
    ///         it</b>, because they all edit a shader that parses cleanly and zero equals zero. That
    ///         is the whole reason this one starts from a broken file. Found by
    ///         <c>Vixen.Net.Fuzz</c>'s <c>raven</c> target, which reported it thirty-two ways in four
    ///         hundred cases.
    ///     </para>
    ///     <para>
    ///         The second case is the subtler half: a member ends by requiring a line break, and that
    ///         check reports at the <i>next</i> token — outside everything the member owns, with the
    ///         whitespace between them belonging to the next token's trivia. So the reuse gate has to
    ///         look past the node it is judging.
    ///     </para>
    /// </remarks>
    [Theory]
    // A member that is itself broken, with the edit in a later one that is not.
    [InlineData("package A.B\n\nstruct A {\n    var x: @@@\n}\n\nstruct B {\n    var EDIT: float\n}\n")]
    // Two declarations on one line: the missing terminator between them is reported at the second
    // one's first token, which is past everything the first one owns.
    [InlineData("package A.B\n\nstruct A {\n} struct B {\n}\n\nstruct C {\n    var EDIT: float\n}\n")]
    // A broken member inside a shader, so the candidate walk reaches it by nesting rather than at
    // the top level.
    [InlineData("package A.B\n\nshader S {\n    func F(): int {\n        return @@@\n    }\n}\n\nstruct T {\n    var EDIT: float\n}\n")]
    public void Errors_in_untouched_members_survive_a_reparse(string source) {
        var oldTree = Parse(source);

        // The premise, stated rather than assumed: with no errors to lose this proves nothing, which
        // is exactly how every test above passed while the defect was live.
        Assert.NotEmpty(oldTree.Diagnostics);

        // The edit goes in a member the errors are not in, because reuse of the *untouched* members
        // is what drops them.
        var at = source.IndexOf("EDIT", StringComparison.Ordinal);
        var newText = oldTree.Text!.WithChanges(new TextChange(new(at, 4), "renamed"));

        AssertMatchesFullParse(oldTree.WithChangedText(newText), newText);
    }
}
