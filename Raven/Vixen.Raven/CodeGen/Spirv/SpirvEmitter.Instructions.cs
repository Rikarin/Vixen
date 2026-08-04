// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>A resolved storage location: a pointer, plus a swizzle the pointer could not reach.</summary>
/// <param name="Id">The pointer id, from a variable or an <c>OpAccessChain</c>.</param>
/// <param name="Type">The type the pointer points at.</param>
/// <param name="Layout">
///     The packing rule its type is decorated with, or null when it is the plain type. A rule rather
///     than a flag because a uniform block and a storage buffer lay the same struct out differently.
/// </param>
/// <param name="Storage">Its storage class, which every further access chain off it has to repeat.</param>
/// <param name="Swizzle">A multi-lane swizzle that has to be applied after loading.</param>
readonly record struct SpirvPointer(
    uint Id,
    IrType Type,
    LayoutRule? Layout,
    SpirvStorageClass Storage,
    IrSwizzleAccess? Swizzle = null,
    bool NonUniform = false
);

partial class SpirvEmitter {
    /// <summary>
    ///     Memory semantics for a barrier: <c>AcquireRelease</c> over <c>WorkgroupMemory</c>.
    /// </summary>
    /// <remarks>
    ///     What glslang emits for the same GLSL, which is what keeps docs/plan/07 § C's differential
    ///     comparing like with like — and what the operation means: everything this invocation wrote
    ///     to shared storage before the barrier is visible after it, to everyone.
    /// </remarks>
    const uint WorkgroupRelease = 0x8 | 0x100;

    readonly Dictionary<IrVariable, uint> opaqueParameters = [];
    readonly Dictionary<IrVariable, uint> pointers = [];
    readonly Dictionary<int, uint> values = [];

    /// <summary>
    ///     The integer value behind each constant, kept because SPIR-V needs to know a *literal*
    ///     index and not merely a constant one: <c>OpCompositeExtract</c> takes literals, so
    ///     pulling element 2 out of a value the function never stored is only expressible when the
    ///     2 is readable here. Nothing in the IR distinguishes a constant index from a runtime one,
    ///     which is right — the distinction is a target's, and this is the target.
    /// </summary>
    readonly Dictionary<int, int> constants = [];

    readonly Stack<(uint Merge, uint Continue)> loops = new();

    /// <summary>True once the block being built has branched or returned.</summary>
    bool terminated;

    // --- Blocks ------------------------------------------------------------

    void Add(SpirvInstruction instruction) => module.AddFunctionInstruction(instruction);

    uint Emit(SpirvOp op, uint resultType, params SpirvOperand[] operands) {
        var id = module.AllocateId();
        Add(new(op, resultType, id, operands));
        return id;
    }

    void BeginBlock(uint label) {
        Add(new(SpirvOp.Label, null, label));
        terminated = false;
    }

    /// <summary>Branches, unless the block has already ended. Answers whether it did.</summary>
    bool Branch(uint target) {
        if (terminated) {
            return false;
        }

        Add(new(SpirvOp.Branch, null, null, SpirvOperand.Id(target)));
        terminated = true;
        return true;
    }

    // --- Statements --------------------------------------------------------

    void EmitBlock(IrBlock block) {
        foreach (var statement in block.Statements) {
            EmitStatement(statement);
        }
    }

    void EmitStatement(IrStatement statement) {
        // Anything after a return or a branch is unreachable, and SPIR-V has no
        // way to spell an instruction that follows a block's terminator.
        if (terminated) {
            return;
        }

        switch (statement) {
            case IrBlock block:
                EmitBlock(block);
                break;

            case IrInstruction instruction:
                EmitInstruction(instruction);
                break;

            case IrIfStatement conditional:
                EmitIf(conditional);
                break;

            case IrLoopStatement loop:
                EmitLoop(loop);
                break;

            case IrReturnStatement { Value: { } value }:
                Add(new(SpirvOp.ReturnValue, null, null, SpirvOperand.Id(Value(value))));
                terminated = true;
                break;

            case IrReturnStatement:
                Add(new(SpirvOp.Return, null, null));
                terminated = true;
                break;

            // A block terminator in its own right, so nothing follows it and no branch is needed:
            // the invocation ends here rather than control moving somewhere else.
            case IrDiscardStatement:
                Add(new(SpirvOp.Kill, null, null));
                terminated = true;
                break;

            case IrBreakStatement when loops.Count > 0:
                Branch(loops.Peek().Merge);
                break;

            case IrContinueStatement when loops.Count > 0:
                Branch(loops.Peek().Continue);
                break;
        }
    }

    /// <summary>
    ///     A two-way branch with the merge block declared up front, which is what
    ///     makes the construct structured rather than an arbitrary jump.
    /// </summary>
    void EmitIf(IrIfStatement conditional) {
        var condition = Value(conditional.Condition);
        var merge = module.AllocateId();
        var then = module.AllocateId();
        var otherwise = conditional.Else is null ? merge : module.AllocateId();

        Add(
            new(
                SpirvOp.SelectionMerge,
                null,
                null,
                SpirvOperand.Id(merge),
                SpirvOperand.Enumerant(SpirvSelectionControl.None)
            )
        );

        Add(
            new(
                SpirvOp.BranchConditional,
                null,
                null,
                SpirvOperand.Id(condition),
                SpirvOperand.Id(then),
                SpirvOperand.Id(otherwise)
            )
        );

        terminated = true;

        BeginBlock(then);
        EmitBlock(conditional.Then);
        var reachable = Branch(merge);

        if (conditional.Else is { } elseBlock) {
            BeginBlock(otherwise);
            EmitBlock(elseBlock);
            reachable |= Branch(merge);
        } else {
            reachable = true;
        }

        BeginBlock(merge);

        // Both arms left the construct, so nothing falls into the merge. It still
        // has to exist — OpSelectionMerge named it — so it gets a terminator that
        // says it is never entered.
        if (!reachable) {
            Add(new(SpirvOp.Unreachable, null, null));
            terminated = true;
        }
    }

    /// <summary>
    ///     A structured loop: a header that declares the merge and continue targets,
    ///     a block that recomputes the condition, the body, and the step.
    /// </summary>
    /// <remarks>
    ///     This is where SPIR-V is kinder than GLSL. Its <c>continue</c> target is a
    ///     block of its own, so a counted loop's step simply goes there and a
    ///     <c>continue</c> branches straight to it — none of the first-iteration flag
    ///     the GLSL backend needs.
    /// </remarks>
    void EmitLoop(IrLoopStatement loop) {
        var header = module.AllocateId();
        var condition = module.AllocateId();
        var body = module.AllocateId();
        var @continue = module.AllocateId();
        var merge = module.AllocateId();

        Branch(header);
        BeginBlock(header);

        Add(
            new(
                SpirvOp.LoopMerge,
                null,
                null,
                SpirvOperand.Id(merge),
                SpirvOperand.Id(@continue),
                SpirvOperand.Enumerant(SpirvLoopControl.None)
            )
        );

        // A `repeat` tests after the body, so the header goes straight to it and
        // the test lives at the end of the continue block instead.
        Add(new(SpirvOp.Branch, null, null, SpirvOperand.Id(loop.TestBeforeBody ? condition : body)));

        terminated = true;

        if (loop.TestBeforeBody) {
            BeginBlock(condition);
            EmitBlock(loop.Condition);
            BranchOnCondition(loop, body, merge);
        }

        loops.Push((merge, @continue));
        BeginBlock(body);
        EmitBlock(loop.Body);
        Branch(@continue);
        loops.Pop();

        BeginBlock(@continue);

        if (loop.Continue is { } step) {
            EmitBlock(step);
        }

        if (loop.TestBeforeBody) {
            Branch(header);
        } else {
            EmitBlock(loop.Condition);
            BranchOnCondition(loop, header, merge);
        }

        BeginBlock(merge);
    }

    void BranchOnCondition(IrLoopStatement loop, uint whenTrue, uint whenFalse) {
        Add(
            new(
                SpirvOp.BranchConditional,
                null,
                null,
                SpirvOperand.Id(Value(loop.ConditionValue)),
                SpirvOperand.Id(whenTrue),
                SpirvOperand.Id(whenFalse)
            )
        );

        terminated = true;
    }

