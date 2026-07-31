// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Markup.Parsing;

/// <summary>
///     What the lexer produces. Separate from <see cref="Syntax.SyntaxKind" /> for the same reason
///     Raven keeps the two apart: a raw token is a run of characters, a tree token is a role in a
///     grammar, and one raw kind can play more than one role — a <c>NameToken</c> is a tag name in
///     one position and an attribute name in the next.
/// </summary>
enum VxmlTokenKind {
    /// <summary>End of input. Always last, always zero width.</summary>
    EndOfFile,

    /// <summary>Whitespace. Trivia, except when it is part of a text run.</summary>
    Whitespace,

    /// <summary>An <c>&lt;!-- … --&gt;</c> comment. Always trivia.</summary>
    Comment,

    /// <summary>A run of literal text in content or inside a quoted attribute value.</summary>
    Text,

    /// <summary><c>&lt;</c>.</summary>
    LessThan,

    /// <summary><c>&lt;/</c>.</summary>
    LessThanSlash,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,

    /// <summary><c>/&gt;</c>.</summary>
    SlashGreaterThan,

    /// <summary><c>=</c> between an attribute name and its value.</summary>
    Equals,

    /// <summary><c>"</c> or <c>'</c>.</summary>
    Quote,

    /// <summary><c>@</c>.</summary>
    At,

    /// <summary><c>(</c>.</summary>
    OpenParen,

    /// <summary><c>)</c>.</summary>
    CloseParen,

    /// <summary><c>{</c>, in a position where a directive body can start.</summary>
    OpenBrace,

    /// <summary><c>}</c>, closing a directive body.</summary>
    CloseBrace,

    /// <summary><c>:</c> ending a switch arm's label.</summary>
    Colon,

    /// <summary>A tag, attribute or namespace name.</summary>
    Name,

    /// <summary>A C# expression, captured whole.</summary>
    Expression,

    /// <summary>An <c>@code</c> body, captured whole.</summary>
    Code,

    /// <summary>A <c>&lt;style&gt;</c> body, captured whole.</summary>
    Css,

    /// <summary><c>@component</c>.</summary>
    ComponentKeyword,

    /// <summary><c>@using</c>.</summary>
    UsingKeyword,

    /// <summary><c>@namespace</c>.</summary>
    NamespaceKeyword,

    /// <summary><c>@tag</c>.</summary>
    TagKeyword,

    /// <summary><c>@code</c>.</summary>
    CodeKeyword,

    /// <summary><c>@if</c>.</summary>
    IfKeyword,

    /// <summary><c>else</c>.</summary>
    ElseKeyword,

    /// <summary><c>@for</c>.</summary>
    ForKeyword,

    /// <summary><c>var</c>.</summary>
    VarKeyword,

    /// <summary><c>in</c>.</summary>
    InKeyword,

    /// <summary><c>@switch</c>.</summary>
    SwitchKeyword,

    /// <summary><c>case</c>.</summary>
    CaseKeyword,

    /// <summary><c>default</c>.</summary>
    DefaultKeyword
}
