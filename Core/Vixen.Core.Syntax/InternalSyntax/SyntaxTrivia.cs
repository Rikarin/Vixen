namespace Vixen.Core.Syntax.InternalSyntax;

/// <summary>
///     Green trivia: a run of insignificant text (whitespace, line breaks,
///     comments) carried on the leading or trailing edge of a token.
/// </summary>
sealed class SyntaxTrivia : GreenNode {
    public string Text { get; }

    public override bool IsTrivia => true;

    internal SyntaxTrivia(int rawKind, string text) : base(rawKind, text.Length) {
        Text = text;
    }

    public override GreenNode? GetSlot(int index) => throw new InvalidOperationException("Trivia has no slots.");

    // Trivia is never a node slot; it is carried on tokens and projected through them.
    public override SyntaxNode CreateRed(SyntaxNode? parent, int position) =>
        throw new InvalidOperationException("Trivia is not realized as a standalone red node.");

    // Trivia is itself trivia; it contributes no "significant" width.
    public override int GetLeadingTriviaWidth() => 0;
    public override int GetTrailingTriviaWidth() => 0;

    public override void WriteTo(TextWriter writer) => writer.Write(Text);

    public override string ToString() => Text;
}
