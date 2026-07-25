// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Lowering;

/// <summary>Statement lowering: structured control flow, with the sugar removed.</summary>
public sealed partial class Lowerer {
    void LowerStatement(BoundStatement statement) {
        switch (statement) {
            case BoundBlockStatement block:
                // Scoping is already resolved, so a nested block adds nothing;
                // flatten it into the enclosing one.
                foreach (var nested in block.Statements) {
                    if (CurrentBlockIsTerminated) {
                        break;
                    }

                    LowerStatement(nested);
                }

                break;

            case BoundLocalDeclarationStatement declaration:
                LowerLocalDeclaration(declaration);
                break;

            case BoundExpressionStatement expression:
                LowerExpressionForEffect(expression.Expression);
                break;

            case BoundIfStatement { Condition.ConstantValue: bool known } conditional: {
                // The branch not taken is dropped rather than emitted and left for the
                // driver to strip. This is what makes a [Permutation] key pay for itself:
                // `if (UseSkinning)` against a false key emits no skinning code, no
                // uniforms it referenced, and no branch.
                //
                // The dead branch was still bound, so it is still type-checked — a
                // permutation you have switched off cannot rot. That is the main thing
                // this has over textual `#if`.
                if ((known ? conditional.Consequence : conditional.Alternative) is { } live) {
                    LowerStatement(live);
                }

                break;
            }

            case BoundIfStatement conditional: {
                var condition = LowerExpression(conditional.Condition);
                var then = EmitInto(() => LowerStatement(conditional.Consequence));
                var otherwise = conditional.Alternative is { } alternative
                    ? EmitInto(() => LowerStatement(alternative))
                    : null;

                Emit(new IrIfStatement(condition, then, otherwise));
                break;
            }

            // `while (false)` never runs. `while (true)` is left alone: it is a real
            // infinite loop, and only the condition is constant, not the body.
            case BoundWhileStatement { Condition.ConstantValue: false }:
                break;

            case BoundWhileStatement loop:
                EmitLoop(
                    () => LowerExpression(loop.Condition),
                    () => LowerStatement(loop.Body),
                    null,
                    true
                );
                break;

            case BoundRepeatStatement loop:
                EmitLoop(
                    () => LowerExpression(loop.Condition),
                    () => LowerStatement(loop.Body),
                    null,
                    false
                );
                break;

            case BoundForStatement loop:
                LowerFor(loop);
                break;

            case BoundReturnStatement @return:
                Emit(
                    new IrReturnStatement(
                        @return.Expression is { } value ? LowerExpression(value)
                            // A bare `return` in a constructor still hands back the value
                            // being built.
                        : IsConstructingSelf ? Load(SelfPlace!)
                        : null
                    )
                );
                break;

            case BoundBreakStatement:
                Emit(new IrBreakStatement());
                break;

            case BoundContinueStatement:
                Emit(new IrContinueStatement());
                break;

            case BoundNoOpStatement:
                break;

            case BoundLocalFunctionStatement:
                ReportUnsupported(statement, "A local function");
                break;

            case BoundSwitchStatement:
                ReportUnsupported(statement, "A switch statement");
                break;

            default:
                ReportUnsupported(statement, "This statement");
                break;
        }
    }

    void LowerLocalDeclaration(BoundLocalDeclarationStatement declaration) {
        var type = LowerType(declaration.Local.Type, declaration.Syntax);
        if (type.IsVoid) {
            return;
        }

        var variable = Function.AddLocal(declaration.Local.Name, type);
        variables[declaration.Local] = variable;

        if (declaration.Initializer is { } initializer) {
            Emit(new IrStoreInstruction(new(variable), LowerExpression(initializer)));
        }
    }

    /// <summary>
    ///     Emits a structured loop, running each part into its own block.
    ///     <paramref name="continueStep" /> is the work a <c>for</c> loop does before
    ///     re-testing.
    /// </summary>
    void EmitLoop(Func<IrValue> condition, Action body, Action? continueStep, bool testBeforeBody) {
        IrValue? conditionValue = null;
        var conditionBlock = EmitInto(() => conditionValue = condition());
        var bodyBlock = EmitInto(body);
        var continueBlock = continueStep is null ? null : EmitInto(continueStep);

        Emit(new IrLoopStatement(conditionBlock, conditionValue!, bodyBlock, continueBlock, testBeforeBody));
    }

