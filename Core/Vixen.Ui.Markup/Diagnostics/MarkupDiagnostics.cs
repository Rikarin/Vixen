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

    /// <summary>A capitalised attribute on a lowercase tag, which sets no property.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tag's case decides what an attribute <i>is</i>, and until this rule nothing
    ///         said so.</b> On a capitalised tag a parameter becomes <c>n1.AccessibleName = …</c> and
    ///         Roslyn typechecks it; on a lowercase one it becomes
    ///         <c>ctx.Attribute(n1, "AccessibleName", "Save")</c>, which reaches the style tree as
    ///         data a selector can match and nothing else reads. So
    ///         <c>&lt;div AccessibleName="Save" Focusable="true"&gt;</c> compiled, ran, and did
    ///         nothing at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The check is the attribute's case, not a lookup of the property.</b> The binder
    ///         is syntax only — the generator never touches the compilation, which is what keeps a
    ///         C# edit from re-running it — so there is no list of <c>[UiProperty]</c> names to
    ///         consult here. What survives is the convention that separates the two intents: a
    ///         property is PascalCase and a selector-matchable attribute is <c>data-state</c>,
    ///         <c>role</c>, <c>aria-label</c>. A capitalised name on a plain element is therefore an
    ///         author who expected an assignment.
    ///     </para>
    ///     <para>
    ///         A warning rather than an error because a capitalised attribute <i>is</i> matchable —
    ///         <c>[AccessibleName]</c> selects on it — so the reading is legal, merely almost never
    ///         meant. Lowercase the name to say a selector was the point.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor InertElementAttribute = new(
        "VXML2020",
        "The attribute sets no property",
        "'{0}' on '<{1}>' only adds an attribute a selector can match: a lowercase tag builds a "
        + "plain element, so nothing assigns '{0}'. Use the capitalised tag of the control that has "
        + "it, or lowercase the name if a selector is what you meant.",
        BindingCategory,
        DiagnosticSeverity.Warning
    );

    /// <summary>A <c>&lt;provide&gt;</c> missing the half that makes it mean anything.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves are required and neither has a defensible default.</b> A missing
    ///     <c>type</c> cannot be inferred — <c>Provide&lt;T&gt;</c> keys on the type argument rather
    ///     than on the value's runtime type, so inferring would give the concrete class and
    ///     <c>Inject&lt;ITheme&gt;</c> would find nothing — and a missing <c>value</c> has nothing to
    ///     provide. Both would otherwise emit a tag that compiles, runs, and provides nothing, which
    ///     is a defect an author meets as an injection that silently answers null.
    /// </remarks>
    public static readonly DiagnosticDescriptor IncompleteProvide = new(
        "VXML2021",
        "'<provide>' needs both a type and a value",
        "'<provide>' has no usable '{0}'. Write '<provide type=\"ITheme\" value=\"@theme\" />': the type is "
        + "the key 'Inject<T>' looks for and cannot be inferred from the value.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>&lt;provide type&gt;</c> written as an expression rather than as a type name.</summary>
    /// <remarks>
    ///     ⚠ <b>The key is a generic argument, so it is decided when the file is compiled and not
    ///     when it runs.</b> An interpolated <c>type</c> would have to become
    ///     <c>Provide&lt;{whatever this string says}&gt;</c>, which is not a thing C# can spell — the
    ///     same reason <c>slot</c> has to be a literal name, one level further up.
    /// </remarks>
    public static readonly DiagnosticDescriptor InterpolatedProvideType = new(
        "VXML2022",
        "'<provide type>' is not a literal type name",
        "'type' becomes a generic argument, so it has to be written out. '{0}' interpolates.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>&lt;provide&gt;</c> written with children, which nothing would build.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than dropped, on <c>VXML2017</c>'s rule.</b> Children read as a
    ///     narrower scope — provide this value <i>to these</i> — and there is no such scope: a
    ///     provide is a declaration on the element it was written in, and it reaches everything after
    ///     it there. Building the children anyway would put them where the tag is, one level up from
    ///     where they were written, which is a layout the author did not ask for.
    /// </remarks>
    public static readonly DiagnosticDescriptor ProvideContent = new(
        "VXML2023",
        "'<provide>' has no content",
        "'<provide>' declares a value on the element it is written in and reaches everything after "
        + "it there, so it has nowhere to put children. Write it as '<provide … />'.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An <c>exit</c> was written outside an <c>@for</c> body.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="RefsOutsideLoop" />'s shape and <see cref="RefsOutsideLoop" />'s reason.</b>
    ///     An exit is an interval the <i>reconciler</i> holds a removed row for, so it is read by the
    ///     enclosing loop and by nothing else. Written anywhere else there is no reconciler to read
    ///     it, and the honest failure is this rather than an attribute silently dropped — which is
    ///     what an unconsumed one would be, since nothing downstream would ever ask for it.
    /// </remarks>
    public static readonly DiagnosticDescriptor ExitOutsideLoop = new(
        "VXML2024",
        "'exit' outside @for",
        "'exit' is the interval an @for holds a removed row for, and outside a loop nothing removes "
        + "rows. An element that leaves because an @if arm changed has no exit yet.",
        BindingCategory,
        DiagnosticSeverity.Error
    );

    /// <summary>An <c>exit</c> whose value is not a duration.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A literal duration and not an expression, which is the opposite of what
    ///         <c>key</c> takes.</b> A key is an identity and has to be computed per row; an exit is
    ///         the same number the author already wrote in <c>transition: opacity 200ms</c>, so it is
    ///         written the same way and read at compile time. That is also what lets this message
    ///         exist at all: an expression's mistakes are Roslyn's and land on generated code.
    ///     </para>
    ///     <para>
    ///         The optional second word is the class the row wears on its way out, which defaults to
    ///         <c>leaving</c> — the name <c>ExitSpec</c> defaults to, so the two cannot drift.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor InvalidExitDuration = new(
        "VXML2025",
        "'exit' is not a duration",
        "'{0}' is not a duration. Write the number the stylesheet already has — exit=\"200ms\" or "
        + "exit=\"0.2s\" — optionally followed by the class the row wears on its way out, as in "
        + "exit=\"200ms fading\". The default class is 'leaving'.",
        BindingCategory,
        DiagnosticSeverity.Error
    );
}
