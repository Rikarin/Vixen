// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Which comparison <c>Analysis/Compare</c> makes. The numbers are <c>Compare.rvn</c>'s <c>operation</c>.</summary>
enum TextureCompareOperation {
    /// <summary>Above the value.</summary>
    Greater = 0,

    /// <summary>At the value or above it.</summary>
    GreaterOrEqual = 1,

    /// <summary>Below the value.</summary>
    Less = 2,

    /// <summary>At the value or below it.</summary>
    LessOrEqual = 3,

    /// <summary>Within the tolerance of the value.</summary>
    Equal = 4,

    /// <summary>Further than the tolerance from the value.</summary>
    NotEqual = 5
}

/// <summary>
///     Each colour channel compared with one value, to one where the comparison holds and nought where it does not:
///     a mask's Compare Mask (Patina #110).
/// </summary>
[Node(
    "Analysis/Compare",
    Preview = true,
    Summary = "Each colour channel to white where a comparison with a value holds."
)]
sealed partial class CompareNode : TextureNode {
    /// <summary>What to compare.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>What each channel is compared with.</summary>
    [Input(SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar Value = 0.5f;

    /// <summary>How far from the value still counts as equal to it. Read by Equal and Not Equal alone.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.5f)]
    public Scalar Tolerance = 0.05f;

    /// <summary>Which comparison. One of <c>TextureCompareOperation</c>'s names.</summary>
    [Setting(AcceptedFrom = typeof(TextureCompareOperation))]
    public string Operation = "Greater";

    /// <summary>One or nought per channel. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(
            emitter,
            TextureColourOperators.Compare,
            new("value", emitter.Number(nameof(Value))),
            new("tolerance", emitter.Number(nameof(Tolerance))),
            new(
                "operation",
                (float)(int)TextureSettings.Enum(emitter, nameof(Operation), TextureCompareOperation.Greater)
            )
        );
}
