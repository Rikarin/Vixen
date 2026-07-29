// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics;

/// <summary>Where <see cref="DebugFont" /> puts the segments it produces.</summary>
/// <remarks>
///     An interface rather than a delegate, and taken by <c>ref</c> as a constrained type parameter,
///     so emitting a line of text costs no allocation and no indirect call: the sink is a struct that
///     appends to whatever list the caller already has, and the JIT inlines it.
/// </remarks>
public interface IDebugFontSink {
    /// <summary>Takes one stroke.</summary>
    /// <param name="head">Where it starts, in text space.</param>
    /// <param name="tail">Where it ends.</param>
    void Segment(Vector2 head, Vector2 tail);
}

/// <summary>
///     A stroke font: every glyph is a handful of line segments, so text costs nothing but the
///     pipeline that is already drawing debug geometry.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a font like this exists at all, next to a real text stack.</b> <c>Vixen.Ui.Text</c>
///         shapes, wraps and rasterises properly, and everything the user is meant to read goes
///         through it. Debug text is the one place where that is the wrong dependency: it has to work
///         in a build with no atlas uploaded, no font asset resident and no UI document — the frame
///         where the asset system is the thing being debugged — and it has to come out of the same
///         draw call as the lines it labels. A table of strokes has no assets, no atlas and no
///         upload; it is the same vertex buffer as everything else here.
///     </para>
///     <para>
///         Deliberately not a text renderer. There is no kerning, no shaping, no bidi and no
///         script beyond ASCII: anything outside 32–126 is drawn as a box, which is the honest
///         rendering of "this font cannot show you that" and is what a diagnostic overlay should do
///         rather than silently dropping the character.
///     </para>
///     <para>
///         <b>Text space has y pointing down</b> and its origin at the top-left of the first glyph's
///         cap box, because that is what a screen-space overlay wants and what every layout in this
///         engine already means by a position. World-space text flips it once, in the renderer, where
///         the camera basis is known.
///     </para>
/// </remarks>
public static class DebugFont {
    /// <summary>How wide a glyph's cell is, in grid units.</summary>
    public const int GridWidth = 4;

    /// <summary>Which grid row the baseline sits on.</summary>
    public const int GridBaseline = 2;

    /// <summary>Which grid row a capital's top sits on.</summary>
    public const int GridTop = 8;

    /// <summary>How far one glyph moves the pen, in grid units — the cell plus its gap.</summary>
    public const int GridAdvance = 5;

    /// <summary>How tall a capital is, in grid units. The unit <c>size</c> is measured in.</summary>
    public const int GridCapHeight = GridTop - GridBaseline;

    /// <summary>The first character the table covers.</summary>
    public const char FirstCharacter = ' ';

    /// <summary>The last character the table covers.</summary>
    public const char LastCharacter = '~';

