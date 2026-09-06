// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Syntax.Text;

namespace Vixen.Ui.Markup.Binding;

/// <summary>
///     A run of C# the binder resolved nothing about, and where it came from.
/// </summary>
/// <param name="Text">The source characters, exactly as written.</param>
/// <param name="Position">Where they are in the <c>.vxml</c>, so a <c>#line</c> can point back.</param>
/// <remarks>
///     ⚠ <b>The binder does not look inside this.</b> It carries the text and the span, and the
///     emitter copies both into the generated C# under a <c>#line</c> — which is what makes Roslyn
///     the typechecker rather than something hand-rolled here. The position is the whole reason
///     that works: without it an error lands in generated code the author has never seen.
/// </remarks>
public sealed record BoundExpression(string Text, LinePositionSpan Position);

/// <summary>What an attribute turned out to mean.</summary>
public enum BoundAttributeKind {
    /// <summary>An ordinary value: a class list, a component parameter.</summary>
    Parameter,

    /// <summary>An event handler, from <c>on:click</c> or the <c>onclick</c> alias.</summary>
    Event,

    /// <summary>A two-way binding, from <c>bind:value</c>.</summary>
    Bind,

    /// <summary>The reconciler's identity for this element, from <c>key</c>.</summary>
    Key,

    /// <summary>A member of the generated class to assign this element to, from <c>ref</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A kind of its own rather than a universal, though it means the same on both sorts of
    ///     tag.</b> <c>class</c> and <c>binding-path</c> are universal <i>and</i> land as style-tree
    ///     attributes; this one lands nowhere in the document at all — it is an assignment in the
    ///     <c>Build</c> body and nothing else — so it needs its own arm in the emitter rather than a
    ///     name on a list.
    /// </remarks>
    Ref,

    /// <summary>A value-change handler, from <c>change:Value</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Not an <see cref="Event" /> with a different name, and the distinction is the reason
    ///     <c>on:change</c> does not exist.</b> An event is a routed gesture: the runtime's table maps
    ///     a name to <c>Action&lt;UiElement, Action&lt;UiEvent&gt;, RoutingStrategy&gt;</c>, and no
    ///     entry in it can hand a handler a value. A change names a <c>[UiProperty]</c> instead — the
    ///     same thing <see cref="Bind" /> names, resolved the same way — and is delivered by
    ///     <c>UiElement.PropertyChanged</c>.
    /// </remarks>
    Changed,

    /// <summary>An <c>@for</c> row's element, registered into a keyed handle, from <c>refs</c>.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Ref" />'s answer to the question <see cref="Ref" /> cannot answer, rather
    ///     than a relaxation of it.</b> A <c>ref</c> is an assignment and a loop has many rows;
    ///     this is a registration under the key the loop reconciles on, which is the only identity a
    ///     row has that survives a reorder. The two are exclusive by position: <c>VXML2010</c> refuses
    ///     <c>ref</c> inside a loop and <c>VXML2013</c> refuses <c>refs</c> outside one.
    /// </remarks>
    Refs,

    /// <summary>The element name to create a capitalised tag under, from <c>tag</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Consumed at creation like <see cref="Key" />, not applied afterwards like every
    ///         other attribute.</b> A tag is interned into the style node when the element is made
    ///         and there is no setter for it, which is deliberate — a rule that matched
    ///         <c>scroll-view</c> for one frame and <c>add-component-list</c> for the next would be
    ///         a cascade that depends on when you looked.
    ///     </para>
    ///     <para>
    ///         <b>Why it is a language feature and not a subclass.</b> <c>@tag</c> is a header, so
    ///         "the same part under another name" needed a second type; and a control whose tag a
    ///         stylesheet names — <c>Part&lt;ScrollView&gt;("add-component-list")</c> — needed a
    ///         subclass, which <c>sealed</c> refuses. Both are one string written at the place it is
    ///         true.
    ///     </para>
    /// </remarks>
    Tag,

    /// <summary>An expression to run against what the tag made, from <c>use</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The escape for a control fed by a <i>method</i>, which is the half of shape 1 that
    ///     no amount of property binding reaches.</b> <c>Inspect(descriptor, provider, targets)</c>
    ///     is three arguments and <c>SetItems(rows)</c> is a collection; neither is a
    ///     <c>[UiProperty]</c>, so neither <see cref="Parameter" /> nor <see cref="Bind" /> can carry
    ///     it. Emitted as <c>BuildContext.Use</c>, which is an effect — so it re-runs when what it
    ///     read changes, and leaves with the region that declared it.
    /// </remarks>
    Use,

