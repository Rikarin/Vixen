// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using HarfBuzzSharp;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbScript = HarfBuzzSharp.Script;

namespace Vixen.Ui.Text;

/// <summary>Turns text into positioned glyphs.</summary>
/// <remarks>
///     <para>
///         The shaping itself is HarfBuzz's — ADR-009's dependency register names it and
///         <c>docs/plan/spikes/text-harfbuzz</c> cleared it. What is Vixen's is everything around
///         the call: which runs the text is cut into, what direction and script each one is given,
///         which order the results are drawn in, and how a glyph is related back to the character
///         it came from.
///     </para>
///     <para>
///         That distinction decides how this is tested. A comparison against <c>hb_shape</c> would
///         be HarfBuzz judging itself and would survive any mistake in the paragraph above. The
///         Consortium's <c>text-rendering-tests</c> would not: its expectations were written from
///         the OpenType specification by people who were not running a shaper, and shaping a
///         Kannada string as Latin fails them.
///     </para>
/// </remarks>
public static class TextShaper {
    /// <summary>The tag each script is handed to the shaper as, worked out once per script.</summary>
    /// <remarks>
    ///     <see cref="Script" />'s values <i>are</i> the packed ISO 15924 tags, so this is a
    ///     formality — but HarfBuzzSharp's <c>Script</c> parses from a string, and doing that per
    ///     run would allocate four characters for every run of every paragraph of every frame.
    /// </remarks>
    static readonly ConcurrentDictionary<Script, HbScript> Tags = new();

    /// <summary>Shapes a paragraph.</summary>
    /// <param name="font">The font to shape with.</param>
    /// <param name="text">The paragraph.</param>
    /// <param name="direction">Its base direction. Auto works it out from the first strong character.</param>
    /// <returns>The glyphs, in the order they are drawn.</returns>
    public static ShapedText Shape(FontFace font, string text, ParagraphDirection direction = ParagraphDirection.Auto) {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        var items = TextItemizer.Itemize(text, direction);
        if (items.Count == 0) {
            return new ShapedText(text, [], 0);
        }

        var shaped = new ShapedRun[items.Count];
        for (var i = 0; i < items.Count; i++) {
            shaped[i] = ShapeRun(font, text, items[i]);
        }

        var order = TextItemizer.VisualOrder(items);
        var runs = new ShapedRun[order.Length];
        var advance = 0f;

        for (var i = 0; i < order.Length; i++) {
            runs[i] = shaped[order[i]];
            advance += runs[i].Advance;
        }

        return new ShapedText(text, runs, advance);
    }

    /// <summary>Shapes one run.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ The whole string goes into the buffer and only the run's range is marked as the
    ///         item. That is not laziness — it is how a shaper is given <i>context</i>. An Arabic
    ///         letter's form depends on whether the letter beside it joins, and a run boundary that
    ///         hides the neighbour makes an initial form where a medial one belongs. Handing over
    ///         only the substring would produce visibly wrong Arabic at every run boundary and
    ///         nothing would say so.
    ///     </para>
    ///     <para>
    ///         It also means the clusters that come back are indices into the original text rather
    ///         than into a substring, which is what the rest of the system wants to talk in.
    ///     </para>
    ///     <para>
    ///         The language is left unset on purpose. HarfBuzz's default is taken from the process
    ///         locale, which would make shaping depend on the machine it runs on — a golden test
    ///         that passes in one timezone and fails in another is worse than no golden test.
    ///     </para>
    /// </remarks>
    internal static ShapedRun ShapeRun(FontFace font, string text, TextItem item) {
        using var buffer = new HbBuffer();

        buffer.AddUtf16(text, item.Start, item.Length);
        buffer.Direction = item.IsRightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = TagFor(item.Script);

        // ⚠ Default-ignorables are deleted rather than hidden. Left alone, HarfBuzz keeps a zero
        // width invisible glyph for every zero-width joiner, variation selector and bidi control —
        // which is correct and is not what a renderer wants: each one is still a quad to batch and
        // still an entry in the glyph run that maps back to no visible mark. Deleting them is the
        // same choice Raqm makes by default, and the Consortium's expectations are written against
        // it. The cost is that a cluster can end up with no glyphs at all, which hit testing has to
        // be able to say something sensible about.
        buffer.Flags = BufferFlags.RemoveDefaultIgnorables;

        font.Shaper.Shape(buffer, []);

        var infos = buffer.GetGlyphInfoSpan();
        var positions = buffer.GetGlyphPositionSpan();
        var glyphs = new ShapedGlyph[infos.Length];
        var advance = 0f;

        for (var i = 0; i < infos.Length; i++) {
            glyphs[i] = new ShapedGlyph(
                (ushort)infos[i].Codepoint,
                (int)infos[i].Cluster,
                positions[i].XAdvance,
                positions[i].YAdvance,
                positions[i].XOffset,
                positions[i].YOffset
            );

            advance += positions[i].XAdvance;
        }

        return new ShapedRun(item, font, glyphs, advance);
    }

    static HbScript TagFor(Script script) => Tags.GetOrAdd(
        script,
        static value => HbScript.Parse(string.Create(
            4,
            (uint)value,
            static (span, tag) => {
                span[0] = (char)(byte)(tag >> 24);
                span[1] = (char)(byte)(tag >> 16);
                span[2] = (char)(byte)(tag >> 8);
                span[3] = (char)(byte)tag;
            }
        ))
    );
}
