// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;
using Green = Vixen.Core.Syntax.InternalSyntax;
using SyntaxList = Vixen.Core.Syntax.SyntaxList;

namespace Vixen.Raven.Parsing;

/// <summary>
///     The hand-written recursive-descent parser (docs/plan/18 steps 4–5), a
///     mechanical translation of <c>RavenParser.g4</c> — the grammar stays beside
///     this file as the executable specification, and the tree differential test
///     keeps the two in agreement over the corpus.
/// </summary>
/// <remarks>
///     Behaviour the tree pins, inherited from the ANTLR front end and deliberately
///     preserved: newlines are structural for the parser but only ever trivia in the
///     tree; a run of blank lines between statements is a zero-width
///     <see cref="EmptyStatementSyntax" />; a dotted name in expression position is
///     one <see cref="QualifiedNameSyntax" />, with <c>MemberAccess</c> reserved for
///     suffixes on non-name expressions; and <c>(a) + b</c> is a cast, because the
///     cast alternative outranks the binary one.
/// </remarks>
sealed class RavenParser : SyntaxParser {
    readonly DiagnosticBag diagnostics;
    readonly SourceText text;
    readonly string filePath;
    readonly Blender? blender;

    /// <summary>Raw indices skipped during recovery; they travel as leading trivia.</summary>
    readonly HashSet<int> skipped = [];

    /// <summary>
    ///     <c>&gt;</c>s a speculative scan has taken out of a <c>&gt;&gt;</c> token and still owes
    ///     to an enclosing type-argument list. See <see cref="ScanBalancedAngles" />.
    /// </summary>
    int pendingCloseAngles;

    /// <summary>Type-argument lists whose arguments the parser is inside of, right now.</summary>
    int typeArgumentDepth;

    RavenParser(
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

    public static CompilationUnitSyntax Parse(
        IReadOnlyList<LexedToken> tokens,
        DiagnosticBag diagnostics,
        SourceText text,
        string filePath,
        Blender? blender = null
    ) =>
        new RavenParser(tokens, diagnostics, text, filePath, blender).ParseCompilationUnit();

    // ================================================================== Tokens

    RavenTokenKind Kind => (RavenTokenKind)Current.RawKind;

    RavenTokenKind PeekKind(int n) => (RavenTokenKind)Peek(n).RawKind;

    bool At(RavenTokenKind kind) => Kind == kind;

    bool AtEnd => At(RavenTokenKind.EndOfFile);

    /// <summary>Consumes the current token unconditionally as the given tree kind.</summary>
    SyntaxToken Take(SyntaxKind treeKind) => TokenAt(Advance(), treeKind);

    /// <summary>Consumes the expected token, or reports and fabricates a zero-width missing one.</summary>
    SyntaxToken Expect(RavenTokenKind kind, SyntaxKind treeKind) {
        if (At(kind)) {
            return Take(treeKind);
        }

        ReportExpected(SyntaxFacts.GetText(treeKind) is { Length: > 0 } fixedText ? $"'{fixedText}'" : treeKind.ToString());
        return Missing(treeKind);
    }

    SyntaxToken ExpectIdentifier() {
        if (At(RavenTokenKind.Identifier)) {
            return Take(SyntaxKind.IdentifierToken);
        }

        ReportExpected("an identifier");
        return Missing(SyntaxKind.IdentifierToken);
    }

    static SyntaxToken Missing(SyntaxKind kind) =>
        (SyntaxToken)new Green.SyntaxToken((int)kind, string.Empty, isMissing: true).CreateRed(null, 0);

    /// <summary>
    ///     Builds the red token for a consumed raw index, carrying every trivia,
    ///     newline and recovery-skipped token immediately preceding it — the same
    ///     attachment rule the ANTLR translator used, so trees compare byte-exact.
    /// </summary>
    SyntaxToken TokenAt(int rawIndex, SyntaxKind kind) {
        var raw = Tokens[rawIndex];
        var text = raw.RawKind == (int)RavenTokenKind.EndOfFile ? string.Empty : raw.Text;
        return (SyntaxToken)new Green.SyntaxToken((int)kind, text, GatherLeadingTrivia(rawIndex)).CreateRed(null, 0);
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

        return collected.Count == 1 ? collected[0] : Green.SyntaxList.List(collected.ToArray());
    }

    bool IsTriviaLike(int rawIndex) {
        var token = Tokens[rawIndex];
        return token.IsTrivia || token.IsEndOfLine || skipped.Contains(rawIndex);
    }

    SyntaxKind TriviaKind(int rawIndex) {
        var token = Tokens[rawIndex];
        if (token.IsEndOfLine) {
            return SyntaxKind.EndOfLineTrivia;
        }

        if (!token.IsTrivia) {
            return SyntaxKind.SkippedTokensTrivia;
        }

        return (RavenTokenKind)token.RawKind switch {
            RavenTokenKind.SingleLineComment => SyntaxKind.SingleLineCommentTrivia,
            RavenTokenKind.MultiLineComment => SyntaxKind.MultiLineCommentTrivia,
            _ => SyntaxKind.WhitespaceTrivia
        };
    }

    // ------------------------------------------------------------ Newlines

    bool AtNewLine => At(RavenTokenKind.NewLine);

    /// <summary>Consumes a run of newline tokens; they surface as trivia on the next tree token.</summary>
    void SkipNewLines() {
        while (AtNewLine) {
            Advance();
        }
    }

    /// <summary>Grammar <c>NL+</c>: at least one terminator, then any number more.</summary>
    void ExpectNewLines() {
        if (!AtNewLine && !AtEnd) {
            ReportExpected("a line break");
            return;
        }

        SkipNewLines();
    }

    /// <summary>Grammar <c>NL</c>: exactly one terminator.</summary>
    void ExpectSingleNewLine() {
        if (AtNewLine) {
            Advance();
        } else if (!AtEnd) {
            ReportExpected("a line break");
        }
    }

    // ------------------------------------------------------------ Diagnostics

    void ReportExpected(string expectation) => Report($"expected {expectation}, found {Describe(Current)}");

    void Report(string message) {
        var current = Current;
        var span = current.Width > 0
            ? new TextSpan(current.Position, current.Width)
            : new TextSpan(Math.Min(current.Position, text.Length), 0);
        diagnostics.Add(SyntaxDiagnostics.SyntaxError, Location.Create(filePath, span, text), message);
    }

    static string Describe(LexedToken token) =>
        (RavenTokenKind)token.RawKind switch {
            RavenTokenKind.EndOfFile => "end of file",
            RavenTokenKind.NewLine => "end of line",
            _ => $"'{token.Text}'"
        };

    /// <summary>Skips one token during recovery; it becomes trivia on the next consumed token.</summary>
    void SkipCurrent() {
        Report($"unexpected {Describe(Current)}");
        skipped.Add(Advance());
    }

    /// <summary>
    ///     Recovery at a declaration or statement boundary: one diagnostic at the
    ///     first offending token, then the rest of the line is skipped silently —
    ///     a broken line reads as one error, not one per token. The skipped source
    ///     still travels as trivia, so the tree reproduces the file.
    /// </summary>
    void SkipToLineEnd() {
        SkipCurrent();
        while (!AtNewLine && !AtEnd && !At(RavenTokenKind.CloseBrace)) {
            skipped.Add(Advance());
        }
    }

    // ================================================================== Compilation unit

    CompilationUnitSyntax ParseCompilationUnit() {
        SkipNewLines();
        var package = ParsePackageDirective();

        List<SyntaxNode?> imports = [];
        while (At(RavenTokenKind.ImportKeyword)) {
            imports.Add(ParseImportDirective());
        }

        List<SyntaxNode?> members = [];
        while (!AtEnd) {
            var before = RawPosition;
            var member = TryReuseMember() ?? ParseMemberDeclaration();
            if (member is null || RawPosition == before) {
                SkipToLineEnd();
                SkipNewLines();
                continue;
            }

            members.Add(member);
            SkipNewLines();
        }

        var endOfFile = TokenAt(RawPosition, SyntaxKind.EndOfFileToken);

        return SyntaxFactory.CompilationUnit(
            package,
            new(SyntaxList.List(imports.ToArray())),
            new(SyntaxList.List(members.ToArray())),
            endOfFile
        );
    }

    PackageDirectiveSyntax ParsePackageDirective() {
        var keyword = Expect(RavenTokenKind.PackageKeyword, SyntaxKind.PackageKeyword);
        var name = ParseName();
        ExpectNewLines();
        return (PackageDirectiveSyntax)SyntaxFactory.PackageDirective(keyword, name);
    }

    ImportDirectiveSyntax ParseImportDirective() {
        var keyword = Take(SyntaxKind.ImportKeyword);
        var @static = At(RavenTokenKind.StaticKeyword) ? Take(SyntaxKind.StaticKeyword) : null;
        var name = ParseName();
        ExpectNewLines();
        return (ImportDirectiveSyntax)SyntaxFactory.ImportDirective(keyword, @static, name);
    }

    // ================================================================== Names

    /// <summary>
    ///     <c>name</c>: a dotted chain of simple names, consumed greedily — the whole
    ///     of <c>a.b.c</c> is one qualified name, exactly as the left-recursive
    ///     grammar rule folds it.
    /// </summary>
    NameSyntax ParseName() {
        NameSyntax name = ParseSimpleName();
        while (At(RavenTokenKind.Dot)) {
            var dot = Take(SyntaxKind.DotToken);
            var right = ParseSimpleName();
            name = (NameSyntax)SyntaxFactory.QualifiedName(name, dot, right);
        }

        return name;
    }

    SimpleNameSyntax ParseSimpleName() {
        var identifier = ExpectIdentifier();

        if (At(RavenTokenKind.LessThan) && ScanTypeArgumentList()) {
            var typeArguments = ParseTypeArgumentList();
            return (SimpleNameSyntax)SyntaxFactory.GenericName(identifier, typeArguments);
        }

        return (SimpleNameSyntax)SyntaxFactory.IdentifierName(identifier);
    }

    IdentifierNameSyntax ParseIdentifierName() =>
        (IdentifierNameSyntax)SyntaxFactory.IdentifierName(ExpectIdentifier());

    TypeArgumentListSyntax ParseTypeArgumentList() {
        var lessThan = Take(SyntaxKind.LessThanToken);

        List<SyntaxNode?> arguments = [];
        List<SyntaxToken> commas = [];

        // Each argument re-scans its own name, so it needs to know an enclosing list is open:
        // that is what lets the inner list of `Box<Box<float>>` end on a `>>`.
        typeArgumentDepth++;

        try {
            if (!At(RavenTokenKind.GreaterThan)) {
                arguments.Add(ParseType());
                while (At(RavenTokenKind.Comma)) {
                    commas.Add(Take(SyntaxKind.CommaToken));
                    arguments.Add(ParseType());
                }
            }
        } finally {
            typeArgumentDepth--;
        }

        return (TypeArgumentListSyntax)SyntaxFactory.TypeArgumentList(
            lessThan,
            Separated<TypeSyntax>(arguments, commas),
            ExpectCloseAngle()
        );
    }

    /// <summary>
    ///     Consumes the <c>&gt;</c> closing a type-argument list, splitting a <c>&gt;&gt;</c>
    ///     token when the inner of two nested lists ends on one.
    /// </summary>
    /// <remarks>
    ///     Only reached once <see cref="ScanTypeArgumentList" /> has decided this really is a
    ///     type-argument list, which is what keeps <c>a &lt; b &gt;&gt; c</c> a comparison: that
    ///     scan refuses to split a <c>&gt;&gt;</c> whose second half no enclosing list would take.
    /// </remarks>
    SyntaxToken ExpectCloseAngle() {
        // One `>` comes off the front; the rest stays a token for the enclosing list to take,
        // which is what makes `Box<Box<Box<float>>>`'s `>>>` work by the same rule as `>>`.
        switch (Kind) {
            case RavenTokenKind.GreaterThanGreaterThan:
                SplitCurrentToken((int)RavenTokenKind.GreaterThan, 1, (int)RavenTokenKind.GreaterThan);
                break;

            case RavenTokenKind.UnsignedShiftRight:
                SplitCurrentToken((int)RavenTokenKind.GreaterThan, 1, (int)RavenTokenKind.GreaterThanGreaterThan);
                break;
        }

        return Expect(RavenTokenKind.GreaterThan, SyntaxKind.GreaterThanToken);
    }

    // ================================================================== Types

    static bool IsPredefinedTypeKeyword(RavenTokenKind kind) =>
        kind is >= RavenTokenKind.BoolKeyword and <= RavenTokenKind.Mat4x3Keyword;

    static SyntaxKind PredefinedTypeTreeKind(RavenTokenKind kind) =>
        // The two blocks run in the same order, so the offset maps 1:1.
        (SyntaxKind)((int)SyntaxKind.BoolKeyword + (kind - RavenTokenKind.BoolKeyword));

    bool AtTypeStart =>
        At(RavenTokenKind.Identifier) || IsPredefinedTypeKeyword(Kind) || At(RavenTokenKind.OpenParen);

    /// <summary>
    ///     One <c>type</c> production.
    /// </summary>
    /// <param name="allowSizes">
    ///     Whether <c>[4]</c> may size the array. On in a type position, where nothing but the
    ///     type can own a <c>[</c>; off where a type competes with an expression, so that
    ///     <c>a[i]</c> stays element access. That positional split is the whole disambiguation:
    ///     the two readings are told apart by where they appear, never by what is between the
    ///     brackets. The oracle grammar splits `type` and `unsized_type` for the same reason.
    /// </param>
    TypeSyntax ParseType(bool allowSizes = true) {
        var type = ParseCoreType();

        // `type array_rank_specifier+` folds every rank into one ArrayType node.
        if (At(RavenTokenKind.OpenBracket) && ScanArrayRank(allowSizes)) {
            List<SyntaxNode?> ranks = [];
            while (At(RavenTokenKind.OpenBracket) && ScanArrayRank(allowSizes)) {
                ranks.Add(ParseArrayRankSpecifier());
            }

            return (TypeSyntax)SyntaxFactory.ArrayType(type, new(SyntaxList.List(ranks.ToArray())));
        }

        return type;
    }

    TypeSyntax ParseCoreType() {
        if (IsPredefinedTypeKeyword(Kind)) {
            return (TypeSyntax)SyntaxFactory.PredefinedType(Take(PredefinedTypeTreeKind(Kind)));
        }

        if (At(RavenTokenKind.OpenParen)) {
            return ParseTupleType();
        }

        if (At(RavenTokenKind.Identifier)) {
            return (TypeSyntax)ParseName();
        }

        ReportExpected("a type");
        return (TypeSyntax)SyntaxFactory.IdentifierName(Missing(SyntaxKind.IdentifierToken));
    }

    TypeSyntax ParseTupleType() {
        var open = Take(SyntaxKind.OpenParenToken);

        List<SyntaxNode?> elements = [ParseTupleElement()];
        List<SyntaxToken> commas = [];
        while (At(RavenTokenKind.Comma)) {
            commas.Add(Take(SyntaxKind.CommaToken));
            elements.Add(ParseTupleElement());
        }

        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        return (TypeSyntax)SyntaxFactory.TupleType(open, Separated<TupleElementSyntax>(elements, commas), close);
    }

    TupleElementSyntax ParseTupleElement() {
        SyntaxToken? identifier = null;
        SyntaxToken? colon = null;
        if (At(RavenTokenKind.Identifier) && PeekKind(1) == RavenTokenKind.Colon) {
            identifier = Take(SyntaxKind.IdentifierToken);
            colon = Take(SyntaxKind.ColonToken);
        }

        var type = ParseType();
        return (TupleElementSyntax)SyntaxFactory.TupleElement(identifier, colon, type);
    }

    ArrayRankSpecifierSyntax ParseArrayRankSpecifier() {
        var open = Take(SyntaxKind.OpenBracketToken);

        // A size and commas are alternatives, not a sequence: `[4,4]` is not a shape the
        // node can hold, because a multi-dimensional array is never sized.
        ExpressionSyntax? size = null;
        List<SyntaxNode?> commas = [];

        if (At(RavenTokenKind.Comma) || At(RavenTokenKind.CloseBracket)) {
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
            }
        } else {
            // A size is an expression, so the enclosing type-argument list stops being an
            // excuse to split a `>>` inside it: `Box<float[a < b >> c]>` is a shift again.
            var enclosing = typeArgumentDepth;
            typeArgumentDepth = 0;

            try {
                size = ParseExpression();
            } finally {
                typeArgumentDepth = enclosing;
            }
        }

        var close = Expect(RavenTokenKind.CloseBracket, SyntaxKind.CloseBracketToken);
        return (ArrayRankSpecifierSyntax)SyntaxFactory.ArrayRankSpecifier(
            open,
            size,
            new(SyntaxList.List(commas.ToArray())),
            close
        );
    }

