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

    /// <summary>A <c>ref</c> was written inside an <c>@for</c> body.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An error rather than a warning, because there is no reading of it that is
    ///         right.</b> A <c>ref</c> is one assignment to one member; a loop runs the body once per
    ///         item, so the member would hold whichever row happened to be built last — and after a
    ///         reconciliation it would hold whichever row happened to be built last <i>the first time
    ///         the sequence contained it</i>, because <c>BuildContext.For</c> reuses a surviving key's
    ///         region and does not re-run its body. A list-valued <c>ref</c> would have the same
    ///         defect and a longer explanation.
    ///     </para>
    ///     <para>
    ///         What the author wants instead is either the loop's own container — put the <c>ref</c>
    ///         on the element the <c>@for</c> is inside — or a model keyed the way the rows are, which
    ///         is what they already have.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor RefInLoop = new(
        "VXML2010",
        "'ref' inside @for",
        "'ref' cannot be inside an @for: the body runs once per item and there is one member to "
        + "assign. Put it on the element the loop is inside.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An <c>@for</c> key reads a member of the loop variable rather than the variable.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one thing about <c>@for</c> that is not obvious from the syntax, and it is the
    ///         opposite of what <see cref="MissingKey" /> teaches.</b> A key that survives an update
    ///         <i>keeps its region</i>: <c>BuildContext.For</c> matches the key, reuses the elements
    ///         and does not re-run the body, whose per-item bindings closed over the item as it was
    ///         when that key first appeared. So a row keyed on <c>row.Label</c> is a row whose every
    ///         value is frozen at the first reading for as long as the label is unchanged.
    ///     </para>
    ///     <para>
    ///         <b>Decidable without a semantic model, and only in this shape.</b> The rule an author
    ///         needs — key on the value when the item is immutable data, on the object when it holds
    ///         signals — turns on whether the item's properties are signal-backed, which is type
    ///         resolution the binder deliberately does not do. What it can see is syntax: a key that
    ///         is a <i>projection</i> of the loop variable throws away exactly the part of the item's
    ///         identity that changing it would have shown. Keying on the variable itself is right for
    ///         both kinds of model, which is why the suggested fix is the same either way.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ProjectedKey = new(
        "VXML2011",
        "@for key is a member of the item",
        "'{0}' keys on part of '{1}' rather than on '{1}' itself. A key that survives keeps its "
        + "region and its body is not re-run, so every binding in the row would be frozen at the "
        + "values '{1}' had when '{0}' first appeared. Write key=\"@{1}\".",
        BindingCategory,
        DiagnosticSeverity.Warning
    );

    /// <summary>A named <c>&lt;slot&gt;</c> in a file that declared <c>@inherits</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Because a <c>UiElement</c> has one place for content and a <c>Component</c> has as
    ///     many as it declares.</b> A component's caller writes into a named slot because
    ///     <c>Inner(Component)</c> reads a dictionary the component filled; an element answers the
    ///     same question with <c>ContentHost</c>, which is one property and therefore one slot. A
    ///     second name would be an element nothing can address — a hole in the tree that looks like
    ///     a feature.
    /// </remarks>
    public static readonly DiagnosticDescriptor NamedSlotOnElement = new(
        "VXML2012",
        "Named slot in an @inherits component",
        "'{0}' is a named slot, and an @inherits component has only one: a UiElement projects "
        + "content through 'ContentHost'. Write '<slot />'.",
        BindingCategory,
        DiagnosticSeverity.Error
    );
}
