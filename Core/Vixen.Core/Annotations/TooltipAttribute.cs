// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Explanatory text shown when the pointer rests on the member's inspector row.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class TooltipAttribute : Attribute {
    /// <summary>The text to show.</summary>
    public string Text { get; }

    /// <summary>Attaches <paramref name="text" /> to the annotated element.</summary>
    /// <param name="text">The text to show.</param>
    public TooltipAttribute(string text) => Text = text;
}