    // --- Instructions ------------------------------------------------------

    void EmitInstruction(IrInstruction instruction) {
        switch (instruction) {
            case IrConstantInstruction constant:
                // A SPIR-V constant is a module-level declaration, so it never
                // becomes an instruction inside the body at all.
                values[constant.Result.Id] = types.Constant(constant.Value, constant.Result.Type);

                // Only what could actually be an index. A `uint` past int.MaxValue — a hash seed,
                // say — is no array's index, so it is left out rather than overflowed into one.
                if (constant.Value is int or uint and <= int.MaxValue) {
                    constants[constant.Result.Id] = Convert.ToInt32(constant.Value, CultureInfo.InvariantCulture);
                }

                return;

            case IrStoreInstruction store:
                EmitStore(store.Place, Value(store.Value));
                return;

            case IrLoadInstruction load:
                values[load.Result.Id] = EmitLoad(load.Place);
                return;

            case IrArrayLengthInstruction length:
                values[length.Result.Id] = EmitArrayLength(length);
                return;

            case IrAtomicInstruction atomic:
                values[atomic.Result.Id] = EmitAtomic(atomic);
                return;

            case IrCallInstruction call: {
                // Even a void call needs a result id in SPIR-V.
                var result = Emit(
                    SpirvOp.FunctionCall,
                    types.Type(call.Function.ReturnType),
                    [
                        SpirvOperand.Id(functions[call.Function]),
                        .. call.Arguments.Select(Argument)
                    ]
                );

                if (call.Result is { } value) {
                    values[value.Id] = result;
                }

                return;
            }

            // A texel store defines nothing, so it never lands in `values`. OpImageWrite takes no
            // result id at all, unlike OpFunctionCall above.
            case IrIntrinsicInstruction { Result: null, Intrinsic: IrIntrinsic.StoreImage } store
                when store.Arguments.Count == 3:
                Add(
                    new(
                        SpirvOp.ImageWrite,
                        null,
                        null,
                        SpirvOperand.Id(Value(store.Arguments[0])),
                        SpirvOperand.Id(Value(store.Arguments[1])),
                        SpirvOperand.Id(Value(store.Arguments[2]))
                    )
                );

                return;

            // The barriers define nothing either, and take their scopes and semantics as constant
            // ids rather than as literals — SPIR-V spells them that way so they can be
            // specialization constants.
            case IrIntrinsicInstruction { Result: null, Intrinsic: IrIntrinsic.ControlBarrier }:
                Add(
                    new(
                        SpirvOp.ControlBarrier,
                        null,
                        null,
                        SpirvOperand.Id(types.ConstantUInt((uint)SpirvScope.Workgroup)),
                        SpirvOperand.Id(types.ConstantUInt((uint)SpirvScope.Workgroup)),
                        SpirvOperand.Id(types.ConstantUInt(WorkgroupRelease))
                    )
                );

                return;

            case IrIntrinsicInstruction { Result: null, Intrinsic: IrIntrinsic.MemoryBarrierShared }:
                Add(
                    new(
                        SpirvOp.MemoryBarrier,
                        null,
                        null,
                        SpirvOperand.Id(types.ConstantUInt((uint)SpirvScope.Workgroup)),
                        SpirvOperand.Id(types.ConstantUInt(WorkgroupRelease))
                    )
                );

                return;
        }

        if (instruction.Result is not { } target) {
            return;
        }

        values[target.Id] = instruction switch {
            IrUnaryInstruction unary => EmitUnary(unary),
            IrBinaryInstruction binary => EmitBinary(binary),
            IrConvertInstruction convert => EmitConvert(convert),
            IrIntrinsicInstruction intrinsic => EmitIntrinsic(intrinsic),
            IrConstructInstruction construct => EmitConstruct(construct),
            IrExtractInstruction extract => EmitExtract(extract),
            IrSelectInstruction select => EmitSelect(select),
            _ => Unimplemented(instruction.GetType().Name, target.Type)
        };
    }

    uint EmitUnary(IrUnaryInstruction unary) {
        var resultType = types.Type(unary.Result.Type);
        var operand = SpirvOperand.Id(Value(unary.Operand));
        var component = unary.Operand.Type.ComponentType.Kind;

        var op = unary.Op switch {
            IrUnaryOp.Negate when component is IrTypeKind.Float or IrTypeKind.Double => SpirvOp.FNegate,
            IrUnaryOp.Negate => SpirvOp.SNegate,
            IrUnaryOp.Not => SpirvOp.LogicalNot,
            _ => SpirvOp.Not
        };

        return Emit(op, resultType, operand);
    }

    uint EmitBinary(IrBinaryInstruction binary) {
        var resultType = types.Type(binary.Result.Type);
        var left = SpirvOperand.Id(Value(binary.Left));
        var right = SpirvOperand.Id(Value(binary.Right));

        // Which instruction an operator becomes is decided by the operands, not
        // the result: a comparison yields a bool but compares floats.
        var component = binary.Left.Type.ComponentType.Kind;

        if (Shaped(binary) is { } shaped) {
            return Emit(shaped.Op, resultType, shaped.Swap ? [right, left] : [left, right]);
        }

        var op = binary.Op switch {
            IrBinaryOp.Add => Real(component) ? SpirvOp.FAdd : SpirvOp.IAdd,
            IrBinaryOp.Subtract => Real(component) ? SpirvOp.FSub : SpirvOp.ISub,
            IrBinaryOp.Multiply => Real(component) ? SpirvOp.FMul : SpirvOp.IMul,
            IrBinaryOp.Divide => Real(component) ? SpirvOp.FDiv
                : Unsigned(component) ? SpirvOp.UDiv : SpirvOp.SDiv,
            // `%` keeps the sign of its dividend, which is the remainder rather
            // than the modulus; the `mod` intrinsic is the other one.
            IrBinaryOp.Modulo => Real(component) ? SpirvOp.FRem
                : Unsigned(component) ? SpirvOp.UMod : SpirvOp.SRem,
            IrBinaryOp.ShiftLeft => SpirvOp.ShiftLeftLogical,
            IrBinaryOp.ShiftRight when Unsigned(component) => SpirvOp.ShiftRightLogical,
            IrBinaryOp.ShiftRight => SpirvOp.ShiftRightArithmetic,
            IrBinaryOp.UnsignedShiftRight => SpirvOp.ShiftRightLogical,
            IrBinaryOp.BitwiseAnd => SpirvOp.BitwiseAnd,
            IrBinaryOp.BitwiseOr => SpirvOp.BitwiseOr,
            IrBinaryOp.BitwiseXor => SpirvOp.BitwiseXor,
            IrBinaryOp.LogicalAnd => SpirvOp.LogicalAnd,
            IrBinaryOp.LogicalOr => SpirvOp.LogicalOr,
            IrBinaryOp.Equal => component == IrTypeKind.Bool ? SpirvOp.LogicalEqual
                : Real(component) ? SpirvOp.FOrdEqual : SpirvOp.IEqual,
            IrBinaryOp.NotEqual => component == IrTypeKind.Bool ? SpirvOp.LogicalNotEqual
                : Real(component) ? SpirvOp.FOrdNotEqual : SpirvOp.INotEqual,
            IrBinaryOp.LessThan => Comparison(component, SpirvOp.FOrdLessThan, SpirvOp.SLessThan, SpirvOp.ULessThan),
            IrBinaryOp.LessThanOrEqual => Comparison(
                component,
                SpirvOp.FOrdLessThanEqual,
                SpirvOp.SLessThanEqual,
                SpirvOp.ULessThanEqual
            ),
            IrBinaryOp.GreaterThan => Comparison(
                component,
                SpirvOp.FOrdGreaterThan,
                SpirvOp.SGreaterThan,
                SpirvOp.UGreaterThan
            ),
            _ => Comparison(
                component,
                SpirvOp.FOrdGreaterThanEqual,
                SpirvOp.SGreaterThanEqual,
                SpirvOp.UGreaterThanEqual
            )
        };

        // SPIR-V's arithmetic instructions take scalars and vectors and no matrices at all, where GLSL
        // spells a matrix sum with the same `+` as a float one. So a matrix operand is decomposed here
        // into its columns rather than being handed to an instruction that cannot take it — which the
        // validator catches, and only for the shaders somebody compiles.
        if (binary.Result.Type is IrMatrixType matrix
            && binary.Left.Type is IrMatrixType
            && binary.Right.Type is IrMatrixType) {
            return Columnwise(matrix, op, resultType, left, right);
        }

        return Emit(op, resultType, left, right);
    }

