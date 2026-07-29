// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Text;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     <see cref="Vixen.Core.Syntax" />'s navigation over a real grammar rather than the toy one
///     its own tests use.
/// </summary>
/// <remarks>
///     Worth having on both sides. The shared tests prove the traversal is not Raven-shaped; this
///     one proves it survives contact with a generated tree — where a member list is a list node,
///     an optional slot is empty, and the trivia a token carries is whatever the lexer put there.
///     These are the queries [doc 09](docs/plan/09-ui-framework.md)'s <c>CodeEditor</c> and the
///     shader graph's span mapping are built on.
/// </remarks>
public class SyntaxNavigationTests {
    const string Source = """
                          package A

                          shader S {
                              // the tint the material sets
                              var tint: float4

                              [FragmentShader]
                              func Fragment(uv: float2): float4 {
                                  return tint
                              }
                          }

                          """;

    static SyntaxNode Root() {
        var tree = SyntaxTree.ParseText(Source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);
        return tree.GetRoot();
    }

    [Fact]
    public void Descendant_tokens_are_the_source_retokenized() {
        // Concatenating every token's full text is the file, which is the round-trip property
        // stated from the token side rather than the node side.
        Assert.Equal(Source, string.Concat(Root().DescendantTokens().Select(t => t.ToFullString())));
    }

    [Fact]
    public void A_member_list_flattens_so_members_are_children_of_the_shader() {
        var shader = Assert.Single(Root().DescendantNodes().OfType<ShaderDeclarationSyntax>());

        // Two members declared, and neither is reached through a list node.
        Assert.Equal(2, shader.ChildNodes().OfType<MemberDeclarationSyntax>().Count());
        Assert.DoesNotContain(shader.ChildNodes(), child => child.IsList);
    }

    [Fact]
    public void FindToken_lands_on_the_identifier_under_a_caret() {
        var root = Root();
        var caret = Source.IndexOf("tint: float4", StringComparison.Ordinal);

        var token = root.FindToken(caret);
        Assert.Equal("tint", token.Text);

        // And the declaration it belongs to, which is the second question every editor asks.
        var field = token.FirstAncestorOrSelf<FieldDeclarationSyntax>();
        Assert.NotNull(field);
        Assert.Equal("var tint: float4", field.ToString());
    }

    [Fact]
    public void A_caret_in_a_comment_belongs_to_the_token_the_comment_leads() {
        var root = Root();
        var inComment = Source.IndexOf("the tint", StringComparison.Ordinal);

        var token = root.FindToken(inComment);
        Assert.Equal("var", token.Text);

        var comment = Assert.Single(
            token.LeadingTrivia,
            t => (SyntaxKind)t.RawKind == SyntaxKind.SingleLineCommentTrivia
        );

        Assert.Equal("// the tint the material sets", comment.Text);
        Assert.True(comment.Span.Contains(inComment));
    }

    [Fact]
    public void FindNode_maps_a_selected_range_back_to_the_construct() {
        var root = Root();
        var start = Source.IndexOf("return tint", StringComparison.Ordinal);

        var node = root.FindNode(new TextSpan(start, "return tint".Length));

        Assert.IsType<ReturnStatementSyntax>(node);
        Assert.Equal("return tint", node.ToString());
    }

    [Fact]
    public void Every_position_in_the_file_resolves_to_a_token() {
        var root = Root();

        for (var position = 0; position <= Source.Length; position++) {
            var token = root.FindToken(position);
            Assert.True(
                token.FullSpan.Contains(position) || position == Source.Length,
                $"Position {position} resolved to '{token.Text}' at {token.FullSpan}."
            );
        }
    }

    [Fact]
    public void Reformatting_does_not_change_what_the_tree_says() {
        var spaced = SyntaxTree.ParseText(Source, path: "Test.rvn").GetRoot();
        var tight = SyntaxTree.ParseText(Source.Replace("    ", "  ", StringComparison.Ordinal), path: "Test.rvn")
            .GetRoot();

        Assert.NotEqual(spaced.ToFullString(), tight.ToFullString());
        Assert.True(spaced.IsEquivalentTo(tight));

        var renamed = SyntaxTree.ParseText(Source.Replace("tint", "colour", StringComparison.Ordinal), path: "Test.rvn")
            .GetRoot();

        Assert.False(spaced.IsEquivalentTo(renamed));
    }

    /// <summary>
    ///     A tree with errors still navigates. That is the point of recovery emitting missing
    ///     tokens rather than discarding the tree: an editor asks these questions while the file
    ///     is mid-edit and broken more often than not.
    /// </summary>
    [Fact]
    public void A_recovered_tree_still_answers_every_position() {
        const string Broken = "package A\n\nshader S {\n    var tint:\n}\n";

        var tree = SyntaxTree.ParseText(Broken, path: "Test.rvn");
        Assert.NotEmpty(tree.Diagnostics);

        var root = tree.GetRoot();
        Assert.Equal(Broken, root.ToFullString());

        for (var position = 0; position <= Broken.Length; position++) {
            root.FindToken(position);
        }

        // The fabricated type name is in the tree and is marked as fabricated, which is what
        // lets binding walk past it and a squiggle land on it.
        Assert.Contains(root.DescendantTokens(), token => token.IsMissing);
    }
}
