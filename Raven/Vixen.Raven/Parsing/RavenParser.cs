// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;
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
        (SyntaxToken)new Green.SyntaxToken((int)kind, string.Empty).CreateRed(null, 0);

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
        if (!At(RavenTokenKind.GreaterThan)) {
            arguments.Add(ParseType());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                arguments.Add(ParseType());
            }
        }

        var greaterThan = Expect(RavenTokenKind.GreaterThan, SyntaxKind.GreaterThanToken);
        return (TypeArgumentListSyntax)SyntaxFactory.TypeArgumentList(
            lessThan,
            Separated<TypeSyntax>(arguments, commas),
            greaterThan
        );
    }

    // ================================================================== Types

    static bool IsPredefinedTypeKeyword(RavenTokenKind kind) =>
        kind is >= RavenTokenKind.BoolKeyword and <= RavenTokenKind.Mat4x3Keyword;

    static SyntaxKind PredefinedTypeTreeKind(RavenTokenKind kind) =>
        // The two blocks run in the same order, so the offset maps 1:1.
        (SyntaxKind)((int)SyntaxKind.BoolKeyword + (kind - RavenTokenKind.BoolKeyword));

    bool AtTypeStart =>
        At(RavenTokenKind.Identifier) || IsPredefinedTypeKeyword(Kind) || At(RavenTokenKind.OpenParen);

    TypeSyntax ParseType() {
        var type = ParseCoreType();

        // `type array_rank_specifier+` folds every rank into one ArrayType node.
        if (At(RavenTokenKind.OpenBracket) && ScanArrayRank()) {
            List<SyntaxNode?> ranks = [];
            while (At(RavenTokenKind.OpenBracket) && ScanArrayRank()) {
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
        List<SyntaxNode?> commas = [];
        while (At(RavenTokenKind.Comma)) {
            commas.Add(Take(SyntaxKind.CommaToken));
        }

        var close = Expect(RavenTokenKind.CloseBracket, SyntaxKind.CloseBracketToken);
        return (ArrayRankSpecifierSyntax)SyntaxFactory.ArrayRankSpecifier(
            open,
            new(SyntaxList.List(commas.ToArray())),
            close
        );
    }

    // ------------------------------------------------------------ Type scanning (no tree tokens)

    /// <summary>An empty rank — <c>[</c> commas <c>]</c> — as opposed to element access.</summary>
    bool ScanArrayRank() {
        var n = 1;
        while (PeekKind(n) == RavenTokenKind.Comma) {
            n++;
        }

        return PeekKind(n) == RavenTokenKind.CloseBracket;
    }

    bool ScanTypeArgumentList() {
        var mark = RawPosition;
        var ok = TryScanTypeArgumentList();
        ResetTo(mark);
        return ok;
    }

    bool TryScanTypeArgumentList() {
        if (!At(RavenTokenKind.LessThan)) {
            return false;
        }

        Advance();
        if (At(RavenTokenKind.GreaterThan)) {
            Advance();
            return true;
        }

        if (!TryScanType()) {
            return false;
        }

        while (At(RavenTokenKind.Comma)) {
            Advance();
            if (!TryScanType()) {
                return false;
            }
        }

        if (!At(RavenTokenKind.GreaterThan)) {
            return false;
        }

        Advance();
        return true;
    }

    /// <summary>Advances over one <c>type</c> production without building anything.</summary>
    bool TryScanType() {
        if (IsPredefinedTypeKeyword(Kind)) {
            Advance();
        } else if (At(RavenTokenKind.Identifier)) {
            Advance();
            if (At(RavenTokenKind.LessThan) && !TryScanTypeArgumentList()) {
                return false;
            }

            while (At(RavenTokenKind.Dot)) {
                Advance();
                if (!At(RavenTokenKind.Identifier)) {
                    return false;
                }

                Advance();
                if (At(RavenTokenKind.LessThan) && !TryScanTypeArgumentList()) {
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

        while (At(RavenTokenKind.OpenBracket) && ScanArrayRank()) {
            Advance();
            while (At(RavenTokenKind.Comma)) {
                Advance();
            }

            Advance();
        }

        return true;
    }

    bool TryScanTupleElement() {
        if (At(RavenTokenKind.Identifier) && PeekKind(1) == RavenTokenKind.Colon) {
            Advance();
            Advance();
        }

        return TryScanType();
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

            if (!TryScanType()) {
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
        [RavenTokenKind.StreamKeyword] = SyntaxKind.StreamKeyword
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
        if (blender.TryReuse(fullStart) is not { } green) {
            return null;
        }

        var end = fullStart + green.FullWidth;
        var next = RawIndexAt(end);
        if (next is null) {
            return null;
        }

        ResetTo(next.Value);
        return green.CreateRed(null, 0) as MemberDeclarationSyntax;
    }

    /// <summary>The raw index of the token starting exactly at <paramref name="position" />, or null.</summary>
    int? RawIndexAt(int position) {
        int low = 0, high = Tokens.Count - 1;
        while (low <= high) {
            var middle = (low + high) / 2;
            var start = Tokens[middle].Position;
            if (start == position) {
                return middle;
            }

            if (start < position) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return null;
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
        var ok = TryScanType() && At(RavenTokenKind.OperatorKeyword);
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
                if (!TryScanType()) {
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
            accessors.Add(ParseAccessorDeclaration());
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

    ParameterListSyntax ParseParameterList() {
        var open = Expect(RavenTokenKind.OpenParen, SyntaxKind.OpenParenToken);

        // Recovery: something that can neither start a parameter nor close the list
        // is skipped once, with one diagnostic pointing at it; the close paren is
        // then fabricated silently rather than reported a second time.
        var recovered = false;
        while (!At(RavenTokenKind.CloseParen)
               && !AtNewLine
               && !AtEnd
               && !At(RavenTokenKind.OpenBrace)
               && !At(RavenTokenKind.Arrow)
               && !At(RavenTokenKind.Identifier)
               && !At(RavenTokenKind.OpenBracket)) {
            SkipCurrent();
            recovered = true;
        }

        List<SyntaxNode?> parameters = [];
        List<SyntaxToken> commas = [];
        if (At(RavenTokenKind.Identifier) || At(RavenTokenKind.OpenBracket)) {
            parameters.Add(ParseParameter());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                parameters.Add(ParseParameter());
            }
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

    ParameterSyntax ParseParameter() {
        var attributes = ParseInlineAttributeLists();
        var identifier = ExpectIdentifier();

        SyntaxToken? colon = null;
        TypeSyntax? type = null;
        if (At(RavenTokenKind.Colon)) {
            colon = Take(SyntaxKind.ColonToken);
            type = ParseType();
        }

        var @default = At(RavenTokenKind.Equals) ? ParseEqualsValueClause() : null;
        return (ParameterSyntax)SyntaxFactory.Parameter(attributes, identifier, colon, type, @default);
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

        var ok = TryScanType()
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

                case RavenTokenKind.OpenBracket when !ScanArrayRank():
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
                // A dotted name is a single qualified-name expression; array ranks
                // after it make it a type expression, exactly as the type primary would.
                var name = (ExpressionSyntax)ParseName();
                if (At(RavenTokenKind.OpenBracket) && ScanArrayRank()) {
                    List<SyntaxNode?> ranks = [];
                    while (At(RavenTokenKind.OpenBracket) && ScanArrayRank()) {
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
                    return (ExpressionSyntax)ParseType();
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

    ArgumentListSyntax ParseArgumentList() {
        var open = Take(SyntaxKind.OpenParenToken);

        List<SyntaxNode?> arguments = [];
        List<SyntaxToken> commas = [];
        if (!At(RavenTokenKind.CloseParen) && !AtEnd) {
            arguments.Add(ParseArgument());
            while (At(RavenTokenKind.Comma)) {
                commas.Add(Take(SyntaxKind.CommaToken));
                arguments.Add(ParseArgument());
            }
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
