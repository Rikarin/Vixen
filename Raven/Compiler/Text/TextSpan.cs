namespace Vixen.Raven.Text;

/// <summary>
/// An immutable half-open interval <c>[Start, End)</c> into source text,
/// measured in characters. The primary currency for spans and diagnostics.
/// </summary>
public readonly record struct TextSpan(int Start, int Length) : IComparable<TextSpan> {
    public int End => Start + Length;
    public bool IsEmpty => Length == 0;

    public bool Contains(int position) => unchecked((uint)(position - Start) < (uint)Length);

    public bool Contains(TextSpan span) => span.Start >= Start && span.End <= End;

    public bool OverlapsWith(TextSpan other) =>
        Math.Max(Start, other.Start) < Math.Min(End, other.End);

    public static TextSpan FromBounds(int start, int end) {
        if (start < 0) {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end < start) {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        return new TextSpan(start, end - start);
    }

    public int CompareTo(TextSpan other) {
        var diff = Start - other.Start;
        return diff != 0 ? diff : Length - other.Length;
    }

    public override string ToString() => $"[{Start}..{End})";
}
