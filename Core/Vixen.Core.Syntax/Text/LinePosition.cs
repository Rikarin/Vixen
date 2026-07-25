namespace Vixen.Core.Syntax.Text;

/// <summary>
///     A zero-based (line, character) coordinate into source text. <see cref="Character" />
///     counts UTF-16 code units from the start of the line.
/// </summary>
/// <param name="Line">Zero-based line number.</param>
/// <param name="Character">Zero-based UTF-16 code-unit offset within the line.</param>
public readonly record struct LinePosition(int Line, int Character) : IComparable<LinePosition> {
    /// <summary>Orders by <see cref="Line" />, then by <see cref="Character" />.</summary>
    public static bool operator <(LinePosition left, LinePosition right) => left.CompareTo(right) < 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator <=(LinePosition left, LinePosition right) => left.CompareTo(right) <= 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >(LinePosition left, LinePosition right) => left.CompareTo(right) > 0;

    /// <inheritdoc cref="op_LessThan" />
    public static bool operator >=(LinePosition left, LinePosition right) => left.CompareTo(right) >= 0;

    /// <summary>Orders by <see cref="Line" />, breaking ties with <see cref="Character" />.</summary>
    public int CompareTo(LinePosition other) {
        var diff = Line - other.Line;
        return diff != 0 ? diff : Character - other.Character;
    }

    /// <summary>Formats as one-based <c>line,column</c> for human-readable output.</summary>
    public override string ToString() => $"{Line + 1},{Character + 1}";
}

/// <summary>A half-open span between two <see cref="LinePosition" />s.</summary>
/// <param name="Start">First position covered.</param>
/// <param name="End">One past the last position covered.</param>
public readonly record struct LinePositionSpan(LinePosition Start, LinePosition End) {
    /// <summary>Renders both endpoints in the one-based form used by compiler output.</summary>
    public override string ToString() => $"({Start})-({End})";
}
