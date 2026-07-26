// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax.Text;

/// <summary>
///     An immutable snapshot of source text with a precomputed line index, so a
///     character offset (or <see cref="TextSpan" />) can be mapped to a
///     <see cref="LinePosition" /> for diagnostics. Line breaks recognised are
///     <c>\r\n</c>, <c>\n</c>, and <c>\r</c>.
/// </summary>
public sealed class SourceText {
    readonly string text;

    /// <summary>Start offset of each line; always begins with 0.</summary>
    readonly int[] lineStarts;

    /// <summary>The text this one was edited from, when it was.</summary>
    readonly SourceText? predecessor;

    /// <summary>The edits that produced this text from <see cref="predecessor" />.</summary>
    readonly TextChangeRange[] changes;

    /// <summary>Number of characters in the text.</summary>
    public int Length => text.Length;

    /// <summary>Number of lines. A trailing newline yields a final empty line.</summary>
    public int LineCount => lineStarts.Length;

    /// <summary>The character at <paramref name="position" />.</summary>
    public char this[int position] => text[position];

    SourceText(string text, SourceText? predecessor = null, TextChangeRange[]? changes = null) {
        this.text = text;
        this.predecessor = predecessor;
        this.changes = changes ?? [];
        lineStarts = ComputeLineStarts(text);
    }

    /// <summary>Snapshots a string. A null string is treated as empty.</summary>
    public static SourceText From(string text) => new(text ?? string.Empty);

    /// <summary>
    ///     Applies edits and returns the result, remembering where it differs so a reparse can
    ///     ask.
    /// </summary>
    /// <remarks>
    ///     Spans are in <em>this</em> text's coordinates, so a caller describes a batch of edits
    ///     without adjusting for its own earlier ones. They must be sorted and must not overlap —
    ///     an overlapping pair has no single well-defined result, so it is rejected rather than
    ///     silently resolved.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A change lies outside the text.</exception>
    /// <exception cref="ArgumentException">Changes are unsorted or overlap.</exception>
    public SourceText WithChanges(IEnumerable<TextChange> changes) {
        ArgumentNullException.ThrowIfNull(changes);

        var ordered = changes.ToArray();
        if (ordered.Length == 0) {
            return this;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var ranges = new TextChangeRange[ordered.Length];
        var position = 0;

        for (var i = 0; i < ordered.Length; i++) {
            var change = ordered[i];

            if (change.Span.Start < 0 || change.Span.End > text.Length) {
                throw new ArgumentOutOfRangeException(
                    nameof(changes),
                    $"Change {change} lies outside a text of length {text.Length}."
                );
            }

            if (change.Span.Start < position) {
                throw new ArgumentException(
                    $"Changes must be sorted and must not overlap; {change} starts before {position}.",
                    nameof(changes)
                );
            }

            builder.Append(text, position, change.Span.Start - position);
            builder.Append(change.NewText);

            ranges[i] = new(change.Span, change.NewText?.Length ?? 0);
            position = change.Span.End;
        }

        builder.Append(text, position, text.Length - position);
        return new(builder.ToString(), this, ranges);
    }

    /// <inheritdoc cref="WithChanges(IEnumerable{TextChange})" />
    public SourceText WithChanges(params TextChange[] changes) => WithChanges((IEnumerable<TextChange>)changes);

    /// <summary>
    ///     Where this text differs from <paramref name="oldText" />, for an incremental reparse.
    /// </summary>
    /// <remarks>
    ///     Exact when <paramref name="oldText" /> is in this text's edit history — the usual case,
    ///     since an editor holds the previous snapshot. Otherwise the whole document is reported
    ///     as changed: conservative, always correct, and never silently wrong about a region a
    ///     reparser would then have trusted.
    /// </remarks>
    public IReadOnlyList<TextChangeRange> GetChangeRanges(SourceText oldText) {
        ArgumentNullException.ThrowIfNull(oldText);

        if (ReferenceEquals(this, oldText)) {
            return [];
        }

        if (ReferenceEquals(predecessor, oldText)) {
            return changes;
        }

        return [new TextChangeRange(new TextSpan(0, oldText.Length), Length)];
    }

    /// <summary>The substring covered by <paramref name="span" />.</summary>
    public string ToString(TextSpan span) => text.Substring(span.Start, span.Length);

    /// <summary>The whole text.</summary>
    public override string ToString() => text;

    /// <summary>
    ///     Maps a character offset to its zero-based (line, character). Offsets are
    ///     clamped to <c>[0, Length]</c> so end-of-file positions resolve cleanly.
    /// </summary>
    public LinePosition GetLinePosition(int position) {
        if (position < 0) {
            position = 0;
        } else if (position > text.Length) {
            position = text.Length;
        }

        var line = FindLineIndex(position);
        return new(line, position - lineStarts[line]);
    }

    /// <summary>Maps a span to zero-based start and end line positions.</summary>
    public LinePositionSpan GetLinePositionSpan(TextSpan span) =>
        new(GetLinePosition(span.Start), GetLinePosition(span.End));

    /// <summary>Start offset of the given zero-based line.</summary>
    public int GetLineStart(int line) {
        if (line < 0 || line >= lineStarts.Length) {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        return lineStarts[line];
    }

    /// <summary>
    ///     The text of the given zero-based line, without its line break. Used when
    ///     rendering a diagnostic under the source it points at.
    /// </summary>
    public string GetLineText(int line) {
        var start = GetLineStart(line);
        var end = line + 1 < lineStarts.Length ? lineStarts[line + 1] : text.Length;

        // Trim the break the next line's start sits after.
        while (end > start && text[end - 1] is '\n' or '\r') {
            end--;
        }

        return text[start..end];
    }

    int FindLineIndex(int position) {
        // Binary search for the greatest line start <= position.
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo < hi) {
            var mid = lo + (hi - lo + 1) / 2;
            if (lineStarts[mid] <= position) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        return lo;
    }

    static int[] ComputeLineStarts(string text) {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '\r') {
                // Treat \r\n as a single break.
                if (i + 1 < text.Length && text[i + 1] == '\n') {
                    i++;
                }

                starts.Add(i + 1);
            } else if (c == '\n') {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }
}
