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
    static readonly string[] EventModifiers = ["stop", "prevent", "capture", "once", "self"];

    readonly SourceText text;
    readonly string filePath;
    readonly DiagnosticBag diagnostics;

    /// <summary>Slot names already claimed, so the second one can be reported.</summary>
    readonly HashSet<string> slots = new(StringComparer.Ordinal);

    /// <summary>Whether the walk is inside an <c>@for</c> body, where elements need keys.</summary>
    bool inLoop;

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

        var usings = ImmutableArray.CreateBuilder<string>();
        foreach (var @using in document.Usings) {
            if (!@using.Name.IsMissing) {
                usings.Add(@using.Name.Text);
            }
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

        var content = BindContent(markup);

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

        return new(
            directive.Identifier.Text,
            @namespace,
            tag,
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

    ImmutableArray<BoundNode> BindContent(SyntaxList<MarkupSyntax> content) {
        var nodes = ImmutableArray.CreateBuilder<MarkupSyntax>(content.Count);
        foreach (var node in content) {
            nodes.Add(node);
        }

        return BindContent(nodes);
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

        if (inLoop && !attributes.Any(a => a.Kind == BoundAttributeKind.Key)) {
            Report(MarkupDiagnostics.MissingKey, element.StartTag.Name.Span, tag);
        }

        // Only the roots of a loop body need identity; once inside one, children move with it.
        var outer = inLoop;
        inLoop = false;
        var children = BindContent(element.Content);
        inLoop = outer;

        return new BoundElement(
            tag,
            SyntaxFacts.IsComponentName(tag),
            attributes,
            children,
            Position(element.StartTag.Name)
        );
    }

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

        return new BoundSlot(name);
    }

    BoundIf BindIf(IfSyntax @if) {
        var branches = ImmutableArray.CreateBuilder<BoundBranch>();
        var @else = ImmutableArray<BoundNode>.Empty;

        // `else if` nests in the tree because that is what it is; it flattens here because that is
        // what an emitter wants — one chain of tested arms and at most one untested one.
        for (var current = @if; current is not null;) {
            if (!current.Condition.IsMissing) {
                branches.Add(new(Expression(current.Condition), BindContent(current.Body.Content)));
            }

            switch (current.Else?.Body) {
                case IfSyntax next:
                    current = next;
                    continue;
                case MarkupBlockSyntax block:
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
        var outer = inLoop;
        inLoop = true;
        var body = BindContent(@for.Body.Content);
        inLoop = outer;

        BoundExpression? key = null;
        foreach (var node in body) {
            if (node is BoundElement { Key: { } found }) {
                key = found;
                break;
            }
        }

        return new(
            @for.Identifier.IsMissing ? "item" : @for.Identifier.Text,
            Expression(@for.Sequence),
            key,
            body
        );
    }

    BoundSwitch BindSwitch(SwitchSyntax @switch) {
        var cases = ImmutableArray.CreateBuilder<BoundCase>();

        foreach (var section in @switch.Sections) {
            var pattern = section.Pattern is { IsMissing: false } token ? Expression(token) : null;
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

        // An event or a binding is a reference to something, so a string cannot be one. Saying so
        // here rather than letting Roslyn say it means the message can name the fix.
        if (kind is BoundAttributeKind.Event or BoundAttributeKind.Bind or BoundAttributeKind.Key
            && value is not [BoundExpressionPart]) {
            Report(MarkupDiagnostics.ExpectedExpressionValue, attribute.Name.Span, written);
            return null;
        }

        // A parameter on a component becomes a property assignment, so its name has to be one.
        if (kind == BoundAttributeKind.Parameter && isComponent && !IsUniversal(name) && !IsPropertyPath(name)) {
            Report(MarkupDiagnostics.InvalidParameterName, attribute.Name.Span, name, tag);
            return null;
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
    internal static bool IsUniversal(string name) =>
        string.Equals(name, "class", StringComparison.Ordinal)
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

        var colon = written.IndexOf(':', StringComparison.Ordinal);

        if (colon < 0) {
            return written.StartsWith("on", StringComparison.Ordinal)
                && EventAliases.Contains(written[2..], StringComparer.Ordinal)
                    ? (BoundAttributeKind.Event, written[2..], [])
                    : (BoundAttributeKind.Parameter, written, []);
        }

        var prefix = written[..colon];
        var rest = written[(colon + 1)..];

        if (string.Equals(prefix, "bind", StringComparison.Ordinal)) {
            return (BoundAttributeKind.Bind, rest, []);
        }

        if (!string.Equals(prefix, "on", StringComparison.Ordinal)) {
            Report(MarkupDiagnostics.UnknownAttributeDirective, span, written);
            return (null, written, []);
        }

        var parts = rest.Split('.');
        var modifiers = ImmutableArray.CreateBuilder<string>(parts.Length - 1);

        for (var i = 1; i < parts.Length; i++) {
            if (!EventModifiers.Contains(parts[i], StringComparer.Ordinal)) {
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