    /// <summary>The same operation applied to each column of two matrices, gathered back into one.</summary>
    uint Columnwise(IrMatrixType matrix, SpirvOp op, uint resultType, SpirvOperand left, SpirvOperand right) {
        var columnType = types.Type(new IrVectorType(matrix.Component, matrix.Rows));
        var columns = new SpirvOperand[matrix.Columns];

        for (var column = 0; column < matrix.Columns; column++) {
            var index = SpirvOperand.Literal(column);
            var a = Emit(SpirvOp.CompositeExtract, columnType, left, index);
            var b = Emit(SpirvOp.CompositeExtract, columnType, right, index);

            columns[column] = SpirvOperand.Id(Emit(op, columnType, SpirvOperand.Id(a), SpirvOperand.Id(b)));
        }

        return Emit(SpirvOp.CompositeConstruct, resultType, columns);
    }

    /// <summary>
    ///     The products whose operands have different shapes. SPIR-V spells each of
    ///     them as its own instruction with a fixed operand order, so a vector times a
    ///     scalar is not the same instruction as two vectors multiplied.
    /// </summary>
    static (SpirvOp Op, bool Swap)? Shaped(IrBinaryInstruction binary) {
        var left = binary.Left.Type;
        var right = binary.Right.Type;

        return (binary.Op, left, right) switch {
            (IrBinaryOp.MatrixMultiply, IrMatrixType, IrMatrixType) => (SpirvOp.MatrixTimesMatrix, false),
            (IrBinaryOp.MatrixMultiply, IrMatrixType, IrVectorType) => (SpirvOp.MatrixTimesVector, false),
            (IrBinaryOp.MatrixMultiply, IrVectorType, IrMatrixType) => (SpirvOp.VectorTimesMatrix, false),

            // A matrix scaled. The lowerer calls `*` on a matrix a matrix multiply whatever is on the
            // other side of it, so this is the same product as the `Multiply` line below and arrives
            // under the other name — and without it the operator falls past every case in `EmitBinary`
            // into its default, which is a comparison.
            (IrBinaryOp.MatrixMultiply, IrMatrixType, { IsScalar: true }) => (SpirvOp.MatrixTimesScalar, false),
            (IrBinaryOp.MatrixMultiply, { IsScalar: true }, IrMatrixType) => (SpirvOp.MatrixTimesScalar, true),

            // Scalar operands come second in SPIR-V, whichever side they were on.
            (IrBinaryOp.Multiply, IrVectorType, { IsScalar: true }) => (SpirvOp.VectorTimesScalar, false),
            (IrBinaryOp.Multiply, { IsScalar: true }, IrVectorType) => (SpirvOp.VectorTimesScalar, true),
            (IrBinaryOp.Multiply, IrMatrixType, { IsScalar: true }) => (SpirvOp.MatrixTimesScalar, false),
            (IrBinaryOp.Multiply, { IsScalar: true }, IrMatrixType) => (SpirvOp.MatrixTimesScalar, true),
            (IrBinaryOp.Multiply, IrMatrixType, IrVectorType) => (SpirvOp.MatrixTimesVector, false),
            (IrBinaryOp.Multiply, IrVectorType, IrMatrixType) => (SpirvOp.VectorTimesMatrix, false),
            (IrBinaryOp.Multiply, IrMatrixType, IrMatrixType) => (SpirvOp.MatrixTimesMatrix, false),
            _ => null
        };
    }

    static bool Real(IrTypeKind component) => component is IrTypeKind.Float or IrTypeKind.Double;

    /// <summary>Whether an integer component is unsigned, at either width.</summary>
    /// <remarks>
    ///     Asked wherever SPIR-V splits an operation by signedness — division, remainder, the right
    ///     shift, every ordered comparison. A test against <c>UInt</c> alone was right while 32 bits
    ///     was the only width and would have made <c>uint64</c> arithmetic signed, which is exactly
    ///     wrong for the packed word the whole type exists for: the top bit of a depth-above-id key
    ///     is data, and a signed compare reads it as a sign.
    /// </remarks>
    static bool Unsigned(IrTypeKind component) => component is IrTypeKind.UInt or IrTypeKind.UInt64;

    static SpirvOp Comparison(IrTypeKind component, SpirvOp real, SpirvOp signed, SpirvOp unsigned) =>
        Real(component) ? real : Unsigned(component) ? unsigned : signed;

    uint EmitConvert(IrConvertInstruction convert) {
        var value = Value(convert.Operand);

        if (convert.ConversionKind == IrConversionKind.Splat) {
            return Splat(convert.Result.Type, value);
        }

        var resultType = types.Type(convert.Result.Type);
        var from = convert.Operand.Type.ComponentType.Kind;
        var to = convert.Result.Type.ComponentType.Kind;

        if (from == to) {
            // Nothing changes representation, so nothing is emitted.
            return value;
        }

        // Integer to integer is its own path, because SPIR-V splits what one Raven conversion does:
        // a width change and a signedness change are two instructions, and the width-changing one
        // cannot also change signedness.
        if (IsInteger(from) && IsInteger(to)) {
            return EmitIntegerConvert(convert, value, from, to);
        }

        var op = (from, to) switch {
            (IrTypeKind.Int or IrTypeKind.Int64, IrTypeKind.Float or IrTypeKind.Double) => SpirvOp.ConvertSToF,
            (IrTypeKind.UInt or IrTypeKind.UInt64, IrTypeKind.Float or IrTypeKind.Double) => SpirvOp.ConvertUToF,
            (IrTypeKind.Float or IrTypeKind.Double, IrTypeKind.Int or IrTypeKind.Int64) => SpirvOp.ConvertFToS,
            (IrTypeKind.Float or IrTypeKind.Double, IrTypeKind.UInt or IrTypeKind.UInt64) => SpirvOp.ConvertFToU,
            (IrTypeKind.Float, IrTypeKind.Double) or (IrTypeKind.Double, IrTypeKind.Float) => SpirvOp.FConvert,
            _ => SpirvOp.Nop
        };

        return op == SpirvOp.Nop
            ? Unimplemented(
                $"The conversion from '{convert.Operand.Type.Name}' to '{convert.Result.Type.Name}'",
                convert.Result.Type
            )
            : Emit(op, resultType, SpirvOperand.Id(value));
    }

    /// <summary>
    ///     Converts between two integer types, in up to two steps.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>OpSConvert</c> and <c>OpUConvert</c> change the <em>width</em> and require the
    ///         widths to differ; <c>OpBitcast</c> changes the interpretation and requires them to
    ///         match. So a conversion that does both — <c>uint</c> to <c>int64</c> — is a widen in
    ///         the source's own signedness followed by a reinterpretation, and doing it the other
    ///         way round would sign-extend a number that was never signed.
    ///     </para>
    ///     <para>
    ///         Componentwise, so this holds for vectors too, even though nothing currently builds a
    ///         64-bit one.
    ///     </para>
    /// </remarks>
    uint EmitIntegerConvert(IrConvertInstruction convert, uint value, IrTypeKind from, IrTypeKind to) {
        var resultType = types.Type(convert.Result.Type);

        // Same width: the bits do not move, only what they are read as.
        if (Wide(from) == Wide(to)) {
            return Emit(SpirvOp.Bitcast, resultType, SpirvOperand.Id(value));
        }

        var widened = Unsigned(from) ? IrScalarType.UInt64 : IrScalarType.Int64;
        var narrowed = Unsigned(from) ? IrScalarType.UInt : IrScalarType.Int;
        var intermediate = Wide(to) ? widened : narrowed;

        // The width change, keeping the source's signedness so the extension is the right one.
        var converted = Emit(
            Unsigned(from) ? SpirvOp.UConvert : SpirvOp.SConvert,
            types.Type(Componentwise(convert.Result.Type, intermediate)),
            SpirvOperand.Id(value)
        );

        return Unsigned(from) == Unsigned(to)
            ? converted
            : Emit(SpirvOp.Bitcast, resultType, SpirvOperand.Id(converted));
    }

