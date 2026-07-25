// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>A resolved storage location: a pointer, plus a swizzle the pointer could not reach.</summary>
/// <param name="Id">The pointer id, from a variable or an <c>OpAccessChain</c>.</param>
/// <param name="Type">The type the pointer points at.</param>
/// <param name="Layout">Whether that type is the explicitly laid out variant.</param>
/// <param name="Swizzle">A multi-lane swizzle that has to be applied after loading.</param>
readonly record struct SpirvPointer(uint Id, IrType Type, bool Layout, IrSwizzleAccess? Swizzle = null);

partial class SpirvEmitter {
    readonly Dictionary<IrVariable, uint> opaqueParameters = [];
    readonly Dictionary<IrVariable, uint> pointers = [];
    readonly Dictionary<int, uint> values = [];
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
                return;

            case IrStoreInstruction store:
                EmitStore(store.Place, Value(store.Value));
                return;

            case IrLoadInstruction load:
                values[load.Result.Id] = EmitLoad(load.Place);
                return;

            case IrCallInstruction call: {
                // Even a void call needs a result id in SPIR-V.
                var result = Emit(
                    SpirvOp.FunctionCall,
                    types.Type(call.Function.ReturnType),
                    [
                        SpirvOperand.Id(functions[call.Function]),
                        .. call.Arguments.Select(a => SpirvOperand.Id(Value(a)))
                    ]
                );

                if (call.Result is { } value) {
                    values[value.Id] = result;
                }

                return;
            }
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
                : component == IrTypeKind.UInt ? SpirvOp.UDiv : SpirvOp.SDiv,
            // `%` keeps the sign of its dividend, which is the remainder rather
            // than the modulus; the `mod` intrinsic is the other one.
            IrBinaryOp.Modulo => Real(component) ? SpirvOp.FRem
                : component == IrTypeKind.UInt ? SpirvOp.UMod : SpirvOp.SRem,
            IrBinaryOp.ShiftLeft => SpirvOp.ShiftLeftLogical,
            IrBinaryOp.ShiftRight when component == IrTypeKind.UInt => SpirvOp.ShiftRightLogical,
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

        return Emit(op, resultType, left, right);
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

    static SpirvOp Comparison(IrTypeKind component, SpirvOp real, SpirvOp signed, SpirvOp unsigned) =>
        Real(component) ? real : component == IrTypeKind.UInt ? unsigned : signed;

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

        var op = (from, to) switch {
            (IrTypeKind.Int, IrTypeKind.Float or IrTypeKind.Double) => SpirvOp.ConvertSToF,
            (IrTypeKind.UInt, IrTypeKind.Float or IrTypeKind.Double) => SpirvOp.ConvertUToF,
            (IrTypeKind.Float or IrTypeKind.Double, IrTypeKind.Int) => SpirvOp.ConvertFToS,
            (IrTypeKind.Float or IrTypeKind.Double, IrTypeKind.UInt) => SpirvOp.ConvertFToU,
            (IrTypeKind.Float, IrTypeKind.Double) or (IrTypeKind.Double, IrTypeKind.Float) => SpirvOp.FConvert,
            (IrTypeKind.Int, IrTypeKind.UInt) or (IrTypeKind.UInt, IrTypeKind.Int) => SpirvOp.Bitcast,
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

            case IrIntrinsic.LoadTexture:
                return EmitFetch(intrinsic, resultType, arguments);

            case IrIntrinsic.ArrayLength:
                // Unsized arrays are rejected before this, so the length is known.
                return types.ConstantInt(
                    intrinsic.Arguments[0].Type is IrArrayType { Length: { } length } ? length : 0
                );
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
        if (entryPoint.Stage == ShaderStage.Pixel) {
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

    // --- Places ------------------------------------------------------------

    uint EmitLoad(IrPlace place) {
        // An opaque parameter never became memory, so reading it is just the value.
        if (place.Chain.Count == 0 && opaqueParameters.TryGetValue(place.Root, out var opaque)) {
            return opaque;
        }

        var pointer = Resolve(place);
        var plain = types.Type(pointer.Type);
        var pointee = pointer.Layout ? types.Type(pointer.Type, true) : plain;

        if (pointee != plain) {
            // The laid-out form of an aggregate is a different type from the plain
            // one, so a whole-aggregate read out of a uniform block would need a
            // member-by-member copy this backend does not build yet.
            return Unimplemented($"Reading the whole '{pointer.Type.Name}' out of a uniform block", pointer.Type);
        }

        var loaded = Emit(SpirvOp.Load, pointee, SpirvOperand.Id(pointer.Id));

        return pointer.Swizzle is { } swizzle
            ? Shuffle(loaded, loaded, swizzle.Components, types.Type(swizzle.ResultType(pointer.Type)))
            : loaded;
    }

    void EmitStore(IrPlace place, uint value) {
        var pointer = Resolve(place);

        if (pointer.Swizzle is not { } swizzle) {
            Add(new(SpirvOp.Store, null, null, SpirvOperand.Id(pointer.Id), SpirvOperand.Id(value)));
            return;
        }

        // Writing some lanes of a vector means reading it, shuffling the new lanes
        // in, and writing the whole thing back.
        var whole = types.Type(pointer.Type);
        var original = Emit(SpirvOp.Load, whole, SpirvOperand.Id(pointer.Id));
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

    /// <summary>Turns a place into a pointer, building an access chain when it needs one.</summary>
    SpirvPointer Resolve(IrPlace place) {
        uint baseId;
        SpirvStorageClass storage;
        List<SpirvOperand> indices = [];

        if (globals.TryGetValue(place.Root, out var global)) {
            baseId = global.Variable;
            storage = global.Storage;

            if (global.Member is { } member) {
                indices.Add(SpirvOperand.Id(types.ConstantInt(member)));
            }
        } else if (pointers.TryGetValue(place.Root, out var pointer)) {
            baseId = pointer;
            storage = SpirvStorageClass.Function;
        } else {
            Report(BackendDiagnostics.NotImplemented, $"Reaching the variable '{place.Root.Name}'");
            return new(types.ConstantInt(0), place.Type, false);
        }

        var layout = storage == SpirvStorageClass.Uniform;
        var type = place.Root.Type;
        IrSwizzleAccess? trailing = null;

        foreach (var access in place.Chain) {
            switch (access) {
                case IrFieldAccess field:
                    indices.Add(SpirvOperand.Id(types.ConstantInt(field.Index)));
                    break;

                // A matrix indexes by column, which an access chain reaches exactly as it
                // reaches an array element or a vector lane.
                case IrIndexAccess index:
                    indices.Add(SpirvOperand.Id(Value(index.Index)));
                    break;

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
            return new(baseId, type, layout, trailing);
        }

        var chain = Emit(
            SpirvOp.AccessChain,
            types.Pointer(storage, types.Type(type, layout)),
            [SpirvOperand.Id(baseId), .. indices]
        );

        return new(chain, type, layout, trailing);
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
