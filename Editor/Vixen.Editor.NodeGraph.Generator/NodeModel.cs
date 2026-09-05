// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.NodeGraph.Generator;

/// <summary>One port, as the generator read it off a field.</summary>
/// <remarks>
///     A value type holding only strings and primitives, because it is the incremental pipeline's
///     cache key: two compilations that read the same declaration must produce equal models, or the
///     generator re-runs on every keystroke. That is why nothing here is an <c>ISymbol</c>.
/// </remarks>
sealed record PortModel(
    string Field,
    string Name,
    bool IsInput,
    string Kind,
    ImmutableArray<float> Default,
    string Type,
    string Summary
) {
    public bool Equals(PortModel? other) =>
        other is not null
        && Field == other.Field
        && Name == other.Name
        && IsInput == other.IsInput
        && Kind == other.Kind
        && Type == other.Type
        && Summary == other.Summary
        && Default.SequenceEqual(other.Default);

    public override int GetHashCode() {
        var hash = Field.GetHashCode();

        hash = (hash * 31) + Name.GetHashCode();
        hash = (hash * 31) + Kind.GetHashCode();
        hash = (hash * 31) + Default.Length;

        return hash;
    }
}

/// <summary>One setting, as the generator read it off a <see langword="string" /> field.</summary>
/// <remarks>
///     ⚠ <b>A setting has no direction and its field is always a string, and it does now have a
///     kind</b> — <c>SettingKind</c>, which says how that string is <em>read</em> rather than how it
///     is stored. See <c>SettingAttribute</c>; the storage stayed text so that a saved graph and a
///     node type that renamed a member still understand each other.
/// </remarks>
sealed record SettingModel(
    string Field,
    string Name,
    string Default,
    string Summary,
    string Kind,
    float Minimum,
    float Maximum,
    string Group
);

/// <summary>One node type, as the generator read it off a class.</summary>
sealed record NodeModel(
    string Namespace,
    string TypeName,
    string Accessibility,
    string Path,
    string Summary,
    bool Preview,
    ImmutableArray<PortModel> Ports,
    ImmutableArray<SettingModel> Settings,
    ImmutableArray<DiagnosticModel> Problems
) {
    /// <summary>Its fully qualified name, for the registration list.</summary>
    public string FullName => Namespace.Length == 0 ? TypeName : Namespace + "." + TypeName;

    public bool Equals(NodeModel? other) =>
        other is not null
        && Namespace == other.Namespace
        && TypeName == other.TypeName
        && Accessibility == other.Accessibility
        && Path == other.Path
        && Summary == other.Summary
        && Preview == other.Preview
        && Ports.SequenceEqual(other.Ports)
        && Settings.SequenceEqual(other.Settings)
        && Problems.SequenceEqual(other.Problems);

    public override int GetHashCode() {
        var hash = FullName.GetHashCode();

        hash = (hash * 31) + Path.GetHashCode();
        hash = (hash * 31) + Ports.Length;

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
