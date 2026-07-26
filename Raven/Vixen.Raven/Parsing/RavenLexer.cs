// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Raven.Diagnostics;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;

namespace Vixen.Raven.Parsing;

/// <summary>
///     The hand-written lexer (docs/plan/18 step 2). Produces the full raw token
///     list — trivia included — that <see cref="RavenParser" /> parses and builds
///     tree tokens from. Token boundaries replicate the retired ANTLR lexer's
///     maximal munch exactly; the token-stream differential test pins that.
/// </summary>
sealed class RavenLexer {
    static readonly Dictionary<string, RavenTokenKind> Keywords = new(StringComparer.Ordinal) {
        ["get"] = RavenTokenKind.GetKeyword,
        ["set"] = RavenTokenKind.SetKeyword,
        ["willSet"] = RavenTokenKind.WillSetKeyword,
        ["didSet"] = RavenTokenKind.DidSetKeyword,
        ["func"] = RavenTokenKind.FuncKeyword,
        ["protocol"] = RavenTokenKind.ProtocolKeyword,
        ["self"] = RavenTokenKind.SelfKeyword,
        ["shader"] = RavenTokenKind.ShaderKeyword,
        ["struct"] = RavenTokenKind.StructKeyword,
        ["var"] = RavenTokenKind.VarKeyword,
        ["val"] = RavenTokenKind.ValKeyword,
        ["repeat"] = RavenTokenKind.RepeatKeyword,
        ["import"] = RavenTokenKind.ImportKeyword,
        ["package"] = RavenTokenKind.PackageKeyword,
        ["init"] = RavenTokenKind.InitKeyword,
        ["base"] = RavenTokenKind.BaseKeyword,
        ["break"] = RavenTokenKind.BreakKeyword,
        ["case"] = RavenTokenKind.CaseKeyword,
        ["continue"] = RavenTokenKind.ContinueKeyword,
        ["default"] = RavenTokenKind.DefaultKeyword,
        ["else"] = RavenTokenKind.ElseKeyword,
        ["enum"] = RavenTokenKind.EnumKeyword,
        ["false"] = RavenTokenKind.FalseKeyword,
        ["for"] = RavenTokenKind.ForKeyword,
        ["if"] = RavenTokenKind.IfKeyword,
        ["in"] = RavenTokenKind.InKeyword,
        ["operator"] = RavenTokenKind.OperatorKeyword,
        ["return"] = RavenTokenKind.ReturnKeyword,
        ["switch"] = RavenTokenKind.SwitchKeyword,
        ["true"] = RavenTokenKind.TrueKeyword,
        ["while"] = RavenTokenKind.WhileKeyword,
        ["where"] = RavenTokenKind.WhereKeyword,
        ["compose"] = RavenTokenKind.ComposeKeyword,
        ["const"] = RavenTokenKind.ConstKeyword,
        ["override"] = RavenTokenKind.OverrideKeyword,
        ["readonly"] = RavenTokenKind.ReadOnlyKeyword,
        ["static"] = RavenTokenKind.StaticKeyword,
        ["stream"] = RavenTokenKind.StreamKeyword,
        ["inout"] = RavenTokenKind.InOutKeyword,
        ["bool"] = RavenTokenKind.BoolKeyword,
        ["bool2"] = RavenTokenKind.Bool2Keyword,
        ["bool3"] = RavenTokenKind.Bool3Keyword,
        ["bool4"] = RavenTokenKind.Bool4Keyword,
        ["int"] = RavenTokenKind.IntKeyword,
        ["int2"] = RavenTokenKind.Int2Keyword,
        ["int3"] = RavenTokenKind.Int3Keyword,
        ["int4"] = RavenTokenKind.Int4Keyword,
        ["uint"] = RavenTokenKind.UIntKeyword,
        ["uint2"] = RavenTokenKind.UInt2Keyword,
        ["uint3"] = RavenTokenKind.UInt3Keyword,
        ["uint4"] = RavenTokenKind.UInt4Keyword,
        ["float"] = RavenTokenKind.FloatKeyword,
        ["float2"] = RavenTokenKind.Float2Keyword,
        ["float3"] = RavenTokenKind.Float3Keyword,
        ["float4"] = RavenTokenKind.Float4Keyword,
        ["double"] = RavenTokenKind.DoubleKeyword,
        ["double2"] = RavenTokenKind.Double2Keyword,
        ["double3"] = RavenTokenKind.Double3Keyword,
        ["double4"] = RavenTokenKind.Double4Keyword,
        ["mat2"] = RavenTokenKind.Mat2Keyword,
        ["mat2x3"] = RavenTokenKind.Mat2x3Keyword,
        ["mat2x4"] = RavenTokenKind.Mat2x4Keyword,
        ["mat3"] = RavenTokenKind.Mat3Keyword,
        ["mat3x2"] = RavenTokenKind.Mat3x2Keyword,
        ["mat3x4"] = RavenTokenKind.Mat3x4Keyword,
        ["mat4"] = RavenTokenKind.Mat4Keyword,
        ["mat4x2"] = RavenTokenKind.Mat4x2Keyword,
        ["mat4x3"] = RavenTokenKind.Mat4x3Keyword
    };

