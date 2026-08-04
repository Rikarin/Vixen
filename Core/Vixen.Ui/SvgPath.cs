// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>What was wrong with a piece of path data.</summary>
/// <remarks>
///     ⚠ <b>An exception rather than a silently truncated path, and only for what cannot be read at
///     all.</b> A command letter nobody has defined, a number that is not a number, a path that opens
///     with a curve and has no pen position — those are typos in a string somebody wrote, and a parser
///     that answered them with the half it managed would put a shape on screen that is wrong in a way
///     nobody can see the cause of. What is <i>not</i> an error is a path that ends early or a stray
///     comma; see <see cref="SvgPath.Parse" />.
/// </remarks>
public sealed class SvgPathException : FormatException {
    /// <summary>Describes a fault.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="position">Where in the data it is, as a character offset.</param>
    public SvgPathException(string message, int position) : base(message) => Position = position;

    /// <summary>Where in the data the fault is, as a character offset.</summary>
    public int Position { get; }
}

/// <summary>Reads SVG path data — the <c>d</c> attribute — into a <see cref="PathBuilder" />.</summary>
/// <remarks>
///     <para>
///         <b>What every icon set on earth is distributed as.</b> Material, Lucide, Feather, Fluent,
///         Bootstrap and Phosphor all ship a 24-square grid and a string per glyph, and until this
///         existed the only way into this engine was to transcribe one into
///         <c>PathBuilder.LineTo</c> calls by hand — which is why the editor's icon set is two dozen
///         shapes drawn from line segments and why <c>[EditorIcon]</c> was documented as a mechanism
///         that could not be built. It is a hundred and fifty lines. See <c>EditorIcon</c>.
///     </para>
///     <para>
///         ⚠ <b>Every command in the grammar, including the two that are not curves or lines.</b>
///         <c>H</c>/<c>V</c> are what a rectangle is written with and <c>A</c> — the elliptical arc —
///         is what a rounded corner is written with, and an icon set that dropped either would render
///         about a third of Material Symbols as a squashed polygon. The arc is the long part of this
///         file and it is F.6.5 of the SVG specification, converted to centre parametrisation and
///         emitted as cubics.
///     </para>
///     <para>
///         ⚠ <b>Relative commands are resolved here and not carried.</b> A <see cref="PathSegment" />
///         holds absolute points, so <c>h20</c> becomes a line to the pen plus twenty — which means
///         the pen has to be tracked through every command including <c>Z</c>, whose end point is
///         where the <i>contour</i> started rather than where the path did. Getting that one wrong
///         shows as every closed subpath after the first landing in the wrong place.
///     </para>
///     <para>
///         ⚠ <b>The smooth commands need the previous control point, and its reflection is only
///         defined after a curve of the same family.</b> <c>S</c> after a line reflects nothing and
///         the control point is the pen itself — the specification's own rule, and the one a
///         from-memory implementation always gets wrong, because the wrong answer only shows on
///         paths that mix lines and smooth curves.
///     </para>
/// </remarks>
public static class SvgPath {
    /// <summary>Reads path data.</summary>
    /// <param name="data">The <c>d</c> attribute's value.</param>
    /// <returns>The path, in the coordinates the data is written in.</returns>
    /// <exception cref="SvgPathException">The data cannot be read.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Whitespace and commas are the same thing and both are optional</b>, which is the
    ///         part of the grammar that makes a naive split-on-space parser wrong: <c>M12 2L2 22h20z</c>
    ///         is one perfectly ordinary path and has no separator between the <c>2</c> and the
    ///         <c>L</c>. Numbers end where a number stops being a number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A command letter may be followed by more argument groups than it takes</b>, and
    ///         the extra ones repeat it — <c>L1 1 2 2</c> is two lines, and a repeated <c>M</c> is a
    ///         <c>L</c> rather than a second move, which is the one repetition rule that is not
    ///         "the same command again". An exporter that writes a polygon as one <c>M</c> with
    ///         twenty pairs after it is relying on both.
    ///     </para>
    /// </remarks>
    public static PathBuilder Parse(string data) {
        ArgumentNullException.ThrowIfNull(data);

        var path = new PathBuilder();
        var reader = new Reader(data);

        // The pen, where the current contour began, and the previous curve's control point reflected
        // into this one. All three are absolute.
        var pen = Vector2.Zero;
        var start = Vector2.Zero;
        var control = Vector2.Zero;

        // Which family the last command was, because a reflection is only defined after its own kind.
        var smoothCubic = false;
        var smoothQuadratic = false;

        var command = '\0';
        var started = false;

        while (true) {
            reader.SkipSeparators();

            if (reader.AtEnd) {
                break;
            }

            if (char.IsAsciiLetter(reader.Peek)) {
                command = reader.Read();
            } else if (command == '\0') {
                throw new SvgPathException("path data begins with a number rather than a command", reader.Position);
            } else if (command is 'M' or 'm') {
                // ⚠ A repeated moveto is a lineto, and this is the only command whose repetition is a
                // different command. A polygon written as one `M` with its whole outline after it —
                // which is what several exporters emit — is otherwise a path of overlapping moves and
                // draws nothing at all.
                command = command == 'M' ? 'L' : 'l';
            }

            var relative = char.IsLower(command);
            var origin = relative ? pen : Vector2.Zero;

            switch (char.ToUpperInvariant(command)) {
                case 'M': {
                    pen = origin + reader.Point();
                    start = pen;
                    started = true;

                    path.MoveTo(pen);
                    smoothCubic = smoothQuadratic = false;

                    break;
                }

                case 'L': {
                    Pen(ref started, path, pen, reader);
                    pen = origin + reader.Point();

                    path.LineTo(pen);
                    smoothCubic = smoothQuadratic = false;

                    break;
                }

                case 'H': {
                    Pen(ref started, path, pen, reader);
                    pen = new Vector2((relative ? pen.X : 0f) + reader.Number(), pen.Y);

                    path.LineTo(pen);
                    smoothCubic = smoothQuadratic = false;

                    break;
                }

                case 'V': {
                    Pen(ref started, path, pen, reader);
                    pen = new Vector2(pen.X, (relative ? pen.Y : 0f) + reader.Number());

                    path.LineTo(pen);
                    smoothCubic = smoothQuadratic = false;

                    break;
                }

                case 'C': {
                    Pen(ref started, path, pen, reader);

                    var first = origin + reader.Point();
                    var second = origin + reader.Point();

                    pen = origin + reader.Point();

                    path.CubicTo(first, second, pen);

                    control = second;
                    smoothCubic = true;
                    smoothQuadratic = false;

                    break;
                }

                case 'S': {
                    Pen(ref started, path, pen, reader);

                    // The reflection of the previous cubic's second control point, or the pen itself
                    // when the previous command was not a cubic — F.6 of the specification, and the
                    // rule a from-memory implementation drops.
                    var first = smoothCubic ? (pen * 2f) - control : pen;
                    var second = origin + reader.Point();

                    pen = origin + reader.Point();

                    path.CubicTo(first, second, pen);

                    control = second;
                    smoothCubic = true;
                    smoothQuadratic = false;

                    break;
                }

                case 'Q': {
                    Pen(ref started, path, pen, reader);

                    var handle = origin + reader.Point();

                    pen = origin + reader.Point();

                    path.QuadraticTo(handle, pen);

                    control = handle;
                    smoothQuadratic = true;
                    smoothCubic = false;

                    break;
                }

                case 'T': {
                    Pen(ref started, path, pen, reader);

                    var handle = smoothQuadratic ? (pen * 2f) - control : pen;

                    pen = origin + reader.Point();

                    path.QuadraticTo(handle, pen);

                    control = handle;
                    smoothQuadratic = true;
                    smoothCubic = false;

                    break;
                }

                case 'A': {
                    Pen(ref started, path, pen, reader);

                    var radii = reader.Point();
                    var rotation = reader.Number();
                    var large = reader.Flag();
                    var sweep = reader.Flag();
                    var end = origin + reader.Point();

                    Arc(path, pen, end, radii, rotation, large, sweep);

                    pen = end;
                    smoothCubic = smoothQuadratic = false;

                    break;
                }

                case 'Z': {
                    path.Close();

                    // ⚠ Back to where the *contour* started, not where the path did. A second subpath
                    // that opens with a relative command after a `Z` resolves it against this, and a
                    // parser that left the pen at the closing point puts every later contour at an
                    // offset — which looks like the icon coming apart rather than like an arithmetic
                    // slip.
                    pen = start;
                    smoothCubic = smoothQuadratic = false;

                    break;
                }

                default:
                    throw new SvgPathException($"'{command}' is not a path command", reader.Position);
            }
        }

        return path;
    }

