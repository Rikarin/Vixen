// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Text;
using Vixen.Ui.Markup.Diagnostics;
using Vixen.Ui.Markup.Syntax;

namespace Vixen.Ui.Markup.Binding;

/// <summary>
///     Turns a parsed document into a <see cref="BoundComponent" />: what each tag is, what each
///     attribute means, and where every expression came from.
/// </summary>
/// <remarks>
///     <para>
///         <b>There is no semantic model here, and that is the design rather than a shortcut.</b>
///         The original sketch had the binder resolve <c>&lt;Counter Title="x" /&gt;</c> against the
///         C# type <c>Counter</c> using Roslyn's <c>Compilation</c>. It does not need to: if the
///         emitter puts the tag's name and the attribute's name into an object initializer under a
///         <c>#line</c>, then an unknown component, a misspelt parameter and a wrong type are all
///         reported by Roslyn — at the right character of the <c>.vxml</c> — with no type resolution
///         on this side at all. What is left for the binder is the set of mistakes Roslyn
///         <i>cannot</i> catch, because they are about markup rather than about C#.
///     </para>
///     <para>
///         Which is why every diagnostic below is structural: a duplicate attribute, an event
///         handler given a string, two slots with one name, a loop whose elements have no identity.
///         A rule that a C# compiler would catch anyway does not belong here.
///     </para>
/// </remarks>
public sealed class Binder {
    /// <summary>
    ///     The <c>onclick</c>-style aliases VXML accepts for <c>on:click</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A closed list, deliberately.</b> The obvious rule — "a name starting with
    ///     <c>on</c> is an event" — silently turns a component parameter called <c>online</c> into
    ///     a handler, and the author's first clue is a type error inside generated code. The
    ///     alias exists because <c>onclick</c> is what everyone types first; <c>on:</c> is the
    ///     syntax, and it works for every event including the ones not named here.
    /// </remarks>
    static readonly string[] EventAliases = [
        "click", "dblclick", "pointerdown", "pointerup", "pointermove", "pointerenter", "pointerleave",
        "wheel", "keydown", "keyup", "focus", "blur", "input", "change"
    ];

    /// <summary>Modifiers an <c>on:</c> binding may carry.</summary>
    /// <remarks>
    ///     ⚠ <b><c>handled</c> is the one that cannot be implemented in <c>BuildContext.On</c>.</b>
    ///     The other four are filters and flags around a handler this already owns; whether an event
    ///     is delivered <i>at all</i> once something has marked it handled is decided by
    ///     <c>UiElement.AddHandler</c>, which is the subscription table's call and not this one's.
    ///     That is why the table's entries take an <c>EventSubscription</c> rather than a
    ///     <c>RoutingStrategy</c>.
    /// </remarks>
    static readonly string[] EventModifiers = ["stop", "prevent", "capture", "once", "self", "handled"];

    /// <summary>The prefix of the one modifier that is a name rather than a word.</summary>
    /// <remarks>
    ///     ⚠ <b>The only open-ended modifier, and the only one that moves the subscription instead
    ///     of qualifying it.</b> <c>slot="header"</c> gives markup a spelling for writing children
    ///     into a control's part; until this there was none for putting a <i>handler</i> on one,
    ///     because <c>on:</c> is an attribute on a tag and a part is not a tag. What stood in for it
    ///     was eleven lines walking up from <c>args.Source</c> to find out which header a drag began
    ///     in — see <c>ComponentsView</c> — and the next panel that wanted one would have written
    ///     them again.
    /// </remarks>
    internal const string SlotModifierPrefix = "slot-";

    /// <summary>Whether a modifier names a slot, and which.</summary>
    /// <param name="modifier">The characters after the dot.</param>
    /// <returns>The slot's name, or null when this is not a slot modifier.</returns>
    /// <remarks>
    ///     <c>slot-</c> with nothing after it is not one: a slot with no name reaches
    ///     <c>NamedHost("")</c>, which every control answers with null, and a run-time
    ///     "publishes no slot named ''" is a worse report than the modifier diagnostic.
    /// </remarks>
    internal static string? SlotOf(string modifier) =>
        IsSlotModifier(modifier) ? modifier[SlotModifierPrefix.Length..] : null;

    static bool IsSlotModifier(string modifier) =>
        modifier.StartsWith(SlotModifierPrefix, StringComparison.Ordinal)
        && modifier.Length > SlotModifierPrefix.Length;

    readonly SourceText text;
    readonly string filePath;
    readonly DiagnosticBag diagnostics;

    /// <summary>Slot names already claimed, so the second one can be reported.</summary>
    readonly HashSet<string> slots = new(StringComparer.Ordinal);

    /// <summary>Whether the walk is inside an <c>@for</c> body, where elements need keys.</summary>
    bool inLoop;

    /// <summary>
    ///     How many <c>@for</c> bodies the walk is inside, for the rules that hold all the way down.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A depth beside <see cref="inLoop" /> rather than a fix to it.</b> That flag is
    ///     deliberately cleared when the walk enters a loop root's children, because only the roots
    ///     of a body need keys — once inside one, children move with it. <c>ref</c> needs the
    ///     opposite question: a <c>ref</c> nested three elements deep in a loop body is assigned
    ///     once per item exactly as one on the root is.
    /// </remarks>
    int loops;

