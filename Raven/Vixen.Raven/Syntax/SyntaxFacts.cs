// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Syntax;

/// <summary>
///     Static facts about syntax kinds. Currently the canonical source text of
///     fixed-text tokens (keywords, punctuation) used when a token is synthesized
///     without an originating source token.
/// </summary>
public static class SyntaxFacts {
    /// <summary>
    ///     Canonical text for a fixed-text token kind, or empty string if the kind
    ///     has no fixed text (identifiers, literals, end-of-file).
    /// </summary>
    public static string GetText(SyntaxKind kind) =>
        kind switch {
            // Keywords
            SyntaxKind.PackageKeyword => "package",
            SyntaxKind.ImportKeyword => "import",
            SyntaxKind.ShaderKeyword => "shader",
            SyntaxKind.StructKeyword => "struct",
            SyntaxKind.ProtocolKeyword => "protocol",
            SyntaxKind.EnumKeyword => "enum",
            SyntaxKind.InitKeyword => "init",
            SyntaxKind.GetKeyword => "get",
            SyntaxKind.SetKeyword => "set",
            SyntaxKind.WillSetKeyword => "willSet",
            SyntaxKind.DidSetKeyword => "didSet",
            SyntaxKind.FuncKeyword => "func",
            SyntaxKind.ReturnKeyword => "return",
            SyntaxKind.IfKeyword => "if",
            SyntaxKind.ElseKeyword => "else",
            SyntaxKind.ForKeyword => "for",
            SyntaxKind.WhileKeyword => "while",
            SyntaxKind.SwitchKeyword => "switch",
            SyntaxKind.CaseKeyword => "case",
            SyntaxKind.BreakKeyword => "break",
            SyntaxKind.ContinueKeyword => "continue",
            SyntaxKind.WhereKeyword => "where",
            SyntaxKind.SelfKeyword => "self",
            SyntaxKind.BaseKeyword => "base",
            SyntaxKind.RepeatKeyword => "repeat",
            SyntaxKind.OperatorKeyword => "operator",
            SyntaxKind.InKeyword => "in",
            SyntaxKind.VarKeyword => "var",
            SyntaxKind.ValKeyword => "val",
            SyntaxKind.TrueKeyword => "true",
            SyntaxKind.FalseKeyword => "false",
            SyntaxKind.DefaultKeyword => "default",
            SyntaxKind.StaticKeyword => "static",
            SyntaxKind.ComposeKeyword => "compose",
            SyntaxKind.ConstKeyword => "const",
            SyntaxKind.OverrideKeyword => "override",
            SyntaxKind.ReadOnlyKeyword => "readonly",
            SyntaxKind.StreamKeyword => "stream",
            SyntaxKind.InOutKeyword => "inout",

            // Punctuation
            SyntaxKind.DotToken => ".",
            SyntaxKind.CommaToken => ",",
            SyntaxKind.ColonToken => ":",
            SyntaxKind.EqualsToken => "=",
            SyntaxKind.ArrowToken => "=>",
            SyntaxKind.DotDotToken => "..",
            SyntaxKind.QuestionToken => "?",
            SyntaxKind.OpenParenToken => "(",
            SyntaxKind.CloseParenToken => ")",
            SyntaxKind.OpenBracketToken => "[",
            SyntaxKind.CloseBracketToken => "]",
            SyntaxKind.LessThanToken => "<",
            SyntaxKind.GreaterThanToken => ">",
            SyntaxKind.OpenBraceToken => "{",
            SyntaxKind.CloseBraceToken => "}",

            _ => string.Empty
        };
}
