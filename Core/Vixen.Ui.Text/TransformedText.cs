// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Text;

/// <summary>What <c>text-transform</c> does to the characters before they are shaped.</summary>
/// <remarks>
///     CSS Text 3 § 2.1. <c>full-width</c> and <c>full-size-kana</c> are not here: both are
///     compatibility mappings for Japanese input methods, neither has a utility class in any
///     framework this repository follows, and both would want a second table for a case no
///     interface in this tree can reach.
/// </remarks>
public enum TextTransform : byte {
    /// <summary>The text is drawn as it was written. The initial value.</summary>
    None,

    /// <summary>Every character in uppercase.</summary>
    Uppercase,

    /// <summary>Every character in lowercase.</summary>
    Lowercase,

    /// <summary>The first letter of every word in titlecase, and the rest untouched.</summary>
    /// <remarks>
    ///     ⚠ <b>The rest is untouched, not lowercased.</b> <c>capitalize</c> titlecases a word's
    ///     first letter and says nothing about the others, so <c>iPhone</c> stays <c>IPhone</c>
    ///     rather than becoming <c>Iphone</c>. Every browser does this and it surprises people.
    /// </remarks>
    Capitalize
}

/// <summary>An element's text after <c>text-transform</c>, and the map back to what was written.</summary>
/// <remarks>
///     <para>
///         <b>The map is the point, and the four keywords are the easy part.</b> A full Unicode case
///         mapping changes the UTF-16 <i>length</i> — <c>straße</c> uppercases to <c>STRASSE</c>, one
///         character becoming two — and what a text layout hands out is every one of them an index:
///         a caret index, a selection range, the start of a line. Shipping the keywords without this
///         puts the caret in the wrong character of an editable field, silently, and only on the
///         strings where it expands.
///     </para>
///     <para>
///         ⚠ <b>.NET's own casing would never have shown that.</b> <c>string.ToUpperInvariant</c>,
///         <c>ToUpper</c> in every culture, and <c>Rune.ToUpperInvariant</c> over all 1 112 064
///         scalars implement the <i>simple</i> mappings, which are one code point to one by
///         definition — measured, not assumed. So the naive implementation is index-safe and draws
///         <c>STRAßE</c>, which is a different defect and a visible one. The expansions come from
///         <c>SpecialCasingTable</c>, which is the UCD's unconditional rows and is what makes this
///         type necessary at all.
///     </para>
///     <para>
///         ⚠ <b><see cref="ToSource" /> is many-to-one and <see cref="ToDrawn" /> is not.</b> Both
///         units of the <c>SS</c> that came from one <c>ß</c> map back to that <c>ß</c>, because
///         there is no caret position between them — the two letters are one character the author
///         typed. So source → drawn → source is the identity and drawn → source → drawn is not, and
///         a click in the middle of an expansion snaps to its start. That is the behaviour a text
///         field wants and it is the reason the pair is not a single offset.
///     </para>
///     <para>
///         Identity is the overwhelmingly common case — no transform at all, or a transform under
///         which every character keeps its length — and it allocates no arrays and returns the
///         <i>same string instance</i>, which is what keeps the shaping cache's fast path and
///         <c>UiElement.Block</c>'s reference test meaning what they meant.
///     </para>
/// </remarks>
public sealed class TransformedText {
    /// <summary>Where each drawn index came from, or null when the map is the identity.</summary>
    readonly int[]? sourceOf;

    /// <summary>Where each source index went, or null when the map is the identity.</summary>
    readonly int[]? drawnOf;

    TransformedText(string source, string text, int[]? sourceOf, int[]? drawnOf) {
        Source = source;
        Text = text;
        this.sourceOf = sourceOf;
        this.drawnOf = drawnOf;
    }

    /// <summary>The text as it was written.</summary>
    public string Source { get; }

    /// <summary>The text as it is shaped and drawn.</summary>
    /// <remarks>
    ///     The same instance as <see cref="Source" /> when nothing changed, which is deliberate: a
    ///     substring taken from it hashes into the shaping cache under the entry the untransformed
    ///     string already had.
    /// </remarks>
    public string Text { get; }

    /// <summary>Whether every index means the same thing in both strings.</summary>
    public bool IsIdentity => drawnOf is null;