    static bool IsInteger(IrTypeKind component) =>
        component is IrTypeKind.Int or IrTypeKind.UInt or IrTypeKind.Int64 or IrTypeKind.UInt64;

    static bool Wide(IrTypeKind component) => component is IrTypeKind.Int64 or IrTypeKind.UInt64;

    /// <summary>The shape of <paramref name="type" /> with a different component scalar.</summary>
    static IrType Componentwise(IrType type, IrScalarType component) =>
        type switch {
            IrVectorType vector => new IrVectorType(component, vector.Size),
            _ => component
        };

    /// <summary>
    ///     Builds an aggregate. A vector is easy — SPIR-V lets constituents be
    ///     scalars or shorter vectors and concatenates them, so <c>float4(v3, w)</c>
    ///     passes straight through. A matrix is not: it takes its columns and nothing
    ///     else, so a flat run of scalars has to be gathered into them first.
    /// </summary>
    uint EmitConstruct(IrConstructInstruction construct) {
        var resultType = types.Type(construct.Result.Type);

        if (construct.Result.Type is not IrMatrixType matrix) {
            return Emit(
                SpirvOp.CompositeConstruct,
                resultType,
                [.. construct.Arguments.Select(a => SpirvOperand.Id(Value(a)))]
            );
        }

        var columnType = new IrVectorType(matrix.Component, matrix.Rows);

        if (construct.Arguments.Count == matrix.Columns
            && construct.Arguments.All(argument => columnType.Equals(argument.Type))) {
            return Emit(
                SpirvOp.CompositeConstruct,
                resultType,
                [.. construct.Arguments.Select(a => SpirvOperand.Id(Value(a)))]
            );
        }

        // Flatten whatever came in, then take Rows of them per column — the same
        // order a GLSL matrix constructor fills.
        var componentType = types.Type(matrix.Component);
        List<uint> scalars = [];

        foreach (var argument in construct.Arguments) {
            if (argument.Type is IrVectorType vector) {
                for (var lane = 0; lane < vector.Size; lane++) {
                    scalars.Add(
                        Emit(
                            SpirvOp.CompositeExtract,
                            componentType,
                            SpirvOperand.Id(Value(argument)),
                            SpirvOperand.Literal(lane)
                        )
                    );
                }
            } else {
                scalars.Add(Value(argument));
            }
        }

        if (scalars.Count != matrix.Rows * matrix.Columns) {
            return Unimplemented($"Building a '{matrix.Name}' from these parts", matrix);
        }

        var columns = new SpirvOperand[matrix.Columns];

        for (var column = 0; column < matrix.Columns; column++) {
            columns[column] = SpirvOperand.Id(
                Emit(
                    SpirvOp.CompositeConstruct,
                    types.Type(columnType),
                    [.. scalars.Skip(column * matrix.Rows).Take(matrix.Rows).Select(SpirvOperand.Id)]
                )
            );
        }

        return Emit(SpirvOp.CompositeConstruct, resultType, columns);
    }

    /// <summary>Broadcasts a scalar across a vector, or across every column of a matrix.</summary>
    uint Splat(IrType target, uint scalar) {
        switch (target) {
            case IrVectorType vector:
                return Emit(
                    SpirvOp.CompositeConstruct,
                    types.Type(vector),
                    [.. Enumerable.Repeat(SpirvOperand.Id(scalar), vector.Size)]
                );

            case IrMatrixType matrix: {
                var column = Emit(
                    SpirvOp.CompositeConstruct,
                    types.Type(new IrVectorType(matrix.Component, matrix.Rows)),
                    [.. Enumerable.Repeat(SpirvOperand.Id(scalar), matrix.Rows)]
                );

                return Emit(
                    SpirvOp.CompositeConstruct,
                    types.Type(matrix),
                    [.. Enumerable.Repeat(SpirvOperand.Id(column), matrix.Columns)]
                );
            }

            default:
                return scalar;
        }
    }

    uint EmitSelect(IrSelectInstruction select) {
        var condition = Value(select.Condition);

        // Before SPIR-V 1.4 a select over vectors needs a condition per lane, so a
        // scalar test is broadcast rather than passed through.
        if (select.Result.Type is IrVectorType vector && select.Condition.Type.IsScalar) {
            condition = Emit(
                SpirvOp.CompositeConstruct,
                types.Type(new IrVectorType(IrScalarType.Bool, vector.Size)),
                [.. Enumerable.Repeat(SpirvOperand.Id(condition), vector.Size)]
            );
        }

        return Emit(
            SpirvOp.Select,
            types.Type(select.Result.Type),
            SpirvOperand.Id(condition),
            SpirvOperand.Id(Value(select.WhenTrue)),
            SpirvOperand.Id(Value(select.WhenFalse))
        );
    }

    uint EmitExtract(IrExtractInstruction extract) {
        var source = Value(extract.Source);
        var resultType = types.Type(extract.Result.Type);

        // A value has no address, so parts of it come out with composite
        // instructions rather than an access chain.
        if (extract.Chain is [IrSwizzleAccess { Components.Count: > 1 } swizzle]) {
            return Shuffle(source, source, swizzle.Components, resultType);
        }

        // A runtime index into a value is only reachable for a vector; anything
        // else would need an address.
        if (extract.Chain is [IrIndexAccess dynamic] && extract.Source.Type is IrVectorType) {
            return Emit(
                SpirvOp.VectorExtractDynamic,
                resultType,
                SpirvOperand.Id(source),
                SpirvOperand.Id(Value(dynamic.Index))
            );
        }

        List<SpirvOperand> indices = [];
        var type = extract.Source.Type;

        foreach (var access in extract.Chain) {
            switch (access) {
                case IrFieldAccess field:
                    indices.Add(SpirvOperand.Literal(field.Index));
                    break;

                case IrSwizzleAccess { Components: [var only] }:
                    indices.Add(SpirvOperand.Literal(only));
                    break;

                // A constant index is a literal index, which is all OpCompositeExtract accepts.
                // This is what lets a spread of an array be flattened, and how any part of a
                // value with no storage — a call's result, a construct — is read at a fixed
                // position.
                case IrIndexAccess { Index: var index } when constants.TryGetValue(index.Id, out var literal):
                    indices.Add(SpirvOperand.Literal(literal));
                    break;

                default:
                    return Unimplemented("Indexing a value that has no address", extract.Result.Type);
            }

            type = access.ResultType(type);
        }

        return Emit(SpirvOp.CompositeExtract, resultType, [SpirvOperand.Id(source), .. indices]);
    }

    // --- Intrinsics --------------------------------------------------------

    uint EmitIntrinsic(IrIntrinsicInstruction intrinsic) {
        var result = intrinsic.Result!;
        var resultType = types.Type(result.Type);
        var arguments = intrinsic.Arguments.Select(a => SpirvOperand.Id(Value(a))).ToArray();

        switch (intrinsic.Intrinsic) {
            case IrIntrinsic.Saturate:
                // SPIR-V has no saturate, so it is a clamp against constants that
                // have to be materialized in the type the argument came in.
                return ExtInst(
                    GlslStd450.FClamp,
                    resultType,
                    arguments[0],
                    SpirvOperand.Id(Splat(result.Type, types.ConstantFloat(0))),
                    SpirvOperand.Id(Splat(result.Type, types.ConstantFloat(1)))
                );

            case IrIntrinsic.SampleTexture:
                return EmitSample(intrinsic, resultType, arguments);

            case IrIntrinsic.SampleTextureLevel:
                return EmitSampleLevel(intrinsic, resultType, arguments);

            case IrIntrinsic.SampleTextureGrad:
                return EmitSampleGrad(intrinsic, resultType, arguments);

            case IrIntrinsic.LoadTexture:
                return EmitFetch(intrinsic, resultType, arguments);

            case IrIntrinsic.TextureSize:
                return EmitQuerySize(intrinsic, resultType, arguments);

            case IrIntrinsic.LoadImage:
                return Emit(SpirvOp.ImageRead, resultType, arguments[0], arguments[1]);

            case IrIntrinsic.ImageSize:
                // No level operand: a storage image has one, which is also why this is
                // OpImageQuerySize rather than the Lod form a sampled texture needs.
                module.AddCapability(SpirvCapability.ImageQuery);
                return Emit(SpirvOp.ImageQuerySize, resultType, arguments[0]);

            case IrIntrinsic.BitCast:
                // Same width in and out, so the identity is genuinely nothing to emit — and
                // OpBitcast with equal operand and result types is not legal SPIR-V.
                return intrinsic.Arguments[0].Type.Equals(result.Type)
                    ? Value(intrinsic.Arguments[0])
                    : Emit(SpirvOp.Bitcast, resultType, arguments[0]);

            case IrIntrinsic.ArrayLength:
                // Unsized arrays are rejected before this, so the length is known.
                return types.ConstantInt(
                    intrinsic.Arguments[0].Type is IrArrayType { Length: { } length } ? length : 0
                );

            case IrIntrinsic.TraceRayQuery:
                return EmitTraceRayQuery(intrinsic, resultType, arguments);
        }

        var mapping = SpirvIntrinsics.Map(intrinsic.Intrinsic, result.Type.ComponentType.Kind);

        return mapping switch {
            { Extended: { } extended } => ExtInst(extended, resultType, arguments),
            { Core: { } core } => Emit(core, resultType, arguments),
            _ => Unimplemented($"The '{intrinsic.Intrinsic}' intrinsic", result.Type)
        };
    }

