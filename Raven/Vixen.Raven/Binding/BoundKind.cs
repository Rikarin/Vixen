// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Binding;

/// <summary>Discriminator for <see cref="BoundNode" />, mirroring Roslyn's bound tree.</summary>
public enum BoundKind {
    // Expressions
    LiteralExpression,
    LocalExpression,
    ParameterExpression,
    FieldExpression,
    PropertyExpression,
    SelfExpression,
    BaseExpression,
    TypeExpression,
    NamespaceExpression,
    MethodGroupExpression,
    InvocationExpression,
    ObjectCreationExpression,
    ConversionExpression,
    UnaryExpression,
    BinaryExpression,
    AssignmentExpression,
    ConditionalExpression,
    ArrayAccessExpression,
    RangeExpression,
    TupleExpression,
    CollectionExpression,
    ErrorExpression,

    // Statements
    BlockStatement,
    LocalDeclarationStatement,
    ExpressionStatement,
    IfStatement,
    WhileStatement,
    RepeatStatement,
    ForStatement,
    ReturnStatement,
    BreakStatement,
    ContinueStatement,
    SwitchStatement,
    NoOpStatement
}