    // ------------------------------------------------------------ Type scanning (no tree tokens)

    /// <summary>
    ///     Whether the <c>[</c> ahead opens an array rank rather than an element access:
    ///     <c>[</c> commas <c>]</c> always, and <c>[</c> expression <c>]</c> only where a
    ///     size is allowed.
    /// </summary>
    bool ScanArrayRank(bool allowSizes) {
        var n = 1;
        while (PeekKind(n) == RavenTokenKind.Comma) {
            n++;
        }

        if (PeekKind(n) == RavenTokenKind.CloseBracket) {
            return true;
        }

        return allowSizes && ScanArraySize();
    }

    /// <summary>
    ///     A non-empty <c>[</c> … <c>]</c> that closes on this line. Scanning to the *matching*
    ///     bracket rather than the first one keeps a nested access in the size — <c>T[n[0]]</c> —
    ///     from ending it early; stopping at a newline keeps a following attribute list from
    ///     being swallowed when the type before it failed to parse.
    /// </summary>
    bool ScanArraySize() {
        var depth = 0;

        for (var n = 0;; n++) {
            switch (PeekKind(n)) {
                case RavenTokenKind.OpenBracket:
                    depth++;
                    break;

                case RavenTokenKind.CloseBracket:
                    if (--depth == 0) {
                        return n > 1;
                    }

                    break;

                case RavenTokenKind.NewLine or RavenTokenKind.EndOfFile
                    or RavenTokenKind.OpenBrace or RavenTokenKind.CloseBrace:
                    return false;
            }
        }
    }

    bool ScanTypeArgumentList() {
        var mark = RawPosition;
        var ok = ScanBalancedAngles(ScanTypeArgumentListCore);
        ResetTo(mark);
        return ok;
    }

    /// <summary>
    ///     Runs a speculative type scan with its own budget of unmatched <c>&gt;</c>s, and only
    ///     accepts it if the budget came back to zero.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <c>&gt;&gt;</c> may stand for the two <c>&gt;</c>s that close
    ///         <c>Buffer&lt;Buffer&lt;float&gt;&gt;</c> — the lexer took the longer token because
    ///         nothing at that level told it not to. The inner list consumes the token and leaves a
    ///         credit for its second half; an enclosing list spends it.
    ///     </para>
    ///     <para>
    ///         Requiring the credit to be spent — by this scan, or by a list the parser has
    ///         already opened around it — is what keeps this from swallowing shifts. In
    ///         <c>a &lt; b &gt;&gt; c</c> there is neither, so the credit survives, the scan is
    ///         rejected, and the expression stays the comparison it was. That is the same rule C#
    ///         applies, arrived at from the other side.
    ///     </para>
    /// </remarks>
    bool ScanBalancedAngles(Func<bool> scan) {
        var saved = pendingCloseAngles;
        pendingCloseAngles = 0;

        var ok = scan() && (pendingCloseAngles == 0 || typeArgumentDepth > 0);

        pendingCloseAngles = saved;
        return ok;
    }

    /// <summary>Whether the scan is positioned on a <c>&gt;</c>, counting one owed by a split.</summary>
    bool AtCloseAngle => pendingCloseAngles > 0 || At(RavenTokenKind.GreaterThan);

