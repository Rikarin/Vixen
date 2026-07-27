// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;

namespace Vixen.Ui.Markup.Syntax;

/// <summary>Every kind a VXML green node, token or trivia can carry.</summary>
public enum SyntaxKind : ushort {
    /// <summary>Unset. No node in a well-formed tree carries it.</summary>
    None,

    /// <summary>
    ///     The anonymous list node. Pinned to <see cref="SyntaxKinds.List" /> because the shared
    ///     tree creates list nodes without knowing VXML's enum; if the values diverged, casting a
    ///     list node's <c>RawKind</c> would name the wrong member.
    /// </summary>
    ListKind = SyntaxKinds.List,

    // ------------------------------------------------------------------ Nodes

    /// <summary>A whole file.</summary>
    Document,

    /// <summary><c>@component Name</c>.</summary>
    ComponentDirective,

    /// <summary><c>@using Namespace</c>.</summary>
    UsingDirective,

    /// <summary><c>@code { … }</c>.</summary>
    CodeBlock,

    /// <summary>A braced run of content.</summary>
    MarkupBlock,

    /// <summary>An opening or self-closing tag.</summary>
    StartTag,

    /// <summary>A closing tag.</summary>
    EndTag,

    /// <summary>An element.</summary>
    Element,

    /// <summary><c>&lt;style&gt;…&lt;/style&gt;</c>.</summary>
    StyleBlock,

    /// <summary>One attribute on a tag.</summary>
    Attribute,

    /// <summary>A quoted attribute value.</summary>
    QuotedAttributeValue,

    /// <summary>An unquoted <c>@expr</c> attribute value.</summary>
    ExpressionAttributeValue,

    /// <summary>A run of literal text.</summary>
    Text,

    /// <summary><c>@expr</c>.</summary>
    Interpolation,

    /// <summary><c>@if</c> with its body and optional <c>else</c>.</summary>
    If,

    /// <summary>The <c>else</c> arm of an <c>@if</c>.</summary>
    ElseClause,

    /// <summary><c>@for</c>.</summary>
    For,

    /// <summary><c>@switch</c>.</summary>
    Switch,

    /// <summary>One <c>case</c> or <c>default</c> arm.</summary>
    SwitchSection,

    // ------------------------------------------------------------------ Keywords

    /// <summary><c>@component</c>.</summary>
    ComponentKeyword,

    /// <summary><c>@using</c>.</summary>
    UsingKeyword,

    /// <summary><c>@code</c>.</summary>
    CodeKeyword,

    /// <summary><c>@if</c>.</summary>
    IfKeyword,

    /// <summary><c>else</c> — the one keyword written without an <c>@</c>, because it follows a brace.</summary>
    ElseKeyword,

    /// <summary><c>@for</c>.</summary>
    ForKeyword,

    /// <summary><c>var</c>, in an <c>@for</c> header.</summary>
    VarKeyword,

    /// <summary><c>in</c>, in an <c>@for</c> header.</summary>
    InKeyword,

    /// <summary><c>@switch</c>.</summary>
    SwitchKeyword,

    /// <summary><c>case</c>.</summary>
    CaseKeyword,

    /// <summary><c>default</c>.</summary>
    DefaultKeyword,

    // ------------------------------------------------------------------ Punctuation

    /// <summary><c>&lt;</c> opening a tag.</summary>
    LessThanToken,

    /// <summary><c>&lt;/</c> opening a closing tag.</summary>
    LessThanSlashToken,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThanToken,

    /// <summary><c>/&gt;</c>, which closes a tag and its element at once.</summary>
    SlashGreaterThanToken,

    /// <summary><c>=</c>.</summary>
    EqualsToken,

    /// <summary><c>"</c> or <c>'</c> around an attribute value.</summary>
    QuoteToken,

    /// <summary><c>@</c> introducing an expression.</summary>
    AtToken,

    /// <summary><c>(</c>.</summary>
    OpenParenToken,

    /// <summary><c>)</c>.</summary>
    CloseParenToken,

    /// <summary><c>{</c>.</summary>
    OpenBraceToken,

    /// <summary><c>}</c>.</summary>
    CloseBraceToken,

    /// <summary><c>:</c> ending a switch arm's label.</summary>
    ColonToken,

    // ------------------------------------------------------------------ Terminals

    /// <summary>A bare identifier: a component name, an <c>@for</c> variable.</summary>
    IdentifierToken,

    /// <summary>A tag, attribute or namespace name — dots, dashes and colons included.</summary>
    NameToken,

    /// <summary>Literal text in content or inside a quoted attribute value.</summary>
    TextToken,

    /// <summary>A C# expression, verbatim and unparsed.</summary>
    ExpressionToken,

    /// <summary>The body of an <c>@code</c> block, verbatim and unparsed.</summary>
    CodeToken,

    /// <summary>The body of a <c>&lt;style&gt;</c> block, verbatim and unparsed.</summary>
    CssToken,

    /// <summary>End of file. Always the last token, always zero width.</summary>
    EndOfFileToken,

    // ------------------------------------------------------------------ Trivia

    /// <summary>Whitespace the tree carries but the grammar ignores.</summary>
    WhitespaceTrivia,

    /// <summary>An <c>&lt;!-- … --&gt;</c> comment.</summary>
    CommentTrivia,

    /// <summary>Source the parser skipped while recovering. Its presence means a diagnostic was reported.</summary>
    SkippedTokensTrivia
}
