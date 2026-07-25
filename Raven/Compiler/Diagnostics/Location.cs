using Vixen.Raven.Text;

namespace Vixen.Raven.Diagnostics;

/// <summary>
/// A location in source: a file path plus a <see cref="TextSpan"/>. When backed by
/// a <see cref="SourceText"/> it can resolve the span to a
/// <see cref="LinePositionSpan"/> (line/column) for display.
/// </summary>
public sealed class Location {
    /// <summary>A location with no source position (e.g. compiler-synthesized).</summary>
    public static readonly Location None = new(string.Empty, default, null);

    readonly SourceText? text;

    Location(string filePath, TextSpan span, SourceText? text) {
        FilePath = filePath;
        SourceSpan = span;
        this.text = text;
    }

    public string FilePath { get; }

    public TextSpan SourceSpan { get; }

    /// <summary>
    /// The text this location points into, or <c>null</c> when it is unbacked.
    /// Rendering a diagnostic with the offending line needs it.
    /// </summary>
    public SourceText? SourceText => text;

    /// <summary>True when this is <see cref="None"/> (no meaningful source span).</summary>
    public bool IsNone => text is null && SourceSpan == default && FilePath.Length == 0;

    public static Location Create(string filePath, TextSpan span, SourceText text) =>
        new(filePath ?? string.Empty, span, text);

    /// <summary>The span mapped to zero-based line/character, or a zero span when unbacked.</summary>
    public LinePositionSpan GetLineSpan() =>
        text is null ? default : text.GetLinePositionSpan(SourceSpan);

    public override string ToString() {
        if (IsNone) {
            return "<none>";
        }

        var lineSpan = GetLineSpan();
        var file = FilePath.Length == 0 ? "<unknown>" : FilePath;
        return $"{file}({lineSpan.Start})";
    }
}
