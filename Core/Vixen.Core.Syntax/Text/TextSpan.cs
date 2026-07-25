namespace Vixen.Core.Syntax.Text;

/// <summary>
///     An immutable half-open interval <c>[Start, End)</c> into source text,
///     measured in characters. The primary currency for spans and diagnostics.
/// </summary>
/// <param name="Start">Zero-based character offset of the first character.</param>
/// <param name="Length">Number of characters covered.</param>
public readonly record struct TextSpan(int Start, int Length) : IComparable<TextSpan> {
    /// <summary>Orders by <see cref="Start" />, then by <see cref="Length" />.</summary>
    public static bool operator <(TextSpan left, TextSpan right) => left.CompareTo(right) < 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator <=(TextSpan left, TextSpan right) => left.CompareTo(right) <= 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >(TextSpan left, TextSpan right) => left.CompareTo(right) > 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >=(TextSpan left, TextSpan right) => left.CompareTo(right) >= 0;

    /// <summary>One past the last character. Exclusive, so an empty span has <c>End == Start</c>.</summary>
    public int End => Start + Length;

    /// <summary>Whether the span covers no characters.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Whether <paramref name="position" /> falls inside the span. <see cref="End" /> is excluded.</summary>
    public bool Contains(int position) => unchecked((uint)(position - Start) < (uint)Length);

    /// <summary>Whether <paramref name="span" /> lies entirely within this one.</summary>
    public bool Contains(TextSpan span) => span.Start >= Start && span.End <= End;

    /// <summary>Whether the two spans share at least one character. Touching spans do not overlap.</summary>
    public bool OverlapsWith(TextSpan other) => Math.Max(Start, other.Start) < Math.Min(End, other.End);

    /// <summary>Builds a span from inclusive <paramref name="start" /> to exclusive <paramref name="end" />.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="start" /> is negative, or <paramref name="end" /> precedes it.
    /// </exception>
    public static TextSpan FromBounds(int start, int end) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);

        return new(start, end - start);
    }

    /// <summary>Orders by <see cref="Start" />, breaking ties with <see cref="Length" />.</summary>
    public int CompareTo(TextSpan other) {
        var diff = Start - other.Start;
        return diff != 0 ? diff : Length - other.Length;
    }

    /// <summary>Renders as <c>[start..end)</c>, matching the half-open convention.</summary>
    public override string ToString() => $"[{Start}..{End})";
}
