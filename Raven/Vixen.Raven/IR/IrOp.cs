
namespace Vixen.Raven.IR;

/// <summary>
///     A binary opcode. Unlike the bound tree these are already type-resolved:
///     <see cref="Add" /> on two <c>f32</c> vectors is componentwise float addition,
///     and nothing else.
/// </summary>
public enum IrBinaryOp {
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,

    /// <summary>Matrix × matrix, matrix × vector or vector × matrix — a real product.</summary>
    MatrixMultiply,
    ShiftLeft,
    ShiftRight,
    UnsignedShiftRight,
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

public enum IrUnaryOp {
    Negate,
    Not,
    BitwiseNot
}

/// <summary>
///     A representation change. The bound tree's conversion kinds collapse to the
///     handful a GPU actually performs.
/// </summary>
public enum IrConversionKind {
    /// <summary>Between numeric scalar representations, or componentwise between vectors.</summary>
    Numeric,

    /// <summary>A scalar broadcast across every lane of a vector or matrix.</summary>
    Splat
}
