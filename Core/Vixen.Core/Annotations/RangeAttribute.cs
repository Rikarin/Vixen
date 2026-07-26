// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Constrains a numeric member to an interval and asks the inspector for a slider rather than
///     a text field. The bounds are an editing affordance, not a runtime invariant: nothing clamps
///     a value assigned from code.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class RangeAttribute : Attribute {
    /// <summary>Lowest value the editor will produce.</summary>
    public double Minimum { get; }

    /// <summary>Highest value the editor will produce.</summary>
    public double Maximum { get; }

    /// <summary>
    ///     Increment the editor snaps to. <c>0</c> — the default — leaves the control continuous.
    /// </summary>
    public double Step { get; set; }

    /// <summary>
    ///     Whether the slider maps its travel logarithmically. Right for intensities and radii,
    ///     where the interesting values crowd the low end.
    /// </summary>
    public bool Logarithmic { get; set; }

    /// <summary>Constrains the annotated member to <c>[minimum, maximum]</c>.</summary>
    /// <param name="minimum">Lowest value the editor will produce.</param>
    /// <param name="maximum">Highest value the editor will produce.</param>
    public RangeAttribute(double minimum, double maximum) {
        Minimum = minimum;
        Maximum = maximum;
    }
}
