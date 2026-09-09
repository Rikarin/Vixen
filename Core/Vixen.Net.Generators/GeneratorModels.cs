// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Vixen.Net.Generators;

/// <summary>A diagnostic, reduced to values so it can live in a cached model.</summary>
/// <remarks>
///     <see cref="Diagnostic" /> holds a <see cref="Location" />, which holds a syntax tree, which
///     holds the whole compilation — putting one in a cached model roots everything the generator was
///     supposed to let go of. The location is kept as the span it was.
/// </remarks>
readonly record struct DiagnosticInfo(
    string Id,
    string Title,
    string Message,
    DiagnosticSeverity Severity,
    string FilePath,
    int Start,
    int Length,
    int Line,
    int Character
) {
    public Diagnostic ToDiagnostic() {
        var descriptor = new DiagnosticDescriptor(Id, Title, "{0}", "Vixen.Net", Severity, isEnabledByDefault: true);

        var location = FilePath.Length == 0
            ? Location.None
            : Location.Create(
                FilePath,
                new(Start, Length),
                new(new(Line, Character), new(Line, Character + Length))
            );

        return Diagnostic.Create(descriptor, location, Message);
    }

    public static DiagnosticInfo At(Location location, string id, string title, string message, DiagnosticSeverity severity) {
        var span = location.GetLineSpan();

        return new(
            id,
            title,
            message,
            severity,
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character
        );
    }
}

/// <summary>Where a symbol was, reduced to values for the same reason a diagnostic is.</summary>
/// <remarks>
///     A model that carries a <see cref="Location" /> roots the compilation it came from, so a model
///     that has to be able to report against a symbol <i>later</i> — after the models have been
///     grouped, when what is wrong is a relationship between two of them rather than anything about
///     either one — carries this instead.
/// </remarks>
readonly record struct SourceSpanInfo(string FilePath, int Start, int Length, int Line, int Character) {
    /// <summary>The first of a symbol's locations, or nowhere.</summary>
    /// <param name="locations">The symbol's locations.</param>
    /// <returns>The span.</returns>
    public static SourceSpanInfo Of(ImmutableArray<Location> locations) {
        if (locations.Length == 0) {
            return default;
        }

        var location = locations[0];
        var span = location.GetLineSpan();

        return new(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character
        );
    }

    /// <summary>Back to a location, for reporting.</summary>
    /// <returns>The location, or <see cref="Location.None" /> if there was not one.</returns>
    public Location ToLocation() =>
        FilePath.Length == 0
            ? Location.None
            : Location.Create(
                FilePath,
                new(Start, Length),
                new(new(Line, Character), new(Line, Character + Length))
            );
}

/// <summary>One replicated component, reduced to what emitting it needs.</summary>
/// <param name="ClassName">What to call the generated replicator.</param>
/// <param name="HintName">The file name it is emitted under.</param>
/// <param name="Source">The C# to emit, or empty if it could not be emitted.</param>
/// <param name="Diagnostics">What was wrong with the declaration.</param>
readonly record struct ReplicatorModel(
    string ClassName,
    string HintName,
    string Source,
    EquatableArray<DiagnosticInfo> Diagnostics
);
