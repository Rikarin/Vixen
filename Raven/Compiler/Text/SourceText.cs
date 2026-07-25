namespace Vixen.Raven.Text;

/// <summary>
/// An immutable snapshot of source text with a precomputed line index, so a
/// character offset (or <see cref="TextSpan"/>) can be mapped to a
/// <see cref="LinePosition"/> for diagnostics. Line breaks recognised are
/// <c>\r\n</c>, <c>\n</c>, and <c>\r</c>.
/// </summary>
public sealed class SourceText {
    readonly string text;

    /// <summary>Start offset of each line; always begins with 0.</summary>
    readonly int[] lineStarts;

    SourceText(string text) {
        this.text = text;
        lineStarts = ComputeLineStarts(text);
    }

    public static SourceText From(string text) => new(text ?? string.Empty);

    public int Length => text.Length;

    public char this[int position] => text[position];

    /// <summary>Number of lines. A trailing newline yields a final empty line.</summary>
    public int LineCount => lineStarts.Length;

    public string ToString(TextSpan span) => text.Substring(span.Start, span.Length);

    public override string ToString() => text;

    /// <summary>
    /// Maps a character offset to its zero-based (line, character). Offsets are
    /// clamped to <c>[0, Length]</c> so end-of-file positions resolve cleanly.
    /// </summary>
    public LinePosition GetLinePosition(int position) {
        if (position < 0) {
            position = 0;
        } else if (position > text.Length) {
            position = text.Length;
        }

        var line = FindLineIndex(position);
        return new LinePosition(line, position - lineStarts[line]);
    }

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
    /// The text of the given zero-based line, without its line break. Used when
    /// rendering a diagnostic under the source it points at.
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