    readonly SlidingTextWindow window;
    readonly DiagnosticBag diagnostics;
    readonly SourceText text;
    readonly string filePath;

    RavenLexer(string source, DiagnosticBag diagnostics, SourceText text, string filePath) {
        window = new(source);
        this.diagnostics = diagnostics;
        this.text = text;
        this.filePath = filePath;
    }

    /// <summary>Lexes the whole source into raw tokens; the last entry is always end-of-file.</summary>
    public static List<LexedToken> Lex(string source, DiagnosticBag diagnostics, SourceText text, string filePath) {
        var lexer = new RavenLexer(source, diagnostics, text, filePath);
        var tokens = new List<LexedToken>();

        while (true) {
            var token = lexer.Next();
            tokens.Add(token);
            if (token.RawKind == (int)RavenTokenKind.EndOfFile) {
                return tokens;
            }
        }
    }

    LexedToken Next() {
        var start = window.Position;
        var current = window.Current;

        if (window.AtEnd) {
            return Make(RavenTokenKind.EndOfFile, start);
        }

        switch (current) {
            case '\r':
                window.Advance();
                window.TryAdvance('\n');
                return Make(RavenTokenKind.NewLine, start, LexedTokenFlags.EndOfLine);

            case '\n' or '\u0085' or '\u2028' or '\u2029':
                window.Advance();
                return Make(RavenTokenKind.NewLine, start, LexedTokenFlags.EndOfLine);

            case '/':
                return window.Peek(1) switch {
                    '/' => LexSingleLineComment(start),
                    '*' => LexDelimitedComment(start),
                    '=' => Two(RavenTokenKind.SlashEquals, start),
                    _ => One(RavenTokenKind.Slash, start)
                };

            case '"':
                return LexString(start);

            case >= '0' and <= '9':
                return LexNumber(start);

            case '.':
                if (IsDigit(window.Peek(1))) {
                    return LexReal(start);
                }

                if (window.Peek(1) == '.') {
                    return Two(RavenTokenKind.DotDot, start);
                }

                return One(RavenTokenKind.Dot, start);

            case '{': return One(RavenTokenKind.OpenBrace, start);
            case '}': return One(RavenTokenKind.CloseBrace, start);
            case '[': return One(RavenTokenKind.OpenBracket, start);
            case ']': return One(RavenTokenKind.CloseBracket, start);
            case '(': return One(RavenTokenKind.OpenParen, start);
            case ')': return One(RavenTokenKind.CloseParen, start);
            case ',': return One(RavenTokenKind.Comma, start);
            case ':': return One(RavenTokenKind.Colon, start);
            case '?': return One(RavenTokenKind.Question, start);
            case '~': return One(RavenTokenKind.Tilde, start);

            case '+':
                return window.Peek(1) switch {
                    '+' => Two(RavenTokenKind.PlusPlus, start),
                    '=' => Two(RavenTokenKind.PlusEquals, start),
                    _ => One(RavenTokenKind.Plus, start)
                };

            case '-':
                return window.Peek(1) switch {
                    '-' => Two(RavenTokenKind.MinusMinus, start),
                    '=' => Two(RavenTokenKind.MinusEquals, start),
                    _ => One(RavenTokenKind.Minus, start)
                };

            case '*':
                return window.Peek(1) == '='
                    ? Two(RavenTokenKind.StarEquals, start)
                    : One(RavenTokenKind.Star, start);

            case '%':
                return window.Peek(1) == '='
                    ? Two(RavenTokenKind.PercentEquals, start)
                    : One(RavenTokenKind.Percent, start);

            case '&':
                return window.Peek(1) switch {
                    '&' => Two(RavenTokenKind.AmpersandAmpersand, start),
                    '=' => Two(RavenTokenKind.AmpersandEquals, start),
                    _ => One(RavenTokenKind.Ampersand, start)
                };

            case '|':
                return window.Peek(1) switch {
                    '|' => Two(RavenTokenKind.BarBar, start),
                    '=' => Two(RavenTokenKind.BarEquals, start),
                    _ => One(RavenTokenKind.Bar, start)
                };

            case '^':
                return window.Peek(1) == '='
                    ? Two(RavenTokenKind.CaretEquals, start)
                    : One(RavenTokenKind.Caret, start);

            case '!':
                return window.Peek(1) == '='
                    ? Two(RavenTokenKind.BangEquals, start)
                    : One(RavenTokenKind.Bang, start);

            case '=':
                return window.Peek(1) switch {
                    '=' => Two(RavenTokenKind.EqualsEquals, start),
                    '>' => Two(RavenTokenKind.Arrow, start),
                    _ => One(RavenTokenKind.Equals, start)
                };

            case '<':
                if (window.Peek(1) == '<') {
                    return window.Peek(2) == '='
                        ? Fixed(RavenTokenKind.LessThanLessThanEquals, start, 3)
                        : Fixed(RavenTokenKind.LessThanLessThan, start, 2);
                }

                return window.Peek(1) == '='
                    ? Two(RavenTokenKind.LessThanEquals, start)
                    : One(RavenTokenKind.LessThan, start);

            case '>':
                if (window.Peek(1) == '>') {
                    if (window.Peek(2) == '>') {
                        return window.Peek(3) == '='
                            ? Fixed(RavenTokenKind.UnsignedShiftRightEquals, start, 4)
                            : Fixed(RavenTokenKind.UnsignedShiftRight, start, 3);
                    }

                    return window.Peek(2) == '='
                        ? Fixed(RavenTokenKind.GreaterThanGreaterThanEquals, start, 3)
                        : Fixed(RavenTokenKind.GreaterThanGreaterThan, start, 2);
                }

                return window.Peek(1) == '='
                    ? Two(RavenTokenKind.GreaterThanEquals, start)
                    : One(RavenTokenKind.GreaterThan, start);
        }

        if (IsWhitespace(current)) {
            while (IsWhitespace(window.Current)) {
                window.Advance();
            }

            return Make(RavenTokenKind.Whitespace, start, LexedTokenFlags.Trivia);
        }

        if (IsIdentifierStart(current)) {
            window.Advance();
            while (IsIdentifierPart(window.Current)) {
                window.Advance();
            }

            var identifier = window.GetText(start, window.Position);
            return new(
                (int)(Keywords.GetValueOrDefault(identifier, RavenTokenKind.Identifier)),
                identifier,
                start,
                LexedTokenFlags.None
            );
        }

        // Unrecognized character: report it and carry it as trivia so the tree still
        // reproduces the file byte-for-byte.
        window.Advance();
        Report(SyntaxDiagnostics.InvalidCharacter, start, window.Position, $"'{current}'");
        return Make(RavenTokenKind.Whitespace, start, LexedTokenFlags.Trivia);
    }