    /// <summary>Applies a transform, and builds the map if it moved anything.</summary>
    /// <param name="source">The element's own text.</param>
    /// <param name="transform">What to do to it.</param>
    /// <returns>The drawn text and the map between the two.</returns>
    /// <remarks>
    ///     ⚠ <b>The identity test is "did any character change length", not "did the string
    ///     change".</b> <c>hello</c> uppercased is a different string of the same shape, and every
    ///     index in it still means what it meant — so it needs no arrays, and paying for them would
    ///     put an allocation and two indirections on every uppercased label in an interface.
    /// </remarks>
    public static TransformedText Of(string? source, TextTransform transform) {
        source ??= string.Empty;

        if (transform == TextTransform.None || source.Length == 0) {
            return new TransformedText(source, source, null, null);
        }

        var text = new StringBuilder(source.Length);

        // ⚠ A list rather than an array, and that is not a style choice. `sourceOf` is indexed by
        // the *drawn* length, which is not known until the walk is over — an earlier draft sized it
        // from the source and then wrote past the end of the entries it had, which put the last
        // character of an expanded string one index out and nothing else wrong.
        var sourceOf = new List<int>(source.Length + 1);
        var drawnOf = new int[source.Length + 1];
        var moved = false;

        // `capitalize` needs to know where the words are, and UAX#29 is what CSS means by a word.
        // The other two transforms never ask, so the list is not built for them.
        var starts = transform == TextTransform.Capitalize ? WordStarts(source) : null;

        var at = 0;

        while (at < source.Length) {
            // ⚠ A lone surrogate is not a scalar and `Rune` refuses it, but a string can hold one —
            // an editable field mid-keystroke does, between the two halves of an astral character
            // being typed. Copied through untouched rather than replaced, because replacing it
            // would change what the field's own value says the next time it is read back.
            if (!Rune.TryGetRuneAt(source, at, out var rune)) {
                Record(sourceOf, drawnOf, at, 1, text.Length, 1);
                text.Append(source[at]);
                at++;
                continue;
            }

            var length = rune.Utf16SequenceLength;

            var mapped = transform switch {
                TextTransform.Uppercase => Upper(rune),
                TextTransform.Lowercase => Lower(rune),
                _ => starts!.Contains(at) ? Title(rune) : rune.ToString()
            };

            Record(sourceOf, drawnOf, at, length, text.Length, mapped.Length);
            moved |= mapped.Length != length;
            text.Append(mapped);
            at += length;
        }

        sourceOf.Add(source.Length);
        drawnOf[^1] = text.Length;

        var drawn = text.ToString();

        return moved
            ? new TransformedText(source, drawn, sourceOf.ToArray(), drawnOf)
            : new TransformedText(source, drawn, null, null);
    }

    /// <summary>Where a source index sits in <see cref="Text" />.</summary>
    /// <param name="index">A UTF-16 index into <see cref="Source" />.</param>
    /// <returns>The matching index into <see cref="Text" />, clamped to it.</returns>
    public int ToDrawn(int index) =>
        drawnOf is null
            ? Math.Clamp(index, 0, Text.Length)
            : drawnOf[Math.Clamp(index, 0, drawnOf.Length - 1)];

    /// <summary>Which source index a drawn index belongs to.</summary>
    /// <param name="index">A UTF-16 index into <see cref="Text" />.</param>
    /// <returns>The matching index into <see cref="Source" />, clamped to it.</returns>
    /// <remarks>
    ///     An index inside a character's expansion comes back as the start of that character: see
    ///     the type's remarks on why the two directions are not inverses.
    /// </remarks>
    public int ToSource(int index) =>
        sourceOf is null
            ? Math.Clamp(index, 0, Source.Length)
            : sourceOf[Math.Clamp(index, 0, sourceOf.Length - 1)];

    /// <summary>Writes one character's worth of both directions of the map.</summary>
    /// <remarks>
    ///     ⚠ <b>Every code unit of the source gets the same drawn index, and every code unit of the
    ///     drawn text gets the same source index.</b> Both are collapses and both are wanted: an
    ///     index between the halves of a surrogate pair, and an index between the two <c>S</c>s of
    ///     an expanded <c>ß</c>, are positions no caret can occupy.
    /// </remarks>
    static void Record(List<int> sourceOf, int[] drawnOf, int source, int sourceLength, int drawn, int drawnLength) {
        for (var i = 0; i < sourceLength; i++) {
            drawnOf[source + i] = drawn;
        }

        for (var i = 0; i < drawnLength; i++) {
            sourceOf.Add(source);
        }
    }

