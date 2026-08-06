// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv.Solving;

/// <summary>What one solve did, for the report and for nothing else.</summary>
/// <param name="Iterations">How many iterations actually ran, which is the budget unless one of the guards fired.</param>
/// <param name="Preconditioner">What ran, which is not always what was asked for.</param>
/// <param name="PreconditionerFellBack">Whether an incomplete Cholesky broke down and became a Jacobi.</param>
/// <param name="StoppedEarly">Whether a guard against dividing by zero ended the loop before the budget did.</param>
/// <param name="Residual">The Euclidean norm of <c>b − Ax</c> when the loop ended.</param>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Residual" /> is an observation and never a decision, and the difference is
///         the reason docs/plan/42 § D5 and § B6 exist.</b> Nothing in this namespace reads it. A
///         solver that stopped when the residual came in under a threshold would be making a
///         floating-point comparison whose outcome can differ between two platforms that agree on
///         every bit of arithmetic leading up to it — one runs seventeen iterations, the other
///         eighteen, and the coordinates differ in the last place. That is a content hash that moves,
///         which § D12 gates against. So the budget is a count, the count is in
///         <c>UvSettings.SolverIterations</c>, and this number goes in the report where a human can
///         read it and decide the budget was too small.
///     </para>
///     <para>
///         <see cref="StoppedEarly" /> is not that comparison. It fires on an exact zero — a residual
///         that is already gone, or a direction the matrix maps to nothing — where the next line would
///         divide by it and produce a NaN that poisons everything after. The solution when it fires is
///         the same solution the remaining iterations would have left, because those iterations would
///         have added zero.
///     </para>
/// </remarks>
readonly record struct SolveReport(
    int Iterations,
    PreconditionerKind Preconditioner,
    bool PreconditionerFellBack,
    bool StoppedEarly,
    double Residual
);
