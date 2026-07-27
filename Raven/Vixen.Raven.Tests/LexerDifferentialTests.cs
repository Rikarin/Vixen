// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Antlr4.Runtime;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;
using Vixen.Raven.Parsing;
using Xunit;
using Antlr = Vixen.Raven.Grammar;

namespace Tests;

/// <summary>
///     Doc 18 step 2: the hand-written lexer must produce the same token sequence —
///     kind, text and trivia — as the ANTLR lexer, over the whole corpus. A
///     token-stream differential is a cheap, total check, and it is what makes the
///     parser differential meaningful: same tokens in, same tree expected out.
/// </summary>
public class LexerDifferentialTests {
    public static TheoryData<string> CorpusFiles() {
        var data = new TheoryData<string>();
        foreach (var file in CorpusLocator.All()) {
            data.Add(file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Token_streams_are_identical(string path) {
        var text = File.ReadAllText(path);

        var expected = AntlrTokens(text).ToArray();
        var actual = HandTokens(text).ToArray();

        // Compare pairwise for a readable failure: the first diverging token names
        // the construct, not just an index.
        for (var i = 0; i < Math.Min(expected.Length, actual.Length); i++) {
            Assert.True(
                expected[i] == actual[i],
                $"{Path.GetFileName(path)}: token {i} differs.\nANTLR: {expected[i]}\nHand:  {actual[i]}"
            );
        }

        Assert.Equal(expected.Length, actual.Length);
    }

    static IEnumerable<(RavenTokenKind Kind, string Text)> HandTokens(string text) {
        var bag = new DiagnosticBag();
        foreach (var token in RavenLexer.Lex(text, bag, SourceText.From(text), "diff.rvn")) {
            if (token.RawKind != (int)RavenTokenKind.EndOfFile) {
                yield return ((RavenTokenKind)token.RawKind, token.Text);
            }
        }

        Assert.Empty(bag);
    }

    static IEnumerable<(RavenTokenKind Kind, string Text)> AntlrTokens(string text) {
        var lexer = new Antlr.RavenLexer(new AntlrInputStream(text));
        lexer.RemoveErrorListeners();

        while (true) {
            var token = lexer.NextToken();
            if (token.Type == TokenConstants.Eof) {
                yield break;
            }

            yield return (Map(token.Type), token.Text);
        }
    }

    static RavenTokenKind Map(int antlrType) =>
        antlrType switch {
            Antlr.RavenLexer.NL => RavenTokenKind.NewLine,
            Antlr.RavenLexer.WHITESPACES => RavenTokenKind.Whitespace,
            Antlr.RavenLexer.SINGLE_LINE_COMMENT or Antlr.RavenLexer.SINGLE_LINE_DOC_COMMENT =>
                RavenTokenKind.SingleLineComment,
            Antlr.RavenLexer.DELIMITED_COMMENT
                or Antlr.RavenLexer.DELIMITED_DOC_COMMENT
                or Antlr.RavenLexer.EMPTY_DELIMITED_DOC_COMMENT => RavenTokenKind.MultiLineComment,
            Antlr.RavenLexer.IDENTIFIER => RavenTokenKind.Identifier,
            Antlr.RavenLexer.INTEGER_LITERAL => RavenTokenKind.IntegerLiteral,
            Antlr.RavenLexer.HEX_INTEGER_LITERAL => RavenTokenKind.HexIntegerLiteral,
            Antlr.RavenLexer.BIN_INTEGER_LITERAL => RavenTokenKind.BinIntegerLiteral,
            Antlr.RavenLexer.REAL_LITERAL => RavenTokenKind.RealLiteral,
            Antlr.RavenLexer.STRING_LITERAL => RavenTokenKind.StringLiteral,
            Antlr.RavenLexer.GET => RavenTokenKind.GetKeyword,
            Antlr.RavenLexer.SET => RavenTokenKind.SetKeyword,
            Antlr.RavenLexer.WILL_SET => RavenTokenKind.WillSetKeyword,
            Antlr.RavenLexer.DID_SET => RavenTokenKind.DidSetKeyword,
            Antlr.RavenLexer.FUNC => RavenTokenKind.FuncKeyword,
            Antlr.RavenLexer.PROTOCOL => RavenTokenKind.ProtocolKeyword,
            Antlr.RavenLexer.SELF => RavenTokenKind.SelfKeyword,
            Antlr.RavenLexer.SHADER => RavenTokenKind.ShaderKeyword,
            Antlr.RavenLexer.STRUCT => RavenTokenKind.StructKeyword,
            Antlr.RavenLexer.VAR => RavenTokenKind.VarKeyword,
            Antlr.RavenLexer.VAL => RavenTokenKind.ValKeyword,
            Antlr.RavenLexer.REPEAT => RavenTokenKind.RepeatKeyword,
            Antlr.RavenLexer.IMPORT => RavenTokenKind.ImportKeyword,
            Antlr.RavenLexer.PACKAGE => RavenTokenKind.PackageKeyword,
            Antlr.RavenLexer.INIT => RavenTokenKind.InitKeyword,
            Antlr.RavenLexer.BASE => RavenTokenKind.BaseKeyword,
            Antlr.RavenLexer.BREAK => RavenTokenKind.BreakKeyword,
            Antlr.RavenLexer.CASE => RavenTokenKind.CaseKeyword,
            Antlr.RavenLexer.CONTINUE => RavenTokenKind.ContinueKeyword,
            Antlr.RavenLexer.DEFAULT => RavenTokenKind.DefaultKeyword,
            Antlr.RavenLexer.DISCARD => RavenTokenKind.DiscardKeyword,
            Antlr.RavenLexer.ELSE => RavenTokenKind.ElseKeyword,
            Antlr.RavenLexer.ENUM => RavenTokenKind.EnumKeyword,
            Antlr.RavenLexer.FALSE => RavenTokenKind.FalseKeyword,
            Antlr.RavenLexer.FOR => RavenTokenKind.ForKeyword,
            Antlr.RavenLexer.IF => RavenTokenKind.IfKeyword,
            Antlr.RavenLexer.IN => RavenTokenKind.InKeyword,
            Antlr.RavenLexer.OPERATOR => RavenTokenKind.OperatorKeyword,
            Antlr.RavenLexer.RETURN => RavenTokenKind.ReturnKeyword,
            Antlr.RavenLexer.SWITCH => RavenTokenKind.SwitchKeyword,
            Antlr.RavenLexer.TRUE => RavenTokenKind.TrueKeyword,
            Antlr.RavenLexer.WHILE => RavenTokenKind.WhileKeyword,
            Antlr.RavenLexer.WHERE => RavenTokenKind.WhereKeyword,
            Antlr.RavenLexer.COMPOSE => RavenTokenKind.ComposeKeyword,
            Antlr.RavenLexer.CONST => RavenTokenKind.ConstKeyword,
            Antlr.RavenLexer.OVERRIDE => RavenTokenKind.OverrideKeyword,
            Antlr.RavenLexer.READONLY => RavenTokenKind.ReadOnlyKeyword,
            Antlr.RavenLexer.STATIC => RavenTokenKind.StaticKeyword,
            Antlr.RavenLexer.STREAM => RavenTokenKind.StreamKeyword,
            Antlr.RavenLexer.INOUT => RavenTokenKind.InOutKeyword,
            Antlr.RavenLexer.BOOL => RavenTokenKind.BoolKeyword,
            Antlr.RavenLexer.BOOL2 => RavenTokenKind.Bool2Keyword,
            Antlr.RavenLexer.BOOL3 => RavenTokenKind.Bool3Keyword,
            Antlr.RavenLexer.BOOL4 => RavenTokenKind.Bool4Keyword,
            Antlr.RavenLexer.INT => RavenTokenKind.IntKeyword,
            Antlr.RavenLexer.INT2 => RavenTokenKind.Int2Keyword,
            Antlr.RavenLexer.INT3 => RavenTokenKind.Int3Keyword,
            Antlr.RavenLexer.INT4 => RavenTokenKind.Int4Keyword,
            Antlr.RavenLexer.UINT => RavenTokenKind.UIntKeyword,
            Antlr.RavenLexer.UINT2 => RavenTokenKind.UInt2Keyword,
            Antlr.RavenLexer.UINT3 => RavenTokenKind.UInt3Keyword,
            Antlr.RavenLexer.UINT4 => RavenTokenKind.UInt4Keyword,
            Antlr.RavenLexer.FLOAT => RavenTokenKind.FloatKeyword,
            Antlr.RavenLexer.FLOAT2 => RavenTokenKind.Float2Keyword,
            Antlr.RavenLexer.FLOAT3 => RavenTokenKind.Float3Keyword,
            Antlr.RavenLexer.FLOAT4 => RavenTokenKind.Float4Keyword,
            Antlr.RavenLexer.DOUBLE => RavenTokenKind.DoubleKeyword,
            Antlr.RavenLexer.DOUBLE2 => RavenTokenKind.Double2Keyword,
            Antlr.RavenLexer.DOUBLE3 => RavenTokenKind.Double3Keyword,
            Antlr.RavenLexer.DOUBLE4 => RavenTokenKind.Double4Keyword,
            Antlr.RavenLexer.MAT2 => RavenTokenKind.Mat2Keyword,
            Antlr.RavenLexer.MAT2X3 => RavenTokenKind.Mat2x3Keyword,
            Antlr.RavenLexer.MAT2X4 => RavenTokenKind.Mat2x4Keyword,
            Antlr.RavenLexer.MAT3 => RavenTokenKind.Mat3Keyword,
            Antlr.RavenLexer.MAT3X2 => RavenTokenKind.Mat3x2Keyword,
            Antlr.RavenLexer.MAT3X4 => RavenTokenKind.Mat3x4Keyword,
            Antlr.RavenLexer.MAT4 => RavenTokenKind.Mat4Keyword,
            Antlr.RavenLexer.MAT4X2 => RavenTokenKind.Mat4x2Keyword,
            Antlr.RavenLexer.MAT4X3 => RavenTokenKind.Mat4x3Keyword,
            Antlr.RavenLexer.OPEN_BRACE => RavenTokenKind.OpenBrace,
            Antlr.RavenLexer.CLOSE_BRACE => RavenTokenKind.CloseBrace,
            Antlr.RavenLexer.OPEN_BRACKET => RavenTokenKind.OpenBracket,
            Antlr.RavenLexer.CLOSE_BRACKET => RavenTokenKind.CloseBracket,
            Antlr.RavenLexer.OPEN_PARENS => RavenTokenKind.OpenParen,
            Antlr.RavenLexer.CLOSE_PARENS => RavenTokenKind.CloseParen,
            Antlr.RavenLexer.DOT => RavenTokenKind.Dot,
            Antlr.RavenLexer.DOUBLE_DOT => RavenTokenKind.DotDot,
            Antlr.RavenLexer.COMMA => RavenTokenKind.Comma,
            Antlr.RavenLexer.COLON => RavenTokenKind.Colon,
            Antlr.RavenLexer.INTERR => RavenTokenKind.Question,
            Antlr.RavenLexer.LAMBDA => RavenTokenKind.Arrow,
            Antlr.RavenLexer.PLUS => RavenTokenKind.Plus,
            Antlr.RavenLexer.MINUS => RavenTokenKind.Minus,
            Antlr.RavenLexer.STAR => RavenTokenKind.Star,
            Antlr.RavenLexer.DIV => RavenTokenKind.Slash,
            Antlr.RavenLexer.PERCENT => RavenTokenKind.Percent,
            Antlr.RavenLexer.AMP => RavenTokenKind.Ampersand,
            Antlr.RavenLexer.BITWISE_OR => RavenTokenKind.Bar,
            Antlr.RavenLexer.CARET => RavenTokenKind.Caret,
            Antlr.RavenLexer.BANG => RavenTokenKind.Bang,
            Antlr.RavenLexer.TILDE => RavenTokenKind.Tilde,
            Antlr.RavenLexer.ASSIGNMENT => RavenTokenKind.Equals,
            Antlr.RavenLexer.LT => RavenTokenKind.LessThan,
            Antlr.RavenLexer.GT => RavenTokenKind.GreaterThan,
            Antlr.RavenLexer.OP_INC => RavenTokenKind.PlusPlus,
            Antlr.RavenLexer.OP_DEC => RavenTokenKind.MinusMinus,
            Antlr.RavenLexer.OP_AND => RavenTokenKind.AmpersandAmpersand,
            Antlr.RavenLexer.OP_OR => RavenTokenKind.BarBar,
            Antlr.RavenLexer.OP_EQ => RavenTokenKind.EqualsEquals,
            Antlr.RavenLexer.OP_NE => RavenTokenKind.BangEquals,
            Antlr.RavenLexer.OP_LE => RavenTokenKind.LessThanEquals,
            Antlr.RavenLexer.OP_GE => RavenTokenKind.GreaterThanEquals,
            Antlr.RavenLexer.OP_ADD_ASSIGNMENT => RavenTokenKind.PlusEquals,
            Antlr.RavenLexer.OP_SUB_ASSIGNMENT => RavenTokenKind.MinusEquals,
            Antlr.RavenLexer.OP_MULT_ASSIGNMENT => RavenTokenKind.StarEquals,
            Antlr.RavenLexer.OP_DIV_ASSIGNMENT => RavenTokenKind.SlashEquals,
            Antlr.RavenLexer.OP_MOD_ASSIGNMENT => RavenTokenKind.PercentEquals,
            Antlr.RavenLexer.OP_AND_ASSIGNMENT => RavenTokenKind.AmpersandEquals,
            Antlr.RavenLexer.OP_OR_ASSIGNMENT => RavenTokenKind.BarEquals,
            Antlr.RavenLexer.OP_XOR_ASSIGNMENT => RavenTokenKind.CaretEquals,
            Antlr.RavenLexer.OP_LEFT_SHIFT => RavenTokenKind.LessThanLessThan,
            Antlr.RavenLexer.OP_LEFT_SHIFT_ASSIGNMENT => RavenTokenKind.LessThanLessThanEquals,
            Antlr.RavenLexer.OP_RIGHT_SHIFT => RavenTokenKind.GreaterThanGreaterThan,
            Antlr.RavenLexer.OP_RIGHT_SHIFT_ASSIGNMENT => RavenTokenKind.GreaterThanGreaterThanEquals,
            Antlr.RavenLexer.OP_UNSIGNED_RIGHT_SHIFT => RavenTokenKind.UnsignedShiftRight,
            Antlr.RavenLexer.OP_UNSIGNED_RIGHT_SHIFT_ASSIGNMENT => RavenTokenKind.UnsignedShiftRightEquals,
            _ => throw new InvalidOperationException($"Unmapped ANTLR token type {antlrType}.")
        };
}

/// <summary>The corpus both differentials run over: every fixture plus the shipped library sample.</summary>
static class CorpusLocator {
    public static IEnumerable<string> All() {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures");
        foreach (var file in Directory.EnumerateFiles(fixtures, "*.rvn").OrderBy(f => f, StringComparer.Ordinal)) {
            yield return Path.GetFullPath(file);
        }

        // The whole shipped library tree, recursively: the two examples at the root — Example1 the
        // syntax showcase, Example2 the compute shader — and every package beside them. Having them
        // here is the only reason `else if` was ever noticed, and it closes § G's parse row for the
        // library rather than for the fixtures alone.
        var library = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library");
        foreach (var file in Directory.EnumerateFiles(library, "*.rvn", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal)) {
            yield return Path.GetFullPath(file);
        }
    }
}
