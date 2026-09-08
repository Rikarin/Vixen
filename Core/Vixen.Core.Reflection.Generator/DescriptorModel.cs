// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Vixen.Core.Reflection.Generator;

/// <summary>Where a refusal happened, reduced to values so it can live in a cached model.</summary>
/// <remarks>
///     ⚠ <b>A Roslyn <see cref="Location" /> cannot go in the model, and that is why this exists.</b>
///     It holds a syntax tree, which holds the whole compilation, so a model carrying one roots
///     everything the incremental pipeline was supposed to let go of — and, worse here, compares by
///     reference, so every keystroke would produce a model unequal to the last one and rerun the
///     emission. The span is kept as the numbers it was, which is
///     <c>Vixen.Net.Generators.DiagnosticInfo</c>'s arrangement.
/// </remarks>
/// <param name="FilePath">The file the type was declared in.</param>
/// <param name="Start">The declaration's start, in characters from the beginning of the file.</param>
/// <param name="Length">Its length in characters.</param>
/// <param name="Line">Its zero-based start line.</param>
/// <param name="Character">Its zero-based start column.</param>
readonly record struct LocationInfo(string FilePath, int Start, int Length, int Line, int Character) {
    /// <summary>Rebuilds the Roslyn location, or <see cref="Location.None" /> if there was none.</summary>
    /// <returns>The location to report at.</returns>
    public Location ToLocation() =>
        FilePath.Length == 0
            ? Location.None
            : Location.Create(FilePath, new(Start, Length), new(new(Line, Character), new(Line, Character + Length)));

    /// <summary>Reduces a Roslyn location to the numbers that survive a model comparison.</summary>
    /// <param name="location">Where the declaration is.</param>
    /// <returns>The reduced location.</returns>
    public static LocationInfo At(Location location) {
        var span = location.GetLineSpan();

        return new(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character
        );
    }
}

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
    ///     Whether the member is part of the type's <i>data</i>: false when it carries
    ///     <c>[DataMemberIgnore]</c>. Distinct from <see cref="IsEditorVisible" />, which is about
    ///     the panel — see <c>MemberDescriptor.IsSerialized</c> for why both exist.
    /// </summary>
    bool IsSerialized,
    /// <summary>
    ///     Rendered argument lists for <c>CollectionFactory.Register</c>, one per collection type
    ///     reachable from this member's declared type. Empty for everything that is not a collection.
    /// </summary>
    ImmutableArray<string> CollectionFactories,
    /// <summary>
    ///     Whether the member may hold <see langword="null" />, as the <i>source</i> declared it. This
    ///     is the one fact about a member that does not survive into its <c>Type</c> — every reference
    ///     type is nullable to the CLR — so a binder has no other way to ask it.
    /// </summary>
    bool IsNullable
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
    string? Warning,

    /// <summary>
    ///     Where to report <see cref="Warning" />, or <see langword="null" /> when there is none.
    ///     ⚠ Without it the refusal lands on the project rather than on the declaration, and with
    ///     <c>TreatWarningsAsErrors</c> that is a build error with no file and no line on it.
    /// </summary>
    LocationInfo? WarningLocation
);