    /// <summary>Consumes a <c>&gt;</c>, taking half of a <c>&gt;&gt;</c> and owing the other half.</summary>
    bool ScanCloseAngle() {
        if (pendingCloseAngles > 0) {
            pendingCloseAngles--;
            return true;
        }

        if (At(RavenTokenKind.GreaterThan)) {
            Advance();
            return true;
        }

        // `>>` and `>>>` are the only tokens that can be shared, because every character of
        // them is a `>`. A `>=` is not: its tail is not something an enclosing list could take,
        // so splitting it would turn `a < b >= c` into a generic name and an assignment.
        if (At(RavenTokenKind.GreaterThanGreaterThan)) {
            Advance();
            pendingCloseAngles += 1;
            return true;
        }

        if (At(RavenTokenKind.UnsignedShiftRight)) {
            Advance();
            pendingCloseAngles += 2;
            return true;
        }

        return false;
    }

    bool ScanTypeArgumentListCore() {
        if (!At(RavenTokenKind.LessThan)) {
            return false;
        }

        Advance();
        if (AtCloseAngle) {
            return ScanCloseAngle();
        }

        if (!ScanTypeCore(true)) {
            return false;
        }

        while (At(RavenTokenKind.Comma)) {
            Advance();
            if (!ScanTypeCore(true)) {
                return false;
            }
        }

        return ScanCloseAngle();
    }

    /// <summary>
    ///     Advances over one <c>type</c> production without building anything, as the outermost
    ///     scan: every <c>&gt;</c> it consumed has to have closed a list it opened.
    /// </summary>
    /// <param name="allowSizes">
    ///     Must be false wherever the scan is deciding <em>whether</em> this is a type at all
    ///     against an expression reading — a cast — and true wherever a type is already certain and
    ///     the scan only has to get past it.
    /// </param>
    bool TryScanType(bool allowSizes) => ScanBalancedAngles(() => ScanTypeCore(allowSizes));

    /// <summary>
    ///     The recursive half of <see cref="TryScanType" />, sharing the enclosing scan's
    ///     close-angle budget.
    /// </summary>
    bool ScanTypeCore(bool allowSizes) {
        if (IsPredefinedTypeKeyword(Kind)) {
            Advance();
        } else if (At(RavenTokenKind.Identifier)) {
            Advance();
            if (At(RavenTokenKind.LessThan) && !ScanTypeArgumentListCore()) {
                return false;
            }

            while (At(RavenTokenKind.Dot)) {
                Advance();
                if (!At(RavenTokenKind.Identifier)) {
                    return false;
                }

                Advance();
                if (At(RavenTokenKind.LessThan) && !ScanTypeArgumentListCore()) {
                    return false;
                }
            }
        } else if (At(RavenTokenKind.OpenParen)) {
            Advance();
            if (!TryScanTupleElement()) {
                return false;
            }

            var elements = 1;
            while (At(RavenTokenKind.Comma)) {
                Advance();
                if (!TryScanTupleElement()) {
                    return false;
                }

                elements++;
            }

            if (elements < 2 || !At(RavenTokenKind.CloseParen)) {
                return false;
            }

            Advance();
        } else {
            return false;
        }

        // Balanced rather than token-by-token, because a size is an expression.
        while (At(RavenTokenKind.OpenBracket) && ScanArrayRank(allowSizes)) {
            if (!TryScanBalanced(RavenTokenKind.OpenBracket, RavenTokenKind.CloseBracket)) {
                return false;
            }
        }

        return true;
    }

    bool TryScanTupleElement() {
        if (At(RavenTokenKind.Identifier) && PeekKind(1) == RavenTokenKind.Colon) {
            Advance();
            Advance();
        }

        return ScanTypeCore(true);
    }

    // ================================================================== Attributes

    /// <summary>Grammar <c>(attribute_list NL*)*</c> — the form declarations use.</summary>
    SyntaxList<AttributeListSyntax> ParseAttributeListsWithNewlines() {
        List<SyntaxNode?> lists = [];
        while (At(RavenTokenKind.OpenBracket) && ScanAttributeList()) {
            lists.Add(ParseAttributeList());
            SkipNewLines();
        }

        return new(SyntaxList.List(lists.ToArray()));
    }

    /// <summary>Grammar <c>attribute_list*</c> — the inline form a parameter uses.</summary>
    SyntaxList<AttributeListSyntax> ParseInlineAttributeLists() {
        List<SyntaxNode?> lists = [];
        while (At(RavenTokenKind.OpenBracket) && ScanAttributeList()) {
            lists.Add(ParseAttributeList());
        }

        return new(SyntaxList.List(lists.ToArray()));
    }

    AttributeListSyntax ParseAttributeList() {
        var open = Take(SyntaxKind.OpenBracketToken);

        List<SyntaxNode?> attributes = [ParseAttribute()];
        List<SyntaxToken> commas = [];
        while (At(RavenTokenKind.Comma)) {
            commas.Add(Take(SyntaxKind.CommaToken));
            attributes.Add(ParseAttribute());
        }

        var close = Expect(RavenTokenKind.CloseBracket, SyntaxKind.CloseBracketToken);
        return (AttributeListSyntax)SyntaxFactory.AttributeList(
            open,
            Separated<AttributeSyntax>(attributes, commas),
            close
        );
    }

    AttributeSyntax ParseAttribute() {
        var name = ParseName();
        AttributeArgumentListSyntax? arguments = null;
        if (At(RavenTokenKind.OpenParen)) {
            arguments = ParseAttributeArgumentList();
        }

        return (AttributeSyntax)SyntaxFactory.Attribute(name, arguments);
    }