    /// <summary>The innermost <c>@for</c>'s variable, so a key can be compared against it.</summary>
    string? item;

    /// <summary>Whether the file declared <c>@inherits</c>, so its class is an element.</summary>
    /// <remarks>Read before the content is bound, because <c>&lt;slot&gt;</c> means less on one.</remarks>
    bool isElement;

    /// <summary>Whether the content being bound is the component's own top level.</summary>
    /// <remarks>
    ///     ⚠ <b>Only <c>&lt;self /&gt;</c> asks, and it has to.</b> A <c>&lt;self /&gt;</c> inside an
    ///     <c>@for</c> subscribes the host once per row — the handler is on one element and the loop
    ///     runs N times — which is not a thing anybody means and is invisible until the fourth
    ///     duplicate. Nested inside a <c>&lt;div&gt;</c> it is merely a lie about where it is. One
    ///     rule refuses both.
    /// </remarks>
    bool atTopLevel;

    Binder(SourceText text, string filePath, DiagnosticBag diagnostics) {
        this.text = text;
        this.filePath = filePath;
        this.diagnostics = diagnostics;
    }

    /// <summary>Binds a parsed tree.</summary>
    /// <param name="tree">The tree to bind.</param>
    /// <param name="diagnostics">
    ///     Everything reported, the tree's own parse diagnostics included — a caller decides
    ///     whether to emit by asking one bag one question.
    /// </param>
    /// <returns>The bound component, or null when the file declares none.</returns>
    public static BoundComponent? Bind(SyntaxTree tree, out ImmutableArray<Diagnostic> diagnostics) {
        ArgumentNullException.ThrowIfNull(tree);

        var bag = new DiagnosticBag();
        bag.AddRange(tree.Diagnostics);

        var document = tree.GetDocument();
        var binder = new Binder(tree.Text ?? SourceText.From(string.Empty), tree.FilePath, bag);
        var component = binder.BindDocument(document);

        diagnostics = [.. bag.ToArray()];
        return component;
    }

    BoundComponent? BindDocument(DocumentSyntax document) {
        if (document.Component is not { } directive || directive.Identifier.IsMissing) {
            Report(MarkupDiagnostics.MissingComponentDirective, document.Component?.Span ?? new TextSpan(0, 0));
            return null;
        }

        isElement = document.Inherits is { Name.IsMissing: false };

        // An alias is carried into the model already joined, because the emitter's job is to write
        // `using {text};` and a model that made it re-assemble the two halves would be a second
        // place that has to know the alias exists.
        var usings = ImmutableArray.CreateBuilder<string>();
        foreach (var @using in document.Usings) {
            if (@using.Name.IsMissing) {
                continue;
            }

            // `using static X = Y;` is not C#, so an aliased static import is refused here rather
            // than copied through into a generated file that cannot compile — the whole reason this
            // directive is parsed at all.
            if (@using.StaticKeyword is { IsMissing: false } && @using.Alias is { IsMissing: false } aliased) {
                Report(MarkupDiagnostics.AliasedStaticImport, @using.Span, aliased.Text);
                usings.Add($"static {@using.Name.Text}");
                continue;
            }

            if (@using.StaticKeyword is { IsMissing: false }) {
                usings.Add($"static {@using.Name.Text}");
                continue;
            }

            usings.Add(
                @using.Alias is { IsMissing: false } alias
                    ? $"{alias.Text} = {@using.Name.Text}"
                    : @using.Name.Text
            );
        }

        var code = ImmutableArray.CreateBuilder<BoundExpression>();
        string? css = null;
        var cssIsScoped = false;

        // Code and style blocks are file-level rather than positional: `@code` may sit above the
        // markup or below it, and either way its members land in the same class.
        var markup = ImmutableArray.CreateBuilder<MarkupSyntax>();
        foreach (var node in document.Content) {
            switch (node) {
                case CodeBlockSyntax block when !block.Body.IsMissing:
                    code.Add(Expression(block.Body));
                    break;
                case CodeBlockSyntax:
                    break;
                case StyleBlockSyntax style:
                    css = style.Css.IsMissing ? css : (css ?? string.Empty) + style.Css.Text;
                    cssIsScoped |= HasScopedFlag(style);
                    break;
                default:
                    markup.Add(node);
                    break;
            }
        }

        // ⚠ The top level is not a capitalised tag's children either, and it was the third place a
        // `slot` was accepted and dropped. A component's own markup builds into its root; there is
        // no parent above it to publish a name, so the emitter never looks for one.
        RefuseSlotAttributes(markup, "a component's top level");

        atTopLevel = true;
        var content = BindContent(markup);
        atTopLevel = false;

        // Recursively: a component whose whole markup sits inside an `@if` is not empty, and an
        // emptiness check that only looked at the top level would say it was.
        if (!BuildsAnything(content)) {
            Report(MarkupDiagnostics.EmptyComponent, directive.Identifier.Span, directive.Identifier.Text);
        }

        // A missing name is a parse error that has already been reported; binding it as "no
        // namespace asked for" keeps the emitter's fallback rather than declaring a class inside a
        // namespace called nothing.
        var @namespace = document.Namespace is { Name.IsMissing: false } named ? named.Name.Text : null;
        var tag = document.Tag is { Name.IsMissing: false } tagged ? tagged.Name.Text : null;

        // Carried as an expression for its span alone. Nothing here resolves it — the emitter writes
        // it where a base type goes, under its own `#line`, and a base that does not exist or cannot
        // be derived from is Roslyn's error on the characters the author wrote.
        var inherits = document.Inherits is { Name.IsMissing: false } based ? Expression(based.Name) : null;

        return new(
            directive.Identifier.Text,
            @namespace,
            tag,
            inherits,
            usings.ToImmutable(),
            code.ToImmutable(),
            content,
            css,
            cssIsScoped
        );
    }