    uint ExtInst(GlslStd450 instruction, uint resultType, params SpirvOperand[] operands) =>
        Emit(
            SpirvOp.ExtInst,
            resultType,
            [SpirvOperand.Id(extendedInstructions), SpirvOperand.Literal((uint)instruction), .. operands]
        );

    /// <summary>
    ///     Pairs an image with a sampler and samples it. This is what SPIR-V has that
    ///     GLSL does not: the two bindings stay separate right up to the sample.
    /// </summary>
    uint EmitSample(IrIntrinsicInstruction intrinsic, uint resultType, SpirvOperand[] arguments) {
        if (intrinsic.Arguments is not [{ Type: IrTextureType image }, { Type: IrSamplerType }, _]) {
            return Unimplemented("This form of texture sampling", intrinsic.Result!.Type);
        }

        var combined = Emit(SpirvOp.SampledImage, types.SampledImage(types.Type(image)), arguments[0], arguments[1]);

        // An implicit level of detail needs derivatives, which only a fragment
        // shader has; every other stage has to ask for an explicit one.
        if (entryPoint.Stage == ShaderStage.Fragment) {
            return Emit(SpirvOp.ImageSampleImplicitLod, resultType, SpirvOperand.Id(combined), arguments[2]);
        }

        return Emit(
            SpirvOp.ImageSampleExplicitLod,
            resultType,
            SpirvOperand.Id(combined),
            arguments[2],
            // Image operands: bit 1 is Lod, and the level follows.
            SpirvOperand.Literal(0x2),
            SpirvOperand.Id(types.ConstantFloat(0))
        );
    }

    /// <summary>
    ///     Samples at a stated level of detail. The same instruction the non-fragment path of
    ///     <see cref="EmitSample" /> uses, with the author's level instead of a fabricated zero.
    /// </summary>
    uint EmitSampleLevel(IrIntrinsicInstruction intrinsic, uint resultType, SpirvOperand[] arguments) {
        if (intrinsic.Arguments is not [{ Type: IrTextureType image }, { Type: IrSamplerType }, _, _]) {
            return Unimplemented("This form of texture sampling", intrinsic.Result!.Type);
        }

        var combined = Emit(SpirvOp.SampledImage, types.SampledImage(types.Type(image)), arguments[0], arguments[1]);

        return Emit(
            SpirvOp.ImageSampleExplicitLod,
            resultType,
            SpirvOperand.Id(combined),
            arguments[2],
            // Image operands: bit 1 is Lod, and the level follows.
            SpirvOperand.Literal(0x2),
            arguments[3]
        );
    }

    /// <summary>
    ///     Samples with the caller's own gradients rather than the quad's.
    /// </summary>
    /// <remarks>
    ///     The same <c>OpImageSampleExplicitLod</c> the other two explicit forms use, with the
    ///     <c>Grad</c> operand instead of <c>Lod</c> — and the two are mutually exclusive, which is
    ///     why this is a third method rather than a flag on <see cref="EmitSampleLevel" />. Both
    ///     gradients follow the mask, in x-then-y order, and each has as many lanes as the image has
    ///     coordinates.
    /// </remarks>
    uint EmitSampleGrad(IrIntrinsicInstruction intrinsic, uint resultType, SpirvOperand[] arguments) {
        if (intrinsic.Arguments is not [{ Type: IrTextureType image }, { Type: IrSamplerType }, _, _, _]) {
            return Unimplemented("This form of texture sampling", intrinsic.Result!.Type);
        }

        var combined = Emit(SpirvOp.SampledImage, types.SampledImage(types.Type(image)), arguments[0], arguments[1]);

        return Emit(
            SpirvOp.ImageSampleExplicitLod,
            resultType,
            [
                SpirvOperand.Id(combined),
                arguments[2],
                // Image operands: bit 2 is Grad, and the two gradients follow.
                SpirvOperand.Literal(0x4),
                arguments[3],
                arguments[4]
            ]
        );
    }

    /// <summary>
    ///     Queries one mip level's size. Takes the plain image rather than a sampled one, which
    ///     is why the GLSL side needs the samplerless-texture extension to say the same thing.
    /// </summary>
    uint EmitQuerySize(IrIntrinsicInstruction intrinsic, uint resultType, SpirvOperand[] arguments) {
        if (intrinsic.Arguments is not [{ Type: IrTextureType }, _]) {
            return Unimplemented("This form of texture size query", intrinsic.Result!.Type);
        }

        module.AddCapability(SpirvCapability.ImageQuery);
        return Emit(SpirvOp.ImageQuerySizeLod, resultType, arguments[0], arguments[1]);
    }

    /// <summary>
    ///     Fetches a texel by integer coordinate. The IR packs the level into the
    ///     coordinate's last lane, exactly as the GLSL backend reads it.
    /// </summary>
    uint EmitFetch(IrIntrinsicInstruction intrinsic, uint resultType, SpirvOperand[] arguments) {
        if (intrinsic.Arguments is not [{ Type: IrTextureType image }, { Type: IrVectorType coordinate } source]) {
            return Unimplemented("This form of texel fetch", intrinsic.Result!.Type);
        }

        var wanted = image.Dimension == IrTextureDimension.Texture2D ? 2 : 3;

        if (coordinate.Size <= wanted) {
            return Emit(SpirvOp.ImageFetch, resultType, arguments[0], arguments[1]);
        }

        var coordinateType = types.Type(new IrVectorType(coordinate.Component, wanted));
        var trimmed = Shuffle(Value(source), Value(source), [.. Enumerable.Range(0, wanted)], coordinateType);

        var level = Emit(
            SpirvOp.CompositeExtract,
            types.Type(coordinate.Component),
            SpirvOperand.Id(Value(source)),
            SpirvOperand.Literal(wanted)
        );

        return Emit(
            SpirvOp.ImageFetch,
            resultType,
            arguments[0],
            SpirvOperand.Id(trimmed),
            SpirvOperand.Literal(0x2),
            SpirvOperand.Id(level)
        );
    }

