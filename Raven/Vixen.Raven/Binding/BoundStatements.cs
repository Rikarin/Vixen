// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

internal sealed class BoundBlockStatement(SyntaxNode syntax, IReadOnlyList<BoundStatement> statements)
    : BoundStatement(syntax) {
    public IReadOnlyList<BoundStatement> Statements { get; } = statements;
    public override BoundKind Kind => BoundKind.BlockStatement;
    public override IEnumerable<BoundNode> Children => Statements;
}

internal sealed class BoundLocalDeclarationStatement(
    SyntaxNode syntax,
    LocalSymbol local,
    BoundExpression? initializer
) : BoundStatement(syntax) {
    public LocalSymbol Local { get; } = local;
    public BoundExpression? Initializer { get; } = initializer;
    public override BoundKind Kind => BoundKind.LocalDeclarationStatement;
    public override IEnumerable<BoundNode> Children => Initializer is null ? [] : [Initializer];
}

internal sealed class BoundExpressionStatement(SyntaxNode syntax, BoundExpression expression)
    : BoundStatement(syntax) {
    public BoundExpression Expression { get; } = expression;
    public override BoundKind Kind => BoundKind.ExpressionStatement;
    public override IEnumerable<BoundNode> Children => [Expression];
}

internal sealed class BoundIfStatement(
    SyntaxNode syntax,
    BoundExpression condition,
    BoundStatement consequence,
    BoundStatement? alternative
) : BoundStatement(syntax) {
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Consequence { get; } = consequence;
    public BoundStatement? Alternative { get; } = alternative;
    public override BoundKind Kind => BoundKind.IfStatement;

    public override IEnumerable<BoundNode> Children =>
        Alternative is null ? [Condition, Consequence] : [Condition, Consequence, Alternative];
}

internal sealed class BoundWhileStatement(SyntaxNode syntax, BoundExpression condition, BoundStatement body)
    : BoundStatement(syntax) {
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Body { get; } = body;
    public override BoundKind Kind => BoundKind.WhileStatement;
    public override IEnumerable<BoundNode> Children => [Condition, Body];
}

/// <summary><c>repeat … while (…)</c> — the body runs before the first test.</summary>
internal sealed class BoundRepeatStatement(SyntaxNode syntax, BoundStatement body, BoundExpression condition)
    : BoundStatement(syntax) {
    public BoundStatement Body { get; } = body;
    public BoundExpression Condition { get; } = condition;
    public override BoundKind Kind => BoundKind.RepeatStatement;
    public override IEnumerable<BoundNode> Children => [Body, Condition];
}

/// <summary><c>for (i in sequence) …</c>.</summary>
internal sealed class BoundForStatement(
    SyntaxNode syntax,
    LocalSymbol iterationVariable,
    BoundExpression sequence,
    BoundStatement body
) : BoundStatement(syntax) {
    public LocalSymbol IterationVariable { get; } = iterationVariable;
    public BoundExpression Sequence { get; } = sequence;
    public BoundStatement Body { get; } = body;
    public override BoundKind Kind => BoundKind.ForStatement;
    public override IEnumerable<BoundNode> Children => [Sequence, Body];
}

internal sealed class BoundReturnStatement(SyntaxNode syntax, BoundExpression? expression) : BoundStatement(syntax) {
    public BoundExpression? Expression { get; } = expression;
    public override BoundKind Kind => BoundKind.ReturnStatement;
    public override IEnumerable<BoundNode> Children => Expression is null ? [] : [Expression];
}

internal sealed class BoundBreakStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override BoundKind Kind => BoundKind.BreakStatement;
}

internal sealed class BoundContinueStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override BoundKind Kind => BoundKind.ContinueStatement;
}

/// <summary>Ends the invocation, writing nothing.</summary>
/// <remarks>
///     A terminator like <see cref="BoundReturnStatement" /> rather than a call like every other
///     stage intrinsic, which is the whole reason it needs a keyword: a function cannot express
///     "control does not come back", so nothing after the call would be known to be unreachable.
/// </remarks>
internal sealed class BoundDiscardStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override BoundKind Kind => BoundKind.DiscardStatement;
}

/// <summary>
///     One <c>case</c>/<c>default</c> section: the values that select it and the statements it runs.
/// </summary>
/// <remarks>
///     Sections do not fall through. Each is its own scope and its own block, so a trailing
///     <c>break</c> is redundant rather than load-bearing — which is what lets lowering desugar the
///     whole statement into an if/else chain.
/// </remarks>
/// <param name="Labels">The <c>case</c> values, converted to the governing type. Empty for <c>default</c>.</param>
/// <param name="IsDefault">Whether this section carries the <c>default</c> label.</param>
/// <param name="Statements">The section's body.</param>
internal sealed record BoundSwitchSection(
    IReadOnlyList<BoundExpression> Labels,
    bool IsDefault,
    IReadOnlyList<BoundStatement> Statements
);

/// <summary><c>switch</c> statement.</summary>
internal sealed class BoundSwitchStatement(
    SyntaxNode syntax,
    BoundExpression governingExpression,
    IReadOnlyList<BoundSwitchSection> sections
) : BoundStatement(syntax) {
    public BoundExpression GoverningExpression { get; } = governingExpression;
    public IReadOnlyList<BoundSwitchSection> Sections { get; } = sections;
    public override BoundKind Kind => BoundKind.SwitchStatement;

    public override IEnumerable<BoundNode> Children => [
        GoverningExpression,
        .. Sections.SelectMany(s => s.Labels.Concat<BoundNode>(s.Statements))
    ];
}

/// <summary>An empty statement, or one the binder chose not to model.</summary>
internal sealed class BoundNoOpStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override BoundKind Kind => BoundKind.NoOpStatement;
}
