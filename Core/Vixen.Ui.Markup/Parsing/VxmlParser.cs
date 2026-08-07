// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;
using Vixen.Ui.Markup.Diagnostics;
using Vixen.Ui.Markup.Syntax;
using Green = Vixen.Core.Syntax.InternalSyntax;
using SyntaxList = Vixen.Core.Syntax.SyntaxList;

namespace Vixen.Ui.Markup.Parsing;

/// <summary>
///     The VXML parser: recursive descent over <see cref="VxmlLexer" />'s token list, producing
///     the green/red tree.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every parse produces a tree, and every tree reproduces its file byte for byte.</b>
///         Missing tokens are fabricated zero-width, source the parser could not use travels as
///         skipped-token trivia, and an element the file never closed simply has no end tag. That
///         is not politeness towards bad input — it is the requirement, because the editor reparses
///         on every keystroke and half the keystrokes land on a file that is not finished yet.
///     </para>
///     <para>
///         Recovery is deliberately shallow. A construct that cannot be parsed reports once and
///         stops; it does not guess. The one place the parser does look around is a mismatched end
///         tag, where it asks whether any <i>open ancestor</i> carries that name — and the answer
///         decides between "this element was never closed" and "this close tag is misspelled",
///         which are the same characters and completely different mistakes.
///     </para>
/// </remarks>
sealed class VxmlParser : SyntaxParser {
    readonly DiagnosticBag diagnostics;
    readonly SourceText text;
    readonly string filePath;

    /// <summary>Raw indices skipped during recovery; they travel as leading trivia.</summary>
    readonly HashSet<int> skipped = [];

    /// <summary>Names of the elements currently being parsed, outermost first.</summary>
    /// <remarks>
    ///     ⚠ <b>The only enclosing state a content node's parse depends on, and every branch that
    ///     reads it reports a diagnostic.</b> That is what makes incremental reuse sound here: a
    ///     subtree that parsed without complaint never consulted this list, so it parses the same
    ///     wherever it sits. See <see cref="TryReuseContent" />.
    /// </remarks>
    readonly List<string> openElements = [];

    readonly Blender? blender;

    /// <summary>
    ///     The one parse loop this grammar offers reuse at (see <c>ReuseCandidate.Context</c>).
    ///     Content is the only list VXML builds, so every candidate comes from here and goes back
    ///     here; a language with a second grammar over the same tokens — Raven's enum bodies — needs
    ///     a second value, and this constant is where that would start.
    /// </summary>
    internal const int ContentContext = 0;

    /// <summary>How many green nodes this parse took from the previous tree.</summary>
    int reused;

    VxmlParser(
        IReadOnlyList<LexedToken> tokens,
        DiagnosticBag diagnostics,
        SourceText text,
        string filePath,
        Blender? blender
    )
        : base(tokens) {
        this.diagnostics = diagnostics;
        this.text = text;
        this.filePath = filePath;
        this.blender = blender;
    }

    /// <summary>Parses a token list into a document.</summary>
    /// <param name="tokens">The lexer's output.</param>
    /// <param name="diagnostics">Where parse errors are reported.</param>
    /// <param name="text">The source, for turning offsets into line positions.</param>
    /// <param name="filePath">The path diagnostics name.</param>
    /// <param name="blender">Green nodes the previous tree can lend, or null for a fresh parse.</param>
    /// <returns>The document node. Never null, however broken the input.</returns>
    public static DocumentSyntax Parse(
        IReadOnlyList<LexedToken> tokens,
        DiagnosticBag diagnostics,
        SourceText text,
        string filePath,
        Blender? blender = null
    ) =>
        Parse(tokens, diagnostics, text, filePath, blender, out _);

