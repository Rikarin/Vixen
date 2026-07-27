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

            case BoundDiscardStatement discard:
                // Noted against the function rather than checked here: whether this is a stage that
                // may discard depends on which entry points reach the function, which is not known
                // until every body is lowered. First site per function, because one function that
                // discards is one mistake however many times it does it.
                discards.TryAdd(Function, discard.Syntax);
                Emit(new IrDiscardStatement());
                break;

            case BoundNoOpStatement:
                break;

            case BoundSwitchStatement @switch:
                LowerSwitch(@switch);
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

    // --- switch ------------------------------------------------------------

    /// <summary>
    ///     Desugars a <c>switch</c> into an if/else chain over equality tests.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         No new IR construct, and therefore no new work in either backend: both already emit
    ///         structured <c>if</c>. SPIR-V has <c>OpSwitch</c> and GLSL has <c>switch</c>, so a
    ///         dedicated node could produce a jump table later — but it would have to be built twice
    ///         and neither target gains anything on the sizes of switch a shader writes.
    ///     </para>
    ///     <para>
    ///         The governing expression is evaluated <em>once</em>, into a local. Testing it per
    ///         section would re-run whatever produced it, and it may be a call.
    ///     </para>
    ///     <para>
    ///         Sections do not fall through, so the chain is exact rather than an approximation, and
    ///         a trailing <c>break</c> is dropped as redundant. <c>default</c> becomes the final
    ///         <c>else</c> wherever it was written, which is the one place order stops mattering.
    ///     </para>
    /// </remarks>
    void LowerSwitch(BoundSwitchStatement statement) {
        var governingType = LowerType(statement.GoverningExpression.Type, statement.Syntax);
        var governing = LowerExpression(statement.GoverningExpression);

        // Held in a local so each test reads it rather than recomputing it.
        var subject = Function.AddLocal("switch", governingType);
        Emit(new IrStoreInstruction(new(subject), governing));

        var cases = statement.Sections.Where(s => !s.IsDefault).ToArray();
        var fallback = statement.Sections.FirstOrDefault(s => s.IsDefault);

        // Built back to front, so each section's `else` is the chain already assembled.
        IrBlock? otherwise = fallback is null ? null : EmitInto(() => LowerSection(fallback));

        for (var i = cases.Length - 1; i >= 0; i--) {
            var section = cases[i];
            var tail = otherwise;

            otherwise = EmitInto(
                () => {
                    // A section with no labels can never be selected; emitting the test would
                    // need a constant false, and dropping it says the same thing.
                    if (section.Labels.Count == 0) {
                        if (tail is not null) {
                            Emit(tail);
                        }

                        return;
                    }

                    IrValue? test = null;
                    foreach (var label in section.Labels) {
                        var equal = Emit(
                            result => new IrBinaryInstruction(
                                result,
                                IrBinaryOp.Equal,
                                Load(new(subject)),
                                LowerExpression(label)
                            ),
                            IrScalarType.Bool
                        );

                        test = test is null
                            ? equal
                            : Emit(
                                result => new IrBinaryInstruction(result, IrBinaryOp.LogicalOr, test, equal),
                                IrScalarType.Bool
                            );
                    }

                    Emit(new IrIfStatement(test!, EmitInto(() => LowerSection(section)), tail));
                }
            );
        }

        if (otherwise is not null) {
            Emit(otherwise);
        }
    }

    /// <summary>
    ///     Lowers a section's body, dropping a trailing <c>break</c>.
    /// </summary>
    /// <remarks>
    ///     That <c>break</c> means "leave the switch", and in an if/else chain leaving is what
    ///     reaching the end of the block already does. Only a <em>trailing</em> one is dropped: a
    ///     <c>break</c> inside a loop in the section still belongs to the loop.
    /// </remarks>
    void LowerSection(BoundSwitchSection section) {
        var statements = section.Statements;
        var count = statements.Count;

        if (count > 0 && statements[^1] is BoundBreakStatement) {
            count--;
        }

        for (var i = 0; i < count; i++) {
            LowerStatement(statements[i]);
        }
    }
}
