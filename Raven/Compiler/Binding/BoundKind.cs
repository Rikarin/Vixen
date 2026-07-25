namespace Vixen.Raven.Binding;

/// <summary>Discriminator for <see cref="BoundNode"/>, mirroring Roslyn's bound tree.</summary>
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
    NullCoalescingExpression,
    ArrayAccessExpression,
    RangeExpression,
    TupleExpression,
    CollectionExpression,
    LambdaExpression,
    IsPatternExpression,
    SwitchExpression,
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
    LocalFunctionStatement,
    SwitchStatement,
    NoOpStatement
}
