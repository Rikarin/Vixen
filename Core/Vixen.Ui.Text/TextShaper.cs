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
    /// <param name="variation">Where along a variable font's axes to shape, or null for its defaults.</param>
    /// <param name="features">
    ///     The OpenType features to switch on or off, or null for whatever the face does by default.
    /// </param>
    /// <param name="language">
    ///     The BCP-47 tag the text is written in, or <see langword="null" /> or empty for
    ///     undetermined.
    ///     <para>
    ///         ⚠ <b>Undetermined is the default and stays the default, because the alternative is
    ///         not "the right language" but "this machine's".</b> HarfBuzz takes its own default
    ///         from the process locale, so a paragraph shaped without one would lay out differently
    ///         on a German developer's laptop and on CI — a golden image red on one machine only.
    ///         Nothing in this assembly reads <c>CultureInfo</c>: a language arrives here from
    ///         whatever declared it, or it does not arrive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not the same fact as the script, which is what <see cref="TextItemizer" />
    ///         already cuts on.</b> English, German and French are one script and three different
    ///         sets of language-specific substitutions — and three different hyphenations, which is
    ///         the consumer this exists ahead of. See #600.
    ///     </para>
    /// </param>
    /// <returns>The glyphs, in the order they are drawn.</returns>
    public static ShapedText Shape(
        FontFace font,
        string text,
        ParagraphDirection direction = ParagraphDirection.Auto,
        FontVariation? variation = null,
        FontFeatureSet? features = null,
        string? language = null
    ) {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        // ⚠ Set on every call, including when nobody asked for an instance. The face's shaper is
        // stateful, so a null here has to mean "back to the default" rather than "leave it as
        // whoever shaped last left it" — otherwise a paragraph's advances depend on what was drawn
        // before it, which is a bug that only appears once something animates an axis.
        font.SetInstance(variation);

        var items = TextItemizer.Itemize(text, direction);
        if (items.Count == 0) {
            return new ShapedText(text, [], 0);
        }

        // ⚠ Built once for the paragraph rather than once per run. HarfBuzz copies the array on
        // every `Shape`, and a paragraph of mixed scripts is one item per script change — so a
        // per-run conversion would allocate the same four bytes per feature per run of every string
        // that names one.
        var applied = Convert(features);

        var shaped = new ShapedRun[items.Count];
        for (var i = 0; i < items.Count; i++) {
            shaped[i] = ShapeRun(font, text, items[i], applied, language);
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
    ///         ⚠ <b>The language is left unset unless a caller names one, and unset is not a gap.</b>
    ///         HarfBuzz's default is taken from the process locale, which would make shaping depend
    ///         on the machine it runs on — a golden test that passes in one timezone and fails in
    ///         another is worse than no golden test. A declared language overrides that default
    ///         without ever consulting it; nothing here falls back to <c>CultureInfo</c>.
    ///     </para>
    /// </remarks>
    internal static ShapedRun ShapeRun(FontFace font, string text, TextItem item) =>
        ShapeRun(font, text, item, []);

    internal static ShapedRun ShapeRun(
        FontFace font,
        string text,
        TextItem item,
        Feature[] features,
        string? language = null
    ) {
        using var buffer = new HbBuffer();

        buffer.AddUtf16(text, item.Start, item.Length);
        buffer.Direction = item.IsRightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = TagFor(item.Script);

        // ⚠ Assigned only when a caller declared one, and the branch is the whole guarantee: an
        // assignment of "whatever we have" would reach `new Language("")`, which HarfBuzz resolves
        // through the process locale — exactly the machine dependence being avoided.
        if (!string.IsNullOrEmpty(language)) {
            buffer.Language = new Language(language);
        }

        // ⚠ Default-ignorables are deleted rather than hidden. Left alone, HarfBuzz keeps a zero
        // width invisible glyph for every zero-width joiner, variation selector and bidi control —
        // which is correct and is not what a renderer wants: each one is still a quad to batch and
        // still an entry in the glyph run that maps back to no visible mark. Deleting them is the
        // same choice Raqm makes by default, and the Consortium's expectations are written against
        // it. The cost is that a cluster can end up with no glyphs at all, which hit testing has to
        // be able to say something sensible about.
        buffer.Flags = BufferFlags.RemoveDefaultIgnorables;

        font.Shaper.Shape(buffer, features);

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

    /// <summary>Turns the engine's feature set into the array HarfBuzz wants.</summary>
    /// <remarks>
    ///     An empty array for "nothing asked", which is what the shaper was always given and is the
    ///     path every label in an interface takes.
    /// </remarks>
    static Feature[] Convert(FontFeatureSet? features) {
        if (features is null || features.IsEmpty) {
            return [];
        }

        var applied = new Feature[features.Features.Length];

        for (var i = 0; i < applied.Length; i++) {
            var feature = features.Features[i];

            // ⚠ The four characters rather than the packed integer, because HarfBuzzSharp's `Tag`
            // has no integer constructor and its one-argument overloads are all chars — an
            // implicit conversion picks the wrong one and the tag becomes a single byte.
            var tag = feature.Tag;

            // The whole buffer, which is the only range CSS can express. See `FontFeature`.
            applied[i] = new Feature(
                new Tag((char) (tag >> 24), (char) ((tag >> 16) & 0xFF), (char) ((tag >> 8) & 0xFF), (char) (tag & 0xFF)),
                feature.Value,
                0,
                uint.MaxValue
            );
        }

        return applied;
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