    /// <summary>
    ///     One whole ray query, inline at the call site:
    ///     <c>IrIntrinsic.TraceRayQuery</c>'s contract in blocks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The query variable was hoisted with the function's locals — an <c>OpVariable</c>
    ///         may only sit at the top of the first block, and this is the one opaque type SPIR-V
    ///         puts in <c>Function</c> storage. The traversal itself is a structured loop
    ///         (header → condition → continue → header, with <c>OpRayQueryProceedKHR</c> as the
    ///         test), then a structured selection over the committed intersection kind, and an
    ///         <c>OpPhi</c> joins the two arms' vectors — the same merge discipline
    ///         <see cref="EmitIf" /> and <see cref="EmitLoop" /> keep, because the validator holds
    ///         synthesized flow to exactly the rules it holds lowered flow to.
    ///     </para>
    ///     <para>
    ///         The constant <c>1</c> is three things at once here — ray flags <em>Opaque</em>, the
    ///         <em>committed</em> intersection selector, and
    ///         <c>RayQueryCommittedIntersectionTriangleKHR</c> — which is a coincidence of the
    ///         spec's numbering, not one decision; each use is spelled at its site.
    ///     </para>
    /// </remarks>
    uint EmitTraceRayQuery(IrIntrinsicInstruction intrinsic, uint resultType, SpirvOperand[] arguments) {
        if (intrinsic.Arguments.Count != 5 || rayQueryVariable is not { } query) {
            return Unimplemented("This form of ray query", intrinsic.Result!.Type);
        }

        // Arguments are [structure, origin, tmin, direction, tmax]; flags Opaque (0x01) and cull
        // mask 0xFF are the contract's, so the author never writes them.
        Add(
            new(
                SpirvOp.RayQueryInitializeKHR,
                null,
                null,
                [
                    SpirvOperand.Id(query),
                    arguments[0],
                    SpirvOperand.Id(types.ConstantUInt(1)),
                    SpirvOperand.Id(types.ConstantUInt(0xFF)),
                    arguments[1],
                    arguments[2],
                    arguments[3],
                    arguments[4]
                ]
            )
        );

        var header = module.AllocateId();
        var condition = module.AllocateId();
        var @continue = module.AllocateId();
        var merge = module.AllocateId();

        Branch(header);
        BeginBlock(header);
        Add(
            new(
                SpirvOp.LoopMerge,
                null,
                null,
                SpirvOperand.Id(merge),
                SpirvOperand.Id(@continue),
                SpirvOperand.Enumerant(SpirvLoopControl.None)
            )
        );

        Branch(condition);

        BeginBlock(condition);
        var proceeding = Emit(SpirvOp.RayQueryProceedKHR, types.Bool, SpirvOperand.Id(query));
        Add(
            new(
                SpirvOp.BranchConditional,
                null,
                null,
                SpirvOperand.Id(proceeding),
                SpirvOperand.Id(@continue),
                SpirvOperand.Id(merge)
            )
        );

        terminated = true;

        BeginBlock(@continue);
        Branch(header);
        BeginBlock(merge);

        // Committed (1) rather than candidate (0): the loop above ran the query to completion, so
        // the candidate no longer exists to ask about.
        var committed = types.ConstantUInt(1);
        var kind = Emit(
            SpirvOp.RayQueryGetIntersectionTypeKHR,
            types.UInt,
            SpirvOperand.Id(query),
            SpirvOperand.Id(committed)
        );

        var isTriangle = Emit(
            SpirvOp.IEqual,
            types.Bool,
            SpirvOperand.Id(kind),
            // RayQueryCommittedIntersectionTriangleKHR = 1.
            SpirvOperand.Id(types.ConstantUInt(1))
        );

        var then = module.AllocateId();
        var otherwise = module.AllocateId();
        var endif = module.AllocateId();

        Add(
            new(
                SpirvOp.SelectionMerge,
                null,
                null,
                SpirvOperand.Id(endif),
                SpirvOperand.Enumerant(SpirvSelectionControl.None)
            )
        );

        Add(
            new(
                SpirvOp.BranchConditional,
                null,
                null,
                SpirvOperand.Id(isTriangle),
                SpirvOperand.Id(then),
                SpirvOperand.Id(otherwise)
            )
        );

        terminated = true;

        BeginBlock(then);
        var t = Emit(SpirvOp.RayQueryGetIntersectionTKHR, types.Float, SpirvOperand.Id(query), SpirvOperand.Id(committed));
        var primitive = Emit(
            SpirvOp.RayQueryGetIntersectionPrimitiveIndexKHR,
            types.UInt,
            SpirvOperand.Id(query),
            SpirvOperand.Id(committed)
        );

        var instance = Emit(
            SpirvOp.RayQueryGetIntersectionInstanceIdKHR,
            types.UInt,
            SpirvOperand.Id(query),
            SpirvOperand.Id(committed)
        );

        // The indices come back as uints, so the widening is unsigned — exact below 2^24, which
        // outreaches what a bottom-level structure may hold.
        var hit = Emit(
            SpirvOp.CompositeConstruct,
            resultType,
            SpirvOperand.Id(t),
            SpirvOperand.Id(Emit(SpirvOp.ConvertUToF, types.Float, SpirvOperand.Id(primitive))),
            SpirvOperand.Id(Emit(SpirvOp.ConvertUToF, types.Float, SpirvOperand.Id(instance))),
            SpirvOperand.Id(types.ConstantFloat(1))
        );

        Branch(endif);

        BeginBlock(otherwise);

        // The miss reuses the caller's tmax, so `.x` is a distance a march may take either way.
        var miss = Emit(
            SpirvOp.CompositeConstruct,
            resultType,
            arguments[4],
            SpirvOperand.Id(types.ConstantFloat(-1)),
            SpirvOperand.Id(types.ConstantFloat(-1)),
            SpirvOperand.Id(types.ConstantFloat(0))
        );

        Branch(endif);

        BeginBlock(endif);
        return Emit(
            SpirvOp.Phi,
            resultType,
            SpirvOperand.Id(hit),
            SpirvOperand.Id(then),
            SpirvOperand.Id(miss),
            SpirvOperand.Id(otherwise)
        );
    }

    /// <summary>
    ///     One operand of an <c>OpFunctionCall</c>: a value, or the pointer a by-reference argument
    ///     hands over.
    /// </summary>
    /// <remarks>
    ///     The pointer comes straight out of <see cref="pointers" />, which is the temp's own
    ///     <c>OpVariable</c> — a memory object declaration in function storage, which is the only
    ///     thing SPIR-V accepts here. The lowerer guarantees the argument is a whole local, so there
    ///     is no access chain to reach through and nothing to fall back on.
    /// </remarks>
    SpirvOperand Argument(IrArgument argument) {
        if (!argument.IsByReference) {
            return SpirvOperand.Id(Value(argument.Value!));
        }

        if (pointers.TryGetValue(argument.Reference!, out var pointer)) {
            return SpirvOperand.Id(pointer);
        }

        Report(BackendDiagnostics.NotImplemented, $"Passing '{argument.Reference!.Name}' by reference");
        return SpirvOperand.Id(types.ConstantInt(0));
    }

    // --- Places ------------------------------------------------------------

    uint EmitLoad(IrPlace place) {
        // An opaque parameter never became memory, so reading it is just the value.
        if (place.Chain.Count == 0 && opaqueParameters.TryGetValue(place.Root, out var opaque)) {
            return opaque;
        }

        var pointer = Resolve(place);
        var plain = types.Type(pointer.Type);
        var pointee = types.Type(pointer.Type, pointer.Layout);

        // The laid-out form of an aggregate is a *different type* from the plain one, so it
        // cannot be loaded as itself and there is no cast between the two. It comes out member by
        // member instead.
        var loaded = pointee != plain
            ? LoadAcrossLayout(pointer.Id, pointer.Storage, pointer.Type, pointer.Layout!.Value)
            : Emit(SpirvOp.Load, pointee, SpirvOperand.Id(pointer.Id));

        // ⚠ The *loaded object* as well as the index and the pointer, and this is the one that
        // matters to a driver. `OpImageFetch` takes the image, not the access chain — so the
        // decoration a compiler backend actually reads before deciding whether it may hoist the
        // descriptor load has to be on this id. Raven decorated the other two and not this one:
        // llvmpipe read descriptor zero for all sixty-four invocations of `BindlessProbe`, MoltenVK
        // honoured the intent anyway, and the module validated either way. It is also what glslang
        // emits for `nonuniformEXT`, which is the comparison docs/plan/07 § C keeps.
        if (pointer.NonUniform) {
            module.Decorate(loaded, SpirvDecoration.NonUniform);
        }

        return pointer.Swizzle is { } swizzle
            ? Shuffle(loaded, loaded, swizzle.Components, types.Type(swizzle.ResultType(pointer.Type)))
            : loaded;
    }

