namespace Vixen.Raven.Text;

/// <summary>
/// A zero-based (line, character) coordinate into source text. <see cref="Character"/>
/// counts UTF-16 code units from the start of the line.
/// </summary>
public readonly record struct LinePosition(int Line, int Character) : IComparable<LinePosition> {
    public int CompareTo(LinePosition other) {
        var diff = Line - other.Line;
        return diff != 0 ? diff : Character - other.Character;
    }

    /// <summary>Formats as one-based <c>line,column</c> for human-readable output.</summary>
    public override string ToString() => $"{Line + 1},{Character + 1}";
}

/// <summary>A half-open span between two <see cref="LinePosition"/>s.</summary>
public readonly record struct LinePositionSpan(LinePosition Start, LinePosition End) {
    public override string ToString() => $"({Start})-({End})";
}