    /// <summary>Which of a component's slots this child goes into, from <c>slot="footer"</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Consumed at placement, like <see cref="Key" /> and <see cref="Tag" /> and unlike
    ///         every attribute that survives into the document.</b> It decides which parent the
    ///         emitter writes the child under and then it is gone — it is not a style-tree attribute,
    ///         and a rule written against <c>[slot]</c> matches nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read off the child and meaningful only to its parent</b>, which is the one shape
    ///         no other kind here has. <c>&lt;slot name="footer"&gt;</c> declares the hole and lives
    ///         inside the component; <c>slot="footer"</c> fills it and is written by the consumer, on
    ///         a direct child of the component's tag. The two names have to agree and nothing but a
    ///         compose-time failure can say when they do not — see <c>BuildContext.Into</c> for why
    ///         that is a throw rather than a silent drop.
    ///     </para>
    /// </remarks>
    Slot,

    /// <summary>A sentence describing this element, from <c>help="Save the scene"</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An accessible description first and a hover box second</b>, which is the whole
    ///         reason it is a directive rather than a parameter. <c>Tooltip.Attach</c> wires
    ///         <c>AccessibleRelation.DescribedBy</c>, so the sentence is in
    ///         <c>AccessibleDescription</c> and is read on demand — a tooltip that was only a hover
    ///         behaviour is a sentence written for one kind of user and withheld from another.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Universal in <see cref="Tag" />'s sense and emitted through a seam rather than
    ///         by naming a type.</b> A <c>Tooltip</c> is <c>Vixen.Ui.Controls</c>' and the generated
    ///         file cannot name it: a project referencing only <c>Vixen.Ui</c> would get generated
    ///         code that does not compile, which is worse than a refusal and cannot be refused here,
    ///         since the binder never sees the compilation. So the emitter writes
    ///         <c>ctx.Help(…)</c> and the controls fill the seam from their module initializer, the
    ///         same route <c>on:click</c> already takes.
    ///     </para>
    /// </remarks>
    Help,

    /// <summary>The menu a secondary click on this element opens, from <c>context-menu="@Menu"</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Help" />'s layering exactly — a control in <c>Vixen.Ui.Controls</c>
    ///         attached to an element by a directive whose runtime is in <c>Vixen.Ui</c> — so it
    ///         rides the same seam and decides nothing new about where the call lands.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An expression naming a menu, and not a nested <c>&lt;ContextMenu&gt;</c> the tag
    ///         adopts.</b> The nested spelling is unavailable to <i>this</i> design rather than
    ///         merely unattractive: an overlay has to be a child of the document root, and knowing
    ///         that a tag needs re-parenting means knowing that the tag names an overlay — the type
    ///         resolution the binder deliberately does not do. Written in place it would compile,
    ///         build, and open inside the panel that declared it.
    ///     </para>
    /// </remarks>
    ContextMenu,

    /// <summary>How long a leaving row is kept, from <c>exit="200ms"</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Key" />'s position exactly, and that is what settled the syntax
    ///         question.</b> An exit is a property of the <i>loop</i> and not of the element it is
    ///         written on — so the obvious spelling was a clause on the <c>@for</c> header — but
    ///         <c>key</c> is already a property of the loop written on the row's own element, read
    ///         by <c>BindFor</c> walking the bound body for it. A second convention for the second
    ///         member of the same pair would have been the language disagreeing with itself, and
    ///         this way the interval sits next to the identity it reconciles against and next to the
    ///         class list the transition is written for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A literal, unlike every other directive on this list.</b> The value is a
    ///         duration and an optional class name, read at compile time, because it is the same
    ///         number the stylesheet already carries. An expression would have made the mistake
    ///         Roslyn's, reported against generated code, for a value that can never depend on the
    ///         row.
    ///     </para>
    /// </remarks>
    Exit
}

/// <summary>One piece of an attribute's value.</summary>
public abstract record BoundValuePart;

/// <summary>Literal characters in an attribute value.</summary>
/// <param name="Text">The characters, with <c>@@</c> already decoded to <c>@</c>.</param>
public sealed record BoundLiteralPart(string Text) : BoundValuePart;