    // The strokes, one entry per character from FirstCharacter to LastCharacter.
    //
    // A glyph is polylines separated by '|'; a polyline is a run of vertices, each two digits — x
    // then y — on a grid four wide and nine tall. The baseline is row 2 and a capital reaches row 8,
    // so a descender has rows 0 and 1 to fall into and nothing needs a sign.
    //
    // Written out rather than generated because this is the only copy that will ever exist, and a
    // string per glyph is a form somebody can read, correct and diff. The parse below turns it into
    // a flat float array once.
    static readonly string[] Strokes = [
        "",                                                 // (space)
        "2824|2223",                                        // !
        "1817|3837",                                        // "
        "1317|3337|0444|0646",                              // #
        "473818070615354443321203|2822",                    // $
        "0718271607|0248|2334433223",                       // %
        "42162837360403123243",                             // &
        "2827",                                             // '
        "38272332",                                         // (
        "18272312",                                         // )
        "2824|0745|0547",                                   // *
        "2723|0545",                                        // +
        "2210",                                             // ,
        "0545",                                             // -
        "2223",                                             // .
        "0248",                                             // /
        "183847433212030718|0347",                          // 0
        "172822|0242",                                      // 1
        "07183847460242",                                   // 2
        "0848254443321203",                                 // 3
        "32380444",                                         // 4
        "480805354443321203",                               // 5
        "473818070312324344351504",                         // 6
        "084812",                                           // 7
        "18384735150718|1504031232434435",                  // 8
        "031232434738180706153546",                         // 9
        "2526|2223",                                        // :
        "2526|2210",                                        // ;
        "371533",                                           // <
        "0646|0444",                                        // =
        "173513",                                           // >
        "071838474624|2223",                                // ?
        "331315353212030718384744",                         // @
        "0228|2842|1434",                                   // A
        "0208|0838473505|0535443202",                       // B
        "4738180703123243",                                 // C
        "0208|083847433202",                                // D
        "48080242|0535",                                    // E
        "480802|0535",                                      // F
        "47381807031232434525",                             // G
        "0208|4248|0545",                                   // H
        "1838|2822|1232",                                   // I
        "3833221203",                                       // J
        "0208|4805|0542",                                   // K
        "080242",                                           // L
        "0208254842",                                       // M
        "02084248",                                         // N
        "183847433212030718",                               // O
        "0208|0838473505",                                  // P
        "183847433212030718|2341",                          // Q
        "0208|0838473505|2542",                             // R
        "473818070615354443321203",                         // S
        "0848|2822",                                        // T
        "080312324348",                                     // U
        "082248",                                           // V
        "0812253248",                                       // W
        "0842|0248",                                        // X
        "082548|2522",                                      // Y
        "08480242",                                         // Z
        "38282232",                                         // [
        "0842",                                             // \
        "18282212",                                         // ]
        "162836",                                           // ^
        "0141",                                             // _
        "1827",                                             // `
        "4642|4536160503123243",                            // a
        "0802|0516364543321203",                            // b
        "4536160503123243",                                 // c
        "4842|4536160503123243",                            // d
        "04444536160503123243",                             // e
        "12172838|0636",                                    // f
        "4536160503123243|4641301001",                      // g
        "0802|0516364542",                                  // h
        "2827|2622",                                        // i
        "3837|3631201001",                                  // j
        "0802|0436|0442",                                   // k
        "18132232",                                         // l
        "0602|05162522|25364542",                           // m
        "0602|0516364542",                                  // n
        "163645433212030516",                               // o
        "0600|0516364543321203",                            // p
        "4640|4536160503123243",                            // q
        "0602|051636",                                      // r
        "4616051434433202",                                 // s
        "18132232|0636",                                    // t
        "0603123243|4642",                                  // u
        "062246",                                           // v
        "0612243246",                                       // w
        "0642|0246",                                        // x
        "0622|4610",                                        // y
        "06460242",                                         // z
        "38272615242332",                                   // {
        "2820",                                             // |
        "18272635242312",                                   // }
        "04153344"                                          // ~
    ];

    /// <summary>What an unmapped character is drawn as: a box, so it is visibly missing.</summary>
    const string Tofu = "0206464202";

    // Four floats a segment — x0, y0, x1, y1 in grid units — with a glyph's segments contiguous.
    // One array rather than an array per glyph, so emitting a line touches one allocation's worth of
    // memory rather than one per character.
    static readonly float[] Data;
    static readonly int[] Starts;
    static readonly int[] Counts;
    static readonly int TofuStart;
    static readonly int TofuCount;

    static DebugFont() {
        // The table is indexed by subtraction, so a missing or surplus entry shifts every glyph
        // after it — which draws readable-looking nonsense rather than anything that says what is
        // wrong. Checked here, where it is one comparison at type-initialisation.
        if (Strokes.Length != LastCharacter - FirstCharacter + 1) {
            throw new InvalidOperationException(
                $"The stroke table has {Strokes.Length} glyphs for {LastCharacter - FirstCharacter + 1} characters."
            );
        }

        var segments = new List<float>(2048);

        Starts = new int[Strokes.Length];
        Counts = new int[Strokes.Length];

        for (var index = 0; index < Strokes.Length; index++) {
            Starts[index] = segments.Count / 4;
            Parse(Strokes[index], segments);
            Counts[index] = (segments.Count / 4) - Starts[index];
        }

        TofuStart = segments.Count / 4;
        Parse(Tofu, segments);
        TofuCount = (segments.Count / 4) - TofuStart;

        Data = [.. segments];
    }

    /// <summary>How far one character moves the pen, for text of this height.</summary>
    /// <param name="size">The cap height.</param>
    /// <returns>The advance, in the same units as <paramref name="size" />.</returns>
    public static float AdvanceFor(float size) => size * GridAdvance / GridCapHeight;

