// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Controls whether a member appears in the inspector, independently of whether it is
///     serialised. Use it to surface a non-public member, or to hide a public one that is
///     meaningful to code and noise to a designer.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class EditorVisibleAttribute : Attribute {
    /// <summary>Whether the annotated element is shown.</summary>
    public bool Visible { get; }

    /// <summary>Whether the inspector shows the value but refuses edits.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Label shown in place of the member's name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Shows the annotated element in the inspector.</summary>
    public EditorVisibleAttribute() => Visible = true;

    /// <summary>Shows or hides the annotated element in the inspector.</summary>
    /// <param name="visible"><see langword="false" /> to hide it.</param>
    public EditorVisibleAttribute(bool visible) => Visible = visible;
}
