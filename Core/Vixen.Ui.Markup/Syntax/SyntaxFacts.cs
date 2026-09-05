// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Markup.Syntax;

/// <summary>
///     Static facts about VXML kinds: the canonical text of fixed-text tokens, used both when the
///     parser fabricates a missing token and when a diagnostic wants to name what it expected.
/// </summary>
public static class SyntaxFacts {
    /// <summary>Canonical text for a fixed-text token kind, or the empty string if it has none.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The text a token of that kind always spells, if it always spells one.</returns>
    public static string GetText(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.ComponentKeyword => "@component",
            SyntaxKind.UsingKeyword => "@using",
            SyntaxKind.StaticKeyword => "static",
            SyntaxKind.NamespaceKeyword => "@namespace",
            SyntaxKind.TagKeyword => "@tag",
            SyntaxKind.InheritsKeyword => "@inherits",
            SyntaxKind.CodeKeyword => "@code",
            SyntaxKind.IfKeyword => "@if",
            SyntaxKind.ElseKeyword => "else",
            SyntaxKind.ForKeyword => "@for",
            SyntaxKind.VarKeyword => "var",
            SyntaxKind.InKeyword => "in",
            SyntaxKind.SwitchKeyword => "@switch",
            SyntaxKind.CaseKeyword => "case",
            SyntaxKind.DefaultKeyword => "default",

            SyntaxKind.LessThanToken => "<",
            SyntaxKind.LessThanSlashToken => "</",
            SyntaxKind.GreaterThanToken => ">",
            SyntaxKind.SlashGreaterThanToken => "/>",
            SyntaxKind.EqualsToken => "=",
            SyntaxKind.AtToken => "@",
            SyntaxKind.OpenParenToken => "(",
            SyntaxKind.CloseParenToken => ")",
            SyntaxKind.OpenBraceToken => "{",
            SyntaxKind.CloseBraceToken => "}",
            SyntaxKind.ColonToken => ":",

            _ => string.Empty
        };

    /// <summary>
    ///     Whether a tag name names a component rather than an intrinsic element.
    /// </summary>
    /// <param name="name">The tag name as written.</param>
    /// <returns>True when the name starts with an uppercase letter.</returns>
    /// <remarks>
    ///     The React and Blazor rule, chosen for the same reason: it is decidable from the
    ///     characters alone, so nothing has to consult a registry — and a registry is exactly what
    ///     a parser cannot have, because the types it would name are being compiled alongside it.
    /// </remarks>
    public static bool IsComponentName(string name) => name.Length > 0 && char.IsUpper(name[0]);
}
