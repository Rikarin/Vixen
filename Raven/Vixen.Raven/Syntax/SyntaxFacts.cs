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
            SyntaxKind.ClassKeyword => "class",
            SyntaxKind.RecordKeyword => "record",
            SyntaxKind.ProtocolKeyword => "protocol",
            SyntaxKind.EnumKeyword => "enum",
            SyntaxKind.InitKeyword => "init",
            SyntaxKind.RefKeyword => "ref",
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
            SyntaxKind.NotKeyword => "not",
            SyntaxKind.AndKeyword => "and",
            SyntaxKind.OrKeyword => "or",
            SyntaxKind.IsKeyword => "is",
            SyntaxKind.SwitchKeyword => "switch",
            SyntaxKind.WhenKeyword => "when",
            SyntaxKind.WhereKeyword => "where",
            SyntaxKind.OutKeyword => "out",
            SyntaxKind.SelfKeyword => "self",
            SyntaxKind.OperatorKeyword => "operator",
            SyntaxKind.ImplicitKeyword => "implicit",
            SyntaxKind.ExplicitKeyword => "explicit",
            SyntaxKind.UnderscoreToken => "_",
            SyntaxKind.InKeyword => "in",
            SyntaxKind.VarKeyword => "var",
            SyntaxKind.ValKeyword => "val",
            SyntaxKind.TrueKeyword => "true",
            SyntaxKind.FalseKeyword => "false",
            SyntaxKind.DefaultKeyword => "default",
            SyntaxKind.GlobalKeyword => "global",
            SyntaxKind.StaticKeyword => "static",
            SyntaxKind.AbstractKeyword => "abstract",
            SyntaxKind.ComposeKeyword => "compose",
            SyntaxKind.ConstKeyword => "const",
            SyntaxKind.OverrideKeyword => "override",
            SyntaxKind.PartialKeyword => "partial",
            SyntaxKind.PrivateKeyword => "private",
            SyntaxKind.ProtectedKeyword => "protected",
            SyntaxKind.PublicKeyword => "public",
            SyntaxKind.ReadOnlyKeyword => "readonly",
            SyntaxKind.VirtualKeyword => "virtual",

            // Punctuation
            SyntaxKind.DotToken => ".",
            SyntaxKind.CommaToken => ",",
            SyntaxKind.ColonToken => ":",
            SyntaxKind.EqualsToken => "=",
            SyntaxKind.ArrowToken => "=>",
            SyntaxKind.TildeToken => "~",
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
