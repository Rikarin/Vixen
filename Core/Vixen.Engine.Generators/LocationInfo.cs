// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;

namespace Vixen.Engine.Generators;

/// <summary>Somewhere in the source, reduced to values so it can live in a cached model.</summary>
/// <remarks>
///     ⚠ <b>A Roslyn <see cref="Location" /> cannot go in a generator's model.</b> It holds a syntax
///     tree, which holds the whole compilation, so a model carrying one roots everything the
///     incremental pipeline was supposed to let go of — and compares by reference, so every keystroke
///     would produce a model unequal to the last one and rerun the emission. The span is kept as the
///     numbers it was, which is <c>Vixen.Net.Generators.DiagnosticInfo</c>'s arrangement.
/// </remarks>
/// <param name="FilePath">The file the span is in, or empty when there is none.</param>
/// <param name="Start">Its start, in characters from the beginning of the file.</param>
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
    /// <param name="location">Where in the source.</param>
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

/// <summary>
///     One call that hands a world, a chunk, a command buffer or a context to something the
///     inference did not read.
/// </summary>
/// <param name="Method">The method's name, for the message.</param>
/// <param name="Where">The call site, so the warning lands on the line that causes it.</param>
readonly record struct Handoff(string Method, LocationInfo Where);
