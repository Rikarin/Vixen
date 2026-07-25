// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax.Text;

/// <summary>
///     One edit: replace the characters in <paramref name="Span" /> with
///     <paramref name="NewText" />.
/// </summary>
/// <remarks>
///     An insertion is an empty span with text; a deletion is a span with empty text. Spans are
///     always against the <em>old</em> text, so a batch of edits can be described without the
///     caller having to adjust for its own earlier ones.
/// </remarks>
/// <param name="Span">What to replace, in the old text's coordinates.</param>
/// <param name="NewText">What to put there. Empty to delete.</param>
public readonly record struct TextChange(TextSpan Span, string NewText) {
    /// <summary>An insertion at <paramref name="position" />.</summary>
    public static TextChange Insert(int position, string text) => new(new(position, 0), text);

    /// <summary>A deletion of <paramref name="span" />.</summary>
    public static TextChange Delete(TextSpan span) => new(span, string.Empty);

    /// <summary>Characters removed, minus characters added.</summary>
    public int Delta => (NewText?.Length ?? 0) - Span.Length;

    /// <inheritdoc />
    public override string ToString() =>
        NewText is { Length: > 0 } ? $"{Span} -> \"{NewText}\"" : $"{Span} -> (deleted)";
}

/// <summary>
///     Where two texts differ: <paramref name="Span" /> in the old text became
///     <paramref name="NewLength" /> characters in the new one.
/// </summary>
/// <remarks>
///     What an incremental reparse consumes. Distinct from <see cref="TextChange" /> in carrying
///     only the new <em>length</em>, not the new text: a reparser needs to know which region moved
///     and by how much, and reads the characters from the new text itself.
/// </remarks>
/// <param name="Span">The affected range in the old text.</param>
/// <param name="NewLength">How many characters replaced it.</param>
public readonly record struct TextChangeRange(TextSpan Span, int NewLength) {
    /// <summary>Characters added, minus characters removed.</summary>
    public int Delta => NewLength - Span.Length;

    /// <inheritdoc />
    public override string ToString() => $"{Span} -> {NewLength}";
}
