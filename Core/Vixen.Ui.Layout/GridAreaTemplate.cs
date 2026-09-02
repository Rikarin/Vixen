// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Vixen.Ui.Layout;

/// <summary>CSS Grid §7.3's <c>grid-template-areas</c>: a rectangle of named cells.</summary>
/// <remarks>
///     <para>
///         <b>An area is a name and four lines, and the four lines are what the rest of grid reads.</b>
///         §7.3 says that an area named <c>header</c> makes four implicit named lines —
///         <c>header-start</c> and <c>header-end</c> on both axes — so <c>grid-area: header</c> is
///         shorthand for naming those four, and everything downstream of
///         <see cref="TryGetArea" /> is ordinary line placement. That is why this type resolves to
///         line <i>indices</i> and never to a position: §8 still does the placing.
///     </para>
///     <para>
///         ⚠ <b>The parser is the part with an external oracle, and it is the part that is easy to
///         get plausibly wrong.</b> <c>web-platform-tests</c>'
///         <c>css/css-grid/grid-definition/grid-support-grid-template-areas-001.html</c> pins
///         thirty accepted values <i>with their canonical serialisation</i> and sixteen refused
///         ones, and several of the sixteen are values a hand-written parser accepts happily:
///         <c>"a b a"</c> and <c>"a b" "b a"</c> are non-rectangular areas, and a row count that
///         disagrees between two strings invalidates the whole declaration rather than the row.
///         <c>GridTemplateAreasTests</c> is that file, case for case.
///     </para>
///     <para>
///         ⚠ <b>A run of full stops is <i>one</i> null cell, not one per stop.</b> <c>"..a"</c> is
///         two columns and <c>"...header header...."</c> is four. That is CSS Syntax's tokenisation
///         showing through — the grammar is a sequence of <c>&lt;name&gt;</c> and
///         <c>&lt;null-cell-token&gt;</c> productions rather than a per-character grid — and reading
///         it per character gives a wider grid that lays out and is wrong, which no assertion about
///         one item's position would necessarily catch.
///     </para>
///     <para>
///         ⚠ <b>A name is a run of CSS <i>name code points</i>, which is wider than an identifier.</b>
///         The oracle accepts <c>10</c>, <c>1-st</c>, <c>-minus</c>, <c>©copy_right</c> and
///         <c>line¶</c> — all of which a <c>&lt;custom-ident&gt;</c> reading would refuse for
///         starting with a digit or a hyphen-digit — and refuses <c>10%</c>, <c>USD$</c> and
///         <c>,</c>. So the test is CSS Syntax §4.2's name code point exactly: a letter, a digit,
///         <c>_</c>, <c>-</c>, or anything outside ASCII.
///     </para>
///     <para>
///         <b>Immutable, and compared by its canonical text.</b> A template is written by a
///         stylesheet on every restyle, so the store has to be able to answer "is this the same
///         one?" without walking two grids; the serialisation is computed once in the constructor
///         and is what <see cref="Equals(object)" /> reads.
///     </para>
///     <para>
///         ⚠ <b>Named lines written into a track list — <c>[main-start] 1fr [main-end]</c> — are a
///         different feature and are still not implemented.</b> They have no oracle in either
///         conformance corpus, exactly as this had none, and the WPT files that cover them
///         (<c>grid-placement-using-named-grid-lines-00*</c>) are reftests whose geometry is not
///         stated. This type covers the half that <i>has</i> an oracle.
///     </para>
/// </remarks>
public sealed class GridAreaTemplate {
    /// <summary>A cell no area covers.</summary>
    internal const int NullCell = -1;

    readonly string[] names;
    readonly int[] cells;
    readonly int[] bounds;
    readonly string text;

    GridAreaTemplate(string[] names, int[] cells, int[] bounds, int rows, int columns) {
        this.names = names;
        this.cells = cells;
        this.bounds = bounds;
        Rows = rows;
        Columns = columns;
        text = Serialize();
    }

    /// <summary>How many rows of the explicit grid the template names.</summary>
    public int Rows { get; }

    /// <summary>How many columns of the explicit grid the template names.</summary>
    public int Columns { get; }

    /// <summary>The distinct area names, in the order they were first seen.</summary>
    public IReadOnlyList<string> Names => names;

