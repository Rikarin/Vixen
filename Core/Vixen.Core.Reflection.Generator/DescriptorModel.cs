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
    string? Category,
    string? DisplayName,
    string? Tooltip,
    double? Minimum,
    double? Maximum,
    double Step,
    bool Logarithmic,
    bool IsEditorVisible,
    bool IsEditorReadOnly
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