    /// <summary>
    ///     The element count of a storage buffer's runtime array.
    /// </summary>
    /// <remarks>
    ///     <c>OpArrayLength</c> takes the <em>block variable</em> and a member index, not a pointer to
    ///     the array — which is why the IR carries a place here and why the access chain that would
    ///     normally be built is skipped. The result is a <c>uint</c> in SPIR-V and an <c>int</c> in
    ///     both GLSL and the IR, so it is converted rather than reinterpreted.
    /// </remarks>
    uint EmitArrayLength(IrArrayLengthInstruction instruction) {
        if (!globals.TryGetValue(instruction.Place.Root, out var global) || global.Member is not { } member) {
            return Unimplemented($"The length of '{instruction.Place.Root.Name}'", instruction.Result.Type);
        }

        var length = Emit(
            SpirvOp.ArrayLength,
            types.UInt,
            SpirvOperand.Id(global.Variable),
            SpirvOperand.Literal(member)
        );

        return Emit(SpirvOp.Bitcast, types.Int, SpirvOperand.Id(length));
    }

    /// <summary>
    ///     Reads an aggregate out of an explicitly laid out block into its plain form, member by
    ///     member.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A uniform block's struct is declared with <c>Offset</c> decorations and its arrays
    ///         with an <c>ArrayStride</c>, which makes it a distinct <c>OpTypeStruct</c> from the
    ///         one a local of the same Raven type gets. SPIR-V has no conversion between two struct
    ///         types, so <c>surface = lights[i]</c> cannot be one <c>OpLoad</c>: each leaf is
    ///         loaded through its own access chain and the aggregate is rebuilt with
    ///         <c>OpCompositeConstruct</c>.
    ///     </para>
    ///     <para>
    ///         Only structs and arrays differ between the two forms — a vector or a matrix interns
    ///         to the same id either way, since their layout is expressed as decorations on the
    ///         enclosing struct's members rather than on the type. So the recursion bottoms out at
    ///         the first non-aggregate, where a plain load is the whole answer.
    ///     </para>
    /// </remarks>
    uint LoadAcrossLayout(uint pointer, SpirvStorageClass storage, IrType type, LayoutRule rule) {
        switch (type) {
            case IrStructType structType: {
                var members = new SpirvOperand[structType.Fields.Count];
                for (var i = 0; i < structType.Fields.Count; i++) {
                    members[i] = SpirvOperand.Id(
                        LoadAcrossLayout(
                            Step(pointer, storage, structType.Fields[i].Type, types.ConstantInt(i), rule),
                            storage,
                            structType.Fields[i].Type,
                            rule
                        )
                    );
                }

                return Emit(SpirvOp.CompositeConstruct, types.Type(structType), members);
            }

            case IrArrayType { Length: { } length } array: {
                var elements = new SpirvOperand[length];
                for (var i = 0; i < length; i++) {
                    elements[i] = SpirvOperand.Id(
                        LoadAcrossLayout(
                            Step(pointer, storage, array.Element, types.ConstantInt(i), rule),
                            storage,
                            array.Element,
                            rule
                        )
                    );
                }

                return Emit(SpirvOp.CompositeConstruct, types.Type(array), elements);
            }

            default:
                return Emit(SpirvOp.Load, types.Type(type), SpirvOperand.Id(pointer));
        }
    }

    /// <summary>One access chain step into a laid-out aggregate.</summary>
    uint Step(uint pointer, SpirvStorageClass storage, IrType member, uint index, LayoutRule rule) =>
        Emit(
            SpirvOp.AccessChain,
            types.Pointer(storage, types.Type(member, rule)),
            SpirvOperand.Id(pointer),
            SpirvOperand.Id(index)
        );

    /// <summary>
    ///     Writes a plain aggregate into an explicitly laid out block, member by member.
    /// </summary>
    /// <remarks>
    ///     The mirror of <see cref="LoadAcrossLayout" /> and forced for the same reason: the laid-out
    ///     struct is a different type, so one <c>OpStore</c> of the plain value is invalid — which is
    ///     exactly what <c>spirv-val</c> says when a <c>particles[i] = p</c> is emitted as one.
    /// </remarks>
    void StoreAcrossLayout(uint pointer, SpirvStorageClass storage, IrType type, LayoutRule rule, uint value) {
        switch (type) {
            case IrStructType structType:
                for (var i = 0; i < structType.Fields.Count; i++) {
                    var field = structType.Fields[i].Type;
                    StoreAcrossLayout(
                        Step(pointer, storage, field, types.ConstantInt(i), rule),
                        storage,
                        field,
                        rule,
                        Emit(SpirvOp.CompositeExtract, types.Type(field), SpirvOperand.Id(value), SpirvOperand.Literal(i))
                    );
                }

                return;

            case IrArrayType { Length: { } length } array:
                for (var i = 0; i < length; i++) {
                    StoreAcrossLayout(
                        Step(pointer, storage, array.Element, types.ConstantInt(i), rule),
                        storage,
                        array.Element,
                        rule,
                        Emit(
                            SpirvOp.CompositeExtract,
                            types.Type(array.Element),
                            SpirvOperand.Id(value),
                            SpirvOperand.Literal(i)
                        )
                    );
                }

                return;

            default:
                Add(new(SpirvOp.Store, null, null, SpirvOperand.Id(pointer), SpirvOperand.Id(value)));
                return;
        }
    }

    /// <summary>
    ///     An atomic read-modify-write, as a pointer and two constants the author never writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Device scope and relaxed semantics</b>, which is what glslang emits for the same
    ///         GLSL and therefore what keeps § C's differential comparing like with like. Device
    ///         because a storage buffer is visible to the whole dispatch and a workgroup-scoped
    ///         atomic on one would be wrong wherever two workgroups touched the same counter;
    ///         relaxed because these operations order themselves and nothing else, which is all an
    ///         allocator needs and all the language currently offers a way to ask for. Device stays
    ///         the scope for a <c>groupshared</c> target too: a wider scope than the storage needs
    ///         is always correct, and narrowing it here would make one atomic's spelling depend on
    ///         where its place happened to root.
    ///     </para>
    ///     <para>
    ///         The pointer comes from the same <see cref="Resolve" /> a store uses, marked as a
    ///         write — so an access chain into a buffer element is built exactly once, here as
    ///         there.
    ///     </para>
    /// </remarks>
    uint EmitAtomic(IrAtomicInstruction instruction) {
        var pointer = Resolve(instruction.Place, write: true);
        var type = types.Type(instruction.Result.Type);
        var scope = types.ConstantUInt((uint)SpirvScope.Device);
        var relaxed = types.ConstantUInt(0);
        var signed = instruction.Place.Type is IrScalarType { IsUnsigned: false };

        // The width is a capability of its own, and it is not the type's: a device may offer
        // 64-bit integers and no atomics on them, which is two Vulkan features and two answers.
        if (instruction.Place.Type is IrScalarType { Is64Bit: true }) {
            module.AddCapability(SpirvCapability.Int64Atomics);
        }

        if (instruction.Comparand is { } comparand) {
            // Equal and unequal semantics, in that order, and then value before comparator — the
            // reverse of how GLSL and this language's signature take them.
            return Emit(
                SpirvOp.AtomicCompareExchange,
                type,
                [
                    SpirvOperand.Id(pointer.Id),
                    SpirvOperand.Id(scope),
                    SpirvOperand.Id(relaxed),
                    SpirvOperand.Id(relaxed),
                    SpirvOperand.Id(Value(instruction.Value)),
                    SpirvOperand.Id(Value(comparand))
                ]
            );
        }

        var op = instruction.Op switch {
            IrAtomicOp.Add => SpirvOp.AtomicIAdd,
            IrAtomicOp.Min => signed ? SpirvOp.AtomicSMin : SpirvOp.AtomicUMin,
            IrAtomicOp.Max => signed ? SpirvOp.AtomicSMax : SpirvOp.AtomicUMax,
            IrAtomicOp.And => SpirvOp.AtomicAnd,
            IrAtomicOp.Or => SpirvOp.AtomicOr,
            IrAtomicOp.Xor => SpirvOp.AtomicXor,
            _ => SpirvOp.AtomicExchange
        };

        return Emit(
            op,
            type,
            [
                SpirvOperand.Id(pointer.Id),
                SpirvOperand.Id(scope),
                SpirvOperand.Id(relaxed),
                SpirvOperand.Id(Value(instruction.Value))
            ]
        );
    }

