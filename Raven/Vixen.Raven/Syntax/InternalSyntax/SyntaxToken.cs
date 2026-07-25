namespace Vixen.Raven.Syntax.InternalSyntax;

/// <summary>
///     Green token: a terminal carrying its exact source <see cref="Text" />, an
///     optional typed <see cref="Value" />, and leading/trailing trivia. Tokens have
///     no child slots; their trivia are held directly (not as slots) so that node
///     traversal sees tokens as leaves.
/// </summary>
class SyntaxToken : GreenNode {
    public string Text { get; }
    public GreenNode? LeadingTrivia { get; }
    public GreenNode? TrailingTrivia { get; }

    public override bool IsToken => true;

    /// <summary>The token's semantic value (identifier text, parsed literal, …). Defaults to the text.</summary>
    public virtual object? Value => Text;

    public virtual string ValueText => Text;

    internal SyntaxToken(SyntaxKind kind, string text, GreenNode? leading = null, GreenNode? trailing = null)
        : base(kind) {
        Text = text;
        LeadingTrivia = leading;
        TrailingTrivia = trailing;
        FullWidth = (leading?.FullWidth ?? 0) + text.Length + (trailing?.FullWidth ?? 0);
    }

    public override GreenNode? GetSlot(int index) => throw new InvalidOperationException("Tokens have no slots.");

    public override SyntaxNode CreateRed(SyntaxNode? parent, int position) =>
        new Syntax.SyntaxToken(this, parent, position);

    public override int GetLeadingTriviaWidth() => LeadingTrivia?.FullWidth ?? 0;
    public override int GetTrailingTriviaWidth() => TrailingTrivia?.FullWidth ?? 0;

    public SyntaxToken WithLeadingTrivia(GreenNode? trivia) => new(Kind, Text, trivia, TrailingTrivia);
    public SyntaxToken WithTrailingTrivia(GreenNode? trivia) => new(Kind, Text, LeadingTrivia, trivia);

    public override void WriteTo(TextWriter writer) {
        LeadingTrivia?.WriteTo(writer);
        writer.Write(Text);
        TrailingTrivia?.WriteTo(writer);
    }

    public override string ToString() {
        using var sw = new StringWriter();
        WriteTo(sw);
        return sw.ToString();
    }
}

/// <summary>Green identifier token whose <see cref="SyntaxToken.Value" /> is its text.</summary>
sealed class SyntaxIdentifier : SyntaxToken {
    internal SyntaxIdentifier(string text, GreenNode? leading = null, GreenNode? trailing = null)
        : base(SyntaxKind.IdentifierToken, text, leading, trailing) { }
}

/// <summary>Green literal token carrying a strongly-typed value (int, float, string, …).</summary>
sealed class SyntaxTokenWithValue<T> : SyntaxToken {
    readonly T value;

    public override object? Value => value;
    public override string ValueText => value?.ToString() ?? string.Empty;

    internal SyntaxTokenWithValue(
        SyntaxKind kind,
        string text,
        T value,
        GreenNode? leading = null,
        GreenNode? trailing = null
    )
        : base(kind, text, leading, trailing) {
        this.value = value;
    }
}
