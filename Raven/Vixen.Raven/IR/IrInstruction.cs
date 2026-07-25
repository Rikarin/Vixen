// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.IR;

/// <summary>
///     A step inside a function body. Control flow stays structured — there is no
///     basic-block graph — because both SPIR-V (which requires structured merges in
///     shaders) and the source-level targets want it that way.
/// </summary>
public abstract class IrStatement;

/// <summary>A sequence of statements.</summary>
public sealed class IrBlock : IrStatement {
    readonly List<IrStatement> statements = [];

    public IReadOnlyList<IrStatement> Statements => statements;

    internal void Add(IrStatement statement) => statements.Add(statement);

    internal void AddRange(IEnumerable<IrStatement> items) => statements.AddRange(items);
}

/// <summary>
///     An operation. Most define a value; those that act purely on memory or control
///     (<see cref="IrStoreInstruction" />, a void call) leave <see cref="Result" /> null.
/// </summary>
public abstract class IrInstruction : IrStatement {
    public virtual IrValue? Result => null;

    /// <summary>Values this instruction reads, for verification and later passes.</summary>
    public virtual IEnumerable<IrValue> Operands => [];
}

/// <summary>A compile-time constant materialized into a value.</summary>
public sealed class IrConstantInstruction(IrValue result, object? value) : IrInstruction {
    public override IrValue Result { get; } = result;

    /// <summary>The boxed constant; null means the type's zero value.</summary>
    public object? Value { get; } = value;
}

/// <summary>Reads the storage a place designates.</summary>
public sealed class IrLoadInstruction(IrValue result, IrPlace place) : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrPlace Place { get; } = place;
    public override IEnumerable<IrValue> Operands => IndicesOf(Place);

    internal static IEnumerable<IrValue> IndicesOf(IrPlace place) =>
        place.Chain.OfType<IrIndexAccess>().Select(a => a.Index);
}

/// <summary>Writes a value into the storage a place designates.</summary>
public sealed class IrStoreInstruction(IrPlace place, IrValue value) : IrInstruction {
    public IrPlace Place { get; } = place;
    public IrValue Value { get; } = value;

    public override IEnumerable<IrValue> Operands => [Value, .. IrLoadInstruction.IndicesOf(Place)];
}

public sealed class IrUnaryInstruction(IrValue result, IrUnaryOp op, IrValue operand) : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrUnaryOp Op { get; } = op;
    public IrValue Operand { get; } = operand;
    public override IEnumerable<IrValue> Operands => [Operand];
}

public sealed class IrBinaryInstruction(IrValue result, IrBinaryOp op, IrValue left, IrValue right)
    : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrBinaryOp Op { get; } = op;
    public IrValue Left { get; } = left;
    public IrValue Right { get; } = right;
    public override IEnumerable<IrValue> Operands => [Left, Right];
}

/// <summary>A representation change, always explicit in the IR.</summary>
public sealed class IrConvertInstruction(IrValue result, IrConversionKind kind, IrValue operand)
    : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrConversionKind ConversionKind { get; } = kind;
    public IrValue Operand { get; } = operand;
    public override IEnumerable<IrValue> Operands => [Operand];
}

/// <summary>A built-in operation resolved to an opcode.</summary>
public sealed class IrIntrinsicInstruction(IrValue? result, IrIntrinsic intrinsic, IrValue[] arguments)
    : IrInstruction {
    public override IrValue? Result { get; } = result;
    public IrIntrinsic Intrinsic { get; } = intrinsic;
    public IReadOnlyList<IrValue> Arguments { get; } = arguments;
    public override IEnumerable<IrValue> Operands => Arguments;
}

/// <summary>A call to another function in the module.</summary>
public sealed class IrCallInstruction(IrValue? result, IrFunction function, IrValue[] arguments)
    : IrInstruction {
    public override IrValue? Result { get; } = result;
    public IrFunction Function { get; } = function;
    public IReadOnlyList<IrValue> Arguments { get; } = arguments;
    public override IEnumerable<IrValue> Operands => Arguments;
}

/// <summary>
///     Builds an aggregate from its parts: <c>float3(x, y, z)</c>, a matrix from
///     its rows, or a struct from its fields.
/// </summary>
public sealed class IrConstructInstruction(IrValue result, IrValue[] arguments) : IrInstruction {
    public override IrValue Result { get; } = result;
    public IReadOnlyList<IrValue> Arguments { get; } = arguments;
    public override IEnumerable<IrValue> Operands => Arguments;
}

/// <summary>
///     Reads part of a value that has no storage — the result of a call, say. The
///     addressable case goes through <see cref="IrLoadInstruction" /> instead.
/// </summary>
public sealed class IrExtractInstruction(IrValue result, IrValue source, IReadOnlyList<IrAccess> chain)
    : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrValue Source { get; } = source;
    public IReadOnlyList<IrAccess> Chain { get; } = chain;

    public override IEnumerable<IrValue> Operands => [Source, .. Chain.OfType<IrIndexAccess>().Select(a => a.Index)];
}

/// <summary>
///     Picks one of two values. Both operands are evaluated, matching SPIR-V's
///     <c>OpSelect</c>; lowering only produces it where that is sound.
/// </summary>
public sealed class IrSelectInstruction(IrValue result, IrValue condition, IrValue whenTrue, IrValue whenFalse)
    : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrValue Condition { get; } = condition;
    public IrValue WhenTrue { get; } = whenTrue;
    public IrValue WhenFalse { get; } = whenFalse;
    public override IEnumerable<IrValue> Operands => [Condition, WhenTrue, WhenFalse];
}

/// <summary>A two-way branch with a structured merge.</summary>
public sealed class IrIfStatement(IrValue condition, IrBlock then, IrBlock? otherwise) : IrStatement {
    public IrValue Condition { get; } = condition;
    public IrBlock Then { get; } = then;
    public IrBlock? Else { get; } = otherwise;
}

/// <summary>
///     A structured loop. <see cref="Condition" /> holds the instructions that
///     recompute <see cref="ConditionValue" /> on every iteration, and
///     <see cref="Continue" /> is the step a <c>for</c> loop runs before re-testing —
///     which is also where a <c>continue</c> lands.
/// </summary>
public sealed class IrLoopStatement(
    IrBlock condition,
    IrValue conditionValue,
    IrBlock body,
    IrBlock? @continue,
    bool testBeforeBody
) : IrStatement {
    public IrBlock Condition { get; } = condition;
    public IrValue ConditionValue { get; } = conditionValue;
    public IrBlock Body { get; } = body;
    public IrBlock? Continue { get; } = @continue;

    /// <summary>False for <c>repeat … while</c>, where the body runs first.</summary>
    public bool TestBeforeBody { get; } = testBeforeBody;
}

public sealed class IrReturnStatement(IrValue? value) : IrStatement {
    public IrValue? Value { get; } = value;
}

public sealed class IrBreakStatement : IrStatement;

public sealed class IrContinueStatement : IrStatement;
