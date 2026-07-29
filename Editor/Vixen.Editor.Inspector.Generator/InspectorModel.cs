// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.Inspector.Generator;

/// <summary>One inspected member, as the generator read it off a field or a property.</summary>
/// <remarks>
///     Strings and primitives only, because it is the incremental pipeline's cache key: two
///     compilations that read the same declaration have to produce equal models or the generator
///     re-runs on every keystroke. That is why nothing here is an <c>ISymbol</c>.
/// </remarks>
sealed record MemberModel(
    string Name,
    string DisplayName,
    string TypeName,
    bool IsField,
    bool CanWrite,
    string? Header,
    string? Tooltip,
    double? Minimum,
    double? Maximum,
    double Step,
    bool Logarithmic,
    bool? Hdr,
    bool ShowAlpha,
    string? AssetType,
    bool AllowNull,
    float? CurveMinimum,
    float? CurveMaximum,
    int Lines,
    string? Condition,
    bool ConditionNegated,
    bool IsReadOnly,
    int Order,
    ImmutableArray<string> Attributes
) {
    public bool Equals(MemberModel? other) =>
        other is not null
        && Name == other.Name
        && DisplayName == other.DisplayName
        && TypeName == other.TypeName
        && IsField == other.IsField
        && CanWrite == other.CanWrite
        && Header == other.Header
        && Tooltip == other.Tooltip
        && Minimum == other.Minimum
        && Maximum == other.Maximum
        && Step == other.Step
        && Logarithmic == other.Logarithmic
        && Hdr == other.Hdr
        && ShowAlpha == other.ShowAlpha
        && AssetType == other.AssetType
        && AllowNull == other.AllowNull
        && CurveMinimum == other.CurveMinimum
        && CurveMaximum == other.CurveMaximum
        && Lines == other.Lines
        && Condition == other.Condition
        && ConditionNegated == other.ConditionNegated
        && IsReadOnly == other.IsReadOnly
        && Order == other.Order
        && Attributes.SequenceEqual(other.Attributes);

    public override int GetHashCode() {
        var hash = Name.GetHashCode();

        hash = (hash * 31) + TypeName.GetHashCode();
        hash = (hash * 31) + Order;
        hash = (hash * 31) + Attributes.Length;

        return hash;
    }
}

/// <summary>One inspected type, as the generator read it off a class.</summary>
sealed record InspectedTypeModel(
    string Namespace,
    string TypeName,
    string QualifiedName,
    string SafeName,
    bool CanCreate,
    ImmutableArray<MemberModel> Members,
    ImmutableArray<DiagnosticModel> Problems
) {
    public bool Equals(InspectedTypeModel? other) =>
        other is not null
        && QualifiedName == other.QualifiedName
        && CanCreate == other.CanCreate
        && Members.SequenceEqual(other.Members)
        && Problems.SequenceEqual(other.Problems);

    public override int GetHashCode() {
        var hash = QualifiedName.GetHashCode();

        hash = (hash * 31) + Members.Length;

        return hash;
    }
}

/// <summary>A complaint, carried through the pipeline rather than reported where it was found.</summary>
/// <remarks>
///     Reporting from inside the transform would tie the diagnostic to a cache entry, and a cached
///     entry does not re-report. Carrying it to the output stage is the shape that survives
///     incremental reuse.
/// </remarks>
sealed record DiagnosticModel(
    string Id,
    string Message,
    string FilePath,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter
) {
    /// <summary>Rebuilds the location, so the squiggle lands on the declaration that is wrong.</summary>
    public Microsoft.CodeAnalysis.Location Where() =>
        FilePath.Length == 0
            ? Microsoft.CodeAnalysis.Location.None
            : Microsoft.CodeAnalysis.Location.Create(
                FilePath,
                new Microsoft.CodeAnalysis.Text.TextSpan(Start, Length),
                new Microsoft.CodeAnalysis.Text.LinePositionSpan(
                    new(StartLine, StartCharacter),
                    new(EndLine, EndCharacter)
                )
            );
}
