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

/// <summary>
///     The element count of a runtime-sized array — a storage buffer's contents.
/// </summary>
/// <remarks>
///     <para>
///         Takes a <em>place</em> rather than a value, and that is forced rather than chosen: an
///         unsized array cannot be loaded, so there is no value to ask. Both targets agree — GLSL's
///         <c>data.length()</c> and SPIR-V's <c>OpArrayLength</c> each name the block member, not a
///         copy of it.
///     </para>
///     <para>
///         Only a buffer reaches here. A sized array answers with a constant in the binder, which is
///         why this instruction is about the host's element count and nothing else.
///     </para>
/// </remarks>
public sealed class IrArrayLengthInstruction(IrValue result, IrPlace place) : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrPlace Place { get; } = place;
    public override IEnumerable<IrValue> Operands => IrLoadInstruction.IndicesOf(Place);
}

/// <summary>What an atomic read-modify-write does to the value it finds.</summary>
/// <remarks>
///     Signedness is not here, because the place's type carries it: SPIR-V splits <c>Min</c> and
///     <c>Max</c> into signed and unsigned opcodes and GLSL does not, so the split belongs to the
///     backend that needs it rather than to an IR both read.
/// </remarks>
public enum IrAtomicOp {
    /// <summary>Adds, and answers with what was there.</summary>
    Add,

    /// <summary>Keeps the smaller.</summary>
    Min,

    /// <summary>Keeps the larger.</summary>
    Max,

    /// <summary>Bitwise and.</summary>
    And,

    /// <summary>Bitwise or.</summary>
    Or,

    /// <summary>Bitwise exclusive or.</summary>
    Xor,

    /// <summary>Replaces unconditionally.</summary>
    Exchange,

    /// <summary>Replaces only if what is there equals the comparand.</summary>
    CompareExchange
}

/// <summary>
///     An indivisible read-modify-write of one scalar in a writable resource.
/// </summary>
/// <remarks>
///     <para>
///         <b>Takes a place, for the same reason <see cref="IrArrayLengthInstruction" /> does and a
///         stronger one.</b> The whole content of "atomic" is that the read and the write are the
///         same operation on the same storage; a value loaded out and handed to an intrinsic is a
///         copy, and no instruction on a copy can be atomic. Both targets say so in their operands —
///         GLSL's <c>atomicAdd</c> takes an l-value and SPIR-V's <c>OpAtomicIAdd</c> takes a pointer.
///     </para>
///     <para>
///         <b>The old value is the result, always.</b> That is what makes an atomic add an allocator:
///         every invocation that adds one gets a different answer back, and those answers are exactly
///         the indices <c>0..n</c>. An operation that returned nothing would be a counter nobody could
///         use.
///     </para>
///     <para>
///         Scalar integers only. GLSL 4.5 has no atomic on a float without an extension and none on a
///         vector at all, so a wider set here would be a promise one target could not keep.
///     </para>
/// </remarks>
public sealed class IrAtomicInstruction(IrValue result, IrAtomicOp op, IrPlace place, IrValue value, IrValue? comparand = null)
    : IrInstruction {
    public override IrValue Result { get; } = result;
    public IrAtomicOp Op { get; } = op;
    public IrPlace Place { get; } = place;

    /// <summary>What is added, combined or written.</summary>
    public IrValue Value { get; } = value;

    /// <summary>What the storage has to equal for <see cref="IrAtomicOp.CompareExchange" /> to write.</summary>
    public IrValue? Comparand { get; } = comparand;

    public override IEnumerable<IrValue> Operands =>
        Comparand is { } compared
            ? [Value, compared, .. IrLoadInstruction.IndicesOf(Place)]
            : [Value, .. IrLoadInstruction.IndicesOf(Place)];
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

/// <summary>
///     One argument of a call: an SSA value, or a variable passed by reference.
/// </summary>
/// <remarks>
///     <para>
///         Exactly one of the two is set. A by-reference argument cannot be a value because there is
///         nothing to hand over — the callee needs the storage itself — and it is deliberately an
///         <see cref="IrVariable" /> rather than an <see cref="IrPlace" />: lowering copies any
///         chained place into a temp first, so by the time a call is built the argument is always a
///         whole variable.
///     </para>
///     <para>
///         That is not a simplification for its own sake. SPIR-V requires a pointer argument to
///         <c>OpFunctionCall</c> to be a memory object declaration, so an access chain such as
///         <c>d.color</c> could not be passed at all, and a uniform's storage class could never
///         match the parameter's. Narrowing the IR to what both targets can express keeps the
///         backends from each inventing a different way to cope.
///     </para>
/// </remarks>
public readonly record struct IrArgument {
    IrArgument(IrValue? value, IrVariable? reference) {
        Value = value;
        Reference = reference;
    }

    /// <summary>The value, when this argument is passed by value.</summary>
    public IrValue? Value { get; }

    /// <summary>The variable whose storage is passed, when this argument is by reference.</summary>
    public IrVariable? Reference { get; }

    public bool IsByReference => Reference is not null;

    public IrType Type => Value?.Type ?? Reference!.Type;

    public static IrArgument Of(IrValue value) => new(value, null);

    public static IrArgument ByReference(IrVariable variable) => new(null, variable);

    public override string ToString() => IsByReference ? $"&{Reference!.Name}" : Value!.ToString();
}

/// <summary>A call to another function in the module.</summary>
public sealed class IrCallInstruction(IrValue? result, IrFunction function, IrArgument[] arguments)
    : IrInstruction {
    public override IrValue? Result { get; } = result;
    public IrFunction Function { get; } = function;
    public IReadOnlyList<IrArgument> Arguments { get; } = arguments;

    /// <summary>
    ///     The values this call consumes — by-value arguments only.
    /// </summary>
    /// <remarks>
    ///     A by-reference argument names storage rather than an SSA value, so it is not an operand:
    ///     the verifier's definedness check would otherwise look for a definition of something that
    ///     is a variable, not a value.
    /// </remarks>
    public override IEnumerable<IrValue> Operands =>
        Arguments.Where(argument => !argument.IsByReference).Select(argument => argument.Value!);
}

/// <summary>
///     Builds an aggregate from its parts: <c>float3(x, y, z)</c>, a matrix from
///     its columns, or a struct from its fields.
/// </summary>
/// <remarks>
///     Columns, matching what both backends emit and what <c>m[i]</c> reads back, so
///     <c>mat3(a, b, c, …)</c> fills the column that <c>m[0]</c> returns. See docs/plan/07 § E.
/// </remarks>
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

/// <summary>Ends the invocation without writing anything.</summary>
/// <remarks>
///     A terminator, and the only one that ends more than the function: SPIR-V spells it
///     <c>OpKill</c>, which must be the last instruction in its block. GLSL's <c>discard</c> is an
///     ordinary statement by comparison, and that difference is the GLSL emitter's to absorb.
/// </remarks>
public sealed class IrDiscardStatement : IrStatement;
