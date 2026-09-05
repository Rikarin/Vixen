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
        "'{0}' is not a directive. VXML defines 'on:', 'bind:' and 'change:'.",
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
    ///         ⚠ <b>And a list-valued <c>ref</c> has the same defect, which is why <c>refs</c> is not
    ///         one.</b> The body appends once per key <i>ever</i>, so after a filter or a reorder the
    ///         list is in an order nothing corresponds to and <c>rows[2]</c> is a different control
    ///         from the third row — silently, because both still answer. <c>refs</c> registers under
    ///         the key the reconciler matched on instead, and drops the entry with the row's region.
    ///     </para>
    ///     <para>
    ///         So what the author wants is <c>refs</c> into an <c>ElementRefs&lt;T&gt;</c>; failing
    ///         that, the loop's own container — put the <c>ref</c> on the element the <c>@for</c> is
    ///         inside — or a model keyed the way the rows are, which is what they already have.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor RefInLoop = new(
        "VXML2010",
        "'ref' inside @for",
        "'ref' cannot be inside an @for: the body runs once per item and there is one member to "
        + "assign. Write 'refs' into an ElementRefs<T>, or put the 'ref' on the element the loop "
        + "is inside.",
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

    /// <summary>A <c>refs</c> was written outside an <c>@for</c> body.</summary>
    /// <remarks>
    ///     ⚠ <b>The mirror of <see cref="RefInLoop" />, and refused for the mirror reason.</b> A
    ///     <c>refs</c> handle is keyed on the identity <c>BuildContext.For</c> reconciled the row on,
    ///     and outside a loop there is no such identity — so there is no key to file the element
    ///     under and no key a reader could ask for. One element held once is what <c>ref</c> is for.
    /// </remarks>
    public static readonly DiagnosticDescriptor RefsOutsideLoop = new(
        "VXML2013",
        "'refs' outside @for",
        "'refs' is only inside an @for: its key is the loop's, and outside one there is none. "
        + "Write 'ref' instead.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>tag</c> attribute on a lowercase tag.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Refused because it is a second spelling of something the language already
    ///         says.</b> A lowercase tag is written out as the element name — <c>&lt;fact-row&gt;</c>
    ///         creates <c>fact-row</c> — so <c>&lt;div tag="fact-row"&gt;</c> is the same tree with
    ///         the answer in a different place. Two ways to name one thing is how a stylesheet comes
    ///         to be checked against the wrong one, which is the bug <c>TypeSelectorReachTests</c>
    ///         was written for.
    ///     </para>
    ///     <para>
    ///         On a capitalised tag there is no other spelling: the tag is a <i>type name</i> and the
    ///         element's name comes from the type. That asymmetry is the attribute's whole reason to
    ///         exist, so it is exactly where it is allowed.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor TagOnElement = new(
        "VXML2014",
        "'tag' on a plain element",
        "'tag' renames what a capitalised tag creates, and '{0}' already names its own element. "
        + "Write the tag you want.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary><c>&lt;self /&gt;</c> somewhere other than the component's top level.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The loop case is the one worth an error rather than a warning.</b>
    ///         <c>&lt;self /&gt;</c> emits against the host, so a copy of it inside an <c>@for</c>
    ///         subscribes the <i>same</i> element once per row — five items, five handlers, one
    ///         click counted five times — and the count follows the data, so it is right in the
    ///         test with two rows and wrong in the panel with forty.
    ///     </para>
    ///     <para>
    ///         Nested inside an ordinary tag it is merely a lie about where it is: an author who
    ///         wrote <c>&lt;div&gt;&lt;self on:click=… /&gt;&lt;/div&gt;</c> meant the div. One rule
    ///         refuses both, and the place it is allowed is the place it reads as what it does.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor MisplacedSelf = new(
        "VXML2015",
        "'self' is not at the top level",
        "'<self />' names the component's own element, so it belongs at the top level of the markup "
        + "— not inside another tag, an @if or an @for.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary><c>slot="…"</c> on something that is not a direct child of a capitalised tag.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Position is the whole of what makes this attribute legal, so position is what the
    ///         rule checks.</b> <c>slot="footer"</c> tells the parent which of its holes to put this
    ///         child in, and only the parent's own tag can read it — a <c>&lt;div&gt;</c> has no
    ///         slots, and a grandchild's is addressed to a tag that is not listening. What answers
    ///         the name is <c>Component.Slots</c> or <c>UiElement.NamedHost</c>, and only a
    ///         capitalised tag has either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An <c>@if</c> or an <c>@for</c> body is not a direct child either, and that half
    ///         is about the emitter rather than about the model.</b> A region is anchored on the
    ///         parent it was handed and counts the children it made <i>there</i>; one built under a
    ///         different parent would be reconciled against a sibling list it is not in. So the rule
    ///         is the one the region already obeys — slot where you are — and until it was checked,
    ///         a <c>slot</c> inside a region bound clean and was dropped without a word, because the
    ///         emitter's partition reads <c>BoundElement</c> children and a region is not one.
    ///     </para>
    ///     <para>
    ///         An error rather than a warning because the two silent readings are both bad: dropped,
    ///         the child appears in the wrong place; honoured, it appears in the right place for the
    ///         wrong reason and stops working when the markup is nested one level deeper.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor MisplacedSlotAttribute = new(
        "VXML2016",
        "'slot' is not on a direct child of a capitalised tag",
        "'slot=\"{0}\"' says which of the parent's named slots this content fills, so it belongs on "
        + "a direct child of a capitalised tag. {1} is not one.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>Content written inside a <c>&lt;slot&gt;</c>, which nothing would ever draw.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reported because it was discarded in silence, which is the worse half of not
    ///         having the feature.</b> <c>&lt;slot name="footer"&gt;Nothing yet&lt;/slot&gt;</c> is
    ///         how every other framework writes fallback content, so it is what an author reaches for
    ///         — and the binder ignored the children outright, so it compiled, ran, and drew a hole
    ///         where the words were meant to be.
    ///     </para>
    ///     <para>
    ///         The feature itself is owed rather than refused: real fallback content has to be built
    ///         and then removed if the slot turns out to be filled, and which of those happened is
    ///         not known until the consumer's own build has run. Until it is, an error is the only
    ///         honest answer — see the guide, which says to put the default in the consumer.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor SlotFallbackContent = new(
        "VXML2017",
        "a slot cannot carry fallback content",
        "'<slot>' is a hole a consumer fills, and content written inside one is not drawn when the "
        + "slot is empty. Give the default to the consumer instead.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>slot</c> attribute whose value is an expression rather than one name.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A slot is resolved when the element is created and never again, exactly as
    ///         <see cref="TagOnElement" />'s <c>tag</c> is.</b> It names the <i>parent</i> the
    ///         element is built under, and nothing moves an element between parents afterwards — so
    ///         an interpolated name would be read once and be a lie for the rest of the region's
    ///         life.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Refused because it was read as the default slot, silently.</b> The emitter asks
    ///         the attribute for its literal and takes <c>default</c> when there is none, which is
    ///         the right answer for a bare <c>slot</c> — an author who wrote the word and no value
    ///         meant the default — and the wrong one for <c>slot="@Which"</c>, where it puts the
    ///         header's content in the body and reports nothing.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor DynamicSlotName = new(
        "VXML2018",
        "'slot' is not a literal name",
        "'slot' is read once, when the element is made, so it has to be a literal name. "
        + "'{0}' interpolates.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An <c>@using static</c> was also given an alias, which C# has no spelling for.</summary>
    /// <remarks>
    ///     ⚠ <b>Reported here rather than left to Roslyn.</b> Both halves of the directive are copied
    ///     verbatim, so the two together would reach the generated file as <c>using static X = Y;</c>
    ///     and be reported against a line the author never wrote — the defect this directive's
    ///     lexing was fixed for in the first place.
    /// </remarks>
    public static readonly DiagnosticDescriptor AliasedStaticImport = new(
        "VXML2019",
        "A static import cannot be aliased",
        "'@using static' imports a type's static members, so it has no name to alias. Drop '{0} =', "
        + "or drop 'static'.",
        BindingCategory,
        DiagnosticSeverity.Error
    );
}