/// <summary>An interpolation inside an attribute value.</summary>
/// <param name="Expression">The C#.</param>
public sealed record BoundExpressionPart(BoundExpression Expression) : BoundValuePart;

/// <summary>One resolved attribute.</summary>
/// <param name="Kind">What it means.</param>
/// <param name="Name">The name with its directive prefix and modifiers stripped.</param>
/// <param name="Modifiers">An event's modifiers, in source order: <c>stop</c>, <c>prevent</c>, …</param>
/// <param name="Value">The value's parts; empty for a valueless attribute such as <c>scoped</c>.</param>
/// <param name="NamePosition">
///     Where the name is in the <c>.vxml</c>. A component parameter becomes a property assignment,
///     so an unknown one is Roslyn's error to report — and it can only report it at the right place
///     if the emitter knows where the name was written.
/// </param>
public sealed record BoundAttribute(
    BoundAttributeKind Kind,
    string Name,
    ImmutableArray<string> Modifiers,
    ImmutableArray<BoundValuePart> Value,
    LinePositionSpan NamePosition
) {
    /// <summary>The value when it is one literal and nothing else, otherwise null.</summary>
    public string? Literal => Value is [BoundLiteralPart literal] ? literal.Text : null;

    /// <summary>The value when it is one expression and nothing else, otherwise null.</summary>
    public BoundExpression? Expression => Value is [BoundExpressionPart part] ? part.Expression : null;

    /// <summary>Whether any part of the value has to be evaluated at run time.</summary>
    public bool IsDynamic {
        get {
            foreach (var part in Value) {
                if (part is BoundExpressionPart) {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>Anything the emitter can build.</summary>
public abstract record BoundNode;

/// <summary>Literal text content.</summary>
/// <param name="Text">The characters, with <c>@@</c> already decoded to <c>@</c>.</param>
public sealed record BoundText(string Text) : BoundNode;

/// <summary>An interpolated expression in content position.</summary>
/// <param name="Expression">The C#.</param>
public sealed record BoundInterpolation(BoundExpression Expression) : BoundNode;

/// <summary>An element or a component instance.</summary>
/// <param name="Tag">The tag as written.</param>
/// <param name="IsComponent">Whether the name is PascalCase and so names a type.</param>
/// <param name="Attributes">Its resolved attributes, <c>key</c> included.</param>
/// <param name="Children">Its content.</param>
/// <param name="TagPosition">Where the tag name is, so an unknown component reports on the tag.</param>
public sealed record BoundElement(
    string Tag,
    bool IsComponent,
    ImmutableArray<BoundAttribute> Attributes,
    ImmutableArray<BoundNode> Children,
    LinePositionSpan TagPosition
) : BoundNode {
    /// <summary>The one reserved lowercase tag that is not an element.</summary>
    public const string SelfTag = "self";

    /// <summary>Whether this is <c>&lt;self /&gt;</c>, which names the host rather than a child.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The thing a <c>.vxml</c> body has no other spelling for.</b> <c>on:</c> is an
    ///         attribute on a tag and a component's markup roots are its host's <i>children</i>, so
    ///         a handler on the component's own element — which is where a picker takes Down and
    ///         Enter before the search box treats them as caret movement — could not be written at
    ///         all. Five capture-leg handlers across three editor pickers stayed hand-written for
    ///         exactly that.
    ///     </para>
    ///     <para>
    ///         It is not a component even though it builds no element of its own, because
    ///         everything else about it is an element: its attributes are <c>class</c>,
    ///         <c>style</c>, <c>on:</c> and <c>bind:</c> applied to a <c>UiElement</c>, which is
    ///         what <see cref="IsComponent" /> being false already means to the emitter.
    ///     </para>
    /// </remarks>
    public bool IsSelf => !IsComponent && string.Equals(Tag, SelfTag, StringComparison.Ordinal);

    /// <summary>The <c>key</c> attribute's expression, if it has one.</summary>
    public BoundExpression? Key {
        get {
            foreach (var attribute in Attributes) {
                if (attribute.Kind == BoundAttributeKind.Key) {
                    return attribute.Expression;
                }
            }

            return null;
        }
    }

    /// <summary>The <c>exit</c> attribute, if it has one.</summary>
    /// <remarks>
    ///     Read by the enclosing <c>@for</c> and by nothing else, exactly as <see cref="Key" /> is.
    ///     <c>VXML2024</c> is what stops one being written where no loop will come looking.
    /// </remarks>
    public BoundAttribute? ExitAttribute {
        get {
            foreach (var attribute in Attributes) {
                if (attribute.Kind == BoundAttributeKind.Exit) {
                    return attribute;
                }
            }

            return null;
        }
    }

    /// <summary>The <c>tag</c> attribute, if it has one.</summary>
    /// <remarks>
    ///     Read by the emitter <i>before</i> it walks the attribute list, because the tag is an
    ///     argument to the call that creates the element and every other attribute is a statement
    ///     after it.
    /// </remarks>
    public BoundAttribute? TagOverride {
        get {
            foreach (var attribute in Attributes) {
                if (attribute.Kind == BoundAttributeKind.Tag) {
                    return attribute;
                }
            }

            return null;
        }
    }
}

/// <summary>A content-projection point.</summary>
/// <param name="Name">The slot's name; the default slot is named <c>default</c>.</param>
public sealed record BoundSlot(string Name) : BoundNode;

/// <summary>An ambient value put on the element the tag was written in.</summary>
/// <param name="Type">The key, as the author wrote it. Verbatim C#; the binder does not resolve it.</param>
/// <param name="Value">What is provided.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The key is written out and cannot be inferred, and that is not a limitation of this
///         tag.</b> <c>Provide&lt;T&gt;</c> keys on the type argument rather than on the value's
///         runtime type — deliberately, so an interface is the useful key and a subclass cannot
///         shadow its base — so an inferred key would be the concrete class every time and
///         <c>Inject&lt;ITheme&gt;</c> would find nothing. The binder could not infer it in any case:
///         it never touches the compilation.
///     </para>
///     <para>
///         ⚠ <b>Provided in document order, which is what makes the ordering readable rather than
///         magic.</b> The emitter writes nodes in the order they are written, so a
///         <c>&lt;provide&gt;</c> above its siblings is in place before any of them is built and one
///         written below them is not. That is the same rule an author already reads the file by.
///     </para>
/// </remarks>
public sealed record BoundProvide(string Type, BoundExpression Value) : BoundNode;

/// <summary>An <c>@inject</c> header: the property that reads an ambient value.</summary>
/// <param name="Type">
///     The key, as the author wrote it, carried as an expression for its span alone — the emitter
///     writes it where a type goes, under its own <c>#line</c>.
/// </param>
/// <param name="Name">What the generated property is called.</param>
/// <remarks>
///     ⚠ <b>Not a <see cref="BoundNode" />, because it builds nothing.</b> <c>&lt;provide&gt;</c> is
///     a statement in <c>Build</c> and belongs in the content; its mirror is a <i>member</i>, so it
///     travels beside <c>Usings</c> where the other file-level headers are. That is also why the
///     two cannot be checked against each other: a provide happens at a place in a tree and an
///     inject happens at a moment in a run.
/// </remarks>
public sealed record BoundInject(BoundExpression Type, string Name);

/// <summary>One arm of an <c>@if</c> chain.</summary>
/// <param name="Condition">The C# the arm tests.</param>
/// <param name="Body">What it builds.</param>
public sealed record BoundBranch(BoundExpression Condition, ImmutableArray<BoundNode> Body);

/// <summary>An <c>@if</c>, with every <c>else if</c> flattened into the branch list.</summary>
/// <param name="Branches">The tested arms, in order.</param>
/// <param name="Else">The untested arm; empty when there is none.</param>
public sealed record BoundIf(ImmutableArray<BoundBranch> Branches, ImmutableArray<BoundNode> Else) : BoundNode;

/// <summary>An <c>@for</c>.</summary>
/// <param name="Variable">The loop variable's name.</param>
/// <param name="Sequence">The C# that produces the items.</param>
/// <param name="Key">The body's key expression, if its root element carries one.</param>
/// <param name="Body">What each item builds.</param>
/// <param name="Index">
///     The name bound to the row's position, or null when the loop declares none.
///     ⚠ <b>It arrives in the body as a <c>Signal&lt;int&gt;</c> and not as an <c>int</c>, which is
///     the whole of the feature rather than an implementation note.</b> <c>BuildContext.For</c>
///     reuses a surviving key's region and never re-runs its body, so a position captured by a
///     lambda is the position that row had when its key first appeared — right until anything moves,
///     and silently wrong afterwards. A signal the reconciler writes on each pass is re-read by
///     whatever in the body read it, and a row that did not move costs an equality check.
/// </param>
public sealed record BoundFor(
    string Variable,
    BoundExpression Sequence,
    BoundExpression? Key,
    ImmutableArray<BoundNode> Body,
    string? Index = null
) : BoundNode {
    /// <summary>
    ///     How long a removed row is kept, in whole milliseconds, or null to remove it at once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Init-only properties rather than a sixth positional parameter, deliberately.</b>
    ///         A record's primary constructor is public surface, so widening it is a removal and an
    ///         addition in the same breath — and there is nothing here a caller has to supply. Null
    ///         is the shape every loop in the tree already has.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two properties rather than the record that would pair them.</b> A pair type is
    ///         the better C# and would be a new public type in an assembly whose whole
    ///         <c>Binding</c> namespace is a documentation exemption — a list this repository allows
    ///         to shrink and not to grow. <see cref="ExitClass" /> is meaningless without this, which
    ///         is what the pair would have said; it is said here instead.
    ///     </para>
    ///     <para>
    ///         Milliseconds because that is what a stylesheet's own durations are and what
    ///         <c>TimeSpan.FromMilliseconds</c> takes.
    ///     </para>
    /// </remarks>
    public int? ExitAfter { get; init; }

    /// <summary>
    ///     The class a leaving row wears, or null to take <c>ExitSpec</c>'s own default.
    /// </summary>
    /// <remarks>
    ///     ⚠ Null rather than the string <c>"leaving"</c> copied down here. Two places holding one
    ///     default is how the two come to disagree, so the emitter omits the argument instead.
    /// </remarks>
    public string? ExitClass { get; init; }

    /// <summary>The <c>@empty</c> arm's content; empty when the loop declares none.</summary>
    /// <remarks>
    ///     ⚠ <b>An empty array is "no arm", and that is sound because an arm that draws nothing and
    ///     no arm at all are the same picture.</b> <c>BoundIf.Else</c> already makes this bargain
    ///     for the same reason, and it is what lets this be an <c>ImmutableArray</c> rather than a
    ///     nullable one every consumer would have to unwrap.
    /// </remarks>
    public ImmutableArray<BoundNode> Empty { get; init; } = ImmutableArray<BoundNode>.Empty;
}

/// <summary>One arm of an <c>@switch</c>.</summary>
/// <param name="Pattern">The C# pattern, or null for <c>default</c>.</param>
/// <param name="Body">What the arm builds.</param>
public sealed record BoundCase(BoundExpression? Pattern, ImmutableArray<BoundNode> Body);

/// <summary>An <c>@switch</c>.</summary>
/// <param name="Subject">The C# being matched.</param>
/// <param name="Cases">The arms, in source order.</param>
public sealed record BoundSwitch(BoundExpression Subject, ImmutableArray<BoundCase> Cases) : BoundNode;

/// <summary>A whole <c>.vxml</c>, resolved.</summary>
/// <param name="Name">The class the emitter writes a partial for.</param>
/// <param name="Namespace">
///     The namespace the file asked for, or null to take whatever the caller offers.
/// </param>
/// <param name="Tag">
///     The element name the component's host answers to, or null to take the type's name in lower
///     case.
/// </param>
/// <param name="Inherits">
///     What the generated class derives from, or null for <c>Component</c>.
/// </param>
/// <param name="Usings">Namespaces to import, in source order.</param>
/// <param name="Injects">The <c>@inject</c> headers, in source order; one generated property each.</param>
/// <param name="Code">Every <c>@code</c> body, in source order. Multiple blocks concatenate.</param>
/// <param name="Content">The markup.</param>
/// <param name="Css">The <c>&lt;style&gt;</c> body, if there is one.</param>
/// <param name="CssIsScoped">Whether that style block carried <c>scoped</c>.</param>
public sealed record BoundComponent(
    string Name,
    string? Namespace,
    string? Tag,
    BoundExpression? Inherits,
    ImmutableArray<string> Usings,
    ImmutableArray<BoundInject> Injects,
    ImmutableArray<BoundExpression> Code,
    ImmutableArray<BoundNode> Content,
    string? Css,
    bool CssIsScoped
);