    /// <summary>Parses a token list into a document, saying how much of the old one it kept.</summary>
    /// <param name="tokens">The lexer's output.</param>
    /// <param name="diagnostics">Where parse errors are reported.</param>
    /// <param name="text">The source, for turning offsets into line positions.</param>
    /// <param name="filePath">The path diagnostics name.</param>
    /// <param name="blender">Green nodes the previous tree can lend, or null for a fresh parse.</param>
    /// <param name="reused">How many nodes came from the previous tree.</param>
    /// <returns>The document node.</returns>
    /// <remarks>
    ///     ⚠ The count is exposed for the reason every other counter in this repository is: "a
    ///     keystroke reparses one element and shifts the rest" is a claim, and a claim about work
    ///     avoided that cannot be measured is one nobody can check. Equal trees prove reuse is
    ///     <i>safe</i>; only this says it happened.
    /// </remarks>
    public static DocumentSyntax Parse(
        IReadOnlyList<LexedToken> tokens,
        DiagnosticBag diagnostics,
        SourceText text,
        string filePath,
        Blender? blender,
        out int reused
    ) {
        var parser = new VxmlParser(tokens, diagnostics, text, filePath, blender);
        var document = parser.ParseDocument();
        reused = parser.reused;

        return document;
    }

    // ================================================================== Tokens

    VxmlTokenKind Kind => (VxmlTokenKind)Current.RawKind;

    bool At(VxmlTokenKind kind) => Kind == kind;

    bool AtEnd => At(VxmlTokenKind.EndOfFile);

    SyntaxToken Take(SyntaxKind treeKind) => TokenAt(Advance(), treeKind);

    SyntaxToken Expect(VxmlTokenKind kind, SyntaxKind treeKind) {
        if (At(kind)) {
            return Take(treeKind);
        }

        ReportExpected(SyntaxFacts.GetText(treeKind) is { Length: > 0 } fixedText ? $"'{fixedText}'" : Describe(treeKind));
        return Missing(treeKind);
    }

    /// <summary>
    ///     Takes the token if it is there and fabricates it in silence if it is not.
    /// </summary>
    /// <remarks>
    ///     For the closers the lexer balances itself — the <c>)</c> of a directive header, the
    ///     <c>}</c> of an <c>@code</c> body — a missing one has already been reported, by the pass
    ///     that actually knows where the run started. Saying it again in the parser's words would
    ///     make one unclosed brace two errors pointing at different characters.
    /// </remarks>
    SyntaxToken Fabricate(VxmlTokenKind kind, SyntaxKind treeKind) =>
        At(kind) ? Take(treeKind) : Missing(treeKind);

    static string Describe(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.NameToken => "a name",
            SyntaxKind.IdentifierToken => "an identifier",
            SyntaxKind.ExpressionToken => "an expression",
            SyntaxKind.CodeToken => "C#",
            SyntaxKind.CssToken => "CSS",
            _ => kind.ToString()
        };

    static SyntaxToken Missing(SyntaxKind kind) =>
        (SyntaxToken)new Green.SyntaxToken((int)kind, string.Empty, isMissing: true).CreateRed(null, 0);

    /// <summary>
    ///     Builds the red token for a consumed raw index, carrying every trivia and
    ///     recovery-skipped token immediately preceding it.
    /// </summary>
    SyntaxToken TokenAt(int rawIndex, SyntaxKind kind) {
        var raw = Tokens[rawIndex];
        return (SyntaxToken)new Green.SyntaxToken((int)kind, raw.Text, GatherLeadingTrivia(rawIndex)).CreateRed(null, 0);
    }

    Green.GreenNode? GatherLeadingTrivia(int rawIndex) {
        var first = rawIndex;
        while (first > 0 && IsTriviaLike(first - 1)) {
            first--;
        }

        if (first == rawIndex) {
            return null;
        }

        var collected = new List<Green.GreenNode>(rawIndex - first);
        for (var i = first; i < rawIndex; i++) {
            collected.Add(new Green.SyntaxTrivia((int)TriviaKind(i), Tokens[i].Text));
        }

        return collected.Count == 1 ? collected[0] : Green.SyntaxList.List([.. collected]);
    }

    bool IsTriviaLike(int rawIndex) => Tokens[rawIndex].IsTrivia || skipped.Contains(rawIndex);

    SyntaxKind TriviaKind(int rawIndex) {
        var token = Tokens[rawIndex];

        if (!token.IsTrivia) {
            return SyntaxKind.SkippedTokensTrivia;
        }

        return (VxmlTokenKind)token.RawKind == VxmlTokenKind.Comment
            ? SyntaxKind.CommentTrivia
            : SyntaxKind.WhitespaceTrivia;
    }

    // ------------------------------------------------------------ Diagnostics

    void ReportExpected(string expectation) => Report($"expected {expectation}, found {Describe(Current)}");

