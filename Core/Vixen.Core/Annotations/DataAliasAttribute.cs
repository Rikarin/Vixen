// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Records a former name of a type or member so data written before a rename still loads.
///     Applied repeatedly to carry a chain of renames.
/// </summary>
/// <remarks>
///     This is the half of the versioning story that costs nothing to add and is impossible to add
///     later: once the old name is gone from the source, nobody remembers what it was.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Field |
    AttributeTargets.Property,
    AllowMultiple = true)]
public sealed class DataAliasAttribute : Attribute {
    /// <summary>The former name, as it appears in already-written data.</summary>
    public string Name { get; }

    /// <summary>Records <paramref name="name" /> as a former name of the annotated element.</summary>
    /// <param name="name">The former name, as it appears in already-written data.</param>
    public DataAliasAttribute(string name) => Name = name;
}
