// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax;

/// <summary>
///     Red token: a public leaf wrapping an immutable green token
///     (<see cref="InternalSyntax.SyntaxToken" />). Kept as a <see cref="SyntaxNode" />
///     subclass (rather than a Roslyn-style value type) so tree traversal and the
///     dumper treat tokens uniformly with nodes.
/// </summary>
public class SyntaxToken : SyntaxNode {
    /// <summary>Exact source text of the token (without trivia).</summary>
    public string Text => GreenToken.Text;

    /// <summary>Strongly-typed value (identifier text, parsed literal, …).</summary>
    public object? Value => GreenToken.Value;

    /// <summary>The value rendered as text — for a literal, the value rather than the spelling.</summary>
    public string ValueText => GreenToken.ValueText;

    /// <summary>
    ///     Whether the parser fabricated this token to stand in for one the source did not have.
    /// </summary>
    /// <remarks>
    ///     Recovery emits zero-width tokens rather than abandoning the tree, so an editor can keep
    ///     binding and colouring a file that is mid-edit. What a squiggle needs to know is which
    ///     of the tokens it is walking the author actually wrote.
    /// </remarks>
    public bool IsMissing => GreenToken.IsMissing;

    /// <summary>The trivia between the previous token and this one.</summary>
    public IReadOnlyList<SyntaxTrivia> LeadingTrivia => TriviaOf(GreenToken.LeadingTrivia, Position);

    /// <summary>The trivia between this token and the next one.</summary>
    public IReadOnlyList<SyntaxTrivia> TrailingTrivia =>
        TriviaOf(GreenToken.TrailingTrivia, Position + Green.GetLeadingTriviaWidth() + Text.Length);

    internal InternalSyntax.SyntaxToken GreenToken => (InternalSyntax.SyntaxToken)Green;

    internal SyntaxToken(InternalSyntax.SyntaxToken green, SyntaxNode? parent, int position)
        : base(green, parent, position) { }

    /// <summary>Always null: a token is a leaf and has no child slots.</summary>
    public override SyntaxNode? GetSlot(int index) => null;

    /// <summary>The token's text, without trivia.</summary>
    public override string ToString() => Text;

    /// <summary>
    ///     Flattens one edge's green trivia into positioned values.
    /// </summary>
    /// <remarks>
    ///     A single trivium is stored bare and a run is stored as a list, so this has to handle
    ///     both — the same shape <c>SyntaxList.List</c> produces everywhere else.
    /// </remarks>
    static SyntaxTrivia[] TriviaOf(InternalSyntax.GreenNode? green, int position) {
        if (green is null) {
            return [];
        }

        if (green is InternalSyntax.SyntaxTrivia single) {
            return [new(single.RawKind, single.Text, position)];
        }

        List<SyntaxTrivia> collected = [];

        for (var i = 0; i < green.SlotCount; i++) {
            if (green.GetSlot(i) is InternalSyntax.SyntaxTrivia trivia) {
                collected.Add(new(trivia.RawKind, trivia.Text, position));
                position += trivia.Text.Length;
            }
        }

        return [.. collected];
    }
}