    /// <summary>Reads a <c>grid-template-areas</c> value.</summary>
    /// <param name="value">The declaration's value, verbatim. <c>none</c> is understood and yields no template.</param>
    /// <param name="template">Receives the template, or <see langword="null" /> for <c>none</c>.</param>
    /// <param name="refusal">Receives why it could not be read, when this returns false.</param>
    /// <returns>Whether the whole value was understood.</returns>
    /// <remarks>
    ///     ⚠ <b><c>none</c> returns true with no template</b>, because the property's initial value
    ///     written out is a correct declaration and not a refusal. A refusal is reported and the
    ///     declaration dropped whole — CSS's rule for an invalid value — which for this property is
    ///     the difference between a grid with no named areas and a grid whose named areas are one
    ///     column narrower than the author wrote.
    /// </remarks>
    public static bool TryParse(
        string value,
        out GridAreaTemplate? template,
        [NotNullWhen(false)] out string? refusal
    ) {
        ArgumentNullException.ThrowIfNull(value);

        template = null;
        refusal = null;

        var span = value.AsSpan().Trim();

        if (span.IsEmpty || span.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        List<string> distinct = [];
        List<int> flat = [];
        var rows = 0;
        var columns = -1;
        var at = 0;

        while (at < span.Length) {
            if (char.IsWhiteSpace(span[at])) {
                at++;
                continue;
            }

            if (span[at] is not ('"' or '\'')) {
                // ⚠ The oracle's `"a b"-"c d"`, `"a b" - "c d"` and `"a b" . "c d"` are all refused,
                // and each of the three would otherwise read as two valid rows with a stray token
                // between them. Whatever is not a string and not whitespace kills the declaration.
                refusal = $"'{span[at]}' where a string was expected";
                return false;
            }

            var quote = span[at++];
            var end = span[at..].IndexOf(quote);

            if (end < 0) {
                refusal = "an unterminated string";
                return false;
            }

            var row = span.Slice(at, end);
            at += end + 1;

            var width = 0;

            if (!ReadRow(row, distinct, flat, ref width, out refusal)) {
                return false;
            }

            if (width == 0) {
                refusal = "a row with no cells in it";
                return false;
            }

            if (columns < 0) {
                columns = width;
            } else if (columns != width) {
                // §7.3: "all strings must define the same number of columns, or else the declaration
                // is invalid". The whole declaration, not the row — which is why this returns rather
                // than padding.
                refusal = $"a row of {width} cells in a template {columns} wide";
                return false;
            }

            rows++;
        }

        if (rows == 0 || columns <= 0) {
            refusal = "no rows";
            return false;
        }

        var bounds = new int[distinct.Count * 4];

        if (!MeasureAreas(distinct.Count, flat, rows, columns, bounds, out var open)) {
            // §7.3: "a named grid area must form a single filled-in rectangle". `"a b a"` and
            // `"a b" "b a"` are the oracle's two shapes of this and both lay out perfectly well if
            // it is not checked — as an area spanning something that is not in it.
            refusal = $"'{distinct[open]}' is not a single filled rectangle";
            return false;
        }

        template = new GridAreaTemplate([.. distinct], [.. flat], bounds, rows, columns);
        return true;
    }

    /// <summary>Whether a placement value is spelled like a reference to a named area.</summary>
    /// <param name="value">The whole value of a <c>grid-row-start</c> or its three siblings.</param>
    /// <returns>Whether it is a <c>&lt;custom-ident&gt;</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the bridge's test and not a claim that the area exists</b>, which is the
    ///         only way it can be asked: the container's template is not in hand when a declaration is
    ///         read, and an item may be styled before it is parented. It is here so that the rule
    ///         lives once — the alternative is <c>Vixen.Ui</c> deciding for itself what a name looks
    ///         like and drifting from the tokeniser this file is judged against.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a NARROWER test than <see cref="TryParse" />'s, and the asymmetry is CSS's
    ///         rather than a simplification.</b> Inside a string the grammar is a <c>&lt;name&gt;</c>
    ///         production, so the conformance oracle accepts an area called <c>10</c> or
    ///         <c>1-st</c>; a placement value is a <c>&lt;custom-ident&gt;</c> <i>token</i>, and no
    ///         ident token starts with a digit. So an area may carry a name nothing can ever refer to
    ///         — which is what Chrome does with it too — and, the other way round, <c>4px</c> is a
    ///         dimension rather than an ident and must not be mistaken for a name. A pure
    ///         name-code-point test accepts <c>4px</c> and turns a typo into a silently auto-placed
    ///         item, which is the failure the whole placement bridge exists to prevent.
    ///     </para>
    /// </remarks>
    public static bool IsAreaName(string value) {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0) {
            return false;
        }

        foreach (var code in value) {
            if (!IsNameCodePoint(code)) {
                return false;
            }
        }

        // CSS Syntax §4.3.9: an identifier may open with `-` only when a letter or a second `-`
        // follows, and never with a digit. `-1` is a line, `--x` is a custom property.
        if (value[0] is >= '0' and <= '9') {
            return false;
        }

        return value[0] != '-' || (value.Length > 1 && value[1] is not (>= '0' and <= '9'));
    }

    /// <summary>The area covering one cell, or <see langword="null" /> for a null cell.</summary>
    /// <param name="row">The row, zero-based.</param>
    /// <param name="column">The column, zero-based.</param>
    /// <returns>The area's name, or <see langword="null" />.</returns>
    public string? NameAt(int row, int column) {
        if (row < 0 || row >= Rows || column < 0 || column >= Columns) {
            return null;
        }

        var index = cells[(row * Columns) + column];
        return index == NullCell ? null : names[index];
    }