    /// <summary>How far apart two lines of text sit, for text of this height.</summary>
    /// <param name="size">The cap height.</param>
    /// <returns>The baseline-to-baseline distance.</returns>
    /// <remarks>
    ///     The full grid rather than the cap height, so a descender on one line does not touch the
    ///     capital under it — which for a log tail full of <c>g</c>, <c>p</c> and <c>y</c> is what
    ///     the difference between legible and not comes down to.
    /// </remarks>
    public static float LineHeightFor(float size) => size * (GridTop + 2) / GridCapHeight;

    /// <summary>How wide a string draws — the widest of its lines, if it has several.</summary>
    /// <param name="text">The text.</param>
    /// <param name="size">The cap height.</param>
    /// <returns>The width.</returns>
    public static float MeasureWidth(ReadOnlySpan<char> text, float size) {
        var widest = 0;
        var column = 0;

        foreach (var character in text) {
            if (character == '\n') {
                widest = Math.Max(widest, column);
                column = 0;
                continue;
            }

            column++;
        }

        return Math.Max(widest, column) * AdvanceFor(size);
    }

    /// <summary>How tall a string draws, counting every line.</summary>
    /// <param name="text">The text.</param>
    /// <param name="size">The cap height.</param>
    /// <returns>The height.</returns>
    public static float MeasureHeight(ReadOnlySpan<char> text, float size) {
        var lines = 1;

        foreach (var character in text) {
            if (character == '\n') {
                lines++;
            }
        }

        return ((lines - 1) * LineHeightFor(size)) + size;
    }

    /// <summary>
    ///     How many segments a string draws, so a caller can reserve exactly the room it needs.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The segment count.</returns>
    public static int SegmentCount(ReadOnlySpan<char> text) {
        var total = 0;

        foreach (var character in text) {
            if (character != '\n') {
                total += IsMapped(character) ? Counts[character - FirstCharacter] : TofuCount;
            }
        }

        return total;
    }

    /// <summary>Draws a string into a sink, one segment at a time.</summary>
    /// <typeparam name="TSink">Where the segments go.</typeparam>
    /// <param name="text">The text. <c>\n</c> starts a new line; every other control character is a box.</param>
    /// <param name="origin">The top-left of the first glyph's cap box.</param>
    /// <param name="size">The cap height.</param>
    /// <param name="sink">The sink, by reference so a struct one keeps what it accumulated.</param>
    public static void Emit<TSink>(ReadOnlySpan<char> text, Vector2 origin, float size, ref TSink sink)
        where TSink : IDebugFontSink {
        var scale = size / GridCapHeight;
        var advance = AdvanceFor(size);
        var lineHeight = LineHeightFor(size);

        var x = origin.X;
        var y = origin.Y;

        foreach (var character in text) {
            if (character == '\n') {
                x = origin.X;
                y += lineHeight;
                continue;
            }

            var mapped = IsMapped(character);
            var start = mapped ? Starts[character - FirstCharacter] : TofuStart;
            var count = mapped ? Counts[character - FirstCharacter] : TofuCount;

            for (var segment = 0; segment < count; segment++) {
                var offset = (start + segment) * 4;

                sink.Segment(
                    new(x + (Data[offset] * scale), y + ((GridTop - Data[offset + 1]) * scale)),
                    new(x + (Data[offset + 2] * scale), y + ((GridTop - Data[offset + 3]) * scale))
                );
            }

            x += advance;
        }
    }

    static bool IsMapped(char character) => character is >= FirstCharacter and <= LastCharacter;

    /// <summary>Turns one glyph's polylines into segments.</summary>
    /// <param name="glyph">The encoded glyph.</param>
    /// <param name="segments">Where the four-float segments are appended.</param>
    /// <remarks>
    ///     ⚠ <b>A malformed entry is a crash here, at type-initialisation, and that is deliberate.</b>
    ///     The table is a compile-time constant of this assembly; if it does not parse, the mistake is
    ///     in the source above rather than in anything a caller did, and failing at the first use is
    ///     what makes the typo findable instead of a glyph that quietly draws nothing.
    /// </remarks>
    static void Parse(string glyph, List<float> segments) {
        foreach (var polyline in glyph.Split('|', StringSplitOptions.RemoveEmptyEntries)) {
            for (var vertex = 2; vertex + 1 < polyline.Length; vertex += 2) {
                segments.Add(polyline[vertex - 2] - '0');
                segments.Add(polyline[vertex - 1] - '0');
                segments.Add(polyline[vertex] - '0');
                segments.Add(polyline[vertex + 1] - '0');
            }
        }
    }
}