    void Report(string message) => Report(MarkupDiagnostics.SyntaxError, CurrentSpan, message);

    void Report(DiagnosticDescriptor descriptor, TextSpan span, params object[] arguments) =>
        diagnostics.Add(descriptor, Location.Create(filePath, span, text), arguments);

    TextSpan CurrentSpan => SpanOf(Current);

    /// <summary>Where a token sits in the file, which is the only span a diagnostic may use.</summary>
    TextSpan SpanOf(LexedToken token) =>
        token.Width > 0
            ? new TextSpan(token.Position, token.Width)
            : new TextSpan(Math.Min(token.Position, text.Length), 0);

    static string Describe(LexedToken token) =>
        (VxmlTokenKind)token.RawKind switch {
            VxmlTokenKind.EndOfFile => "end of file",
            VxmlTokenKind.Text or VxmlTokenKind.Whitespace => "text",
            VxmlTokenKind.Code => "C#",
            VxmlTokenKind.Css => "CSS",
            _ => $"'{token.Text}'"
        };

    /// <summary>Skips one token during recovery; it becomes trivia on the next consumed token.</summary>
    void SkipCurrent() {
        Report($"unexpected {Describe(Current)}");
        skipped.Add(Advance());
    }

    // ================================================================== Document

    /// <remarks>
    ///     ⚠ <b>The headers go into one list in source order, not a field each.</b> A node's
    ///     children are printed in the order it stores them, so a document holding
    ///     <c>@namespace</c> and <c>@using</c> in separate fields reproduced them in *field* order
    ///     — and a file that wrote them the other way round did not round-trip. Which header is
    ///     which is a question <see cref="DocumentSyntax.Namespace" /> and friends answer by looking,
    ///     which is a walk of four nodes and cannot disagree with the file.
    /// </remarks>
    DocumentSyntax ParseDocument() {
        List<SyntaxNode?> directives = [];

        if (At(VxmlTokenKind.ComponentKeyword)) {
            directives.Add(ParseComponentDirective());
        }

        var seenNamespace = false;
        var seenTag = false;
        var seenInherits = false;

        // ⚠ `@namespace`, `@tag` and `@using` interleave freely, because there is no reason for them
        // not to and a header order nobody can remember is a diagnostic nobody wants. A *second*
        // `@namespace` or `@tag` stops the loop rather than replacing the first, so it falls through
        // to the content parser and gets the same "unexpected" diagnostic every other stray
        // directive does — and, like them, survives in the tree as trivia.
        while (At(VxmlTokenKind.UsingKeyword) || At(VxmlTokenKind.NamespaceKeyword) || At(VxmlTokenKind.TagKeyword)
               || At(VxmlTokenKind.InheritsKeyword)) {
            if (At(VxmlTokenKind.NamespaceKeyword)) {
                if (seenNamespace) {
                    break;
                }

                seenNamespace = true;
                directives.Add(ParseNamespaceDirective());
                continue;
            }

            if (At(VxmlTokenKind.TagKeyword)) {
                if (seenTag) {
                    break;
                }

                seenTag = true;
                directives.Add(ParseTagDirective());
                continue;
            }

            if (At(VxmlTokenKind.InheritsKeyword)) {
                if (seenInherits) {
                    break;
                }

                seenInherits = true;
                directives.Add(ParseInheritsDirective());
                continue;
            }

            directives.Add(ParseUsingDirective());
        }

        List<SyntaxNode?> content = [];
        while (!AtEnd) {
            var before = RawPosition;
            ParseContentInto(content);

            // Content stops at whatever it cannot be: a stray `}`, a close tag with nothing open,
            // a `case` outside a switch. Nothing else will consume those, so this does — one
            // diagnostic each, and the characters survive as trivia.
            if (RawPosition == before) {
                SkipCurrent();
            }
        }

        return SyntaxFactory.Document(
            new(SyntaxList.List([.. directives])),
            new(SyntaxList.List([.. content])),
            TokenAt(RawPosition, SyntaxKind.EndOfFileToken)
        );
    }

    ComponentDirectiveSyntax ParseComponentDirective() {
        var keyword = Take(SyntaxKind.ComponentKeyword);
        var identifier = Expect(VxmlTokenKind.Name, SyntaxKind.IdentifierToken);
        return SyntaxFactory.ComponentDirective(keyword, identifier);
    }