    /// <summary>Reads path data, answering <see langword="null" /> rather than throwing.</summary>
    /// <param name="data">The <c>d</c> attribute's value, or <see langword="null" />.</param>
    /// <returns>The path, or <see langword="null" /> if it could not be read.</returns>
    /// <remarks>
    ///     For a caller reading a string somebody else wrote — a plugin's attribute, a file on disk —
    ///     where one bad glyph must not be an editor that will not start. The caller reports it; this
    ///     only declines to throw.
    /// </remarks>
    public static PathBuilder? TryParse(string? data) {
        if (string.IsNullOrWhiteSpace(data)) {
            return null;
        }

        try {
            return Parse(data);
        } catch (SvgPathException) {
            return null;
        }
    }

    /// <summary>Opens a contour at the pen for data whose first drawing command is not a move.</summary>
    /// <remarks>
    ///     ⚠ <b>The specification says such data is in error, and refusing it would be worse than
    ///     accepting it.</b> The pen is at the origin and a contour has to start somewhere; a path
    ///     that draws from (0,0) is visibly wrong at a glance, where a parser that threw would take
    ///     out a whole icon set for one glyph an exporter trimmed too eagerly.
    /// </remarks>
    static void Pen(ref bool started, PathBuilder path, Vector2 pen, in Reader reader) {
        _ = reader;

        if (started) {
            return;
        }

        path.MoveTo(pen);
        started = true;
    }