    void EmitStore(IrPlace place, uint value) {
        var pointer = Resolve(place, write: true);

        if (pointer.Swizzle is not { } swizzle) {
            // The laid-out form of an aggregate is a different type from the plain one, so a whole
            // aggregate written into a descriptor goes in member by member.
            if (pointer.Layout is { } rule && types.Type(pointer.Type, rule) != types.Type(pointer.Type)) {
                StoreAcrossLayout(pointer.Id, pointer.Storage, pointer.Type, rule, value);
                return;
            }

            Add(new(SpirvOp.Store, null, null, SpirvOperand.Id(pointer.Id), SpirvOperand.Id(value)));
            return;
        }

        // Writing some lanes of a vector means reading it, shuffling the new lanes
        // in, and writing the whole thing back. For a stream that read has to come from the
        // input variable — an Output cannot be loaded — which is why lowering marks a partial
        // write as a read too.
        var whole = types.Type(pointer.Type);
        var original = Emit(SpirvOp.Load, whole, SpirvOperand.Id(Resolve(place).Id));
        var lanes = ((IrVectorType)pointer.Type).Size;

        var selectors = new int[lanes];
        for (var i = 0; i < lanes; i++) {
            var replacement = swizzle.Components.ToList().IndexOf(i);
            selectors[i] = replacement < 0 ? i : lanes + replacement;
        }

        var merged = Emit(
            SpirvOp.VectorShuffle,
            whole,
            [SpirvOperand.Id(original), SpirvOperand.Id(value), .. selectors.Select(SpirvOperand.Literal)]
        );

        Add(new(SpirvOp.Store, null, null, SpirvOperand.Id(pointer.Id), SpirvOperand.Id(merged)));
    }

    /// <summary>
    ///     Turns a place into a pointer, building an access chain when it needs one.
    /// </summary>
    /// <param name="place">The storage location to reach.</param>
    /// <param name="write">
    ///     Which direction a stream resolves in. Every other root has one variable whichever way it
    ///     is used; a stream has an <c>Input</c> and an <c>Output</c>, because SPIR-V splits what the
    ///     IR models as one.
    /// </param>
    SpirvPointer Resolve(IrPlace place, bool write = false) {
        uint baseId;
        SpirvStorageClass storage;
        List<SpirvOperand> indices = [];

        if (ResolveStream(place.Root, write) is { } stream) {
            baseId = stream;
            storage = write ? SpirvStorageClass.Output : SpirvStorageClass.Input;
        } else if (globals.TryGetValue(place.Root, out var global)) {
            baseId = global.Variable;
            storage = global.Storage;

            if (global.Member is { } member) {
                // A record's member is reached through two more subscripts than a block's: member 0
                // of the block is the runtime array, then the record, then the member. The index is
                // loaded here rather than hoisted, because it is a uniform like any other and this
                // is the only place that knows a per-material access is being made.
                if (global.RecordIndex is { } record && globals.TryGetValue(record.Variable, out var slot)) {
                    indices.Add(SpirvOperand.Id(types.ConstantInt(0)));
                    indices.Add(SpirvOperand.Id(LoadGlobal(slot, record.Type)));
                }

                indices.Add(SpirvOperand.Id(types.ConstantInt(member)));
            }
        } else if (pointers.TryGetValue(place.Root, out var pointer)) {
            baseId = pointer;
            storage = SpirvStorageClass.Function;
        } else {
            Report(BackendDiagnostics.NotImplemented, $"Reaching the variable '{place.Root.Name}'");
            return new(types.ConstantInt(0), place.Type, null, SpirvStorageClass.Function);
        }

        // Which packing rule — if any — this root's type is decorated with. It comes off the global
        // rather than off the storage class, because a uniform block and a storage buffer share the
        // `Uniform` class in the SPIR-V 1.0 form and pack differently. A stream and a local are
        // interface or function storage with no host-visible layout at all.
        var layout = globals.TryGetValue(place.Root, out var root) ? root.Layout : null;
        var type = place.Root.Type;
        IrSwizzleAccess? trailing = null;
        var nonUniform = false;

        foreach (var access in place.Chain) {
            switch (access) {
                case IrFieldAccess field:
                    indices.Add(SpirvOperand.Id(types.ConstantInt(field.Index)));
                    break;

                // A matrix indexes by column, which an access chain reaches exactly as it
                // reaches an array element or a vector lane.
                case IrIndexAccess index: {
                    var id = Value(index.Index);

                    // Indexing a descriptor array is the one subscript whose *index* has to be
                    // decorated. Without it the driver is entitled to assume the number is uniform
                    // across the subgroup and hoist the descriptor load — which is correct for every
                    // other array here and is exactly wrong for the one case that exists so a merged
                    // draw can read a different texture per fragment.
                    if (SpirvTypes.IsDescriptorArray(type)) {
                        module.Decorate(id, SpirvDecoration.NonUniform);
                        nonUniform = true;
                    }

                    indices.Add(SpirvOperand.Id(id));
                    break;
                }

                // One lane is a component of the vector, which a pointer can reach.
                case IrSwizzleAccess { Components: [var only] }:
                    indices.Add(SpirvOperand.Id(types.ConstantInt(only)));
                    break;

                case IrSwizzleAccess swizzle:
                    // More than one lane is not a location at all, so the pointer
                    // stops at the vector and the shuffle happens on the value.
                    trailing = swizzle;
                    break;
            }

            if (trailing is not null) {
                break;
            }

            type = access.ResultType(type);
        }

        if (indices.Count == 0) {
            return new(baseId, type, layout, storage, trailing);
        }

        var chain = Emit(
            SpirvOp.AccessChain,
            types.Pointer(storage, types.Type(type, layout)),
            [SpirvOperand.Id(baseId), .. indices]
        );

        // The pointer as well as the index. The index says the *number* varies across the subgroup;
        // this says the descriptor does, and it is the one a driver reads before deciding whether the
        // load can be hoisted. A module with only the first validates and then samples one material's
        // texture on every fragment of a merged draw — which is indistinguishable from a merge that
        // worked, on the picture and in a capture.
        if (nonUniform) {
            module.Decorate(chain, SpirvDecoration.NonUniform);
        }

        return new(chain, type, layout, storage, trailing, nonUniform);
    }

    /// <summary>Loads a global's value — used for the index a material record is read at.</summary>
    /// <remarks>
    ///     A small access chain and a load rather than anything the value path offers, because the
    ///     index is not an <c>IrValue</c> the body produced: it is a binding, and the body never
    ///     mentions it. Emitted at each use rather than once, which is what a driver would do with it
    ///     anyway — a uniform load is the cheapest thing in the shader and hoisting it here would
    ///     mean deciding where "here" is across basic blocks.
    /// </remarks>
    uint LoadGlobal(SpirvGlobal global, IrType type) {
        var pointer = global.Variable;

        if (global.Member is { } member) {
            pointer = Emit(
                SpirvOp.AccessChain,
                types.Pointer(global.Storage, types.Type(type, global.Layout)),
                SpirvOperand.Id(global.Variable),
                SpirvOperand.Id(types.ConstantInt(member))
            );
        }

        return Emit(SpirvOp.Load, types.Type(type), SpirvOperand.Id(pointer));
    }

    // --- Values ------------------------------------------------------------

    uint Value(IrValue value) => values.TryGetValue(value.Id, out var id) ? id : types.ConstantNull(value.Type);

    uint Shuffle(uint left, uint right, IReadOnlyList<int> components, uint resultType) {
        // A single lane is an extract, not a shuffle: a shuffle always yields a
        // vector, and one component of a vector is a scalar.
        if (components.Count == 1) {
            return Emit(
                SpirvOp.CompositeExtract,
                resultType,
                SpirvOperand.Id(left),
                SpirvOperand.Literal(components[0])
            );
        }

        return Emit(
            SpirvOp.VectorShuffle,
            resultType,
            [SpirvOperand.Id(left), SpirvOperand.Id(right), .. components.Select(SpirvOperand.Literal)]
        );
    }

    uint Unimplemented(string what, IrType type) {
        Report(BackendDiagnostics.NotImplemented, what);
        return types.ConstantNull(type);
    }
}