    UsingDirectiveSyntax ParseUsingDirective() {
        var keyword = Take(SyntaxKind.UsingKeyword);
        var name = Expect(VxmlTokenKind.Name, SyntaxKind.NameToken);
        return SyntaxFactory.UsingDirective(keyword, name);
    }

    NamespaceDirectiveSyntax ParseNamespaceDirective() {
        var keyword = Take(SyntaxKind.NamespaceKeyword);
        var name = Expect(VxmlTokenKind.Name, SyntaxKind.NameToken);
        return SyntaxFactory.NamespaceDirective(keyword, name);
    }

    TagDirectiveSyntax ParseTagDirective() {
        var keyword = Take(SyntaxKind.TagKeyword);
        var name = Expect(VxmlTokenKind.Name, SyntaxKind.NameToken);
        return SyntaxFactory.TagDirective(keyword, name);
    }

    InheritsDirectiveSyntax ParseInheritsDirective() {
        var keyword = Take(SyntaxKind.InheritsKeyword);
        var name = Expect(VxmlTokenKind.Name, SyntaxKind.NameToken);
        return SyntaxFactory.InheritsDirective(keyword, name);
    }

    // ================================================================== Content

    /// <summary>
    ///     Whether the current token ends a run of content. Every list of content in the grammar
    ///     ends at one of these, which is why there is one predicate rather than a parameter.
    /// </summary>
    bool AtContentEnd() =>
        Kind is VxmlTokenKind.EndOfFile
            or VxmlTokenKind.CloseBrace
            or VxmlTokenKind.LessThanSlash
            or VxmlTokenKind.CaseKeyword
            or VxmlTokenKind.DefaultKeyword;

    SyntaxList<MarkupSyntax> ParseContent() {
        List<SyntaxNode?> items = [];
        ParseContentInto(items);
        return new(SyntaxList.List([.. items]));
    }

    void ParseContentInto(List<SyntaxNode?> items) {
        while (!AtContentEnd()) {
            var before = RawPosition;
            var node = TryReuseContent() ?? ParseContentNode();

            if (node is null || RawPosition == before) {
                return;
            }

            items.Add(node);
        }
    }

    MarkupSyntax? ParseContentNode() =>
        Kind switch {
            VxmlTokenKind.LessThan => AtStyleTag() ? ParseStyleBlock() : ParseElement(),
            VxmlTokenKind.Text => SyntaxFactory.Text(Take(SyntaxKind.TextToken)),
            VxmlTokenKind.At => ParseInterpolation(),
            VxmlTokenKind.IfKeyword => ParseIf(),
            VxmlTokenKind.ForKeyword => ParseFor(),
            VxmlTokenKind.SwitchKeyword => ParseSwitch(),
            VxmlTokenKind.CodeKeyword => ParseCodeBlock(),
            VxmlTokenKind.OpenBrace => SyntaxFactory.Text(Take(SyntaxKind.TextToken)),
            _ => null
        };

