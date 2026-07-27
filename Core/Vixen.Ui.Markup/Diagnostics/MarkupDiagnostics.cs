// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Ui.Markup.Diagnostics;

/// <summary>
///     Stable descriptors for everything VXML can complain about. <c>VXML1xxx</c> is syntax —
///     what the lexer and parser report — and <c>VXML2xxx</c> is binding, which is everything
///     structural the parser was happy to accept.
/// </summary>
/// <remarks>
///     There is deliberately no <c>VXML3xxx</c> range for type errors. Expressions reach the
///     generated C# verbatim under a <c>#line</c>, so a mistyped one is reported by Roslyn against
///     the <c>.vxml</c> line — a second, worse typechecker is exactly what this design exists to
///     avoid.
/// </remarks>
public static class MarkupDiagnostics {
    const string SyntaxCategory = "Syntax";
    const string BindingCategory = "Binding";

    // ---------------------------------------------------------------- Syntax

    /// <summary>The token stream did not match the grammar.</summary>
    public static readonly DiagnosticDescriptor SyntaxError = new(
        "VXML1001",
        "Syntax error",
        "Syntax error: {0}",
        SyntaxCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An element was opened and the file ended, or an ancestor closed, before it did.</summary>
    public static readonly DiagnosticDescriptor UnclosedElement = new(
        "VXML1002",
        "Unclosed element",
        "'<{0}>' is never closed.",
        SyntaxCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A closing tag named something other than the element it closed.</summary>
    public static readonly DiagnosticDescriptor MismatchedEndTag = new(
        "VXML1003",
        "Mismatched end tag",
        "'</{1}>' closes '<{0}>'.",
        SyntaxCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An <c>@</c> was followed by something that cannot start an expression.</summary>
    public static readonly DiagnosticDescriptor ExpectedExpression = new(
        "VXML1004",
        "Expected an expression",
        "Expected an expression after '@'. Write '@@' for a literal '@'.",
        SyntaxCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A brace, paren or bracket run reached the end of the file unbalanced.</summary>
    public static readonly DiagnosticDescriptor UnbalancedDelimiter = new(
        "VXML1005",
        "Unbalanced delimiter",
        "'{0}' is never closed.",
        SyntaxCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>&lt;style&gt;</c> block reached the end of the file without <c>&lt;/style&gt;</c>.</summary>
    public static readonly DiagnosticDescriptor UnclosedStyleBlock = new(
        "VXML1006",
        "Unclosed style block",
        "'<style>' is never closed.",
        SyntaxCategory,
        DiagnosticSeverity.Error
    );

    // ---------------------------------------------------------------- Binding

    /// <summary>The file has no <c>@component</c> header, so there is nothing to name the class.</summary>
    public static readonly DiagnosticDescriptor MissingComponentDirective = new(
        "VXML2001",
        "Missing @component",
        "A .vxml file must start with '@component Name'.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>The same attribute was written twice on one tag.</summary>
    public static readonly DiagnosticDescriptor DuplicateAttribute = new(
        "VXML2002",
        "Duplicate attribute",
        "'{0}' is set twice on this tag.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An event or binding attribute was given a literal instead of an expression.</summary>
    public static readonly DiagnosticDescriptor ExpectedExpressionValue = new(
        "VXML2003",
        "Expected an expression",
        "'{0}' needs an expression: write {0}=\"@Handler\".",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An element inside an <c>@for</c> body carries no <c>key</c>.</summary>
    /// <remarks>
    ///     A warning rather than an error, and never a silent index-keyed fallback: without a key
    ///     the reconciler cannot tell a move from a rebuild, so reordering a list destroys and
    ///     recreates every element after the first change — including their focus and scroll state.
    /// </remarks>
    public static readonly DiagnosticDescriptor MissingKey = new(
        "VXML2004",
        "Missing key in @for",
        "'<{0}>' is inside an @for and has no 'key'. Reordering will rebuild it instead of moving it.",
        BindingCategory,
        DiagnosticSeverity.Warning
    );

    /// <summary>Two <c>&lt;slot&gt;</c>s claimed the same name.</summary>
    public static readonly DiagnosticDescriptor DuplicateSlot = new(
        "VXML2005",
        "Duplicate slot",
        "There is already a slot named '{0}'.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An attribute used a directive prefix VXML does not define.</summary>
    public static readonly DiagnosticDescriptor UnknownAttributeDirective = new(
        "VXML2006",
        "Unknown attribute directive",
        "'{0}' is not a directive. VXML defines 'on:' and 'bind:'.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An event binding carried a modifier that means nothing.</summary>
    public static readonly DiagnosticDescriptor UnknownEventModifier = new(
        "VXML2007",
        "Unknown event modifier",
        "'{0}' is not an event modifier.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A component parameter's name could not be a C# identifier.</summary>
    /// <remarks>
    ///     Reported rather than emitted, because <c>n1.data-id = "x"</c> is not a member the
    ///     compiler could complain about — it is a syntax error, and one of those turns the whole
    ///     generated file into noise about everything except the attribute that caused it.
    /// </remarks>
    public static readonly DiagnosticDescriptor InvalidParameterName = new(
        "VXML2008",
        "Invalid parameter name",
        "'{0}' cannot be a parameter on '<{1}>': a component parameter is a C# property.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>Content was found where a single root element was expected.</summary>
    public static readonly DiagnosticDescriptor EmptyComponent = new(
        "VXML2009",
        "Component has no markup",
        "'{0}' declares no elements, so it would build nothing.",
        BindingCategory,
        DiagnosticSeverity.Warning
    );
}
