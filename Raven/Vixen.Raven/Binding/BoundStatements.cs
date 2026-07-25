// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Binding;

public sealed class BoundBlockStatement(SyntaxNode syntax, IReadOnlyList<BoundStatement> statements)
    : BoundStatement(syntax) {
    public IReadOnlyList<BoundStatement> Statements { get; } = statements;
    public override BoundKind Kind => BoundKind.BlockStatement;
    public override IEnumerable<BoundNode> Children => Statements;
}

public sealed class BoundLocalDeclarationStatement(
    SyntaxNode syntax,
    LocalSymbol local,
    BoundExpression? initializer
) : BoundStatement(syntax) {
    public LocalSymbol Local { get; } = local;
    public BoundExpression? Initializer { get; } = initializer;
    public override BoundKind Kind => BoundKind.LocalDeclarationStatement;
    public override IEnumerable<BoundNode> Children => Initializer is null ? [] : [Initializer];
}

public sealed class BoundExpressionStatement(SyntaxNode syntax, BoundExpression expression)
    : BoundStatement(syntax) {
    public BoundExpression Expression { get; } = expression;
    public override BoundKind Kind => BoundKind.ExpressionStatement;
    public override IEnumerable<BoundNode> Children => [Expression];
}

public sealed class BoundIfStatement(
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

public sealed class BoundWhileStatement(SyntaxNode syntax, BoundExpression condition, BoundStatement body)
    : BoundStatement(syntax) {
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Body { get; } = body;
    public override BoundKind Kind => BoundKind.WhileStatement;
    public override IEnumerable<BoundNode> Children => [Condition, Body];
}

/// <summary><c>repeat … while (…)</c> — the body runs before the first test.</summary>
public sealed class BoundRepeatStatement(SyntaxNode syntax, BoundStatement body, BoundExpression condition)
    : BoundStatement(syntax) {
    public BoundStatement Body { get; } = body;
    public BoundExpression Condition { get; } = condition;
    public override BoundKind Kind => BoundKind.RepeatStatement;
    public override IEnumerable<BoundNode> Children => [Body, Condition];
}

/// <summary><c>for (i in sequence) …</c>.</summary>
public sealed class BoundForStatement(
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

public sealed class BoundReturnStatement(SyntaxNode syntax, BoundExpression? expression) : BoundStatement(syntax) {
    public BoundExpression? Expression { get; } = expression;
    public override BoundKind Kind => BoundKind.ReturnStatement;
    public override IEnumerable<BoundNode> Children => Expression is null ? [] : [Expression];
}

public sealed class BoundBreakStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override BoundKind Kind => BoundKind.BreakStatement;
}

public sealed class BoundContinueStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override BoundKind Kind => BoundKind.ContinueStatement;
}

public sealed class BoundLocalFunctionStatement(SyntaxNode syntax, MethodSymbol method, BoundNode? body)
    : BoundStatement(syntax) {
    public MethodSymbol Method { get; } = method;
    public BoundNode? Body { get; } = body;
    public override BoundKind Kind => BoundKind.LocalFunctionStatement;
    public override IEnumerable<BoundNode> Children => Body is null ? [] : [Body];
}

/// <summary><c>switch</c> statement; sections are bound shallowly (see <see cref="BoundIsPatternExpression" />).</summary>
public sealed class BoundSwitchStatement(
    SyntaxNode syntax,
    BoundExpression governingExpression,
    IReadOnlyList<BoundStatement> statements
) : BoundStatement(syntax) {
    public BoundExpression GoverningExpression { get; } = governingExpression;
    public IReadOnlyList<BoundStatement> Statements { get; } = statements;
    public override BoundKind Kind => BoundKind.SwitchStatement;
    public override IEnumerable<BoundNode> Children => [GoverningExpression, .. Statements];
}

/// <summary>An empty statement, or one the binder chose not to model.</summary>
public sealed class BoundNoOpStatement(SyntaxNode syntax) : BoundStatement(syntax) {
    public override BoundKind Kind => BoundKind.NoOpStatement;
}
