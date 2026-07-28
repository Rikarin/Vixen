// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Input;

/// <summary>A <c>.vxinput</c> document that is not in the dialect.</summary>
public sealed class VxInputParseException : Exception {
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="line">The one-based line it is wrong on.</param>
    /// <param name="column">The one-based column.</param>
    public VxInputParseException(string message, int line, int column) : base($"({line},{column}): {message}") {
        Line = line;
        Column = column;
        Detail = message;
    }

    /// <summary>Creates the exception.</summary>
    public VxInputParseException() : base("The document is not in the .vxinput dialect.") =>
        Detail = Message;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public VxInputParseException(string message) : base(message) => Detail = message;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public VxInputParseException(string message, Exception innerException) : base(message, innerException) =>
        Detail = message;

    /// <summary>The one-based line.</summary>
    public int Line { get; }

    /// <summary>The one-based column.</summary>
    public int Column { get; }

    /// <summary>What is wrong, without the position.</summary>
    public string Detail { get; }
}

/// <summary>Turns <c>.vxinput</c> text into a <see cref="VxInputNode" /> tree.</summary>
/// <remarks>
///     <para>
///         Indentation-driven recursive descent over a narrow subset of YAML: block mappings, block
///         sequences, plain and quoted scalars, and <c>#</c> comments. Anchors, aliases, tags,
///         multi-document streams, folded and literal blocks and flow collections are all
///         <em>refused by name</em> rather than mis-parsed, because a file that silently loads as
///         something other than what it says would rebind a player's controls to the wrong thing.
///     </para>
///     <para>
///         <b>Tabs are an error.</b> YAML forbids them as indentation and every editor that inserts
///         one produces a file that another parser reads differently; saying so with a position is
///         the only outcome that ends with the author fixing it.
///     </para>
/// </remarks>
public static class VxInputReader {
    /// <summary>Reads a document.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>Its root node, which for a well-formed action asset is a mapping.</returns>
    /// <exception cref="VxInputParseException">It is not in the dialect.</exception>
    public static VxInputNode Read(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var state = new ReadState(Split(text));
        var root = state.ReadNode(0);
        state.ExpectEnd();
        return root;
    }

    static List<Line> Split(string text) {
        var lines = new List<Line>();
        var start = 0;
        var number = 1;

        while (start <= text.Length) {
            var end = text.IndexOf('\n', start);

            if (end < 0) {
                end = text.Length;
            }

            var raw = text.Substring(start, end - start).TrimEnd('\r');
            lines.Add(new(number, raw));
            number++;
            start = end + 1;
        }

        return lines;
    }

    /// <summary>One source line, and what the scanner worked out about it.</summary>
    readonly struct Line(int number, string text) {
        public int Number { get; } = number;

        public string Text { get; } = text;
    }

    /// <summary>One read in progress: the lines, and where in them we are.</summary>
    sealed class ReadState(List<Line> lines) {
        int index;

        /// <summary>
        ///     The line whose apparent column is not where its text starts, and that column.
        /// </summary>
        /// <remarks>
        ///     A sequence item's value begins after its <c>- </c>, and its sibling keys line up with
        ///     <em>that</em> column rather than with the dash. So while the item's mapping is being
        ///     read the dash line reports the content column instead of its own. The index only ever
        ///     moves forward and the line it names is consumed immediately, so a stale override
        ///     cannot match a later line.
        /// </remarks>
        int overrideIndex = -1;

        int overrideColumn;

        /// <summary>Reads whatever node begins at the current line, at or beyond an indent.</summary>
        /// <param name="indent">The column the node's first character must sit at or beyond.</param>
        public VxInputNode ReadNode(int indent) {
            if (!TryPeek(out var line, out var column)) {
                throw Error("A value was expected and the document ended.", lines.Count, 1);
            }

            if (column < indent) {
                throw Error("This line is indented less than the value it belongs to.", line.Number, column + 1);
            }

            return IsSequenceItem(line.Text, column) ? ReadSequence(column) : ReadMappingFrom(line, column);
        }

        /// <summary>Fails if anything but blank lines and comments remain.</summary>
        public void ExpectEnd() {
            if (TryPeek(out var line, out var column)) {
                throw Error(
                    "The document has a second top-level value. A .vxinput holds exactly one.",
                    line.Number,
                    column + 1
                );
            }
        }

        VxInputSequence ReadSequence(int indent) {
            var items = new List<VxInputNode>();
            var first = lines[index].Number;

            while (TryPeek(out var line, out var column) && column == indent && IsSequenceItem(line.Text, column)) {
                // The item's own content starts after "- ", and that column is what its children are
                // measured against. Anything else makes `- name: x` followed by an indented `type: y`
                // either two entries or a parse error, depending on which column you chose.
                var contentColumn = SkipSpaces(line.Text, column + 1);

                if (contentColumn >= line.Text.Length) {
                    // "-" alone: the value is the block underneath it.
                    index++;
                    items.Add(ReadNode(indent + 1));
                    continue;
                }

                var rest = line.Text.Substring(contentColumn);

                if (TrySplitKey(rest, out _, out _)) {
                    overrideIndex = index;
                    overrideColumn = contentColumn;
                    items.Add(ReadMappingFrom(line, contentColumn));
                } else {
                    index++;
                    items.Add(new VxInputScalar(line.Number, Unquote(rest, line.Number, contentColumn + 1)));
                }
            }

            if (items.Count == 0) {
                throw Error("A sequence with no items.", first, indent + 1);
            }

            return new(first, items);
        }

