namespace Vixen.Raven.Binding;

/// <summary>The operation a <see cref="BoundBinaryExpression"/> performs.</summary>
public enum BinaryOperatorKind {
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    LeftShift,
    RightShift,
    UnsignedRightShift,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    LogicalAnd,
    LogicalOr,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

/// <summary>The operation a <see cref="BoundUnaryExpression"/> performs.</summary>
public enum UnaryOperatorKind {
    Plus,
    Minus,
    BitwiseNot,
    LogicalNot,
    PreIncrement,
    PreDecrement,
    PostIncrement,
    PostDecrement,
    /// <summary><c>^i</c> — an index counted from the end.</summary>
    IndexFromEnd,
    /// <summary><c>x!</c> — asserts the operand is not null.</summary>
    SuppressNullable
}
