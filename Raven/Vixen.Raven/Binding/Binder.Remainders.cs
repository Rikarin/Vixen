// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Binding;

/// <summary><c>RVN2146</c> for a fold written as statements, which ends in <c>%=</c>.</summary>
internal abstract partial class Binder {
    /// <summary>What the statements before a <c>%=</c> wrote into a signed local.</summary>
    enum Dividend {
        /// <summary>A signed remainder, which is negative wherever its dividend was.</summary>
        Remainder,

        /// <summary>A number written to be below nought: negated, or a remainder with something added.</summary>
        Negative
    }

    /// <summary>
    ///     Reports a <c>%=</c> on a signed local that the statements just before it wrote to be negative.
    /// </summary>
    /// <param name="statements">One block's statements, in order.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The fold as statements</b> — <c>var r = v % n</c>, <c>r += n</c>, <c>r %= n</c> — divides the
    ///         number the binary rule looks for, but the dividend of a <c>%=</c> is the target, a plain name, so
    ///         <see cref="WrittenNegative" /> saw nothing in it (Patina #267).
    ///     </para>
    ///     <para>
    ///         Straight-line only, as the binary rule reads only what is written: what a local holds is followed down
    ///         one block, and forgotten at any statement that is not a declaration or an assignment to a local, since
    ///         a branch or a loop could have written it otherwise. Forgetting is silence, never a warning.
    ///     </para>
    /// </remarks>
    void CheckRemainderFolds(IReadOnlyList<BoundStatement> statements) {
        Dictionary<LocalSymbol, Dividend> held = [];

        foreach (var statement in statements) {
            switch (statement) {
                case BoundLocalDeclarationStatement { Initializer: { } initializer } declaration:
                    Hold(held, declaration.Local, Written(initializer));
                    break;

                case BoundExpressionStatement {
                    Expression: BoundAssignmentExpression { Target: BoundLocalExpression target } assignment
                }:
                    Hold(held, target.Local, After(held, target.Local, assignment));
                    break;

                default:
                    held.Clear();
                    break;
            }
        }
    }

    /// <summary>What a local holds once an assignment to it has run, reporting a fold it completes.</summary>
    Dividend? After(Dictionary<LocalSymbol, Dividend> held, LocalSymbol local, BoundAssignmentExpression assignment) {
        var before = held.TryGetValue(local, out var was) ? was : (Dividend?)null;

        switch (assignment.OperatorKind) {
            case null:
                return Written(assignment.Value);

            case BinaryOperatorKind.Add or BinaryOperatorKind.Subtract:
                return before is not null || IsSignedRemainder(assignment.Value) ? Dividend.Negative : null;

            case BinaryOperatorKind.Modulo when IsSigned(local.Type):
                if (before == Dividend.Negative) {
                    Report(
                        SemanticDiagnostics.SignedRemainderOfNegative,
                        assignment.Syntax,
                        local.Type.ToDisplayString()
                    );
                }

                return Dividend.Remainder;

            default:
                return null;
        }
    }

    /// <summary>What a value written whole into a local makes it hold, if anything this rule follows.</summary>
    static Dividend? Written(BoundExpression value) =>
        WrittenNegative(value) ? Dividend.Negative
        : IsSignedRemainder(value) ? Dividend.Remainder
        : null;

    static void Hold(Dictionary<LocalSymbol, Dividend> held, LocalSymbol local, Dividend? dividend) {
        if (dividend is { } known) {
            held[local] = known;
        } else {
            held.Remove(local);
        }
    }
}