    /// <summary>
    ///     Desugars <c>for (i in …)</c>. A range becomes a counted loop over its
    ///     bounds; an array becomes a counted loop over its indices, with the
    ///     element loaded into the iteration variable at the top of the body.
    /// </summary>
    void LowerFor(BoundForStatement loop) {
        var elementType = LowerType(loop.IterationVariable.Type, loop.Syntax);
        if (elementType.IsVoid) {
            return;
        }

        var iteration = Function.AddLocal(loop.IterationVariable.Name, elementType);
        variables[loop.IterationVariable] = iteration;

        if (loop.Sequence is BoundRangeExpression range) {
            LowerRangeFor(range, iteration, elementType, loop);
            return;
        }

        if (loop.Sequence.Type is ArrayTypeSymbol) {
            LowerArrayFor(iteration, loop);
            return;
        }

        ReportUnsupported(loop, $"Iterating a value of type '{loop.Sequence.Type.ToDisplayString()}'");
    }

    void LowerRangeFor(
        BoundRangeExpression range,
        IrVariable iteration,
        IrType elementType,
        BoundForStatement loop
    ) {
        // i = start
        var start = range.Left is { } left ? LowerExpression(left) : Constant(elementType, 0);
        Emit(new IrStoreInstruction(new(iteration), start));

        // The bound is evaluated once, not on every iteration.
        var limit = Function.AddLocal($"{iteration.Name}#limit", elementType);
        var end = range.Right is { } right ? LowerExpression(right) : Constant(elementType, 0);
        Emit(new IrStoreInstruction(new(limit), end));

        EmitLoop(
            () => {
                var current = Load(new(iteration));
                var bound = Load(new(limit));
                return Emit(
                    result => new IrBinaryInstruction(result, IrBinaryOp.LessThanOrEqual, current, bound),
                    IrScalarType.Bool
                );
            },
            () => LowerStatement(loop.Body),
            () => {
                var current = Load(new(iteration));
                var one = Constant(elementType, 1);
                var next = Emit(result => new IrBinaryInstruction(result, IrBinaryOp.Add, current, one), elementType);
                Emit(new IrStoreInstruction(new(iteration), next));
            },
            true
        );
    }

    void LowerArrayFor(IrVariable iteration, BoundForStatement loop) {
        var arrayType = LowerType(loop.Sequence.Type, loop.Syntax);
        if (arrayType.IsVoid) {
            return;
        }

        // Give the sequence storage so the loop can index it repeatedly.
        var source = TryGetPlace(loop.Sequence);
        if (source is null) {
            var temporary = Function.AddLocal($"{iteration.Name}#source", arrayType);
            Emit(new IrStoreInstruction(new(temporary), LowerExpression(loop.Sequence)));
            source = new(temporary);
        }

        var index = Function.AddLocal($"{iteration.Name}#index", IrScalarType.Int);
        Emit(new IrStoreInstruction(new(index), Constant(IrScalarType.Int, 0)));

        var sequence = source;

        EmitLoop(
            () => {
                var current = Load(new(index));
                var length = Emit(
                    result => new IrIntrinsicInstruction(result, IrIntrinsic.ArrayLength, [Load(sequence)]),
                    IrScalarType.Int
                );

                return Emit(
                    result => new IrBinaryInstruction(result, IrBinaryOp.LessThan, current, length),
                    IrScalarType.Bool
                );
            },
            () => {
                var current = Load(new(index));
                var element = Load(sequence.With(new IrIndexAccess(current)));
                Emit(new IrStoreInstruction(new(iteration), element));
                LowerStatement(loop.Body);
            },
            () => {
                var current = Load(new(index));
                var one = Constant(IrScalarType.Int, 1);
                var next = Emit(
                    result => new IrBinaryInstruction(result, IrBinaryOp.Add, current, one),
                    IrScalarType.Int
                );
                Emit(new IrStoreInstruction(new(index), next));
            },
            true
        );
    }
}