    static bool BuildsAnything(ImmutableArray<BoundNode> content) {
        foreach (var node in content) {
            var builds = node switch {
                BoundElement or BoundSlot => true,
                BoundIf @if => @if.Branches.Any(branch => BuildsAnything(branch.Body)) || BuildsAnything(@if.Else),
                BoundSwitch @switch => @switch.Cases.Any(section => BuildsAnything(section.Body)),
                BoundFor @for => BuildsAnything(@for.Body),
                _ => false
            };

            if (builds) {
                return true;
            }
        }

        return false;
    }

    static bool HasScopedFlag(StyleBlockSyntax style) {
        foreach (var attribute in style.StartTag.Attributes) {
            if (string.Equals(attribute.Name.Text, "scoped", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    // ================================================================== Content

    /// <remarks>
    ///     ⚠ <b>Every nested content is bound through this overload and only the document's own goes
    ///     through the other, which is what makes <see cref="atTopLevel" /> one line rather than a
    ///     save-and-restore in each of the four things that can contain markup.</b> A
    ///     <c>MarkupBlockSyntax</c> — the braces after <c>@if</c> or <c>@for</c> — comes through here
    ///     too, and correctly: a brace block is always somebody's body.
    /// </remarks>
    ImmutableArray<BoundNode> BindContent(SyntaxList<MarkupSyntax> content) {
        var nodes = ImmutableArray.CreateBuilder<MarkupSyntax>(content.Count);
        foreach (var node in content) {
            nodes.Add(node);
        }

        var outer = atTopLevel;
        atTopLevel = false;

        var bound = BindContent(nodes);
        atTopLevel = outer;

        return bound;
    }

    ImmutableArray<BoundNode> BindContent(IEnumerable<MarkupSyntax> content) {
        var bound = ImmutableArray.CreateBuilder<BoundNode>();

        foreach (var node in content) {
            switch (node) {
                case TextSyntax text:
                    Append(bound, Decode(text.TextToken.Text));
                    break;

                case InterpolationSyntax interpolation when !interpolation.Expression.IsMissing:
                    bound.Add(new BoundInterpolation(Expression(interpolation.Expression)));
                    break;

                case InterpolationSyntax:
                    break;

                case ElementSyntax element:
                    bound.Add(BindElement(element));
                    break;

                case IfSyntax @if:
                    bound.Add(BindIf(@if));
                    break;

                case ForSyntax @for:
                    bound.Add(BindFor(@for));
                    break;

                case SwitchSyntax @switch:
                    bound.Add(BindSwitch(@switch));
                    break;

                case MarkupBlockSyntax block:
                    bound.AddRange(BindContent(block.Content));
                    break;

                default:
                    break;
            }
        }

        return bound.ToImmutable();
    }

    /// <summary>
    ///     Adds text, merging it into the run before it. Two literals separated by an interpolation
    ///     that turned out to be empty are one run, not two.
    /// </summary>
    static void Append(ImmutableArray<BoundNode>.Builder bound, string text) {
        if (text.Length == 0) {
            return;
        }

        if (bound.Count > 0 && bound[^1] is BoundText previous) {
            bound[^1] = new BoundText(previous.Text + text);
            return;
        }

        bound.Add(new BoundText(text));
    }

    // CA1859 reads the `slot` branch and stops, so it proposes BoundSlot — a sealed sibling of the
    // BoundElement this returns below it. Taking the advice does not compile.
#pragma warning disable CA1859
    BoundNode BindElement(ElementSyntax element) {
#pragma warning restore CA1859
        var tag = element.StartTag.Name.Text;
        var attributes = BindAttributes(element.StartTag);

        if (string.Equals(tag, "slot", StringComparison.Ordinal)) {
            return BindSlot(element, attributes);
        }

        if (string.Equals(tag, "provide", StringComparison.Ordinal)) {
            return BindProvide(element, attributes);
        }

        if (string.Equals(tag, BoundElement.SelfTag, StringComparison.Ordinal)) {
            // ⚠ Reported and then bound anyway, rather than returning here. The emitter writes it
            // against the host wherever it is, so an author who wrote it in the wrong place gets one
            // error about the place rather than that error plus every consequence of the attributes
            // it carried going nowhere.
            if (!atTopLevel) {
                Report(MarkupDiagnostics.MisplacedSelf, element.StartTag.Name.Span);
            }

            return new BoundElement(tag, false, attributes, BindContent(element.Content), Position(element.StartTag.Name));
        }

        if (inLoop && !attributes.Any(a => a.Kind == BoundAttributeKind.Key)) {
            Report(MarkupDiagnostics.MissingKey, element.StartTag.Name.Span, tag);
        }

        // Only the roots of a loop body need identity; once inside one, children move with it.
        var outer = inLoop;
        inLoop = false;
        var children = BindContent(element.Content);
        inLoop = outer;

        var component = SyntaxFacts.IsComponentName(tag);

        // ⚠ Here rather than in the attribute binder, because `slot="footer"` is legal by virtue of
        // its *parent* and an attribute cannot see one. This is the only place in the walk that holds
        // a tag and its own children at once.
        if (!component) {
            RefuseSlotAttributes(element.Content, $"'<{tag}>'");
        }

        return new BoundElement(
            tag,
            component,
            attributes,
            children,
            Position(element.StartTag.Name)
        );
    }

    /// <summary>Reports every <c>slot="…"</c> written somewhere that has no slots to name.</summary>
    /// <param name="content">The children written directly there.</param>
    /// <param name="parent">What to call the thing they were written in, quoted for the message.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only the immediate children, and that is the rule rather than a shortcut.</b> A
    ///         slot name is read by the tag it is written directly inside; a grandchild carrying one
    ///         is addressed to nobody, and its own parent is what this runs for. So walking deeper
    ///         would report the same attribute twice, once at each level, and reporting it at the
    ///         level it was written is the one that names the element the author has to move.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An <c>@if</c>, an <c>@for</c> and an <c>@switch</c> arm each call this for their
    ///         own body, and until they did a <c>slot</c> inside one was accepted and then
    ///         discarded.</b> The emitter partitions a capitalised tag's children by reading the
    ///         <c>slot</c> off each <c>BoundElement</c>, and a region is not one — so the whole
    ///         region, slotted children and all, went to the default host with nothing reported.
    ///         Making the region legal is not the alternative: it anchors on the parent it was
    ///         handed and reconciles against the children it made there.
    ///     </para>
    /// </remarks>
    void RefuseSlotAttributes(SyntaxList<MarkupSyntax> content, string parent) {
        foreach (var child in content) {
            RefuseSlotAttribute(child, parent);
        }
    }

    /// <inheritdoc cref="RefuseSlotAttributes(SyntaxList{MarkupSyntax}, string)" />
    /// <param name="content">The children written directly there.</param>
    /// <param name="parent">What to call the thing they were written in, quoted for the message.</param>
    /// <remarks>
    ///     <c>SyntaxList&lt;T&gt;</c> is a struct enumerator and not an <c>IEnumerable&lt;T&gt;</c>,
    ///     so the top level — which is collected into a builder before it is bound — needs this one.
    /// </remarks>
    void RefuseSlotAttributes(IEnumerable<MarkupSyntax> content, string parent) {
        foreach (var child in content) {
            RefuseSlotAttribute(child, parent);
        }
    }

    /// <inheritdoc cref="RefuseSlotAttributes(SyntaxList{MarkupSyntax}, string)" />
    /// <param name="child">One child written directly there.</param>
    /// <param name="parent">What to call the thing it was written in, quoted for the message.</param>
    void RefuseSlotAttribute(MarkupSyntax child, string parent) {
        if (child is not ElementSyntax element) {
            return;
        }

        foreach (var attribute in element.StartTag.Attributes) {
            if (!string.Equals(attribute.Name.Text, "slot", StringComparison.Ordinal)) {
                continue;
            }

            // ⚠ Reported against the *syntax* and not the bound attribute, which is what buys the
            // squiggle its position. A `BoundAttribute` carries a `LinePositionSpan` for the
            // emitter's benefit and `Report` wants a `TextSpan`; the only place both the tag and its
            // children's real spans are in scope at once is here.
            Report(
                MarkupDiagnostics.MisplacedSlotAttribute,
                attribute.Name.Span,
                Literal(attribute) ?? DefaultSlotName,
                parent
            );
        }
    }

    /// <summary>An attribute's value when it is one literal string, otherwise null.</summary>
    static string? Literal(AttributeSyntax attribute) =>
        attribute.Value is QuotedAttributeValueSyntax { } quoted ? quoted.ToString() : null;

    /// <summary>What an unnamed slot is called, spelled here so the binder needs no runtime.</summary>
    /// <remarks>
    ///     ⚠ <c>BuildContext.DefaultSlot</c> is the same string and is the one that matters, but this
    ///     project is a <c>netstandard2.1</c> analyser and does not reference <c>Vixen.Ui</c>. The two
    ///     are held together by a test rather than by the compiler.
    /// </remarks>
    const string DefaultSlotName = "default";

    BoundSlot BindSlot(ElementSyntax element, ImmutableArray<BoundAttribute> attributes) {
        var name = "default";
        foreach (var attribute in attributes) {
            if (string.Equals(attribute.Name, "name", StringComparison.Ordinal) && attribute.Literal is { } literal) {
                name = literal;
            }
        }

        if (!slots.Add(name)) {
            Report(MarkupDiagnostics.DuplicateSlot, element.StartTag.Name.Span, name);
        }

        if (isElement && !string.Equals(name, "default", StringComparison.Ordinal)) {
            Report(MarkupDiagnostics.NamedSlotOnElement, element.StartTag.Name.Span, name);
        }

        // ⚠ <b>The children were being dropped without a word, and that is worse than not having
        // fallback content at all.</b> Every other framework spells a default this way, so it is what
        // an author writes first; `BindSlot` never looked at `element.Content`, so
        // `<slot name="footer">Nothing yet</slot>` compiled clean and drew an empty hole. Refused
        // out loud until a real fallback exists — see VXML2017.
        if (element.Content.Count > 0) {
            Report(MarkupDiagnostics.SlotFallbackContent, element.StartTag.Name.Span);
        }

        return new BoundSlot(name);
    }

    /// <summary>Binds <c>&lt;provide type="ITheme" value="@theme" /&gt;</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A broken one still becomes a node, on <c>&lt;self&gt;</c>'s rule.</b> Returning
    ///         nothing here would leave the rest of the file bound and the author holding one error
    ///         plus every consequence of the tag's absence. The emitter writes nothing for a node
    ///         missing either half, because this has already said what is wrong and a second
    ///         complaint from Roslyn about <c>Provide&lt;&gt;(…)</c> is noise pointing at generated
    ///         code.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A literal value is accepted and quoted.</b> <c>&lt;provide type="string"
    ///         value="dark" /&gt;</c> is the reading an author expects from every other attribute in
    ///         the language, and refusing it would mean spelling a constant as
    ///         <c>value="@(&quot;dark&quot;)"</c>. What is refused is a value made of a literal and
    ///         an interpolation together, which is a string built at run time and almost certainly a
    ///         mistake in a slot keyed by type.
    ///     </para>
    /// </remarks>
    BoundProvide BindProvide(ElementSyntax element, ImmutableArray<BoundAttribute> attributes) {
        var span = element.StartTag.Name.Span;

        BoundAttribute? type = null;
        BoundAttribute? value = null;

        foreach (var attribute in attributes) {
            if (string.Equals(attribute.Name, "type", StringComparison.Ordinal)) {
                type = attribute;
            } else if (string.Equals(attribute.Name, "value", StringComparison.Ordinal)) {
                value = attribute;
            }
        }

        var key = string.Empty;

        if (type is null) {
            Report(MarkupDiagnostics.IncompleteProvide, span, "type");
        } else if (type.Literal is { Length: > 0 } written) {
            key = written;
        } else if (type.IsDynamic) {
            Report(MarkupDiagnostics.InterpolatedProvideType, span, "type");
        } else {
            Report(MarkupDiagnostics.IncompleteProvide, span, "type");
        }

        var provided = new BoundExpression(string.Empty, Position(element.StartTag.Name));

        switch (value) {
            case null:
                Report(MarkupDiagnostics.IncompleteProvide, span, "value");
                break;

            case { Expression: { } expression }:
                provided = expression;
                break;

            case { Literal: { Length: > 0 } literal }:
                provided = new BoundExpression(Quote(literal), Position(element.StartTag.Name));
                break;

            default:
                Report(MarkupDiagnostics.IncompleteProvide, span, "value");
                break;
        }

        if (element.Content.Count > 0) {
            Report(MarkupDiagnostics.ProvideContent, span);
        }

        return new BoundProvide(key, provided);
    }

    /// <summary>A C# string literal holding exactly those characters.</summary>
    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    BoundIf BindIf(IfSyntax @if) {
        var branches = ImmutableArray.CreateBuilder<BoundBranch>();
        var @else = ImmutableArray<BoundNode>.Empty;

        // `else if` nests in the tree because that is what it is; it flattens here because that is
        // what an emitter wants — one chain of tested arms and at most one untested one.
        for (var current = @if; current is not null;) {
            if (!current.Condition.IsMissing) {
                RefuseSlotAttributes(current.Body.Content, "'@if'");
                branches.Add(new(Expression(current.Condition), BindContent(current.Body.Content)));
            }

            switch (current.Else?.Body) {
                case IfSyntax next:
                    current = next;
                    continue;
                case MarkupBlockSyntax block:
                    RefuseSlotAttributes(block.Content, "'@else'");
                    @else = BindContent(block.Content);
                    break;
                default:
                    break;
            }

            current = null;
        }

        return new(branches.ToImmutable(), @else);
    }

    BoundFor BindFor(ForSyntax @for) {
        var variable = @for.Identifier.IsMissing ? "item" : @for.Identifier.Text;

        var outer = inLoop;
        var outerVariable = item;

        inLoop = true;
        item = variable;
        loops++;
        RefuseSlotAttributes(@for.Body.Content, "'@for'");
        var body = BindContent(@for.Body.Content);
        loops--;
        item = outerVariable;
        inLoop = outer;

        BoundExpression? key = null;
        foreach (var node in body) {
            if (node is BoundElement { Key: { } found }) {
                key = found;
                break;
            }
        }

        // A comma with no name after it leaves a missing token; there is nothing to bind and the
        // parser has already reported it, so the loop is bound as if no index were declared.
        var index = @for.Index is { IsMissing: false } named ? named.Text : null;

        return new(variable, Expression(@for.Sequence), key, body, index);
    }

    /// <summary>Whether a key expression reads a member of the loop variable instead of the variable.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Characters, because the alternative is a semantic model.</b> The rule worth
    ///         enforcing is "key on the value when the item is immutable data, on the object when it
    ///         holds signals", and deciding which of those an item is means resolving its type and
    ///         asking whether its properties are <c>Signal&lt;T&gt;</c> — the typechecking this binder
    ///         deliberately does not do. <c>row.Label</c> is the shape that shape always takes, and
    ///         recognising it costs a <c>StartsWith</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An under-approximation on purpose.</b> <c>@(row.A, row.B)</c> and
    ///         <c>@Key(row)</c> are the same mistake and are not caught, because the syntactic
    ///         evidence runs out — and a rule that guessed would fire on <c>@(row, generation)</c>,
    ///         which is a correct compound key. A warning that is right whenever it speaks is worth
    ///         more than one that is complete.
    ///     </para>
    /// </remarks>
    static bool ProjectsFrom(string key, string variable) {
        if (!key.StartsWith(variable, StringComparison.Ordinal) || key.Length <= variable.Length) {
            return false;
        }

        // `.` and `?.` only. `row[0]` indexes a collection the item happens to be, `row!` is the same
        // item with an annotation, and neither throws away the identity a member access does.
        var rest = key[variable.Length..];
        return rest.StartsWith('.') || rest.StartsWith("?.", StringComparison.Ordinal);
    }

    BoundSwitch BindSwitch(SwitchSyntax @switch) {
        var cases = ImmutableArray.CreateBuilder<BoundCase>();

        foreach (var section in @switch.Sections) {
            var pattern = section.Pattern is { IsMissing: false } token ? Expression(token) : null;
            RefuseSlotAttributes(section.Content, "'@switch'");
            cases.Add(new(pattern, BindContent(section.Content)));
        }

        return new(Expression(@switch.Subject), cases.ToImmutable());
    }

    // ================================================================== Attributes

    ImmutableArray<BoundAttribute> BindAttributes(StartTagSyntax tag) {
        var bound = ImmutableArray.CreateBuilder<BoundAttribute>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var isComponent = SyntaxFacts.IsComponentName(tag.Name.Text);

        foreach (var attribute in tag.Attributes) {
            if (!seen.Add(attribute.Name.Text)) {
                Report(MarkupDiagnostics.DuplicateAttribute, attribute.Name.Span, attribute.Name.Text);
                continue;
            }

            if (BindAttribute(attribute, tag.Name.Text, isComponent) is { } result) {
                bound.Add(result);
            }
        }

        return bound.ToImmutable();
    }

    BoundAttribute? BindAttribute(AttributeSyntax attribute, string tag, bool isComponent) {
        var written = attribute.Name.Text;
        var value = BindValue(attribute.Value);

        var (kind, name, modifiers) = Classify(written, attribute.Name.Span);
        if (kind is null) {
            return null;
        }

        // An event, a binding, a key or a ref is a reference to something, so a string cannot be one.
        // Saying so here rather than letting Roslyn say it means the message can name the fix.
        if (kind is BoundAttributeKind.Event or BoundAttributeKind.Bind or BoundAttributeKind.Key
                or BoundAttributeKind.Ref or BoundAttributeKind.Refs or BoundAttributeKind.Changed
                or BoundAttributeKind.Use or BoundAttributeKind.ContextMenu
            && value is not [BoundExpressionPart]) {
            Report(MarkupDiagnostics.ExpectedExpressionValue, attribute.Name.Span, written);
            return null;
        }

        // ⚠ Refused rather than honoured, because a lowercase tag already *is* the name it wants:
        // `<div tag="fact-row">` and `<fact-row>` differ by nothing but which of the two the reader
        // has to check. On a capitalised tag there is no such spelling, which is the whole reason
        // the attribute exists. See `MarkupDiagnostics.TagOnElement`.
        if (kind == BoundAttributeKind.Tag && !isComponent) {
            Report(MarkupDiagnostics.TagOnElement, attribute.Name.Span, tag);
            return null;
        }

        // ⚠ `tag`'s rule for `tag`'s reason: both are read once, when the element is made. An empty
        // value is the bare `slot`, which names the default one and is what an author who wrote the
        // word and no value meant; anything with an expression in it was silently *also* read as the
        // default, because the emitter asks the attribute for its literal and takes `default` when
        // there is none. See `MarkupDiagnostics.DynamicSlotName`.
        if (kind == BoundAttributeKind.Slot && value is not ([] or [BoundLiteralPart])) {
            Report(MarkupDiagnostics.DynamicSlotName, attribute.Name.Span, written);
            return null;
        }

        // ⚠ Refused rather than assigned N times. See `MarkupDiagnostics.RefInLoop`: the body runs
        // once per item and there is one member, and the last-one-wins reading is worse than it looks
        // because a surviving key's body is not re-run at all.
        if (kind == BoundAttributeKind.Ref && loops > 0) {
            Report(MarkupDiagnostics.RefInLoop, attribute.Name.Span);
            return null;
        }

        // ⚠ And the mirror. `refs` is keyed on the loop's identity, so outside a loop there is no
        // key to file the element under — see `MarkupDiagnostics.RefsOutsideLoop`.
        if (kind == BoundAttributeKind.Refs && loops == 0) {
            Report(MarkupDiagnostics.RefsOutsideLoop, attribute.Name.Span);
            return null;
        }

        if (kind == BoundAttributeKind.Key
            && item is { } loopVariable
            && value is [BoundExpressionPart keyed]
            && ProjectsFrom(keyed.Expression.Text, loopVariable)) {
            Report(
                MarkupDiagnostics.ProjectedKey,
                attribute.Value?.Span ?? attribute.Name.Span,
                keyed.Expression.Text,
                loopVariable
            );
        }

        // A parameter on a component becomes a property assignment, so its name has to be one.
        if (kind == BoundAttributeKind.Parameter && isComponent && !IsUniversal(name) && !IsPropertyPath(name)) {
            Report(MarkupDiagnostics.InvalidParameterName, attribute.Name.Span, name, tag);
            return null;
        }

        // ⚠ And the mirror on the other side of the same case split: on a lowercase tag the parameter
        // is *not* an assignment, it is `ctx.Attribute(n1, "AccessibleName", "Save")` — selector data
        // nothing reads. Warned rather than refused because that reading is legal, and kept rather
        // than dropped for the same reason. See `MarkupDiagnostics.InertElementAttribute` for why the
        // test is the name's case and not a lookup of the property.
        if (kind == BoundAttributeKind.Parameter
            && !isComponent
            && !IsUniversal(name)
            && name.Length > 0
            && char.IsUpper(name[0])) {
            Report(MarkupDiagnostics.InertElementAttribute, attribute.Name.Span, name, tag);
        }

        return new(kind.Value, name, modifiers, value, Position(attribute.Name));
    }

    /// <summary>Attributes that mean the same on a component as on an element, so are never parameters.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>class</c> names style classes, which a component's root element has exactly as much
    ///         as a <c>&lt;div&gt;</c> does — and it is a C# keyword, so treating it as a parameter
    ///         would emit <c>n1.class = …</c> and turn one bad attribute into a file that does not
    ///         parse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>binding-path</c> is here because binding happens after the tree is built</b> —
    ///         doc 36 § P4, and Unity's rule. It says which member of the ambient edit target an
    ///         element shows, and a binder joins the two afterwards; it is not a property of the
    ///         control, so a <c>&lt;Slider binding-path="Speed" /&gt;</c> that tried to assign one
    ///         would be an error on every control that has no such property, which is all of them.
    ///         The hyphen also means it could never be a property name.
    ///     </para>
    /// </remarks>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>style</c> is here for <c>class</c>'s reason exactly.</b> An inline style is a
    ///         cascade origin, and a component's root element has one as much as a <c>&lt;div&gt;</c>
    ///         does — so <c>&lt;ProgressBar style="width: 42%" /&gt;</c> has to reach the element the
    ///         control drew rather than look for a <c>Style</c> property on it. There is one:
    ///         <c>Component.Style</c> is the scoped stylesheet a <c>.vxml</c> declares, and assigning
    ///         a declaration list to it would be a wrong answer that compiles.
    ///     </para>
    /// </remarks>
    internal static bool IsUniversal(string name) =>
        string.Equals(name, "class", StringComparison.Ordinal)
        || string.Equals(name, "style", StringComparison.Ordinal)
        || string.Equals(name, "binding-path", StringComparison.Ordinal);

    /// <summary>
    ///     Whether a name can be written after a <c>.</c> in an assignment's target.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A path rather than a single name</b>, because the control library has properties
    ///     that are objects: a button's icon is <c>LeadingIcon.Geometry</c> and there is no flat name
    ///     for it. Nothing is checked here beyond "this will parse as C#" — whether the path exists
    ///     and whether the value fits is Roslyn's, reported on the characters of the attribute name,
    ///     which is the same bargain the tag name is emitted under.
    /// </remarks>
    static bool IsPropertyPath(string name) {
        foreach (var part in name.Split('.')) {
            if (!IsIdentifier(part)) {
                return false;
            }
        }

        return true;
    }

    static bool IsIdentifier(string name) {
        if (name.Length == 0 || (!char.IsLetter(name[0]) && name[0] != '_')) {
            return false;
        }

        foreach (var c in name) {
            if (!char.IsLetterOrDigit(c) && c != '_') {
                return false;
            }
        }

        return true;
    }

    (BoundAttributeKind? Kind, string Name, ImmutableArray<string> Modifiers) Classify(string written, TextSpan span) {
        if (string.Equals(written, "key", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Key, written, []);
        }

        // ⚠ An expression and not a bare name, which is the same call `key` and `on:` make. What
        // follows the `@` is written where an assignment's target goes, so `ref="@Parts"`,
        // `ref="@_tree"` and `ref="@Row.Field"` all work and a member that is readonly, missing or of
        // the wrong type is Roslyn's error on the characters between the quotes. A bare name would
        // have needed a rule about what a name may be, and then a second one about what it may
        // resolve to — which is the typechecker this design exists not to write.
        if (string.Equals(written, "ref", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Ref, written, []);
        }

        // An expression for `ref`'s reason exactly, and the same freedom: `refs="@Faders"` and
        // `refs="@_row.Faders"` both work, and a member that is not an `ElementRefs<T>` of the right
        // element type is Roslyn's error on the characters between the quotes.
        if (string.Equals(written, "refs", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Refs, written, []);
        }

        // ⚠ Universal in the same sense `class` is — it means the same on a component tag as on a
        // control tag — but unlike `class` it never reaches the style tree as an attribute, because
        // it *is* the element's name. See `BoundAttributeKind.Tag`.
        if (string.Equals(written, "tag", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Tag, written, []);
        }

        // An expression for `ref`'s reason, and the same freedom: what goes between the quotes is a
        // lambda whose parameter is whatever the tag made, so `use="@(v => v.Inspect(a, b, c))"` is
        // typed by C# at the call and a method that does not exist is Roslyn's error on the
        // characters the author wrote.
        if (string.Equals(written, "use", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Use, written, []);
        }

        // ⚠ Recognised here, on every tag, and refused later by *position* rather than by name. What
        // makes `slot="footer"` legal is the parent being a component tag, and an attribute binder
        // does not know its element's parent — so the check is in `BindElement`, where the children
        // are already bound and the tag is in hand. Claiming the name unconditionally is what lets
        // that check see the ones written in the wrong place at all: left as a `Parameter` it would
        // reach the emitter as a property assignment and come back as Roslyn's "no such member",
        // pointing at generated code and naming the wrong problem.
        if (string.Equals(written, "slot", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Slot, written, []);
        }

        // ⚠ Universal in `class`'s sense — it means the same on a component tag as on an element, and
        // is never a property — but it reaches neither the style tree nor an assignment: it emits a
        // call, because what describes an element is an object somebody has to make. See
        // `BoundAttributeKind.Help` for why that call goes through a seam rather than naming
        // `Vixen.Ui.Controls.Tooltip`.
        if (string.Equals(written, "help", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Help, written, []);
        }

        // `help`'s shape with a menu where the sentence is. The hyphen is what makes it safe to
        // claim on every tag: it could never be a property name, which is `binding-path`'s argument.
        if (string.Equals(written, "context-menu", StringComparison.Ordinal)) {
            return (BoundAttributeKind.ContextMenu, written, []);
        }

        var colon = written.IndexOf(':', StringComparison.Ordinal);

        if (colon < 0) {
            return written.StartsWith("on", StringComparison.Ordinal)
                && EventAliases.Contains(written[2..], StringComparer.Ordinal)
                    ? (BoundAttributeKind.Event, written[2..], [])
                    : (BoundAttributeKind.Parameter, written, []);
        }

        var prefix = written[..colon];
        var rest = written[(colon + 1)..];

        // ⚠ The dots after a `bind:` are *event names*, not the filter words `on:` takes, so there is
        // no closed list to check them against and none is checked. `bind:Value.blur` says which
        // moment commits the write, and the moments are the same names `on:` subscribes to — which
        // is why they cannot be validated here: the table is the runtime's and a control library
        // adds to it. That is the bargain an `on:` event name is already emitted under, and the
        // failure is the same one, at compose, naming every event the runtime does know.
        if (string.Equals(prefix, "bind", StringComparison.Ordinal)) {
            var names = rest.Split('.');
            var commits = ImmutableArray.CreateBuilder<string>(names.Length - 1);

            for (var i = 1; i < names.Length; i++) {
                commits.Add(names[i]);
            }

            return (BoundAttributeKind.Bind, names[0], commits.ToImmutable());
        }

        // ⚠ A directive of its own rather than `on:change`, because it is not an event. `on:` maps a
        // name through a table whose entries are `Action<UiElement, Action<UiEvent>, RoutingStrategy>`
        // — a routed gesture, which cannot carry a value — and what follows `change:` is the name of
        // a `[UiProperty]`, resolved exactly the way `bind:` resolves one. Naming the property rather
        // than saying "change" is also what makes it unambiguous on a control that has two.
        if (string.Equals(prefix, "change", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Changed, rest, []);
        }

        if (!string.Equals(prefix, "on", StringComparison.Ordinal)) {
            Report(MarkupDiagnostics.UnknownAttributeDirective, span, written);
            return (null, written, []);
        }

        var parts = rest.Split('.');
        var modifiers = ImmutableArray.CreateBuilder<string>(parts.Length - 1);

        for (var i = 1; i < parts.Length; i++) {
            if (!EventModifiers.Contains(parts[i], StringComparer.Ordinal) && !IsSlotModifier(parts[i])) {
                Report(MarkupDiagnostics.UnknownEventModifier, span, parts[i]);
                continue;
            }

            modifiers.Add(parts[i]);
        }

        return (BoundAttributeKind.Event, parts[0], modifiers.ToImmutable());
    }

    ImmutableArray<BoundValuePart> BindValue(AttributeValueSyntax? value) {
        switch (value) {
            case ExpressionAttributeValueSyntax { Expression.Expression: { IsMissing: false } expression }:
                return [new BoundExpressionPart(Expression(expression))];

            case QuotedAttributeValueSyntax quoted: {
                var parts = ImmutableArray.CreateBuilder<BoundValuePart>();

                foreach (var part in quoted.Content) {
                    switch (part) {
                        case TextSyntax text:
                            parts.Add(new BoundLiteralPart(Decode(text.TextToken.Text)));
                            break;
                        case InterpolationSyntax { Expression: { IsMissing: false } expression }:
                            parts.Add(new BoundExpressionPart(Expression(expression)));
                            break;
                        default:
                            break;
                    }
                }

                // `class=""` is a value, not the absence of one, so an empty quoted run binds to a
                // single empty literal rather than to nothing.
                return parts.Count == 0 ? [new BoundLiteralPart(string.Empty)] : parts.ToImmutable();
            }

            default:
                return [];
        }
    }

    // ================================================================== Helpers

    BoundExpression Expression(SyntaxToken token) => new(token.Text, Position(token));

    LinePositionSpan Position(SyntaxToken token) => text.GetLinePositionSpan(token.Span);

    /// <summary>Turns the escape <c>@@</c> back into the character it stands for.</summary>
    static string Decode(string written) => written.Replace("@@", "@", StringComparison.Ordinal);

    void Report(DiagnosticDescriptor descriptor, TextSpan span, params object[] arguments) =>
        diagnostics.Add(descriptor, Location.Create(filePath, span, text), arguments);
}
