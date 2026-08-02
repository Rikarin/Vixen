// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>How far off a goal ended up, in the units its kind is measured in.</summary>
/// <param name="Kind">Which kind of goal it came from.</param>
/// <param name="Magnitude">
///     How far off. Metres for a position, radians for an orientation or an aim, signed metres for a
///     distance, and for an additive goal the difference between the offset asked for and the offset
///     applied.
/// </param>
/// <param name="Vector">
///     The direction of the miss for a position goal, in model space. Zero for the other kinds.
/// </param>
/// <param name="Applied">How much of the goal was applied at all, in <c>[0, 1]</c>.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Public API, not a debug readout.</b> Three things depend on it: the editor draws it
///         while scrubbing, the governor that decides what a frame can afford uses it to rank what is
///         worth solving, and the variation harness is nothing but a way of collecting it across many
///         bodies. <b>A goal that cannot report why it failed is a goal an author cannot fix.</b>
///     </para>
///     <para>
///         Zero magnitude with <see cref="Applied" /> at zero is not success — it is a goal that
///         never ran. <see cref="Satisfied" /> says so.
///     </para>
/// </remarks>
public readonly record struct ConstraintResidual(GoalKind Kind, float Magnitude, Vector3 Vector, float Applied) {
    /// <summary>A goal that has not been solved yet.</summary>
    public static ConstraintResidual None => default;

    /// <summary>Whether it ran and landed.</summary>
    public bool Satisfied => Applied > 0f && MathF.Abs(Magnitude) <= 1e-4f;

    /// <summary>Whether it ran at all.</summary>
    public bool Ran => Applied > 0f;

    /// <summary>A position miss.</summary>
    /// <param name="miss">From where the effector ended up to where it was wanted.</param>
    /// <param name="applied">How much of the goal was applied.</param>
    /// <returns>The residual.</returns>
    public static ConstraintResidual Of(Vector3 miss, float applied) =>
        new(GoalKind.Position, miss.Length(), miss, applied);

    /// <summary>An angular miss.</summary>
    /// <param name="kind">Which kind of goal.</param>
    /// <param name="radians">How far off.</param>
    /// <param name="applied">How much of the goal was applied.</param>
    /// <returns>The residual.</returns>
    public static ConstraintResidual Of(GoalKind kind, float radians, float applied) =>
        new(kind, radians, Vector3.Zero, applied);

    /// <inheritdoc />
    public override string ToString() =>
        Kind is GoalKind.Position or GoalKind.Distance
            ? $"{Kind} off by {Magnitude:0.###} m at {Applied:0.##}"
            : $"{Kind} off by {MathUtil.RadiansToDegrees(Magnitude):0.#}° at {Applied:0.##}";
}
