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

    /// <summary>Moves forward, never past the end.</summary>
    /// <param name="count">How far.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Clamped, and the clamp is what makes <see cref="GetText" /> total.</b> Every
    ///         multi-character skip in a lexer is a place where the end of the file can arrive in the
    ///         middle of a construct: a backslash escape as the last character asks for two and there
    ///         is one, and the window then sits at <c>Length + 1</c>. Nothing notices, because
    ///         <see cref="AtEnd" /> is <c>&gt;=</c> and every scan stops as it should — and then the
    ///         token that scan produces is cut with a range past the end of the string, which throws
    ///         out of a parser whose whole contract is that it does not.
    ///     </para>
    ///     <para>
    ///         Fixed here rather than at the call sites, which is the same argument
    ///         <c>PacketReader</c> makes for taking bytes in one place: there are a dozen skips
    ///         across two lexers, the property belongs to the window rather than to any of them, and
    ///         a version of this written at each call site is a version that is missing from the
    ///         thirteenth. Found by fuzzing VXML — a <c>@code</c> block whose last character was a
    ///         backslash inside a character literal.
    ///     </para>
    ///     <para>
    ///         A position past the end was never meaningful: <see cref="Peek" /> already answers
    ///         <see cref="InvalidCharacter" /> there and <see cref="Rewind" /> already refuses to
    ///         move forward. Clamping takes nothing away.
    ///     </para>
    /// </remarks>
    public void Advance(int count = 1) => Position = Math.Min(Position + count, text.Length);

    /// <summary>Moves back to a position already passed, undoing a scan that turned out not to be one.</summary>
    /// <remarks>
    ///     A lexer that can only go forward has to decide what a construct is from its first
    ///     character. This is for the cases where it cannot — where the scan itself is the test —
    ///     and the characters have to go back to being whatever they were before it ran.
    /// </remarks>
    public void Rewind(int position) {
        if (position > Position) {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                $"A rewind cannot move forward: the window is already at {Position}."
            );
        }

        Position = position;
    }

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