    /// <summary>
    ///     Incremental reparse: at a content boundary, take the previous tree's green node when the
    ///     blender has one whose new position and width line up exactly with the token stream.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>VXML's unit of reuse is a content node whose subtree reported nothing</b>, and
    ///         working out that it could be one is most of this. The worry written down when the
    ///         `Blender` was first shared was that an element is reusable only if nothing about its
    ///         <i>enclosing</i> content changed, because an unclosed tag above it changes what it is.
    ///         That is true of where the node ends up and false of the node itself: <c>&lt;panel/&gt;</c>
    ///         parses to the same green node whether it is a child or a sibling, and the enclosing
    ///         parse — which decides which — re-runs either way.
    ///     </para>
    ///     <para>
    ///         What genuinely could differ is <see cref="openElements" />, the one piece of enclosing
    ///         state a content node's parse reads. Every branch that reads it reports a diagnostic,
    ///         so a subtree that parsed cleanly never consulted it. Restricting candidates to
    ///         diagnostic-free subtrees therefore makes reuse sound — and it settles a second problem
    ///         at the same time, because a reused subtree's diagnostics are not re-reported and a
    ///         subtree that had none has none to lose.
    ///     </para>
    ///     <para>
    ///         Any mismatch falls through to a normal parse, so reuse can only ever skip work rather
    ///         than change the tree. That the two are equal is pinned by test.
    ///     </para>
    /// </remarks>
    MarkupSyntax? TryReuseContent() {
        if (blender is null) {
            return null;
        }

        // The candidate's full span starts at its leading trivia, so the lookup position is where
        // the pending trivia run begins.
        var firstTrivia = RawPosition;

        while (firstTrivia > 0 && IsTriviaLike(firstTrivia - 1)) {
            firstTrivia--;
        }

        var fullStart = Tokens[firstTrivia].Position;

        // ⚠ The blender is handed the new token stream, not just the position. A candidate whose
        // characters no change touched can still lex differently — this file's mode stack means an
        // unbalanced `</` two lines above turns `<panel class="root">` from a start tag into stray
        // characters inside a tag nobody closed — and splicing the old element in over that stream
        // is a tree that no full reparse would ever produce.
        if (blender.TryReuse(ContentContext, fullStart, Tokens, out var next) is not { } green) {
            return null;
        }

        // ⚠ `ResumeAt`, not `ResetTo`. The token starting exactly where the reused node ends is very
        // often the whitespace before the next real one, and a parser sitting on trivia cannot see
        // the token it is looking at — the close tag of the element whose last child was reused
        // simply vanishes.
        ResumeAt(next);
        reused++;

        return green.CreateRed(null, 0) as MarkupSyntax;
    }

    bool AtStyleTag() =>
        (VxmlTokenKind)Peek(1).RawKind == VxmlTokenKind.Name
        && string.Equals(Peek(1).Text, "style", StringComparison.Ordinal);

    InterpolationSyntax ParseInterpolation() {
        var at = Take(SyntaxKind.AtToken);

        // The lexer already reported an `@` it could not follow, so this fabricates in silence
        // rather than saying the same thing a second time in different words.
        var expression = At(VxmlTokenKind.Expression)
            ? Take(SyntaxKind.ExpressionToken)
            : Missing(SyntaxKind.ExpressionToken);

        return SyntaxFactory.Interpolation(at, expression);
    }

    // ================================================================== Elements

    ElementSyntax ParseElement() {
        // ⚠ The name's span is taken from the token stream, before the tag is built. A node under
        // construction has no parent yet, so its `Position` is relative to itself and every span
        // read off it starts near zero — a diagnostic that points at the top of the file whichever
        // element it is about. Nothing caught this until a source generator turned these spans into
        // IDE squiggles, because the parser's own tests assert which diagnostics were reported and
        // the emitter's assert where *Roslyn* put its own.
        var nameSpan = SpanOf(Peek(1));

        var startTag = ParseStartTag();
        var name = startTag.Name.Text;

        if (startTag.GreaterThanToken.Kind == SyntaxKind.SlashGreaterThanToken) {
            return SyntaxFactory.Element(startTag, default, null);
        }

        openElements.Add(name);
        var content = ParseContent();
        openElements.RemoveAt(openElements.Count - 1);

        return SyntaxFactory.Element(startTag, content, ParseEndTagFor(name, nameSpan));
    }

    /// <summary>
    ///     Matches the close tag against the element that is open, and decides which of the two
    ///     possible mistakes was made.
    /// </summary>
    EndTagSyntax? ParseEndTagFor(string name, TextSpan nameSpan) {
        if (!At(VxmlTokenKind.LessThanSlash)) {
            Report(MarkupDiagnostics.UnclosedElement, nameSpan, name);
            return null;
        }

        var closing = (VxmlTokenKind)Peek(1).RawKind == VxmlTokenKind.Name ? Peek(1).Text : string.Empty;

        if (string.Equals(closing, name, StringComparison.Ordinal)) {
            return ParseEndTag();
        }

        // An ancestor answers to this name, so the close tag is theirs and *this* element is the
        // one that was never closed. Leaving the tag for them is what keeps the rest of the file
        // parsing as its author wrote it, instead of reparenting everything below.
        if (openElements.Contains(closing)) {
            Report(MarkupDiagnostics.UnclosedElement, nameSpan, name);
            return null;
        }

        // Nobody is waiting for it, so it is this element's close tag, misspelled. Taking it here
        // costs one diagnostic instead of two and leaves the tree the shape the author drew.
        var closingSpan = SpanOf(Peek(1));
        var endTag = ParseEndTag();
        Report(MarkupDiagnostics.MismatchedEndTag, closingSpan, name, closing);
        return endTag;
    }

