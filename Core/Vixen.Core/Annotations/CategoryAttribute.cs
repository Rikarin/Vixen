// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Groups a member under a named, collapsible section of the inspector. Members without a
///     category stay in declaration order above the first categorised one.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class CategoryAttribute : Attribute {
    /// <summary>The section name. <c>/</c> nests: <c>"Lighting/Shadows"</c>.</summary>
    public string Name { get; }

    /// <summary>Whether the section starts collapsed.</summary>
    public bool Collapsed { get; set; }

    /// <summary>Groups the annotated element under <paramref name="name" />.</summary>
    /// <param name="name">The section name; <c>/</c> nests.</param>
    public CategoryAttribute(string name) => Name = name;
}