        VxInputMapping ReadMappingFrom(Line first, int indent) {
            var entries = new List<KeyValuePair<string, VxInputNode>>();

            while (TryPeek(out var line, out var column) && column == indent) {
                if (IsSequenceItem(line.Text, column)) {
                    break;
                }

                var rest = line.Text.Substring(column);

                if (!TrySplitKey(rest, out var key, out var inline)) {
                    throw Error(
                        $"'{rest.Trim()}' is not 'key: value'. A mapping entry needs a colon and a space after it.",
                        line.Number,
                        column + 1
                    );
                }

                index++;

                if (inline.Length > 0) {
                    entries.Add(new(key, new VxInputScalar(line.Number, Unquote(inline, line.Number, column + 1))));
                    continue;
                }

                // No value on the key's own line, so it is the block below. A key with nothing under
                // it at all is an empty value rather than an error: a map with no actions yet is a
                // thing an editor writes.
                if (!TryPeek(out var next, out var nextColumn) || nextColumn <= column) {
                    entries.Add(new(key, new VxInputScalar(line.Number, string.Empty)));
                    continue;
                }

                // A sequence may sit at its parent key's own indent, which is how YAML is usually
                // written and how every example in the docs is laid out.
                var childIndent = IsSequenceItem(next.Text, nextColumn) && nextColumn == column
                    ? nextColumn
                    : column + 1;

                entries.Add(new(key, ReadNode(childIndent)));
            }

            if (entries.Count == 0) {
                throw Error("A mapping with no entries.", first.Number, indent + 1);
            }

            return new(first.Number, entries);
        }

        bool TryPeek(out Line line, out int column) {
            while (index < lines.Count) {
                var candidate = lines[index];
                var start = SkipSpaces(candidate.Text, 0);

                if (start >= candidate.Text.Length || candidate.Text[start] == '#') {
                    index++;
                    continue;
                }

                // A tab is not indentation in YAML, and an editor that inserted one produces a file
                // this reader and the asset database's would disagree about. Refused with a position
                // rather than guessed at.
                if (candidate.Text[start] == '\t') {
                    throw Error(
                        "This line is indented with a tab. .vxinput is indented with spaces, two to a level.",
                        candidate.Number,
                        start + 1
                    );
                }

                line = candidate;
                column = index == overrideIndex ? overrideColumn : start;
                return true;
            }

            line = default;
            column = 0;
            return false;
        }

        static int SkipSpaces(string text, int from) {
            var at = from;

            while (at < text.Length && text[at] == ' ') {
                at++;
            }

            return at;
        }

        static bool IsSequenceItem(string text, int column) =>
            column < text.Length
            && text[column] == '-'
            && (column + 1 == text.Length || text[column + 1] == ' ');

        /// <summary>Splits <c>key: value</c>, leaving the value empty when the line is just a key.</summary>
        /// <remarks>
        ///     The colon has to be followed by a space or be at the end of the line, or
        ///     <c>&lt;Keyboard&gt;/w</c> in <c>path: &lt;Keyboard&gt;/w</c> would be split on its own
        ///     colon the moment someone writes a Windows path or a URL in a display name.
        /// </remarks>
        static bool TrySplitKey(string text, out string key, out string value) {
            var quote = '\0';

            for (var at = 0; at < text.Length; at++) {
                var character = text[at];

                if (quote != '\0') {
                    if (character == quote) {
                        quote = '\0';
                    }

                    continue;
                }

                if (character is '\'' or '"') {
                    quote = character;
                    continue;
                }

                if (character != ':') {
                    continue;
                }

                if (at + 1 < text.Length && text[at + 1] != ' ') {
                    continue;
                }

                key = Unquoted(text.Substring(0, at).Trim());
                value = at + 1 < text.Length ? text.Substring(at + 1).Trim() : string.Empty;
                return key.Length > 0;
            }

            key = string.Empty;
            value = string.Empty;
            return false;
        }

        static string Unquoted(string text) =>
            text.Length >= 2 && (text[0] == '\'' || text[0] == '"') && text[text.Length - 1] == text[0]
                ? text.Substring(1, text.Length - 2)
                : text;

        /// <summary>Turns the text after a <c>:</c> or a <c>-</c> into the scalar it denotes.</summary>
        static string Unquote(string text, int line, int column) {
            if (text.Length == 0) {
                return string.Empty;
            }

            var quote = text[0];

            if (quote is not ('\'' or '"')) {
                // A plain scalar ends at " #", which is where a trailing comment begins. A '#'
                // without a space before it is part of the value — `Keyboard&Mouse#2` is a name.
                var comment = text.IndexOf(" #", StringComparison.Ordinal);
                var plain = (comment < 0 ? text : text.Substring(0, comment)).Trim();

                if (plain.Length > 0 && plain[0] is '&' or '*' or '!' or '|' or '>' or '[' or '{') {
                    throw new VxInputParseException(
                        $"'{plain[0]}' begins a YAML feature .vxinput does not use — anchors, aliases, tags, "
                        + "block scalars and flow collections are all out of the dialect. Write the value plainly "
                        + "or in quotes.",
                        line,
                        column
                    );
                }

                return plain;
            }

            var builder = new StringBuilder(text.Length);

            for (var at = 1; at < text.Length; at++) {
                var character = text[at];

                if (character == quote) {
                    // '' inside a single-quoted scalar is one quote, which is YAML's own escape.
                    if (quote == '\'' && at + 1 < text.Length && text[at + 1] == '\'') {
                        builder.Append('\'');
                        at++;
                        continue;
                    }

                    return builder.ToString();
                }

                if (quote == '"' && character == '\\' && at + 1 < text.Length) {
                    at++;

                    builder.Append(
                        text[at] switch {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '0' => '\0',
                            var other => other
                        }
                    );

                    continue;
                }

                builder.Append(character);
            }

            throw new VxInputParseException("This quoted value has no closing quote.", line, column);
        }

        static VxInputParseException Error(string message, int line, int column) => new(message, line, column);
    }
}