    LexedToken LexSingleLineComment(int start) {
        while (!window.AtEnd && !IsNewLine(window.Current)) {
            window.Advance();
        }

        return Make(RavenTokenKind.SingleLineComment, start, LexedTokenFlags.Trivia);
    }

    LexedToken LexDelimitedComment(int start) {
        window.Advance(2);
        while (!window.AtEnd && !(window.Current == '*' && window.Peek(1) == '/')) {
            window.Advance();
        }

        if (window.AtEnd) {
            Report(SyntaxDiagnostics.SyntaxError, start, window.Position, "unterminated comment");
        } else {
            window.Advance(2);
        }

        return Make(RavenTokenKind.MultiLineComment, start, LexedTokenFlags.Trivia);
    }

    LexedToken LexString(int start) {
        window.Advance();
        while (!window.AtEnd && window.Current != '"' && !IsNewLine(window.Current)) {
            // A backslash consumes the next character, so an escaped quote never closes.
            window.Advance(window.Current == '\\' && !window.AtEnd ? 2 : 1);
        }

        if (!window.TryAdvance('"')) {
            Report(SyntaxDiagnostics.SyntaxError, start, window.Position, "unterminated string literal");
        }

        return Make(RavenTokenKind.StringLiteral, start);
    }

    LexedToken LexNumber(int start) {
        if (window.Current == '0' && window.Peek(1) is 'x' or 'X') {
            window.Advance(2);
            ScanSeparatedRun(IsHexDigit);
            ScanIntegerSuffix();
            return Make(RavenTokenKind.HexIntegerLiteral, start);
        }

        if (window.Current == '0' && window.Peek(1) is 'b' or 'B') {
            window.Advance(2);
            ScanSeparatedRun(IsBinaryDigit);
            ScanIntegerSuffix();
            return Make(RavenTokenKind.BinIntegerLiteral, start);
        }

        ScanSeparatedRun(IsDigit);

        // `1.5` is a real; `1..2` is an integer and a range; `1.x` is an integer, a
        // dot and a name. Only a digit after the dot makes it a real.
        if (window.Current == '.' && IsDigit(window.Peek(1))) {
            window.Advance();
            ScanSeparatedRun(IsDigit);
            ScanExponent();
            ScanRealSuffix();
            return Make(RavenTokenKind.RealLiteral, start);
        }

        if (PeeksExponent()) {
            ScanExponent();
            ScanRealSuffix();
            return Make(RavenTokenKind.RealLiteral, start);
        }

        if (IsRealSuffix(window.Current)) {
            window.Advance();
            return Make(RavenTokenKind.RealLiteral, start);
        }

        ScanIntegerSuffix();
        return Make(RavenTokenKind.IntegerLiteral, start);
    }

