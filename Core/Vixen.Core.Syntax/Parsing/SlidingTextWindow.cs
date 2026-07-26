// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax.Parsing;

/// <summary>
///     A character window over source text with cheap peek/advance, shared by every
///     hand-written lexer (Raven today; VXML and VCSS when their front ends land).
///     The name follows Roslyn's <c>SlidingTextWindow</c>; nothing actually slides —
///     source files are small enough to hold whole.
/// </summary>
sealed class SlidingTextWindow(string text) {
    /// <summary>Returned by <see cref="Peek" /> past the end of the text.</summary>
    public const char InvalidCharacter = char.MaxValue;

    readonly string text = text;

    public int Position { get; private set; }

    public bool AtEnd => Position >= text.Length;

    /// <summary>The character at the current position, or <see cref="InvalidCharacter" /> at the end.</summary>
    public char Current => Peek();

    public char Peek(int delta = 0) {
        var index = Position + delta;
        return index >= 0 && index < text.Length ? text[index] : InvalidCharacter;
    }

    public void Advance(int count = 1) => Position += count;

    /// <summary>Consumes <paramref name="expected" /> when it is the current character.</summary>
    public bool TryAdvance(char expected) {
        if (Current != expected) {
            return false;
        }

        Position++;
        return true;
    }

    /// <summary>The text between two absolute positions, end exclusive.</summary>
    public string GetText(int start, int end) => text[start..end];
}