    StartTagSyntax ParseStartTag() {
        var lessThan = Take(SyntaxKind.LessThanToken);
        var name = Expect(VxmlTokenKind.Name, SyntaxKind.NameToken);

        List<SyntaxNode?> attributes = [];
        while (!AtTagEnd()) {
            var before = RawPosition;

            if (At(VxmlTokenKind.Name)) {
                attributes.Add(ParseAttribute());
            } else {
                SkipCurrent();
            }

            if (RawPosition == before) {
                break;
            }
        }

        return SyntaxFactory.StartTag(lessThan, name, new(SyntaxList.List([.. attributes])), ParseTagClose());
    }

    bool AtTagEnd() => Kind is VxmlTokenKind.GreaterThan or VxmlTokenKind.SlashGreaterThan or VxmlTokenKind.EndOfFile;

    SyntaxToken ParseTagClose() =>
        Kind switch {
            VxmlTokenKind.GreaterThan => Take(SyntaxKind.GreaterThanToken),
            VxmlTokenKind.SlashGreaterThan => Take(SyntaxKind.SlashGreaterThanToken),
            _ => Expect(VxmlTokenKind.GreaterThan, SyntaxKind.GreaterThanToken)
        };

    EndTagSyntax ParseEndTag() {
        var lessThanSlash = Take(SyntaxKind.LessThanSlashToken);
        var name = Expect(VxmlTokenKind.Name, SyntaxKind.NameToken);
        var greaterThan = Expect(VxmlTokenKind.GreaterThan, SyntaxKind.GreaterThanToken);
        return SyntaxFactory.EndTag(lessThanSlash, name, greaterThan);
    }

    AttributeSyntax ParseAttribute() {
        var name = Take(SyntaxKind.NameToken);

        if (!At(VxmlTokenKind.Equals)) {
            return SyntaxFactory.Attribute(name, null, null);
        }

        var equals = Take(SyntaxKind.EqualsToken);
        return SyntaxFactory.Attribute(name, equals, ParseAttributeValue());
    }

    AttributeValueSyntax? ParseAttributeValue() {
        if (At(VxmlTokenKind.At)) {
            return SyntaxFactory.ExpressionAttributeValue(ParseInterpolation());
        }

        if (!At(VxmlTokenKind.Quote)) {
            ReportExpected("an attribute value");
            return null;
        }

        var openQuote = Take(SyntaxKind.QuoteToken);

        List<SyntaxNode?> content = [];
        while (Kind is VxmlTokenKind.Text or VxmlTokenKind.At) {
            content.Add(
                At(VxmlTokenKind.Text)
                    ? SyntaxFactory.Text(Take(SyntaxKind.TextToken))
                    : (SyntaxNode)ParseInterpolation()
            );
        }

        var closeQuote = Expect(VxmlTokenKind.Quote, SyntaxKind.QuoteToken);
        return SyntaxFactory.QuotedAttributeValue(openQuote, new(SyntaxList.List([.. content])), closeQuote);
    }

    StyleBlockSyntax ParseStyleBlock() {
        var startTag = ParseStartTag();

        var css = At(VxmlTokenKind.Css) ? Take(SyntaxKind.CssToken) : Missing(SyntaxKind.CssToken);

        if (startTag.GreaterThanToken.Kind == SyntaxKind.SlashGreaterThanToken) {
            return SyntaxFactory.StyleBlock(startTag, css, null);
        }

        // The lexer already reported a `<style>` that ran to the end of the file.
        return SyntaxFactory.StyleBlock(startTag, css, At(VxmlTokenKind.LessThanSlash) ? ParseEndTag() : null);
    }

    // ================================================================== Control flow