    /// <summary>A real that starts at the dot: <c>.5f</c>.</summary>
    LexedToken LexReal(int start) {
        window.Advance();
        ScanSeparatedRun(IsDigit);
        ScanExponent();
        ScanRealSuffix();
        return Make(RavenTokenKind.RealLiteral, start);
    }

    /// <summary>
    ///     Digits with embedded underscore separators — <c>1_000</c> — where a
    ///     trailing underscore is not part of the number (<c>1_</c> is the integer
    ///     <c>1</c> then the identifier <c>_</c>).
    /// </summary>
    void ScanSeparatedRun(Func<char, bool> isDigit) {
        while (isDigit(window.Current)) {
            window.Advance();
        }

        while (window.Current == '_') {
            var underscores = 0;
            while (window.Peek(underscores) == '_') {
                underscores++;
            }

            if (!isDigit(window.Peek(underscores))) {
                return;
            }

            window.Advance(underscores);
            while (isDigit(window.Current)) {
                window.Advance();
            }
        }
    }

    bool PeeksExponent() {
        if (window.Current is not ('e' or 'E')) {
            return false;
        }

        var next = window.Peek(1);
        if (next is '+' or '-') {
            return IsDigit(window.Peek(2));
        }

        return IsDigit(next);
    }

    void ScanExponent() {
        if (!PeeksExponent()) {
            return;
        }

        window.Advance();
        if (window.Current is '+' or '-') {
            window.Advance();
        }

        ScanSeparatedRun(IsDigit);
    }

    void ScanRealSuffix() {
        if (IsRealSuffix(window.Current)) {
            window.Advance();
        }
    }

    void ScanIntegerSuffix() {
        if (window.Current is 'u' or 'U') {
            window.Advance();
        }
    }

    LexedToken One(RavenTokenKind kind, int start) => Fixed(kind, start, 1);
    LexedToken Two(RavenTokenKind kind, int start) => Fixed(kind, start, 2);

    LexedToken Fixed(RavenTokenKind kind, int start, int length) {
        window.Advance(length);
        return Make(kind, start);
    }

    LexedToken Make(RavenTokenKind kind, int start, LexedTokenFlags flags = LexedTokenFlags.None) =>
        new((int)kind, window.GetText(start, window.Position), start, flags);

    void Report(DiagnosticDescriptor descriptor, int start, int end, string argument) =>
        diagnostics.Add(descriptor, Location.Create(filePath, TextSpan.FromBounds(start, end), text), argument);

    static bool IsNewLine(char c) => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    static bool IsWhitespace(char c) =>
        c is '\t' or '\u000B' or '\u000C'
        || (c != SlidingTextWindow.InvalidCharacter
            && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);

    static bool IsDigit(char c) => c is >= '0' and <= '9';
    static bool IsHexDigit(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
    static bool IsBinaryDigit(char c) => c is '0' or '1';
    static bool IsRealSuffix(char c) => c is 'F' or 'f' or 'D' or 'd' or 'M' or 'm';

    static bool IsIdentifierStart(char c) =>
        c == '_'
        || (c != SlidingTextWindow.InvalidCharacter
            && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.LetterNumber);

    static bool IsIdentifierPart(char c) =>
        c != SlidingTextWindow.InvalidCharacter
        && (IsIdentifierStart(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.Format);
}