    /// <summary>The full uppercase mapping, which is not always one code point.</summary>
    static string Upper(Rune rune) =>
        SpecialCasingTable.TryUpper(rune.Value, out var mapping) ? mapping : Rune.ToUpperInvariant(rune).ToString();

    /// <summary>The full lowercase mapping.</summary>
    static string Lower(Rune rune) =>
        SpecialCasingTable.TryLower(rune.Value, out var mapping) ? mapping : Rune.ToLowerInvariant(rune).ToString();

    /// <summary>The full titlecase mapping.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Titlecase is a third case and not a synonym for uppercase.</b> Most of where the
    ///         two differ is in <see cref="SpecialCasingTable" /> — the Greek iota-subscript letters,
    ///         which are also the ones that expand. What is not there is the <i>simple</i> titlecase
    ///         column, which lives in UnicodeData.txt and which this generator does not read: .NET
    ///         exposes no equivalent either, because <c>TextInfo.ToTitleCase</c> is a word-by-word
    ///         operation with rules of its own rather than a per-code-point mapping.
    ///     </para>
    ///     <para>
    ///         <see cref="Digraph" /> is the whole of that column that differs from simple uppercase:
    ///         four Latin digraph triples, closed since Unicode 1.1, whose middle member is the
    ///         titlecase of all three. Everything else titlecases exactly as it uppercases, which is
    ///         why the fallback is <see cref="Rune.ToUpperInvariant" /> and not a gap.
    ///     </para>
    /// </remarks>
    static string Title(Rune rune) =>
        SpecialCasingTable.TryTitle(rune.Value, out var mapping) ? mapping :
        Digraph(rune.Value) is { } digraph ? digraph :
        Rune.ToUpperInvariant(rune).ToString();

    /// <summary>The titlecase of a Latin digraph, or null for everything else.</summary>
    /// <remarks>
    ///     <c>Ǆ ǅ ǆ</c>, <c>Ǉ ǈ ǉ</c>, <c>Ǌ ǋ ǌ</c> and <c>Ǳ ǲ ǳ</c>. Each triple is upper, title,
    ///     lower in code point order, so the titlecase of any member is the middle of its own three.
    /// </remarks>
    static string? Digraph(int value) => value switch {
        >= 0x01C4 and <= 0x01CC => char.ToString((char) (value - ((value - 0x01C4) % 3) + 1)),
        >= 0x01F1 and <= 0x01F3 => "ǲ",
        _ => null
    };

    /// <summary>Where each word's first letter is, as indices into the text.</summary>
    /// <remarks>
    ///     <para>
    ///         CSS says the first <i>typographic letter unit</i> of each word, and the two halves of
    ///         that matter separately. UAX#29 says where a word starts; <see cref="Rune.IsLetter" />
    ///         says which character in it is the first letter — so <c>"hello</c> capitalises the
    ///         <c>h</c> and not the quotation mark, which is the whole reason this is not simply
    ///         "the character after a space".
    ///     </para>
    ///     <para>
    ///         ⚠ A digit is not a letter unit, so <c>1st</c> becomes <c>1St</c>. That reads oddly and
    ///         it is what the specification says and what browsers do.
    ///     </para>
    /// </remarks>
    static HashSet<int> WordStarts(string source) {
        var boundaries = new List<int>();
        WordBreaker.Collect(source, boundaries);

        var starts = new HashSet<int>();

        for (var i = 0; i + 1 < boundaries.Count; i++) {
            var from = boundaries[i];
            var to = boundaries[i + 1];

            for (var at = from; at < to;) {
                if (!Rune.TryGetRuneAt(source, at, out var rune)) {
                    at++;
                    continue;
                }

                if (Rune.IsLetter(rune)) {
                    starts.Add(at);
                    break;
                }

                at += rune.Utf16SequenceLength;
            }
        }

        return starts;
    }
}
