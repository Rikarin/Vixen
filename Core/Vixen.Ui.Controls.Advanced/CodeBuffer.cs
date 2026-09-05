// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui.Text;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>A place in a text buffer, by line and by character within it.</summary>
/// <remarks>
///     ⚠ <b>Two numbers rather than one offset.</b> An offset is smaller and is what a compiler
///     wants; an editor asks "which line is this on" for every row it draws, every diagnostic it
///     places and every caret movement, and answering that from an offset needs a line-start index
///     maintained on every edit. <c>Vixen.Core.Syntax</c>'s <c>SourceText</c> keeps one because it is
///     handed whole files; this is edited a keystroke at a time.
/// </remarks>
/// <param name="Line">Which line, from zero.</param>
/// <param name="Column">How many characters into it, from zero.</param>
public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition> {
    /// <inheritdoc />
    public int CompareTo(TextPosition other) =>
        Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    /// <summary>Whether one place is before another.</summary>
    public static bool operator <(TextPosition left, TextPosition right) => left.CompareTo(right) < 0;

    /// <summary>Whether one place is after another.</summary>
    public static bool operator >(TextPosition left, TextPosition right) => left.CompareTo(right) > 0;

    /// <summary>Whether one place is before another or the same.</summary>
    public static bool operator <=(TextPosition left, TextPosition right) => left.CompareTo(right) <= 0;

    /// <summary>Whether one place is after another or the same.</summary>
    public static bool operator >=(TextPosition left, TextPosition right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() => $"{Line + 1}:{Column + 1}";
}

/// <summary>The text a <see cref="CodeEditor" /> is editing: lines, and the four edits.</summary>
/// <remarks>
///     <para>
///         <b>A list of lines, not a rope and not a gap buffer.</b> The operations an editor actually
///         performs are "give me line 4 200 to draw it" and "insert a character on the line the caret
///         is on", and a list of strings answers the first in constant time and the second by
///         rebuilding one line. A rope wins on inserting into the middle of a ten-megabyte file,
///         which is not what a shader source or a component script is.
///     </para>
///     <para>
///         ⚠ <b>No undo stack.</b> Undo belongs to the application, because it has to be interleaved
///         with everything else the editor does — a rename that touched three files, a refactor, a
///         move — and an undo stack inside the text control can only ever undo typing. What is here
///         is <see cref="Changed" />, which is what such a stack subscribes to. ⚠ <b>And one
///         does</b>: the editor's <c>CodeDocument</c> is on this event and turns each run of typing
///         into a <c>TextEditCommand</c> on the document's <c>CommandStack</c>, alongside the command
///         that renamed the asset — which is the arrangement this paragraph argues for, built.
///     </para>
/// </remarks>
public sealed class CodeBuffer {
    readonly List<string> lines = [string.Empty];

    // Reused across moves rather than allocated per keystroke. `WordBreaker.Collect` clears it, and
    // the document is single-threaded by contract, so one buffer per editor is enough.
    readonly List<int> breaks = [];

    /// <summary>Creates an empty buffer.</summary>
    public CodeBuffer() {
    }

    /// <summary>Creates a buffer holding some text.</summary>
    /// <param name="text">The text.</param>
    public CodeBuffer(string text) => Text = text;

    /// <summary>The lines, without their terminators.</summary>
    public IReadOnlyList<string> Lines => lines;

    /// <summary>How many there are. Never zero — an empty buffer is one empty line.</summary>
    public int LineCount => lines.Count;

    /// <summary>One line.</summary>
    /// <param name="line">Its index.</param>
    public string this[int line] => lines[line];

    /// <summary>The whole thing, joined with newlines.</summary>
    /// <remarks>
    ///     ⚠ <b>Reading normalises the line endings and writing accepts any of them.</b> A file that
    ///     arrives with CRLF and is saved with LF is a whole-file diff, so an editor that silently
    ///     converted would make every Windows checkout unreviewable — which is why what is preserved
    ///     is the application's business: it read the file, it knows what was in it, and it can join
    ///     <see cref="Lines" /> with whatever it found.
    /// </remarks>
    public string Text {
        get => string.Join('\n', lines);
        set {
            lines.Clear();
            lines.AddRange((value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

            if (lines.Count == 0) {
                lines.Add(string.Empty);
            }

            Changed?.Invoke(this);
        }
    }

    /// <summary>Raised after any edit.</summary>
    public event Action<CodeBuffer>? Changed;

    /// <summary>The end of the buffer.</summary>
    public TextPosition End => new(lines.Count - 1, lines[^1].Length);

    /// <summary>Brings a position inside the text.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The nearest place that exists.</returns>
    public TextPosition Clamp(TextPosition position) {
        var line = Math.Clamp(position.Line, 0, lines.Count - 1);
        return new TextPosition(line, Math.Clamp(position.Column, 0, lines[line].Length));
    }

    /// <summary>The text between two places.</summary>
    /// <param name="from">One end.</param>
    /// <param name="to">The other. The two may be given in either order.</param>
    /// <returns>The text, with newlines between lines.</returns>
    public string Slice(TextPosition from, TextPosition to) {
        Order(ref from, ref to);

        if (from.Line == to.Line) {
            return lines[from.Line][from.Column..to.Column];
        }

        var builder = new StringBuilder();
        builder.Append(lines[from.Line].AsSpan(from.Column));

        for (var i = from.Line + 1; i < to.Line; i++) {
            builder.Append('\n').Append(lines[i]);
        }

        return builder.Append('\n').Append(lines[to.Line].AsSpan(0, to.Column)).ToString();
    }

    /// <summary>Puts text in.</summary>
    /// <param name="at">Where.</param>
    /// <param name="text">What. Newlines in it split the line.</param>
    /// <returns>Where the inserted text ends.</returns>
    public TextPosition Insert(TextPosition at, string text) {
        ArgumentNullException.ThrowIfNull(text);

        at = Clamp(at);

        var line = lines[at.Line];
        var before = line[..at.Column];
        var after = line[at.Column..];

        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (parts.Length == 1) {
            lines[at.Line] = before + parts[0] + after;
            Changed?.Invoke(this);

            return new TextPosition(at.Line, at.Column + parts[0].Length);
        }

        lines[at.Line] = before + parts[0];

        for (var i = 1; i < parts.Length; i++) {
            lines.Insert(at.Line + i, parts[i]);
        }

        var last = at.Line + parts.Length - 1;
        var column = parts[^1].Length;

        lines[last] += after;
        Changed?.Invoke(this);

        return new TextPosition(last, column);
    }

    /// <summary>Takes text out.</summary>
    /// <param name="from">One end.</param>
    /// <param name="to">The other. Either order.</param>
    /// <returns>Where the hole is, which is where a caret goes afterwards.</returns>
    public TextPosition Delete(TextPosition from, TextPosition to) {
        Order(ref from, ref to);

        from = Clamp(from);
        to = Clamp(to);

        if (from == to) {
            return from;
        }

        lines[from.Line] = lines[from.Line][..from.Column] + lines[to.Line][to.Column..];

        if (to.Line > from.Line) {
            lines.RemoveRange(from.Line + 1, to.Line - from.Line);
        }

        Changed?.Invoke(this);
        return from;
    }

    /// <summary>The place one character before a position, stepping onto the previous line.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The place before it, or the same one if it is the start.</returns>
    public TextPosition Back(TextPosition position) {
        position = Clamp(position);

        if (position.Column > 0) {
            return position with { Column = position.Column - 1 };
        }

        return position.Line == 0 ? position : new TextPosition(position.Line - 1, lines[position.Line - 1].Length);
    }

    /// <summary>The place one character after a position.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The place after it, or the same one if it is the end.</returns>
    public TextPosition Forward(TextPosition position) {
        position = Clamp(position);

        if (position.Column < lines[position.Line].Length) {
            return position with { Column = position.Column + 1 };
        }

        return position.Line == lines.Count - 1 ? position : new TextPosition(position.Line + 1, 0);
    }

    /// <summary>The start of the word a position is in or just after.</summary>
    /// <param name="position">The position.</param>
    /// <returns>Where Ctrl-Left goes.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Runs of one class at a time, and whitespace is skipped before the run rather than
    ///         with it.</b> That is what makes Ctrl-Left from the end of <c>foo.bar(</c> stop at the
    ///         bracket, then at <c>bar</c>, then at the dot — which is how every editor behaves and is
    ///         not what "characters up to the next space" gives. It is also not what UAX #29 gives,
    ///         which is why <c>TextField</c>'s rule cannot simply be used here: replacing this with
    ///         <c>WordBreaker</c> outright would regress the one case a code editor exists for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>But a run of the <i>word</i> class is then subdivided by <c>WordBreaker</c>, and
    ///         without that step this navigates Japanese as one word.</b> <see cref="IsWord" /> is
    ///         <c>char.IsLetterOrDigit</c>, which is true of every Han, Kana and Thai codepoint —
    ///         languages that put no space between words — so the whole of <c>編集するためのボタン</c>
    ///         was a single class run and Ctrl-Left jumped all of it. UAX #29 is what knows where
    ///         those break, and it costs nothing on code: <c>fooBar</c>, <c>foo_bar</c> and
    ///         <c>abc123</c> are each one word to it, because <c>_</c> is <c>ExtendNumLet</c> and
    ///         digits join letters. So the class rule decides what a run <i>is</i> and the breaker
    ///         only ever divides one further.
    ///     </para>
    /// </remarks>
    public TextPosition WordStart(TextPosition position) {
        position = Clamp(position);

        if (position.Column == 0) {
            return Back(position);
        }

        var line = lines[position.Line];
        var index = position.Column;

        while (index > 0 && char.IsWhiteSpace(line[index - 1])) {
            index--;
        }

        if (index == 0) {
            return position with { Column = 0 };
        }

        var word = IsWord(line[index - 1]);
        var end = index;

        while (index > 0 && !char.IsWhiteSpace(line[index - 1]) && IsWord(line[index - 1]) == word) {
            index--;
        }

        return position with { Column = word ? LastBreakBefore(line, index, end) : index };
    }

    /// <summary>The end of the word a position is in or just before.</summary>
    /// <param name="position">The position.</param>
    /// <returns>Where Ctrl-Right goes.</returns>
    public TextPosition WordEnd(TextPosition position) {
        position = Clamp(position);

        var line = lines[position.Line];

        if (position.Column >= line.Length) {
            return Forward(position);
        }

        var index = position.Column;

        while (index < line.Length && char.IsWhiteSpace(line[index])) {
            index++;
        }

        if (index == line.Length) {
            return position with { Column = index };
        }

        var word = IsWord(line[index]);
        var start = index;

        while (index < line.Length && !char.IsWhiteSpace(line[index]) && IsWord(line[index]) == word) {
            index++;
        }

        return position with { Column = word ? FirstBreakAfter(line, start, index) : index };
    }

    /// <summary>How many characters of whitespace a line starts with.</summary>
    /// <param name="line">Its index.</param>
    /// <returns>The count, or the line's length if it is all whitespace.</returns>
    public int IndentOf(int line) {
        var text = lines[Math.Clamp(line, 0, lines.Count - 1)];
        var index = 0;

        while (index < text.Length && char.IsWhiteSpace(text[index])) {
            index++;
        }

        return index;
    }

    /// <summary>Whether a line has nothing but whitespace on it.</summary>
    /// <param name="line">Its index.</param>
    /// <returns>Whether it is blank.</returns>
    public bool IsBlank(int line) => IndentOf(line) == lines[Math.Clamp(line, 0, lines.Count - 1)].Length;

    /// <summary>The word ending at a position, which is what a completion filters on.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The prefix, which may be empty.</returns>
    public string WordBefore(TextPosition position) {
        position = Clamp(position);

        var line = lines[position.Line];
        var index = position.Column;

        while (index > 0 && IsWord(line[index - 1])) {
            index--;
        }

        return line[index..position.Column];
    }

    static bool IsWord(char value) => char.IsLetterOrDigit(value) || value == '_';

    /// <summary>The last UAX #29 word boundary strictly inside a run, walking back.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">Where the class run begins.</param>
    /// <param name="end">Where the caret is, which is where the run ends for this move.</param>
    /// <returns>The boundary to stop at, which is <paramref name="start" /> when there is none.</returns>
    /// <remarks>
    ///     ⚠ <b>The breaker is run over the run alone rather than over the line, and the two agree
    ///     here because the run's own edges are boundaries.</b> A class run ends at whitespace or at
    ///     a character of the other class, and UAX #29 breaks at both — so the context the substring
    ///     loses is context that could not have moved a boundary. Running it over the line instead
    ///     would mean mapping offsets back and re-deciding which of its boundaries fall inside the
    ///     run, for the same answer.
    /// </remarks>
    int LastBreakBefore(string line, int start, int end) {
        WordBreaker.Collect(line.AsSpan(start, end - start), breaks);

        for (var i = breaks.Count - 1; i >= 0; i--) {
            if (start + breaks[i] < end) {
                return start + breaks[i];
            }
        }

        return start;
    }

    /// <summary>The first UAX #29 word boundary strictly inside a run, walking on.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">Where the caret is, which is where the run begins for this move.</param>
    /// <param name="end">Where the class run ends.</param>
    /// <returns>The boundary to stop at, which is <paramref name="end" /> when there is none.</returns>
    int FirstBreakAfter(string line, int start, int end) {
        WordBreaker.Collect(line.AsSpan(start, end - start), breaks);

        foreach (var boundary in breaks) {
            if (start + boundary > start) {
                return start + boundary;
            }
        }

        return end;
    }

    static void Order(ref TextPosition from, ref TextPosition to) {
        if (from > to) {
            (from, to) = (to, from);
        }
    }
}