    /// <summary>The four lines an area's name stands for.</summary>
    /// <param name="name">The area's name, as <c>grid-area</c> writes it.</param>
    /// <param name="rowStart">Receives the row line the area starts at, zero-based.</param>
    /// <param name="rowEnd">Receives the row line it ends at, one past its last row.</param>
    /// <param name="columnStart">Receives the column line it starts at.</param>
    /// <param name="columnEnd">Receives the column line it ends at.</param>
    /// <returns>Whether there is an area with that name.</returns>
    /// <remarks>
    ///     ⚠ <b>Zero-based track indices, not CSS line numbers.</b> §7.3's implicit lines are
    ///     <c>name-start</c> and <c>name-end</c> and a caller could reasonably expect the 1-based
    ///     numbers a stylesheet writes; these are what <c>ResolveLine</c> has already turned those
    ///     into, so that placement has one representation rather than two.
    /// </remarks>
    public bool TryGetArea(string name, out int rowStart, out int rowEnd, out int columnStart, out int columnEnd) {
        ArgumentNullException.ThrowIfNull(name);

        rowStart = rowEnd = columnStart = columnEnd = 0;

        var index = Array.IndexOf(names, name);

        if (index < 0) {
            return false;
        }

        rowStart = bounds[index * 4];
        rowEnd = bounds[(index * 4) + 1] + 1;
        columnStart = bounds[(index * 4) + 2];
        columnEnd = bounds[(index * 4) + 3] + 1;

        return true;
    }

    /// <summary>The value as CSS serialises it: one quoted string per row, one space per cell.</summary>
    /// <returns>The canonical text.</returns>
    /// <remarks>
    ///     ⚠ <b>Canonical, so <c>".a..."</c> comes back as <c>". a ."</c>.</b> The oracle asserts
    ///     the computed value of thirty declarations and that is what it asserts, which makes this
    ///     an assertion about the tokenisation rather than about string formatting: a parser that
    ///     read a run of stops as one cell per stop round-trips its own mistake and only the
    ///     serialisation says so.
    /// </remarks>
    public override string ToString() => text;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is GridAreaTemplate other && string.Equals(text, other.text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => text.GetHashCode(StringComparison.Ordinal);

    /// <summary>Whether a code point may appear in an area's name, per CSS Syntax §4.2.</summary>
    static bool IsNameCodePoint(char value) =>
        value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'
        || value >= 0x80;

    /// <summary>Cuts one string into its cells, appending each cell's area index to the flat grid.</summary>
    static bool ReadRow(
        ReadOnlySpan<char> row,
        List<string> distinct,
        List<int> flat,
        ref int width,
        [NotNullWhen(false)] out string? refusal
    ) {
        refusal = null;
        var at = 0;

        while (at < row.Length) {
            if (char.IsWhiteSpace(row[at])) {
                at++;
                continue;
            }

            if (row[at] == '.') {
                // A run of stops is one null cell. See the type's remarks.
                while (at < row.Length && row[at] == '.') {
                    at++;
                }

                flat.Add(NullCell);
                width++;
                continue;
            }

            if (!IsNameCodePoint(row[at])) {
                refusal = $"'{row[at]}' is not a name code point";
                return false;
            }

            var start = at;

            while (at < row.Length && IsNameCodePoint(row[at])) {
                at++;
            }

            var name = row[start..at].ToString();
            var index = distinct.IndexOf(name);

            if (index < 0) {
                index = distinct.Count;
                distinct.Add(name);
            }

            flat.Add(index);
            width++;
        }

        return true;
    }

    /// <summary>Finds each area's bounding box and checks that the box holds nothing else.</summary>
    static bool MeasureAreas(int areas, List<int> flat, int rows, int columns, int[] bounds, out int open) {
        open = 0;

        for (var area = 0; area < areas; area++) {
            bounds[area * 4] = int.MaxValue;
            bounds[(area * 4) + 1] = int.MinValue;
            bounds[(area * 4) + 2] = int.MaxValue;
            bounds[(area * 4) + 3] = int.MinValue;
        }

        for (var row = 0; row < rows; row++) {
            for (var column = 0; column < columns; column++) {
                var area = flat[(row * columns) + column];

                if (area == NullCell) {
                    continue;
                }

                bounds[area * 4] = int.Min(bounds[area * 4], row);
                bounds[(area * 4) + 1] = int.Max(bounds[(area * 4) + 1], row);
                bounds[(area * 4) + 2] = int.Min(bounds[(area * 4) + 2], column);
                bounds[(area * 4) + 3] = int.Max(bounds[(area * 4) + 3], column);
            }
        }

        for (var area = 0; area < areas; area++) {
            for (var row = bounds[area * 4]; row <= bounds[(area * 4) + 1]; row++) {
                for (var column = bounds[(area * 4) + 2]; column <= bounds[(area * 4) + 3]; column++) {
                    if (flat[(row * columns) + column] == area) {
                        continue;
                    }

                    open = area;
                    return false;
                }
            }
        }

        return true;
    }

    string Serialize() {
        var builder = new StringBuilder();

        for (var row = 0; row < Rows; row++) {
            if (row > 0) {
                builder.Append(' ');
            }

            builder.Append('"');

            for (var column = 0; column < Columns; column++) {
                if (column > 0) {
                    builder.Append(' ');
                }

                var index = cells[(row * Columns) + column];
                builder.Append(index == NullCell ? "." : names[index]);
            }

            builder.Append('"');
        }

        return builder.ToString();
    }
}
