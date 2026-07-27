// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Core.Serialization.Generator;

/// <summary>How a single member is read and written.</summary>
/// <remarks>
///     Resolved once, while the symbol is in hand, so the emitter is a string builder and not a
///     second pass over the semantic model. It also makes the model comparable, which is what lets
///     the incremental pipeline skip regeneration when nothing relevant changed.
/// </remarks>
enum MemberShape {
    /// <summary>A primitive with a dedicated writer method.</summary>
    Primitive,

    /// <summary>An enum, written as its underlying primitive.</summary>
    Enum,

    /// <summary>An array of a primitive, written as one bulk copy.</summary>
    BlittableArray,

    /// <summary>An array of anything else.</summary>
    Array,

    /// <summary>A <c>List&lt;T&gt;</c>.</summary>
    List,

    /// <summary>A <c>Dictionary&lt;TKey,TValue&gt;</c>.</summary>
    Dictionary,

    /// <summary>A <c>Nullable&lt;T&gt;</c>.</summary>
    Nullable,

    /// <summary>Any other value type, through the registry.</summary>
    Value,

    /// <summary>Any other reference type, through the registry.</summary>
    Reference,

    /// <summary>A reference type that can have subtypes, written with its run-time name.</summary>
    Polymorphic
}

/// <summary>One serialised member.</summary>
/// <param name="IsSettable">
///     Whether deserialisation can put a value back. An <c>init</c>-only setter counts: see
///     <paramref name="IsInitOnly" />.
/// </param>
/// <param name="IsInitOnly">
///     Whether the setter is <c>init</c>-only, and so has to be reached through an
///     <c>[UnsafeAccessor]</c> rather than an assignment.
/// </param>
/// <param name="DeclaringType">
///     The type that declares the member, which is not the contract type when it was inherited. An
///     <c>[UnsafeAccessor]</c> looks its target up on the receiver's own type and does not walk the
///     base chain, so this is the receiver the accessor has to take.
/// </param>
readonly record struct MemberModel(
    string Name,
    string TypeName,
    MemberShape Shape,
    string Primitive,
    string ElementType,
    string SecondElementType,
    bool IsSettable,
    bool IsInitOnly,
    string DeclaringType,
    int Order,
    int Sequence
);

/// <summary>One <c>[DataContract]</c> type, reduced to what the emitter needs.</summary>
readonly record struct ContractModel(
    string Namespace,
    string TypeName,
    string Alias,
    System.Collections.Immutable.ImmutableArray<string> FormerAliases,
    string QualifiedName,
    string SafeName,
    bool IsValueType,
    bool IsSealed,
    int Version,
    bool HasParameterlessConstructor,
    bool HasMigrationHook,
    ImmutableArray<MemberModel> Members,
    ImmutableArray<string> ConstructorParameters,
    string? Error
);