    IfSyntax ParseIf() {
        var keyword = Take(SyntaxKind.IfKeyword);
        var openParen = Expect(VxmlTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var condition = Expect(VxmlTokenKind.Expression, SyntaxKind.ExpressionToken);
        var closeParen = Fabricate(VxmlTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        var body = ParseMarkupBlock();

        return SyntaxFactory.If(
            keyword,
            openParen,
            condition,
            closeParen,
            body,
            At(VxmlTokenKind.ElseKeyword) ? ParseElseClause() : null
        );
    }

    ElseClauseSyntax ParseElseClause() {
        var keyword = Take(SyntaxKind.ElseKeyword);
        return SyntaxFactory.ElseClause(keyword, At(VxmlTokenKind.IfKeyword) ? ParseIf() : ParseMarkupBlock());
    }

    ForSyntax ParseFor() {
        var keyword = Take(SyntaxKind.ForKeyword);
        var openParen = Expect(VxmlTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var varKeyword = Expect(VxmlTokenKind.VarKeyword, SyntaxKind.VarKeyword);
        var identifier = Expect(VxmlTokenKind.Name, SyntaxKind.IdentifierToken);
        var inKeyword = Expect(VxmlTokenKind.InKeyword, SyntaxKind.InKeyword);
        var sequence = Expect(VxmlTokenKind.Expression, SyntaxKind.ExpressionToken);
        var closeParen = Fabricate(VxmlTokenKind.CloseParen, SyntaxKind.CloseParenToken);

        return SyntaxFactory.For(
            keyword,
            openParen,
            varKeyword,
            identifier,
            inKeyword,
            sequence,
            closeParen,
            ParseMarkupBlock()
        );
    }

    SwitchSyntax ParseSwitch() {
        var keyword = Take(SyntaxKind.SwitchKeyword);
        var openParen = Expect(VxmlTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var subject = Expect(VxmlTokenKind.Expression, SyntaxKind.ExpressionToken);
        var closeParen = Fabricate(VxmlTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        var openBrace = Expect(VxmlTokenKind.OpenBrace, SyntaxKind.OpenBraceToken);

        List<SyntaxNode?> sections = [];
        while (Kind is VxmlTokenKind.CaseKeyword or VxmlTokenKind.DefaultKeyword) {
            sections.Add(ParseSwitchSection());
        }

        return SyntaxFactory.Switch(
            keyword,
            openParen,
            subject,
            closeParen,
            openBrace,
            new(SyntaxList.List([.. sections])),
            Expect(VxmlTokenKind.CloseBrace, SyntaxKind.CloseBraceToken)
        );
    }

    SwitchSectionSyntax ParseSwitchSection() {
        var isCase = At(VxmlTokenKind.CaseKeyword);
        var keyword = Take(isCase ? SyntaxKind.CaseKeyword : SyntaxKind.DefaultKeyword);

        var pattern = isCase
            ? At(VxmlTokenKind.Expression) ? Take(SyntaxKind.ExpressionToken) : Missing(SyntaxKind.ExpressionToken)
            : null;

        var colon = Expect(VxmlTokenKind.Colon, SyntaxKind.ColonToken);
        return SyntaxFactory.SwitchSection(keyword, pattern, colon, ParseContent());
    }

    MarkupBlockSyntax ParseMarkupBlock() {
        var openBrace = Expect(VxmlTokenKind.OpenBrace, SyntaxKind.OpenBraceToken);

        // Without an opening brace there is no body to look for, and hunting one down would turn
        // a missing character into a cascade of errors about the markup that follows it.
        if (openBrace.IsMissing) {
            return SyntaxFactory.MarkupBlock(openBrace, default, Missing(SyntaxKind.CloseBraceToken));
        }

        return SyntaxFactory.MarkupBlock(
            openBrace,
            ParseContent(),
            Expect(VxmlTokenKind.CloseBrace, SyntaxKind.CloseBraceToken)
        );
    }

    CodeBlockSyntax ParseCodeBlock() {
        var keyword = Take(SyntaxKind.CodeKeyword);
        var openBrace = Expect(VxmlTokenKind.OpenBrace, SyntaxKind.OpenBraceToken);

        if (openBrace.IsMissing) {
            return SyntaxFactory.CodeBlock(
                keyword,
                openBrace,
                Missing(SyntaxKind.CodeToken),
                Missing(SyntaxKind.CloseBraceToken)
            );
        }

        var body = At(VxmlTokenKind.Code) ? Take(SyntaxKind.CodeToken) : Missing(SyntaxKind.CodeToken);
        return SyntaxFactory.CodeBlock(
            keyword,
            openBrace,
            body,
            Fabricate(VxmlTokenKind.CloseBrace, SyntaxKind.CloseBraceToken)
        );
    }
}
