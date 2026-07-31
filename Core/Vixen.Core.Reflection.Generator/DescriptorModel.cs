// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Core.Reflection.Generator;

/// <summary>One described member, reduced to the strings the emitter concatenates.</summary>
readonly record struct DescribedMember(
    string Name,
    string TypeName,
    int Order,
    bool CanRead,
    bool CanWrite,
    bool IsInitOnly,
    string? Category,
    string? DisplayName,
    string? Tooltip,
    double? Minimum,
    double? Maximum,
    double Step,
    bool Logarithmic,
    bool IsEditorVisible,
    bool IsEditorReadOnly,

    /// <summary>
    ///     The fully-qualified name of what an asset member resolves to, or <see langword="null" />
    ///     for a member that names no asset. Emitted as a <c>typeof</c>, which is a compile-time
    ///     fact and therefore survives trimming — the whole reason this generator exists.
    /// </summary>
    string? AssetType,
    bool AllowsNull,
    /// <summary>
    ///     Rendered argument lists for <c>CollectionFactory.Register</c>, one per collection type
    ///     reachable from this member's declared type. Empty for everything that is not a collection.
    /// </summary>
    ImmutableArray<string> CollectionFactories
);

/// <summary>One described type.</summary>
readonly record struct DescriptorModel(
    string QualifiedName,
    string SafeName,
    string Alias,
    ImmutableArray<string> FormerAliases,
    string Traits,
    bool IsValueType,
    string? Category,
    bool CanCreate,
    ImmutableArray<DescribedMember> Members,
    string? Warning
);