    AttributeArgumentListSyntax ParseAttributeArgumentList() {
        var open = Take(SyntaxKind.OpenParenToken);

        List<SyntaxNode?> arguments = [];
        List<SyntaxToken> commas = [];
        if (!At(RavenTokenKind.CloseParen)) {
            arguments.Add(ParseAttributeArgument());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                arguments.Add(ParseAttributeArgument());
            }
        }

        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        return (AttributeArgumentListSyntax)SyntaxFactory.AttributeArgumentList(
            open,
            Separated<AttributeArgumentSyntax>(arguments, commas),
            close
        );
    }

    AttributeArgumentSyntax ParseAttributeArgument() {
        var nameColon = AtNameColon ? ParseNameColon() : null;
        var expression = ParseExpression();
        return (AttributeArgumentSyntax)SyntaxFactory.AttributeArgument(nameColon, expression);
    }

    bool AtNameColon => At(RavenTokenKind.Identifier) && PeekKind(1) == RavenTokenKind.Colon;

    NameColonSyntax ParseNameColon() {
        var name = ParseIdentifierName();
        var colon = Take(SyntaxKind.ColonToken);
        return (NameColonSyntax)SyntaxFactory.NameColon(name, colon);
    }

    /// <summary>Whether a well-formed attribute list starts here — versus a collection expression.</summary>
    bool ScanAttributeList() {
        var mark = RawPosition;
        var ok = TryScanAttributeList();
        ResetTo(mark);
        return ok;
    }

    bool TryScanAttributeList() {
        Advance();

        while (true) {
            if (!At(RavenTokenKind.Identifier)) {
                return false;
            }

            if (!TryScanType(true)) {
                return false;
            }

            if (At(RavenTokenKind.OpenParen)) {
                if (!TryScanBalanced(RavenTokenKind.OpenParen, RavenTokenKind.CloseParen)) {
                    return false;
                }
            }

            if (At(RavenTokenKind.Comma)) {
                Advance();
                continue;
            }

            return At(RavenTokenKind.CloseBracket);
        }
    }

    bool TryScanBalanced(RavenTokenKind open, RavenTokenKind close) {
        var depth = 0;
        while (!AtEnd && !AtNewLine) {
            if (At(open)) {
                depth++;
            } else if (At(close)) {
                depth--;
                if (depth == 0) {
                    Advance();
                    return true;
                }
            }

            Advance();
        }

        return false;
    }

    // ================================================================== Members

    static readonly Dictionary<RavenTokenKind, SyntaxKind> ModifierKinds = new() {
        [RavenTokenKind.ComposeKeyword] = SyntaxKind.ComposeKeyword,
        [RavenTokenKind.ConstKeyword] = SyntaxKind.ConstKeyword,
        [RavenTokenKind.OverrideKeyword] = SyntaxKind.OverrideKeyword,
        [RavenTokenKind.ReadOnlyKeyword] = SyntaxKind.ReadOnlyKeyword,
        [RavenTokenKind.StaticKeyword] = SyntaxKind.StaticKeyword,
        [RavenTokenKind.StreamKeyword] = SyntaxKind.StreamKeyword,
        [RavenTokenKind.GroupSharedKeyword] = SyntaxKind.GroupSharedKeyword
    };

    SyntaxList<SyntaxToken> ParseModifiers() {
        List<SyntaxNode?> modifiers = [];
        while (ModifierKinds.TryGetValue(Kind, out var treeKind)) {
            modifiers.Add(Take(treeKind));
        }

        return new(SyntaxList.List(modifiers.ToArray()));
    }

    /// <summary>
    ///     Incremental reparse (docs/plan/18 step 7): at a member boundary, take the
    ///     previous tree's green node when the blender has one whose new position and
    ///     width line up exactly with the token stream. Any mismatch falls through to
    ///     a normal parse, so reuse can only ever skip work, not change the tree.
    /// </summary>
    MemberDeclarationSyntax? TryReuseMember() {
        if (blender is null) {
            return null;
        }

        // The candidate's full span starts at its leading trivia, so the lookup
        // position is where the pending trivia run begins.
        var firstTrivia = RawPosition;
        while (firstTrivia > 0 && IsTriviaLike(firstTrivia - 1)) {
            firstTrivia--;
        }

        var fullStart = Tokens[firstTrivia].Position;

        // ⚠ The blender is handed the new token stream, not just the position. Characters no change
        // touched can still lex differently once something next door has changed, and a span test
        // cannot see the difference — so the blender compares the two streams kind for kind before
        // it hands anything over, and returns the index to resume at, which subsumes the boundary
        // check this used to do for itself.
        //
        // ⚠ And it is handed the context, because neither test can see the difference between the
        // same tokens read by two grammars. `ParseMemberDeclaration` is what runs here, so
        // `MemberList` is what may come back — an enum member lexes exactly as it did when its
        // `enum` header was still above it, and splicing one in here is a tree no full parse builds.
        if (blender.TryReuse(ReuseContext.MemberList, fullStart, Tokens, out var next) is not { } green) {
            return null;
        }

        // ⚠ `ResumeAt`, not `ResetTo`: the token starting where a reused member ends is the newline
        // after it, which is trivia. This grammar happens to recover — the caller skips newlines
        // immediately — so nothing here was ever wrong; VXML's does not, which is where the
        // difference between the two was found.
        ResumeAt(next);
        return green.CreateRed(null, 0) as MemberDeclarationSyntax;
    }

    MemberDeclarationSyntax? ParseMemberDeclaration() {
        var rawStart = RawPosition;
        var attributes = ParseAttributeListsWithNewlines();
        var modifiers = ParseModifiers();

        switch (Kind) {
            case RavenTokenKind.FuncKeyword:
                return ParseMethodDeclaration(attributes, modifiers);

            case RavenTokenKind.InitKeyword:
                return ParseConstructorDeclaration(attributes, modifiers);

            case RavenTokenKind.VarKeyword when ScanPropertyShape():
                return ParsePropertyDeclaration(attributes, modifiers);

            case RavenTokenKind.VarKeyword or RavenTokenKind.ValKeyword:
                return ParseFieldDeclaration(attributes, modifiers);

            case RavenTokenKind.ShaderKeyword:
                return ParseTypeDeclaration(attributes, modifiers, SyntaxKind.ShaderKeyword);

            case RavenTokenKind.StructKeyword:
                return ParseTypeDeclaration(attributes, modifiers, SyntaxKind.StructKeyword);

            case RavenTokenKind.ProtocolKeyword:
                return ParseTypeDeclaration(attributes, modifiers, SyntaxKind.ProtocolKeyword);

            case RavenTokenKind.EnumKeyword:
                return ParseEnumDeclaration(attributes, modifiers);

            default:
                // The only remaining member form starts with a type: an operator
                // declaration. Anything else is not a member — surrender the tokens
                // consumed so far back to trivia and let the caller skip the line
                // with a single diagnostic, instead of cascading one per token.
                if (ScanOperatorDeclaration()) {
                    return ParseOperatorDeclaration(attributes, modifiers);
                }

                for (var i = rawStart; i < RawPosition; i++) {
                    skipped.Add(i);
                }

                return null;
        }
    }

    bool ScanOperatorDeclaration() {
        var mark = RawPosition;
        var ok = TryScanType(true) && At(RavenTokenKind.OperatorKeyword);
        ResetTo(mark);
        return ok;
    }

    /// <summary>
    ///     <c>var</c> starts a property only when an accessor list or an arrow
    ///     follows the name and optional type; an initializer or a bare newline is a
    ///     field, which is the alternative the grammar lists first.
    /// </summary>
    bool ScanPropertyShape() {
        var mark = RawPosition;
        Advance();

        var shape = false;
        if (At(RavenTokenKind.Identifier)) {
            Advance();
            if (At(RavenTokenKind.Colon)) {
                Advance();
                if (!TryScanType(true)) {
                    ResetTo(mark);
                    return false;
                }
            }

            shape = At(RavenTokenKind.OpenBrace) || At(RavenTokenKind.Arrow);
        }

        ResetTo(mark);
        return shape;
    }

    FieldDeclarationSyntax ParseFieldDeclaration(
        SyntaxList<AttributeListSyntax> attributes,
        SyntaxList<SyntaxToken> modifiers
    ) {
        var declaration = ParseVariableDeclaration();
        ExpectNewLines();
        return (FieldDeclarationSyntax)SyntaxFactory.FieldDeclaration(attributes, modifiers, declaration);
    }

    VariableDeclarationSyntax ParseVariableDeclaration() {
        var isVar = At(RavenTokenKind.VarKeyword);
        var kind = isVar ? SyntaxKind.VariableDeclaration : SyntaxKind.ConstDeclaration;
        var keyword = isVar
            ? Expect(RavenTokenKind.VarKeyword, SyntaxKind.VarKeyword)
            : Expect(RavenTokenKind.ValKeyword, SyntaxKind.ValKeyword);
        var identifier = ExpectIdentifier();

        SyntaxToken? colon = null;
        TypeSyntax? type = null;
        if (At(RavenTokenKind.Colon)) {
            colon = Take(SyntaxKind.ColonToken);
            type = ParseType();
        }

        var initializer = At(RavenTokenKind.Equals) ? ParseEqualsValueClause() : null;
        return (VariableDeclarationSyntax)SyntaxFactory.VariableDeclaration(
            kind,
            keyword,
            identifier,
            colon,
            type,
            initializer
        );
    }

    EqualsValueClauseSyntax ParseEqualsValueClause() {
        var equals = Take(SyntaxKind.EqualsToken);
        var value = ParseExpression();
        return (EqualsValueClauseSyntax)SyntaxFactory.EqualsValueClause(equals, value);
    }

    ArrowExpressionClauseSyntax ParseArrowExpressionClause() {
        var arrow = Take(SyntaxKind.ArrowToken);
        var expression = ParseExpression();
        return (ArrowExpressionClauseSyntax)SyntaxFactory.ArrowExpressionClause(arrow, expression);
    }

    MethodDeclarationSyntax ParseMethodDeclaration(
        SyntaxList<AttributeListSyntax> attributes,
        SyntaxList<SyntaxToken> modifiers
    ) {
        var keyword = Take(SyntaxKind.FuncKeyword);
        var identifier = ExpectIdentifier();
        var typeParameters = At(RavenTokenKind.LessThan) ? ParseTypeParameterList() : null;
        var parameters = ParseParameterList();
        var constraints = ParseConstraintClauses();

        SyntaxToken? colon = null;
        TypeSyntax? returnType = null;
        if (At(RavenTokenKind.Colon)) {
            colon = Take(SyntaxKind.ColonToken);
            returnType = ParseType();
        }

        var (body, expressionBody) = ParseOptionalBody();

        return (MethodDeclarationSyntax)SyntaxFactory.MethodDeclaration(
            attributes,
            modifiers,
            keyword,
            identifier,
            typeParameters,
            parameters,
            constraints,
            colon,
            returnType,
            body,
            expressionBody
        );
    }

    ConstructorDeclarationSyntax ParseConstructorDeclaration(
        SyntaxList<AttributeListSyntax> attributes,
        SyntaxList<SyntaxToken> modifiers
    ) {
        var keyword = Take(SyntaxKind.InitKeyword);
        var parameters = ParseParameterList();
        var (body, expressionBody) = ParseRequiredBody();

        return (ConstructorDeclarationSyntax)SyntaxFactory.ConstructorDeclaration(
            attributes,
            modifiers,
            keyword,
            parameters,
            body,
            expressionBody
        );
    }

    static readonly Dictionary<RavenTokenKind, bool> OperatorDeclarationOps = new() {
        [RavenTokenKind.Plus] = true,
        [RavenTokenKind.Minus] = true,
        [RavenTokenKind.Bang] = true,
        [RavenTokenKind.Tilde] = true,
        [RavenTokenKind.PlusPlus] = true,
        [RavenTokenKind.MinusMinus] = true,
        [RavenTokenKind.Star] = true,
        [RavenTokenKind.Slash] = true,
        [RavenTokenKind.Percent] = true,
        [RavenTokenKind.LessThanLessThan] = true,
        [RavenTokenKind.GreaterThanGreaterThan] = true,
        [RavenTokenKind.UnsignedShiftRight] = true,
        [RavenTokenKind.Bar] = true,
        [RavenTokenKind.Ampersand] = true,
        [RavenTokenKind.Caret] = true,
        [RavenTokenKind.EqualsEquals] = true,
        [RavenTokenKind.BangEquals] = true,
        [RavenTokenKind.LessThan] = true,
        [RavenTokenKind.LessThanEquals] = true,
        [RavenTokenKind.GreaterThan] = true,
        [RavenTokenKind.GreaterThanEquals] = true
    };

    OperatorDeclarationSyntax ParseOperatorDeclaration(
        SyntaxList<AttributeListSyntax> attributes,
        SyntaxList<SyntaxToken> modifiers
    ) {
        var type = ParseType();
        var operatorKeyword = Expect(RavenTokenKind.OperatorKeyword, SyntaxKind.OperatorKeyword);

        SyntaxToken operatorToken;
        if (OperatorDeclarationOps.ContainsKey(Kind)) {
            operatorToken = Take(SyntaxKind.OperatorToken);
        } else {
            ReportExpected("an operator");
            operatorToken = Missing(SyntaxKind.OperatorToken);
        }

        var parameters = ParseParameterList();
        var (body, expressionBody) = ParseRequiredBody();

        return (OperatorDeclarationSyntax)SyntaxFactory.OperatorDeclaration(
            attributes,
            modifiers,
            type,
            operatorKeyword,
            operatorToken,
            parameters,
            body,
            expressionBody
        );
    }

    PropertyDeclarationSyntax ParsePropertyDeclaration(
        SyntaxList<AttributeListSyntax> attributes,
        SyntaxList<SyntaxToken> modifiers
    ) {
        var keyword = Take(SyntaxKind.VarKeyword);
        var identifier = ExpectIdentifier();

        SyntaxToken? colon = null;
        TypeSyntax? type = null;
        if (At(RavenTokenKind.Colon)) {
            colon = Take(SyntaxKind.ColonToken);
            type = ParseType();
        }

        AccessorListSyntax? accessorList = null;
        ArrowExpressionClauseSyntax? expressionBody = null;
        if (At(RavenTokenKind.OpenBrace)) {
            accessorList = ParseAccessorList();
        } else if (At(RavenTokenKind.Arrow)) {
            expressionBody = ParseArrowExpressionClause();
            ExpectSingleNewLine();
        }

        return (PropertyDeclarationSyntax)SyntaxFactory.PropertyDeclaration(
            attributes,
            modifiers,
            keyword,
            identifier,
            colon,
            type,
            accessorList,
            expressionBody,
            null
        );
    }

    AccessorListSyntax ParseAccessorList() {
        var open = Take(SyntaxKind.OpenBraceToken);
        SkipNewLines();

        List<SyntaxNode?> accessors = [];
        while (Kind is RavenTokenKind.GetKeyword
               or RavenTokenKind.SetKeyword
               or RavenTokenKind.WillSetKeyword
               or RavenTokenKind.DidSetKeyword
               or RavenTokenKind.OpenBracket) {
            // ⚠ The no-progress guard the member and statement loops all keep, and the one loop that
            // did not. OpenBracket is in the set above because an accessor may carry attributes, but
            // whether the bracket *is* an attribute list is decided further down by ScanAttributeList,
            // which resets the position when it says no — and every step under it then fabricates
            // rather than consumes. So `var t{[` left the bracket exactly where it was and this loop
            // added a fabricated accessor for it until the machine ran out of memory. Seven
            // characters, and nothing that measures a parse afterwards can see it, because the parse
            // does not finish.
            var before = RawPosition;
            var accessor = ParseAccessorDeclaration();

            if (RawPosition == before) {
                SkipToLineEnd();
                SkipNewLines();

                continue;
            }

            accessors.Add(accessor);
            SkipNewLines();
        }

        var close = Expect(RavenTokenKind.CloseBrace, SyntaxKind.CloseBraceToken);
        return (AccessorListSyntax)SyntaxFactory.AccessorList(open, new(SyntaxList.List(accessors.ToArray())), close);
    }

    AccessorDeclarationSyntax ParseAccessorDeclaration() {
        var attributes = ParseAttributeListsWithNewlines();

        var (kind, tokenKind) = Kind switch {
            RavenTokenKind.SetKeyword => (SyntaxKind.SetAccessorDeclaration, SyntaxKind.SetKeyword),
            RavenTokenKind.WillSetKeyword => (SyntaxKind.WillSetAccessorDeclaration, SyntaxKind.WillSetKeyword),
            RavenTokenKind.DidSetKeyword => (SyntaxKind.DidSetAccessorDeclaration, SyntaxKind.DidSetKeyword),
            _ => (SyntaxKind.GetAccessorDeclaration, SyntaxKind.GetKeyword)
        };
        var keyword = Kind is RavenTokenKind.GetKeyword
            or RavenTokenKind.SetKeyword
            or RavenTokenKind.WillSetKeyword
            or RavenTokenKind.DidSetKeyword
            ? Take(tokenKind)
            : Missing(tokenKind);

        BlockSyntax? body = null;
        ArrowExpressionClauseSyntax? expressionBody = null;
        if (At(RavenTokenKind.OpenBrace)) {
            body = ParseBlock();
        } else if (At(RavenTokenKind.Arrow)) {
            expressionBody = ParseArrowExpressionClause();
            ExpectSingleNewLine();
        } else {
            ReportExpected("an accessor body");
        }

        return (AccessorDeclarationSyntax)SyntaxFactory.AccessorDeclaration(
            kind,
            attributes,
            keyword,
            body,
            expressionBody
        );
    }

    (BlockSyntax? Body, ArrowExpressionClauseSyntax? ExpressionBody) ParseOptionalBody() {
        if (At(RavenTokenKind.OpenBrace)) {
            return (ParseBlock(), null);
        }

        if (At(RavenTokenKind.Arrow)) {
            var arrow = ParseArrowExpressionClause();
            ExpectSingleNewLine();
            return (null, arrow);
        }

        return (null, null);
    }

    (BlockSyntax? Body, ArrowExpressionClauseSyntax? ExpressionBody) ParseRequiredBody() {
        var (body, expressionBody) = ParseOptionalBody();
        if (body is null && expressionBody is null) {
            ReportExpected("a body");
        }

        return (body, expressionBody);
    }

    // ------------------------------------------------------------ Type declarations

    MemberDeclarationSyntax ParseTypeDeclaration(
        SyntaxList<AttributeListSyntax> attributes,
        SyntaxList<SyntaxToken> modifiers,
        SyntaxKind keywordKind
    ) {
        var keyword = Take(keywordKind);
        var identifier = ExpectIdentifier();
        var typeParameters = At(RavenTokenKind.LessThan) ? ParseTypeParameterList() : null;
        var baseList = At(RavenTokenKind.Colon) ? ParseBaseList() : null;
        var constraints = ParseConstraintClauses();

        SyntaxToken? openBrace = null;
        SyntaxList<MemberDeclarationSyntax> members = new();
        SyntaxToken? closeBrace = null;
        if (At(RavenTokenKind.OpenBrace)) {
            openBrace = Take(SyntaxKind.OpenBraceToken);
            SkipNewLines();

            List<SyntaxNode?> parsed = [];
            while (!At(RavenTokenKind.CloseBrace) && !AtEnd) {
                var before = RawPosition;
                var member = TryReuseMember() ?? ParseMemberDeclaration();
                if (member is null || RawPosition == before) {
                    SkipToLineEnd();
                    SkipNewLines();
                    continue;
                }

                parsed.Add(member);
                SkipNewLines();
            }

            members = new(SyntaxList.List(parsed.ToArray()));
            closeBrace = Expect(RavenTokenKind.CloseBrace, SyntaxKind.CloseBraceToken);
        }

        ExpectSingleNewLine();

        return keywordKind switch {
            SyntaxKind.ShaderKeyword => (MemberDeclarationSyntax)SyntaxFactory.ShaderDeclaration(
                attributes, modifiers, keyword, identifier, typeParameters, baseList, constraints,
                openBrace, members, closeBrace
            ),
            SyntaxKind.StructKeyword => (MemberDeclarationSyntax)SyntaxFactory.StructDeclaration(
                attributes, modifiers, keyword, identifier, typeParameters, baseList, constraints,
                openBrace, members, closeBrace
            ),
            _ => (MemberDeclarationSyntax)SyntaxFactory.ProtocolDeclaration(
                attributes, modifiers, keyword, identifier, typeParameters, baseList, constraints,
                openBrace, members, closeBrace
            )
        };
    }

    EnumDeclarationSyntax ParseEnumDeclaration(
        SyntaxList<AttributeListSyntax> attributes,
        SyntaxList<SyntaxToken> modifiers
    ) {
        var keyword = Take(SyntaxKind.EnumKeyword);
        var identifier = ExpectIdentifier();
        var baseList = At(RavenTokenKind.Colon) ? ParseBaseList() : null;

        var openBrace = Expect(RavenTokenKind.OpenBrace, SyntaxKind.OpenBraceToken);
        SkipNewLines();

        List<SyntaxNode?> members = [];
        List<SyntaxToken> commas = [];
        if (!At(RavenTokenKind.CloseBrace) && !AtEnd) {
            members.Add(ParseEnumMemberDeclaration());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                SkipNewLines();
                members.Add(ParseEnumMemberDeclaration());
            }
        }

        SkipNewLines();
        var closeBrace = Expect(RavenTokenKind.CloseBrace, SyntaxKind.CloseBraceToken);
        ExpectSingleNewLine();

        return (EnumDeclarationSyntax)SyntaxFactory.EnumDeclaration(
            attributes,
            modifiers,
            keyword,
            identifier,
            baseList,
            openBrace,
            Separated<EnumMemberDeclarationSyntax>(members, commas),
            closeBrace
        );
    }

    EnumMemberDeclarationSyntax ParseEnumMemberDeclaration() {
        var attributes = ParseAttributeListsWithNewlines();
        var identifier = ExpectIdentifier();
        var value = At(RavenTokenKind.Equals) ? ParseEqualsValueClause() : null;
        return (EnumMemberDeclarationSyntax)SyntaxFactory.EnumMemberDeclaration(attributes, new(), identifier, value);
    }

    TypeParameterListSyntax ParseTypeParameterList() {
        var lessThan = Take(SyntaxKind.LessThanToken);

        List<SyntaxNode?> parameters = [ParseTypeParameter()];
        List<SyntaxToken> commas = [];
        while (At(RavenTokenKind.Comma)) {
            commas.Add(Take(SyntaxKind.CommaToken));
            parameters.Add(ParseTypeParameter());
        }

        var greaterThan = Expect(RavenTokenKind.GreaterThan, SyntaxKind.GreaterThanToken);
        return (TypeParameterListSyntax)SyntaxFactory.TypeParameterList(
            lessThan,
            Separated<TypeParameterSyntax>(parameters, commas),
            greaterThan
        );
    }

    TypeParameterSyntax ParseTypeParameter() {
        var attributes = ParseAttributeListsWithNewlines();

        SyntaxToken? val = null;
        SyntaxToken? colon = null;
        TypeSyntax? type = null;

        if (At(RavenTokenKind.ValKeyword)) {
            val = Take(SyntaxKind.ValKeyword);
        }

        var identifier = ExpectIdentifier();

        if (val is not null) {
            colon = Expect(RavenTokenKind.Colon, SyntaxKind.ColonToken);
            type = ParseType();
        }

        return (TypeParameterSyntax)SyntaxFactory.TypeParameter(attributes, val, identifier, colon, type);
    }

    SyntaxList<TypeParameterConstraintClauseSyntax> ParseConstraintClauses() {
        List<SyntaxNode?> clauses = [];
        while (At(RavenTokenKind.WhereKeyword)) {
            clauses.Add(ParseConstraintClause());
        }

        return new(SyntaxList.List(clauses.ToArray()));
    }

    TypeParameterConstraintClauseSyntax ParseConstraintClause() {
        var where = Take(SyntaxKind.WhereKeyword);
        var name = ParseIdentifierName();
        var colon = Expect(RavenTokenKind.Colon, SyntaxKind.ColonToken);

        List<SyntaxNode?> constraints = [ParseConstraint()];
        List<SyntaxToken> commas = [];
        while (At(RavenTokenKind.Comma)) {
            commas.Add(Take(SyntaxKind.CommaToken));
            constraints.Add(ParseConstraint());
        }

        return (TypeParameterConstraintClauseSyntax)SyntaxFactory.TypeParameterConstraintClause(
            where,
            name,
            colon,
            Separated<TypeParameterConstraintSyntax>(constraints, commas)
        );
    }

    TypeParameterConstraintSyntax ParseConstraint() {
        if (At(RavenTokenKind.DefaultKeyword)) {
            return (TypeParameterConstraintSyntax)SyntaxFactory.DefaultConstraint(Take(SyntaxKind.DefaultKeyword));
        }

        return (TypeParameterConstraintSyntax)SyntaxFactory.TypeConstraint(ParseType());
    }

    BaseListSyntax ParseBaseList() {
        var colon = Take(SyntaxKind.ColonToken);

        List<SyntaxNode?> types = [ParseSimpleBaseType()];
        List<SyntaxToken> commas = [];
        while (At(RavenTokenKind.Comma)) {
            commas.Add(Take(SyntaxKind.CommaToken));
            types.Add(ParseSimpleBaseType());
        }

        return (BaseListSyntax)SyntaxFactory.BaseList(colon, Separated<BaseTypeSyntax>(types, commas));
    }

    SimpleBaseTypeSyntax ParseSimpleBaseType() =>
        (SimpleBaseTypeSyntax)SyntaxFactory.SimpleBaseType(ParseType());

    /// <summary>
    ///     <c>parameter_list</c>: <c>'(' NL* (parameter (',' NL* parameter)* NL*)? ')'</c>.
    /// </summary>
    /// <remarks>
    ///     The newlines are layout rather than terminators, in the three positions a wide
    ///     signature is broken at — after the <c>(</c>, after each <c>,</c> and before the
    ///     <c>)</c>. They stay out of the recovery loop's stop set: a newline there is still
    ///     what keeps an unclosed list from eating the rest of the file.
    /// </remarks>
    ParameterListSyntax ParseParameterList() {
        var open = Expect(RavenTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        SkipNewLines();

        // Recovery: something that can neither start a parameter nor close the list
        // is skipped once, with one diagnostic pointing at it; the close paren is
        // then fabricated silently rather than reported a second time.
        var recovered = false;
        while (!At(RavenTokenKind.CloseParen)
               && !AtNewLine
               && !AtEnd
               && !At(RavenTokenKind.OpenBrace)
               && !At(RavenTokenKind.Arrow)
               && !AtParameterStart) {
            SkipCurrent();
            recovered = true;
        }

        List<SyntaxNode?> parameters = [];
        List<SyntaxToken> commas = [];
        if (AtParameterStart) {
            parameters.Add(ParseParameter());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                SkipNewLines();
                parameters.Add(ParseParameter());
            }

            SkipNewLines();
        }

        SyntaxToken close;
        if (At(RavenTokenKind.CloseParen)) {
            close = Take(SyntaxKind.CloseParenToken);
        } else {
            if (!recovered) {
                ReportExpected("')'");
            }

            close = Missing(SyntaxKind.CloseParenToken);
        }

        return (ParameterListSyntax)SyntaxFactory.ParameterList(
            open,
            Separated<ParameterSyntax>(parameters, commas),
            close
        );
    }

    /// <summary>
    ///     Direction modifiers a parameter may carry, after its attributes and before its name.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="ModifierKinds" /> — a member's modifiers and a parameter's are
    ///     disjoint sets, and sharing the table would let `static` onto a parameter and `inout` onto
    ///     a field, both of which the parser would then have to un-accept later.
    /// </remarks>
    static readonly Dictionary<RavenTokenKind, SyntaxKind> ParameterModifierKinds = new() {
        [RavenTokenKind.InOutKeyword] = SyntaxKind.InOutKeyword
    };

    /// <summary>
    ///     Whether the current token could begin a parameter: its name, its attributes, or a
    ///     direction modifier.
    /// </summary>
    /// <remarks>
    ///     One predicate because the recovery loop and the entry test have to agree — they were two
    ///     copies of the same list, and adding <c>inout</c> to only one of them made a parameter list
    ///     that recovered past the modifier and then reported the name it had skipped to.
    /// </remarks>
    bool AtParameterStart =>
        At(RavenTokenKind.Identifier)
        || At(RavenTokenKind.OpenBracket)
        || ParameterModifierKinds.ContainsKey(Kind);

    ParameterSyntax ParseParameter() {
        var attributes = ParseInlineAttributeLists();

        List<SyntaxNode?> modifiers = [];
        while (ParameterModifierKinds.TryGetValue(Kind, out var treeKind)) {
            modifiers.Add(Take(treeKind));
        }

        var identifier = ExpectIdentifier();

        SyntaxToken? colon = null;
        TypeSyntax? type = null;
        if (At(RavenTokenKind.Colon)) {
            colon = Take(SyntaxKind.ColonToken);
            type = ParseType();
        }

        var @default = At(RavenTokenKind.Equals) ? ParseEqualsValueClause() : null;

        return (ParameterSyntax)SyntaxFactory.Parameter(
            attributes,
            new(SyntaxList.List(modifiers.ToArray())),
            identifier,
            colon,
            type,
            @default
        );
    }

    // ================================================================== Statements

    BlockSyntax ParseBlock() {
        var open = Expect(RavenTokenKind.OpenBrace, SyntaxKind.OpenBraceToken);
        SkipNewLines();

        List<SyntaxNode?> statements = [];
        while (!At(RavenTokenKind.CloseBrace) && !AtEnd) {
            var before = RawPosition;
            statements.Add(ParseStatement());
            if (RawPosition == before) {
                SkipToLineEnd();
            }
        }

        var close = Expect(RavenTokenKind.CloseBrace, SyntaxKind.CloseBraceToken);
        return (BlockSyntax)SyntaxFactory.Block(new(), open, new(SyntaxList.List(statements.ToArray())), close);
    }

    StatementSyntax ParseStatement() {
        // A bare newline run is a zero-width empty statement, matching how the
        // grammar's greedy statement loop consumed blank lines.
        if (AtNewLine) {
            SkipNewLines();
            return (StatementSyntax)SyntaxFactory.EmptyStatement(new());
        }

        SyntaxList<AttributeListSyntax> attributes = new();
        if (At(RavenTokenKind.OpenBracket) && ScanAttributeList()) {
            List<SyntaxNode?> lists = [];
            var newlineAfterLast = false;
            while (At(RavenTokenKind.OpenBracket) && ScanAttributeList()) {
                lists.Add(ParseAttributeList());
                newlineAfterLast = AtNewLine;
                SkipNewLines();
            }

            attributes = new(SyntaxList.List(lists.ToArray()));

            // The grammar's empty-statement alternative comes before every real
            // statement, so attributes with a line break after the last list are an
            // attributed empty statement — `[Unroll]` on its own line attaches to
            // nothing, and the statement below parses unattributed.
            if (newlineAfterLast || !StartsStatement(Kind)) {
                return (StatementSyntax)SyntaxFactory.EmptyStatement(attributes);
            }
        }

        switch (Kind) {
            case RavenTokenKind.OpenBrace:
                return ParseBlock();

            case RavenTokenKind.BreakKeyword: {
                var keyword = Take(SyntaxKind.BreakKeyword);
                ExpectNewLines();
                return (StatementSyntax)SyntaxFactory.BreakStatement(attributes, keyword);
            }

            case RavenTokenKind.ContinueKeyword: {
                var keyword = Take(SyntaxKind.ContinueKeyword);
                ExpectNewLines();
                return (StatementSyntax)SyntaxFactory.ContinueStatement(attributes, keyword);
            }

            case RavenTokenKind.DiscardKeyword: {
                var keyword = Take(SyntaxKind.DiscardKeyword);
                ExpectNewLines();
                return (StatementSyntax)SyntaxFactory.DiscardStatement(attributes, keyword);
            }

            case RavenTokenKind.RepeatKeyword:
                return ParseRepeatStatement(attributes);

            case RavenTokenKind.ForKeyword:
                return ParseForStatement(attributes);

            case RavenTokenKind.IfKeyword:
                return ParseIfStatement(attributes);

            case RavenTokenKind.ReturnKeyword:
                return ParseReturnStatement(attributes);

            case RavenTokenKind.SwitchKeyword:
                return ParseSwitchStatement(attributes);

            case RavenTokenKind.WhileKeyword:
                return ParseWhileStatement(attributes);

            case RavenTokenKind.VarKeyword or RavenTokenKind.ValKeyword: {
                var declaration = ParseVariableDeclaration();
                ExpectSingleNewLine();
                return (StatementSyntax)SyntaxFactory.LocalDeclarationStatement(attributes, declaration);
            }

            default: {
                var expression = ParseExpression();
                ExpectNewLines();
                return (StatementSyntax)SyntaxFactory.ExpressionStatement(attributes, expression);
            }
        }
    }

    static bool StartsStatement(RavenTokenKind kind) =>
        kind is RavenTokenKind.OpenBrace
            or RavenTokenKind.BreakKeyword
            or RavenTokenKind.ContinueKeyword
            or RavenTokenKind.DiscardKeyword
            or RavenTokenKind.RepeatKeyword
            or RavenTokenKind.ForKeyword
            or RavenTokenKind.IfKeyword
            or RavenTokenKind.ReturnKeyword
            or RavenTokenKind.SwitchKeyword
            or RavenTokenKind.WhileKeyword
            or RavenTokenKind.VarKeyword
            or RavenTokenKind.ValKeyword
        || StartsExpression(kind);

    RepeatStatementSyntax ParseRepeatStatement(SyntaxList<AttributeListSyntax> attributes) {
        var repeatKeyword = Take(SyntaxKind.RepeatKeyword);
        var statement = ParseStatement();
        var whileKeyword = Expect(RavenTokenKind.WhileKeyword, SyntaxKind.WhileKeyword);
        var open = Expect(RavenTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var condition = ParseExpression();
        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        ExpectNewLines();

        return (RepeatStatementSyntax)SyntaxFactory.RepeatStatement(
            attributes,
            repeatKeyword,
            statement,
            whileKeyword,
            open,
            condition,
            close
        );
    }

    ForStatementSyntax ParseForStatement(SyntaxList<AttributeListSyntax> attributes) {
        var forKeyword = Take(SyntaxKind.ForKeyword);
        var open = Expect(RavenTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var identifier = ExpectIdentifier();
        var inKeyword = Expect(RavenTokenKind.InKeyword, SyntaxKind.InKeyword);
        var expression = ParseExpression();
        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        var block = ParseBlock();

        return (ForStatementSyntax)SyntaxFactory.ForStatement(
            attributes,
            forKeyword,
            open,
            identifier,
            inKeyword,
            expression,
            close,
            block
        );
    }

    IfStatementSyntax ParseIfStatement(SyntaxList<AttributeListSyntax> attributes) {
        var ifKeyword = Take(SyntaxKind.IfKeyword);
        var open = Expect(RavenTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var condition = ParseExpression();
        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        var block = ParseBlock();

        ElseClauseSyntax? elseClause = null;
        if (At(RavenTokenKind.ElseKeyword)) {
            var elseKeyword = Take(SyntaxKind.ElseKeyword);

            // `else if` chains: the clause holds a statement, not a block, so the nested
            // `if` is the alternative directly rather than a block wrapping one. Nothing
            // downstream needs to know — the binder and the lowerer both take whatever
            // statement the clause carries. The nested `if` gets no attributes of its own:
            // there is no position to write them in, since they would attach to the outer
            // statement.
            StatementSyntax alternative = At(RavenTokenKind.IfKeyword)
                ? ParseIfStatement(default)
                : ParseBlock();

            elseClause = (ElseClauseSyntax)SyntaxFactory.ElseClause(elseKeyword, alternative);
        }

        return (IfStatementSyntax)SyntaxFactory.IfStatement(
            attributes,
            ifKeyword,
            open,
            condition,
            close,
            block,
            elseClause
        );
    }

    ReturnStatementSyntax ParseReturnStatement(SyntaxList<AttributeListSyntax> attributes) {
        var keyword = Take(SyntaxKind.ReturnKeyword);
        var expression = AtNewLine || AtEnd || At(RavenTokenKind.CloseBrace) ? null : ParseExpression();
        ExpectSingleNewLine();
        return (ReturnStatementSyntax)SyntaxFactory.ReturnStatement(attributes, keyword, expression);
    }

    WhileStatementSyntax ParseWhileStatement(SyntaxList<AttributeListSyntax> attributes) {
        var keyword = Take(SyntaxKind.WhileKeyword);
        var open = Expect(RavenTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var condition = ParseExpression();
        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        var statement = ParseStatement();

        return (WhileStatementSyntax)SyntaxFactory.WhileStatement(
            attributes,
            keyword,
            open,
            condition,
            close,
            statement
        );
    }

    SwitchStatementSyntax ParseSwitchStatement(SyntaxList<AttributeListSyntax> attributes) {
        var keyword = Take(SyntaxKind.SwitchKeyword);
        var open = Expect(RavenTokenKind.OpenParen, SyntaxKind.OpenParenToken);
        var expression = ParseExpression();
        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);

        var openBrace = Expect(RavenTokenKind.OpenBrace, SyntaxKind.OpenBraceToken);
        SkipNewLines();

        List<SyntaxNode?> sections = [];
        while (At(RavenTokenKind.CaseKeyword) || At(RavenTokenKind.DefaultKeyword)) {
            sections.Add(ParseSwitchSection());
        }

        SkipNewLines();
        var closeBrace = Expect(RavenTokenKind.CloseBrace, SyntaxKind.CloseBraceToken);
        SkipNewLines();

        return (SwitchStatementSyntax)SyntaxFactory.SwitchStatement(
            attributes,
            keyword,
            open,
            expression,
            close,
            openBrace,
            new(SyntaxList.List(sections.ToArray())),
            closeBrace
        );
    }

    SwitchSectionSyntax ParseSwitchSection() {
        List<SyntaxNode?> labels = [];
        while (true) {
            if (At(RavenTokenKind.CaseKeyword)) {
                var caseKeyword = Take(SyntaxKind.CaseKeyword);
                var value = ParseExpression();
                var colon = Expect(RavenTokenKind.Colon, SyntaxKind.ColonToken);
                SkipNewLines();
                labels.Add(SyntaxFactory.CaseSwitchLabel(caseKeyword, value, colon));
            } else if (At(RavenTokenKind.DefaultKeyword)) {
                var defaultKeyword = Take(SyntaxKind.DefaultKeyword);
                var colon = Expect(RavenTokenKind.Colon, SyntaxKind.ColonToken);
                SkipNewLines();
                labels.Add(SyntaxFactory.DefaultSwitchLabel(defaultKeyword, colon));
            } else {
                break;
            }
        }

        List<SyntaxNode?> statements = [];
        while (!At(RavenTokenKind.CaseKeyword)
               && !At(RavenTokenKind.DefaultKeyword)
               && !At(RavenTokenKind.CloseBrace)
               && !AtEnd) {
            var before = RawPosition;
            statements.Add(ParseStatement());
            if (RawPosition == before) {
                SkipToLineEnd();
            }
        }

        return (SwitchSectionSyntax)SyntaxFactory.SwitchSection(
            new(SyntaxList.List(labels.ToArray())),
            new(SyntaxList.List(statements.ToArray()))
        );
    }

    // ================================================================== Expressions

    static bool StartsExpression(RavenTokenKind kind) =>
        kind is RavenTokenKind.Bang
            or RavenTokenKind.Plus
            or RavenTokenKind.PlusPlus
            or RavenTokenKind.Minus
            or RavenTokenKind.MinusMinus
            or RavenTokenKind.Tilde
            or RavenTokenKind.OpenParen
            or RavenTokenKind.OpenBracket
            or RavenTokenKind.Identifier
            or RavenTokenKind.IntegerLiteral
            or RavenTokenKind.HexIntegerLiteral
            or RavenTokenKind.BinIntegerLiteral
            or RavenTokenKind.RealLiteral
            or RavenTokenKind.StringLiteral
            or RavenTokenKind.TrueKeyword
            or RavenTokenKind.FalseKeyword
            or RavenTokenKind.DefaultKeyword
            or RavenTokenKind.BaseKeyword
            or RavenTokenKind.SelfKeyword
        || IsPredefinedTypeKeyword(kind);

    ExpressionSyntax ParseExpression() => ParseAssignment();

    static bool IsAssignmentOperator(RavenTokenKind kind) =>
        kind is RavenTokenKind.Equals
            or RavenTokenKind.PlusEquals
            or RavenTokenKind.MinusEquals
            or RavenTokenKind.StarEquals
            or RavenTokenKind.SlashEquals
            or RavenTokenKind.PercentEquals
            or RavenTokenKind.AmpersandEquals
            or RavenTokenKind.BarEquals
            or RavenTokenKind.CaretEquals
            or RavenTokenKind.LessThanLessThanEquals
            or RavenTokenKind.GreaterThanGreaterThanEquals
            or RavenTokenKind.UnsignedShiftRightEquals;

    ExpressionSyntax ParseAssignment() {
        var left = ParseConditional();

        if (IsAssignmentOperator(Kind)) {
            var op = Take(SyntaxKind.OperatorToken);
            var right = ParseAssignment();
            return (ExpressionSyntax)SyntaxFactory.AssignmentExpression(left, op, right);
        }

        return left;
    }

    ExpressionSyntax ParseConditional() {
        var condition = ParseBinary(0);

        if (At(RavenTokenKind.Question)) {
            var question = Take(SyntaxKind.QuestionToken);
            var whenTrue = ParseExpression();
            var colon = Expect(RavenTokenKind.Colon, SyntaxKind.ColonToken);
            var whenFalse = ParseConditional();
            return (ExpressionSyntax)SyntaxFactory.ConditionalExpression(
                condition,
                question,
                whenTrue,
                colon,
                whenFalse
            );
        }

        return condition;
    }

    /// <summary>Binary precedence ladder, loosest first — the grammar's alternative order reversed.</summary>
    static readonly RavenTokenKind[][] BinaryLevels = [
        [RavenTokenKind.BarBar],
        [RavenTokenKind.AmpersandAmpersand],
        [RavenTokenKind.Bar],
        [RavenTokenKind.Caret],
        [RavenTokenKind.Ampersand],
        [RavenTokenKind.EqualsEquals, RavenTokenKind.BangEquals],
        [
            RavenTokenKind.LessThan, RavenTokenKind.LessThanEquals, RavenTokenKind.GreaterThan,
            RavenTokenKind.GreaterThanEquals
        ],
        [RavenTokenKind.DotDot],
        [
            RavenTokenKind.LessThanLessThan, RavenTokenKind.GreaterThanGreaterThan,
            RavenTokenKind.UnsignedShiftRight
        ],
        [RavenTokenKind.Plus, RavenTokenKind.Minus],
        [RavenTokenKind.Star, RavenTokenKind.Slash, RavenTokenKind.Percent]
    ];

    ExpressionSyntax ParseBinary(int level) {
        if (level >= BinaryLevels.Length) {
            return ParseUnary();
        }

        var ops = BinaryLevels[level];
        var left = ParseBinary(level + 1);

        while (Array.IndexOf(ops, Kind) >= 0) {
            if (Kind == RavenTokenKind.DotDot) {
                var dotDot = Take(SyntaxKind.DotDotToken);
                var rangeRight = ParseBinary(level + 1);
                left = (ExpressionSyntax)SyntaxFactory.RangeExpression(left, dotDot, rangeRight);
                continue;
            }

            var op = Take(SyntaxKind.OperatorToken);
            var right = ParseBinary(level + 1);
            left = (ExpressionSyntax)SyntaxFactory.BinaryExpression(left, op, right);
        }

        return left;
    }

    static readonly Dictionary<RavenTokenKind, SyntaxKind> PrefixKinds = new() {
        [RavenTokenKind.Bang] = SyntaxKind.LogicalNotExpression,
        [RavenTokenKind.Plus] = SyntaxKind.UnaryPlusExpression,
        [RavenTokenKind.PlusPlus] = SyntaxKind.PreIncrementExpression,
        [RavenTokenKind.Minus] = SyntaxKind.UnaryMinusExpression,
        [RavenTokenKind.MinusMinus] = SyntaxKind.PreDecrementExpression,
        [RavenTokenKind.Tilde] = SyntaxKind.BitwiseNotExpression
    };

    ExpressionSyntax ParseUnary() {
        if (PrefixKinds.TryGetValue(Kind, out var prefixKind)) {
            var op = Take(SyntaxKind.OperatorToken);
            var operand = ParseUnary();
            return (ExpressionSyntax)SyntaxFactory.PrefixUnaryExpression(prefixKind, op, operand);
        }

        // The cast alternative outranks everything the parenthesized primary could
        // start, so `(name) + x` is a cast of `+x` — the ANTLR resolution, kept.
        if (At(RavenTokenKind.OpenParen) && ScanCast()) {
            var open = Take(SyntaxKind.OpenParenToken);
            var type = ParseType();
            var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
            var operand = ParseUnary();
            return (ExpressionSyntax)SyntaxFactory.CastExpression(open, type, close, operand);
        }

        return ParsePostfix();
    }

    bool ScanCast() {
        var mark = RawPosition;
        Advance();

        // No sizes: this scan is the one place deciding type-or-expression, and reading a
        // size here would turn `(a[4]) - 1` into a cast of `-1`. A cast to a sized array
        // means nothing anyway, so the restriction costs nothing real.
        var ok = TryScanType(false)
            && At(RavenTokenKind.CloseParen)
            && StartsExpression((RavenTokenKind)Peek(1).RawKind);

        ResetTo(mark);
        return ok;
    }

    ExpressionSyntax ParsePostfix() {
        var expression = ParsePrimary();

        while (true) {
            switch (Kind) {
                case RavenTokenKind.OpenParen:
                    expression = (ExpressionSyntax)SyntaxFactory.InvocationExpression(
                        expression,
                        ParseArgumentList()
                    );
                    continue;

                // An empty rank belongs to the type primary below, not here — a size never
                // does, which is what keeps `a[4]` an element access.
                case RavenTokenKind.OpenBracket when !ScanArrayRank(false):
                    expression = (ExpressionSyntax)SyntaxFactory.ElementAccessExpression(
                        expression,
                        ParseBracketedArgumentList()
                    );
                    continue;

                case RavenTokenKind.Dot: {
                    var dot = Take(SyntaxKind.DotToken);
                    var name = ParseSimpleName();
                    expression = (ExpressionSyntax)SyntaxFactory.MemberAccessExpression(expression, dot, name);
                    continue;
                }

                case RavenTokenKind.PlusPlus:
                    expression = (ExpressionSyntax)SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.PostIncrementExpression,
                        expression,
                        Take(SyntaxKind.OperatorToken)
                    );
                    continue;

                case RavenTokenKind.MinusMinus:
                    expression = (ExpressionSyntax)SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.PostDecrementExpression,
                        expression,
                        Take(SyntaxKind.OperatorToken)
                    );
                    continue;

                default:
                    return expression;
            }
        }
    }

    ExpressionSyntax ParsePrimary() {
        switch (Kind) {
            case RavenTokenKind.OpenBracket:
                return ParseCollectionExpression();

            case RavenTokenKind.DefaultKeyword when PeekKind(1) == RavenTokenKind.OpenParen: {
                var keyword = Take(SyntaxKind.DefaultKeyword);
                var open = Take(SyntaxKind.OpenParenToken);
                var type = ParseType();
                var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
                return (ExpressionSyntax)SyntaxFactory.DefaultExpression(keyword, open, type, close);
            }

            case RavenTokenKind.DefaultKeyword:
                return (ExpressionSyntax)SyntaxFactory.LiteralExpression(
                    SyntaxKind.DefaultLiteralExpression,
                    Take(SyntaxKind.DefaultKeyword)
                );

            case RavenTokenKind.BaseKeyword:
                return (ExpressionSyntax)SyntaxFactory.BaseExpression(Take(SyntaxKind.BaseKeyword));

            case RavenTokenKind.SelfKeyword:
                return (ExpressionSyntax)SyntaxFactory.SelfExpression(Take(SyntaxKind.SelfKeyword));

            case RavenTokenKind.TrueKeyword:
                return (ExpressionSyntax)SyntaxFactory.LiteralExpression(
                    SyntaxKind.TrueLiteralExpression,
                    Take(SyntaxKind.TrueKeyword)
                );

            case RavenTokenKind.FalseKeyword:
                return (ExpressionSyntax)SyntaxFactory.LiteralExpression(
                    SyntaxKind.FalseLiteralExpression,
                    Take(SyntaxKind.FalseKeyword)
                );

            case RavenTokenKind.IntegerLiteral
                or RavenTokenKind.HexIntegerLiteral
                or RavenTokenKind.BinIntegerLiteral
                or RavenTokenKind.RealLiteral:
                return (ExpressionSyntax)SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    Take(SyntaxKind.NumericLiteralToken)
                );

            case RavenTokenKind.StringLiteral:
                return (ExpressionSyntax)SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    Take(SyntaxKind.StringLiteralToken)
                );

            case RavenTokenKind.OpenParen:
                return ParseParenthesizedOrTuple();

            case RavenTokenKind.Identifier: {
                // A dotted name is a single qualified-name expression; empty array ranks
                // after it make it a type expression, exactly as the type primary would.
                // Sizes are excluded here, and that is what makes `a[4]` element access.
                var name = (ExpressionSyntax)ParseName();
                if (At(RavenTokenKind.OpenBracket) && ScanArrayRank(false)) {
                    List<SyntaxNode?> ranks = [];
                    while (At(RavenTokenKind.OpenBracket) && ScanArrayRank(false)) {
                        ranks.Add(ParseArrayRankSpecifier());
                    }

                    return (ExpressionSyntax)SyntaxFactory.ArrayType(
                        (TypeSyntax)name,
                        new(SyntaxList.List(ranks.ToArray()))
                    );
                }

                return name;
            }

            default:
                if (IsPredefinedTypeKeyword(Kind)) {
                    return (ExpressionSyntax)ParseType(false);
                }

                ReportExpected("an expression");
                return (ExpressionSyntax)SyntaxFactory.IdentifierName(Missing(SyntaxKind.IdentifierToken));
        }
    }

    ExpressionSyntax ParseParenthesizedOrTuple() {
        var open = Take(SyntaxKind.OpenParenToken);
        var first = ParseArgument();

        if (At(RavenTokenKind.Comma) || first.NameColon is not null) {
            List<SyntaxNode?> arguments = [first];
            List<SyntaxToken> commas = [];
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                arguments.Add(ParseArgument());
            }

            if (arguments.Count < 2) {
                ReportExpected("','");
            }

            var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
            return (ExpressionSyntax)SyntaxFactory.TupleExpression(
                open,
                Separated<ArgumentSyntax>(arguments, commas),
                close
            );
        }

        var closeParen = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        return (ExpressionSyntax)SyntaxFactory.ParenthesizedExpression(open, first.Expression, closeParen);
    }

    ExpressionSyntax ParseCollectionExpression() {
        var open = Take(SyntaxKind.OpenBracketToken);
        SkipNewLines();

        List<SyntaxNode?> elements = [];
        List<SyntaxToken> commas = [];
        if (!At(RavenTokenKind.CloseBracket) && !AtEnd) {
            elements.Add(ParseCollectionElement());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                SkipNewLines();
                elements.Add(ParseCollectionElement());
            }
        }

        SkipNewLines();
        var close = Expect(RavenTokenKind.CloseBracket, SyntaxKind.CloseBracketToken);
        return (ExpressionSyntax)SyntaxFactory.CollectionExpression(
            open,
            Separated<CollectionElementSyntax>(elements, commas),
            close
        );
    }

    CollectionElementSyntax ParseCollectionElement() {
        if (At(RavenTokenKind.DotDot)) {
            var dotDot = Take(SyntaxKind.DotDotToken);
            var expression = ParseExpression();
            return (CollectionElementSyntax)SyntaxFactory.SpreadElement(dotDot, expression);
        }

        return (CollectionElementSyntax)SyntaxFactory.ExpressionElement(ParseExpression());
    }

    /// <summary>
    ///     <c>argument_list</c>: <c>'(' NL* (argument (',' NL* argument)* NL*)? ')'</c> — the
    ///     call side of <see cref="ParseParameterList" />'s newlines, in the same three
    ///     positions.
    /// </summary>
    ArgumentListSyntax ParseArgumentList() {
        var open = Take(SyntaxKind.OpenParenToken);
        SkipNewLines();

        List<SyntaxNode?> arguments = [];
        List<SyntaxToken> commas = [];
        if (!At(RavenTokenKind.CloseParen) && !AtEnd) {
            arguments.Add(ParseArgument());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                SkipNewLines();
                arguments.Add(ParseArgument());
            }

            SkipNewLines();
        }

        var close = Expect(RavenTokenKind.CloseParen, SyntaxKind.CloseParenToken);
        return (ArgumentListSyntax)SyntaxFactory.ArgumentList(open, Separated<ArgumentSyntax>(arguments, commas), close);
    }

    BracketedArgumentListSyntax ParseBracketedArgumentList() {
        var open = Take(SyntaxKind.OpenBracketToken);

        List<SyntaxNode?> arguments = [ParseArgument()];
        List<SyntaxToken> commas = [];
        while (At(RavenTokenKind.Comma)) {
            commas.Add(Take(SyntaxKind.CommaToken));
            arguments.Add(ParseArgument());
        }

        var close = Expect(RavenTokenKind.CloseBracket, SyntaxKind.CloseBracketToken);
        return (BracketedArgumentListSyntax)SyntaxFactory.BracketedArgumentList(
            open,
            Separated<ArgumentSyntax>(arguments, commas),
            close
        );
    }

    ArgumentSyntax ParseArgument() {
        var nameColon = AtNameColon ? ParseNameColon() : null;
        var expression = ParseExpression();
        return (ArgumentSyntax)SyntaxFactory.Argument(nameColon, expression);
    }

    // ================================================================== Lists

    static SeparatedSyntaxList<T> Separated<T>(IReadOnlyList<SyntaxNode?> elements, IReadOnlyList<SyntaxToken> separators)
        where T : SyntaxNode {
        var interleaved = new List<SyntaxNode?>();
        for (var i = 0; i < elements.Count; i++) {
            interleaved.Add(elements[i]);
            if (i < separators.Count) {
                interleaved.Add(separators[i]);
            }
        }

        return new(SyntaxList.List(interleaved.ToArray()));
    }
}
