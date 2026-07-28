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
    Key
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
}

/// <summary>A content-projection point.</summary>
/// <param name="Name">The slot's name; the default slot is named <c>default</c>.</param>
public sealed record BoundSlot(string Name) : BoundNode;

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
public sealed record BoundFor(
    string Variable,
    BoundExpression Sequence,
    BoundExpression? Key,
    ImmutableArray<BoundNode> Body
) : BoundNode;

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
/// <param name="Usings">Namespaces to import, in source order.</param>
/// <param name="Code">Every <c>@code</c> body, in source order. Multiple blocks concatenate.</param>
/// <param name="Content">The markup.</param>
/// <param name="Css">The <c>&lt;style&gt;</c> body, if there is one.</param>
/// <param name="CssIsScoped">Whether that style block carried <c>scoped</c>.</param>
public sealed record BoundComponent(
    string Name,
    string? Namespace,
    ImmutableArray<string> Usings,
    ImmutableArray<BoundExpression> Code,
    ImmutableArray<BoundNode> Content,
    string? Css,
    bool CssIsScoped
);
