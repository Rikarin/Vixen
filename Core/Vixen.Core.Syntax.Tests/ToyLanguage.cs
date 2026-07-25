// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Green = Vixen.Core.Syntax.InternalSyntax;

namespace Vixen.Core.Syntax.Tests;

/// <summary>
///     A second language, built only for these tests.
/// </summary>
/// <remarks>
///     The point of extracting this assembly out of Raven was that the tree is not
///     Raven-shaped. Asserting that with Raven would prove nothing, so the tests use a
///     throwaway language instead: its own kind enum, its own node types, no shader
///     concepts anywhere. If something Raven-specific ever leaks back into the shared
///     tree, this stops compiling.
/// </remarks>
public enum ToyKind {
    None = 0,

    /// <summary>Must match <see cref="SyntaxKinds.List" />, exactly as Raven's does.</summary>
    List = SyntaxKinds.List,

    Word,
    Space,
    Comma,
    Phrase
}

/// <summary>Green terminal: a word or punctuation, optionally carrying trivia.</summary>
sealed class ToyToken : Green.SyntaxToken {
    internal ToyToken(ToyKind kind, string text, Green.GreenNode? leading = null, Green.GreenNode? trailing = null)
        : base((int)kind, text, leading, trailing) { }
}

/// <summary>Green trivia: whitespace between words.</summary>
static class Toy {
    public static Green.SyntaxTrivia Space(string text = " ") => new((int)ToyKind.Space, text);

    /// <summary>A green phrase node holding two slots.</summary>
    public static Green.GreenNode Phrase(Green.GreenNode? left, Green.GreenNode? right) =>
        new ToyPhraseGreen(left, right);
}

sealed class ToyPhraseGreen : Green.GreenNode {
    readonly Green.GreenNode? left;
    readonly Green.GreenNode? right;

    internal ToyPhraseGreen(Green.GreenNode? left, Green.GreenNode? right) : base((int)ToyKind.Phrase) {
        SlotCount = 2;
        this.left = left;
        this.right = right;
        AdjustWidth(left);
        AdjustWidth(right);
    }

    public override Green.GreenNode? GetSlot(int index) =>
        index switch {
            0 => left,
            1 => right,
            _ => null
        };

    public override SyntaxNode CreateRed(SyntaxNode? parent, int position) => new ToyPhrase(this, parent, position);
}

/// <summary>Red phrase node. Note the absence of any <c>Accept</c>: dispatch is the language's business.</summary>
public sealed class ToyPhrase : SyntaxNode {
    internal ToyPhrase(Green.GreenNode green, SyntaxNode? parent, int position) : base(green, parent, position) { }

    /// <summary>This node's kind, projected from the shared raw value.</summary>
    public ToyKind Kind => (ToyKind)RawKind;

    /// <inheritdoc />
    public override SyntaxNode? GetSlot(int index) => GetRed(index);
}

/// <summary>A parsed toy document, supplying what <see cref="SyntaxNode.GetLocation" /> needs.</summary>
public sealed class ToyTree(string path, Text.SourceText text) : ISyntaxTree {
    /// <inheritdoc />
    public string FilePath { get; } = path;

    /// <inheritdoc />
    public Text.SourceText? Text { get; } = text;
}