    /// <summary>Emits an elliptical arc as cubics — F.6.5 of the SVG specification.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Endpoint parametrisation in, centre parametrisation out.</b> SVG says "end here,
    ///         with these radii, this rotation, and one of four arcs" and a Bézier needs a centre, a
    ///         start angle and a sweep. The conversion is the specification's own and the two
    ///         degenerate cases it names are both real: coincident endpoints draw nothing, and a zero
    ///         radius is a straight line.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Radii too small for the endpoints are scaled up rather than refused</b> — F.6.6,
    ///         and it is the commonest thing wrong with hand-edited path data. Refusing would drop the
    ///         segment; scaling gives the arc the author obviously meant.
    ///     </para>
    ///     <para>
    ///         A cubic per quarter turn, which is the standard approximation and is under a
    ///         thousandth of a radius out at icon sizes.
    ///     </para>
    /// </remarks>
    static void Arc(PathBuilder path, Vector2 from, Vector2 to, Vector2 radii, float rotation, bool large, bool sweep) {
        if (from == to) {
            return;
        }

        var rx = MathF.Abs(radii.X);
        var ry = MathF.Abs(radii.Y);

        if (rx == 0f || ry == 0f) {
            path.LineTo(to);
            return;
        }

        var angle = rotation * (MathF.PI / 180f);
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);

        // Into the ellipse's own frame, with the chord's midpoint at the origin.
        var half = (from - to) * 0.5f;
        var x1 = (cos * half.X) + (sin * half.Y);
        var y1 = (-sin * half.X) + (cos * half.Y);

