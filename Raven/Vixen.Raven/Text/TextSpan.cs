namespace Vixen.Raven.Text;

/// <summary>
///     An immutable half-open interval <c>[Start, End)</c> into source text,
///     measured in characters. The primary currency for spans and diagnostics.
/// </summary>
public readonly record struct TextSpan(int Start, int Length) : IComparable<TextSpan> {
    public static bool operator <(TextSpan left, TextSpan right) => left.CompareTo(right) < 0;
    public static bool operator <=(TextSpan left, TextSpan right) => left.CompareTo(right) <= 0;
    public static bool operator >(TextSpan left, TextSpan right) => left.CompareTo(right) > 0;
    public static bool operator >=(TextSpan left, TextSpan right) => left.CompareTo(right) >= 0;

    public int End => Start + Length;
    public bool IsEmpty => Length == 0;

    public bool Contains(int position) => unchecked((uint)(position - Start) < (uint)Length);

    public bool Contains(TextSpan span) => span.Start >= Start && span.End <= End;

    public bool OverlapsWith(TextSpan other) => Math.Max(Start, other.Start) < Math.Min(End, other.End);

    public static TextSpan FromBounds(int start, int end) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);

        return new(start, end - start);
    }

    public int CompareTo(TextSpan other) {
        var diff = Start - other.Start;
        return diff != 0 ? diff : Length - other.Length;
    }

    public override string ToString() => $"[{Start}..{End})";
}