        // F.6.6: grow the radii until the endpoints are reachable.
        var reach = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));

        if (reach > 1f) {
            var grow = MathF.Sqrt(reach);

            rx *= grow;
            ry *= grow;
        }

        var numerator = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        var denominator = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        var factor = denominator == 0f ? 0f : MathF.Sqrt(MathF.Max(0f, numerator / denominator));

        if (large == sweep) {
            factor = -factor;
        }

        var cx1 = factor * rx * y1 / ry;
        var cy1 = -factor * ry * x1 / rx;

        var middle = (from + to) * 0.5f;
        var centre = new Vector2((cos * cx1) - (sin * cy1) + middle.X, (sin * cx1) + (cos * cy1) + middle.Y);

        var start = MathF.Atan2((y1 - cy1) / ry, (x1 - cx1) / rx);
        var end = MathF.Atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx);
        var delta = end - start;

        // The four-arc choice, resolved: the sweep flag says which way round and the difference is a
        // full turn either way.
        if (!sweep && delta > 0f) {
            delta -= MathF.Tau;
        } else if (sweep && delta < 0f) {
            delta += MathF.Tau;
        }

        var steps = Math.Max(1, (int) MathF.Ceiling(MathF.Abs(delta) / (MathF.PI * 0.5f)));
        var step = delta / steps;

        // The magic constant that makes a cubic follow a circular arc, for an arbitrary sweep rather
        // than the familiar quarter-turn 0.5523.
        var handle = 4f / 3f * MathF.Tan(step * 0.25f);

        for (var index = 0; index < steps; index++) {
            var a = start + (step * index);
            var b = a + step;

            var (pa, ta) = OnArc(centre, rx, ry, cos, sin, a);
            var (pb, tb) = OnArc(centre, rx, ry, cos, sin, b);

            path.CubicTo(pa + (ta * handle), pb - (tb * handle), pb);
        }
    }

    /// <summary>A point on the rotated ellipse and its tangent, at an angle.</summary>
    static (Vector2 Point, Vector2 Tangent) OnArc(Vector2 centre, float rx, float ry, float cos, float sin, float angle) {
        var ca = MathF.Cos(angle);
        var sa = MathF.Sin(angle);

        var x = rx * ca;
        var y = ry * sa;

        var dx = -rx * sa;
        var dy = ry * ca;

        return (
            new Vector2((cos * x) - (sin * y) + centre.X, (sin * x) + (cos * y) + centre.Y),
            new Vector2((cos * dx) - (sin * dy), (sin * dx) + (cos * dy))
        );
    }

    /// <summary>A cursor over path data, which is where the grammar's awkwardness is kept.</summary>
    ref struct Reader(string data) {
        readonly string data = data;

        public int Position { get; private set; }

        public readonly bool AtEnd => Position >= data.Length;

        public readonly char Peek => data[Position];

        public char Read() => data[Position++];

        /// <summary>Skips whitespace and commas, which the grammar treats identically.</summary>
        public void SkipSeparators() {
            while (Position < data.Length && (char.IsWhiteSpace(data[Position]) || data[Position] == ',')) {
                Position++;
            }
        }

        /// <summary>Reads a number.</summary>
        /// <exception cref="SvgPathException">There is not one there.</exception>
        public float Number() {
            SkipSeparators();

            var begin = Position;

            if (Position < data.Length && (data[Position] == '+' || data[Position] == '-')) {
                Position++;
            }

            while (Position < data.Length && char.IsAsciiDigit(data[Position])) {
                Position++;
            }

            if (Position < data.Length && data[Position] == '.') {
                Position++;

                while (Position < data.Length && char.IsAsciiDigit(data[Position])) {
                    Position++;
                }
            }

            // ⚠ An exponent, and it is not academic: several optimisers emit `1e-5` for a coordinate
            // that rounded to nothing, and a parser that stopped at the `e` would read the rest as a
            // command letter and throw on a file that is perfectly valid.
            if (Position < data.Length && (data[Position] == 'e' || data[Position] == 'E')) {
                var mark = Position;
                Position++;

                if (Position < data.Length && (data[Position] == '+' || data[Position] == '-')) {
                    Position++;
                }

                if (Position < data.Length && char.IsAsciiDigit(data[Position])) {
                    while (Position < data.Length && char.IsAsciiDigit(data[Position])) {
                        Position++;
                    }
                } else {
                    // An `e` that is not an exponent belongs to whatever comes next.
                    Position = mark;
                }
            }

            var text = data.AsSpan(begin, Position - begin);

            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                throw new SvgPathException($"expected a number and found '{Describe(text)}'", begin);
            }

            return value;
        }

        /// <summary>Reads a pair of numbers.</summary>
        public Vector2 Point() {
            var x = Number();
            var y = Number();

            return new Vector2(x, y);
        }

        /// <summary>Reads one of the arc's two flags, which are a single character each.</summary>
        /// <remarks>
        ///     ⚠ <b>One character, not a number, and the difference is load-bearing.</b> The grammar
        ///     lets the arc's flags run together with what follows — <c>a1 1 0 0110 0</c> is a valid
        ///     arc whose flags are <c>0</c> and <c>1</c> and whose endpoint is <c>10,0</c> — so a
        ///     parser that read them with <see cref="Number" /> would swallow the endpoint's first
        ///     digit and put the arc somewhere else entirely.
        /// </remarks>
        public bool Flag() {
            SkipSeparators();

            if (AtEnd) {
                throw new SvgPathException("an arc is missing its flags", Position);
            }

            var value = Read();

            return value switch {
                '0' => false,
                '1' => true,
                _ => throw new SvgPathException($"an arc flag is '{value}' rather than 0 or 1", Position - 1)
            };
        }

        static string Describe(ReadOnlySpan<char> text) => text.Length == 0 ? "nothing" : text.ToString();
    }
}
