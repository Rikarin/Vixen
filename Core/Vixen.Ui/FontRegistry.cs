// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>Which way a face slants.</summary>
public enum FontStyle : byte {
    /// <summary>Upright.</summary>
    Normal,

    /// <summary>A drawn italic, with its own letterforms.</summary>
    Italic,

    /// <summary>A slanted roman.</summary>
    Oblique
}

/// <summary>One face of a family, and what it claims to be.</summary>
/// <param name="Font">The face.</param>
/// <param name="Weight">Its weight on CSS's 1–1000 scale, where 400 is regular and 700 is bold.</param>
/// <param name="Style">Which way it slants.</param>
public readonly record struct FontVariant(FontFace Font, int Weight, FontStyle Style);

/// <summary>One stretch of a string that one face draws.</summary>
/// <param name="Font">The face.</param>
/// <param name="Start">Where the stretch begins, as a UTF-16 index into the whole string.</param>
/// <param name="Length">How many UTF-16 units it covers.</param>
public readonly record struct FontSpan(FontFace Font, int Start, int Length);

/// <summary>What a <c>font-family</c> declaration names.</summary>
/// <remarks>
///     <para>
///         <b>Registered rather than discovered.</b> Nothing here walks the machine's font
///         directories, and that is the decision rather than the gap: a game ships its fonts, and an
///         interface whose text is laid out by whatever the operating system happened to have
///         installed lays out differently on every machine it runs on. The editor will want a system
///         enumerator; the runtime should not have one.
///     </para>
///     <para>
///         <b>A face's weight and style are stated, not sniffed.</b> They could be read from the
///         file's <c>OS/2</c> table, and that would be the same mistake in miniature as walking the
///         font directories: a shipped asset whose metadata disagrees with what the designer meant
///         would silently pick the wrong face, and the fix would be editing a binary. Registration
///         says what the face is, so it is a line of the caller's code either way.
///     </para>
///     <para>
///         <b>A declaration is a fallback chain, not a first-registered-wins list.</b>
///         <see cref="Chain" /> turns <c>font-family: Inter, Noto Sans JP</c> into both faces in that
///         order and <see cref="Cover" /> hands each grapheme cluster to the first of them that can
///         draw it — which is what CSS means by the list. <see cref="Resolve" /> answers the head of
///         that chain, and is what something wanting one face for one purpose asks.
///     </para>
///     <para>
///         ⚠ <b>The chain ends in <see cref="Fallbacks" /> and then <see cref="Default" />, so a
///         string can still reach <c>.notdef</c>.</b> Nothing here synthesises a glyph or goes looking
///         on the machine: if no registered face has the code point, the primary draws its
///         <c>.notdef</c> box. That is the honest outcome and a visible one, which is the point — a
///         silent blank would look like a layout bug.
///     </para>
/// </remarks>
public sealed class FontRegistry {
    /// <summary>The weight <c>font-weight: normal</c> names, and what a face registered without one gets.</summary>
    public const int RegularWeight = 400;

    /// <summary>The weight <c>font-weight: bold</c> names.</summary>
    public const int BoldWeight = 700;

    readonly Dictionary<string, List<FontVariant>> families = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FontFace> fallbacks = [];
    FontFace? standby;

    /// <summary>The face used when a declaration names nothing that is registered.</summary>
    /// <remarks>
    ///     Null until something is registered, and then the first one registered. An interface whose
    ///     stylesheet has a typo in a family name draws in some font rather than not at all, which is
    ///     both what a browser does and what makes the typo findable.
    /// </remarks>
    public FontFace? Default {
        get => standby;
        set {
            standby = value;
            Revision++;
        }
    }

    /// <summary>How many families are registered.</summary>
    public int Count => families.Count;

    /// <summary>Bumped whenever anything registered here changes.</summary>
    /// <remarks>
    ///     ⚠ <b>So that a cached line knows to be rebuilt.</b> <see cref="UiElement.Line" /> keeps the
    ///     runs it last built and rebuilds them when its text or its font properties change — and
    ///     registering a face changes neither, while changing what the same declaration resolves to.
    ///     Without this, a font registered after the first frame is a font that never appears.
    /// </remarks>
    public int Revision { get; private set; }

    /// <summary>The faces tried after everything a declaration names, in order.</summary>
    /// <remarks>
    ///     What a stylesheet cannot say. A declaration names the families a designer chose; an emoji
    ///     face or a CJK face is wanted behind <i>every</i> declaration in the application, and
    ///     writing it into each one is both noise and a thing to forget.
    /// </remarks>
    public IReadOnlyList<FontFace> Fallbacks => fallbacks;

    /// <summary>Registers a face under a family name.</summary>
    /// <param name="family">The name a stylesheet will use.</param>
    /// <param name="font">The face.</param>
    /// <param name="weight">Its weight, defaulting to regular.</param>
    /// <param name="style">Its slant, defaulting to upright.</param>
    /// <remarks>
    ///     <para>The first face registered also becomes <see cref="Default" />.</para>
    ///     <para>
    ///         Registering the same family, weight and style twice replaces the first — a family is a
    ///         set of variants and two faces claiming to be the same one is a mistake, not a list.
    ///     </para>
    /// </remarks>
    public void Register(string family, FontFace font, int weight = RegularWeight, FontStyle style = FontStyle.Normal) {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(font);

        if (!families.TryGetValue(family, out var variants)) {
            variants = [];
            families[family] = variants;
        }

        var replacing = variants.FindIndex(variant => variant.Weight == weight && variant.Style == style);
        var entry = new FontVariant(font, weight, style);

        if (replacing >= 0) {
            variants[replacing] = entry;
        } else {
            variants.Add(entry);
        }

        standby ??= font;
        Revision++;
    }

    /// <summary>Adds a face to the end of the chain every declaration falls back to.</summary>
    /// <param name="font">The face.</param>
    /// <remarks>Registering the same face twice does nothing: a chain is an order, not a multiset.</remarks>
    public void AddFallback(FontFace font) {
        ArgumentNullException.ThrowIfNull(font);

        if (!fallbacks.Contains(font)) {
            fallbacks.Add(font);
            Revision++;
        }
    }

    /// <summary>The face a <c>font-family</c> declaration resolves to.</summary>
    /// <param name="declaration">
    ///     The declaration's value — a comma-separated list, most wanted first, with quotes allowed
    ///     around a name that needs them.
    /// </param>
    /// <param name="weight">The weight wanted, on CSS's 1–1000 scale.</param>
    /// <param name="style">The slant wanted.</param>
    /// <returns>The best face of the first registered family in the list, or <see cref="Default" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The family is chosen before the weight is.</b> A declaration naming two families
    ///     takes the first that is registered at all, even if the second has a face at exactly the
    ///     weight asked for and the first does not — which is what CSS says, and is worth knowing
    ///     because the other reading looks more helpful right up until a fallback family starts
    ///     winning over the one the designer chose.
    /// </remarks>
    public FontFace? Resolve(string? declaration, int weight = RegularWeight, FontStyle style = FontStyle.Normal) {
        if (string.IsNullOrWhiteSpace(declaration)) {
            return Default;
        }

        foreach (var range in declaration.AsSpan().Split(',')) {
            // The quotes are part of the CSS syntax rather than of the name — `"Noto Sans"` and
            // `Noto Sans` are the same family, and a registry keyed on the quoted form would match
            // whichever way the stylesheet happened to be written.
            var name = declaration.AsSpan(range).Trim().Trim("\"'");

            if (!name.IsEmpty && families.TryGetValue(name.ToString(), out var variants)) {
                return Best(variants, weight, style);
            }
        }

        return Default;
    }

    /// <summary>The whole fallback chain a <c>font-family</c> declaration names.</summary>
    /// <param name="declaration">The declaration's value, most wanted first.</param>
    /// <param name="weight">The weight wanted, on CSS's 1–1000 scale.</param>
    /// <param name="style">The slant wanted.</param>
    /// <param name="into">Filled with the faces to try, in order. Cleared first.</param>
    /// <remarks>
    ///     <para>
    ///         Every registered family the declaration names, then <see cref="Fallbacks" /> —
    ///         de-duplicated, because the same face reached twice is a face asked the same question
    ///         twice and answering it the second time cannot help.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Default" /> only appears when nothing else did.</b> It is a
    ///         <i>substitute</i> for a declaration that names nothing registered, not a tail on every
    ///         chain — putting it on the end of all of them would mean the first font ever registered
    ///         quietly drawing whatever the declared families happen to be missing, which is a
    ///         fallback nobody asked for and cannot see. <see cref="Fallbacks" /> is where a face that
    ///         should stand behind every declaration goes, and saying so is one line.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The weight is chosen per family, not once for the chain.</b> A declaration asking
    ///         for 700 gets each family's best answer to 700, so a fallback that ships only a regular
    ///         contributes its regular rather than being skipped for not having a bold. Skipping it
    ///         would mean a missing glyph rather than a glyph at the wrong weight, and one of those
    ///         is legible.
    ///     </para>
    /// </remarks>
    public void Chain(string? declaration, int weight, FontStyle style, List<FontFace> into) {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        if (!string.IsNullOrWhiteSpace(declaration)) {
            foreach (var range in declaration.AsSpan().Split(',')) {
                var name = declaration.AsSpan(range).Trim().Trim("\"'");

                if (!name.IsEmpty && families.TryGetValue(name.ToString(), out var variants)
                    && Best(variants, weight, style) is { } face) {
                    Append(into, face);
                }
            }
        }

        // ⚠ Before the fallbacks and not after them. A declaration that named nothing registered has
        // no primary, and `Default` is what stands in for one — putting it behind the fallbacks would
        // make an element with no `font-family` draw in whatever face was added as a last resort for
        // some other script.
        if (into.Count == 0 && standby is { } substitute) {
            into.Add(substitute);
        }

        foreach (var face in fallbacks) {
            Append(into, face);
        }
    }

    /// <summary>Splits a string into the stretches each face of a chain draws.</summary>
    /// <param name="text">The string.</param>
    /// <param name="chain">The faces to try, in order, as <see cref="Chain" /> gives them.</param>
    /// <param name="into">Filled with the spans, in text order and tiling the string. Cleared first.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>By grapheme cluster, not by code point.</b> A base letter and the marks on it are
    ///         one cluster and have to be shaped together — splitting an <c>é</c> written as <c>e</c>
    ///         plus a combining acute across two faces puts the accent at the pen position of a
    ///         different font, which is a floating accent rather than a missing glyph. A cluster goes
    ///         to the first face that covers <i>all</i> of it for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A cluster nothing covers goes to the head of the chain</b>, which draws its
    ///         <c>.notdef</c>. Sending it to the last face instead would put the tofu in whichever
    ///         font happened to be registered last, so a missing glyph would change appearance for
    ///         reasons unrelated to the text.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Adjacent clusters on the same face are merged, and that is not only tidiness.</b>
    ///         Each span is shaped on its own, so a boundary is a place where kerning, ligatures and
    ///         Arabic joining all stop — see <c>TextShaper</c>'s remarks on run context. Splitting a
    ///         word that one face covers into two spans would silently unjoin it.
    ///     </para>
    /// </remarks>
    public static void Cover(string text, IReadOnlyList<FontFace> chain, List<FontSpan> into) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (text.Length == 0 || chain.Count == 0) {
            return;
        }

        // The overwhelmingly common case, and worth its own path: one face draws the whole string,
        // so nothing is broken into clusters and no substring is taken. Every label in a Latin
        // interface lands here.
        if (chain.Count == 1 || Covers(chain[0], text, 0, text.Length)) {
            into.Add(new FontSpan(chain[0], 0, text.Length));
            return;
        }

        var boundaries = new List<int>();
        GraphemeBreaker.Collect(text, boundaries);

        for (var i = 0; i + 1 < boundaries.Count; i++) {
            var start = boundaries[i];
            var length = boundaries[i + 1] - start;
            var font = chain[0];

            foreach (var candidate in chain) {
                if (Covers(candidate, text, start, length)) {
                    font = candidate;
                    break;
                }
            }

            if (into.Count > 0 && ReferenceEquals(into[^1].Font, font)) {
                into[^1] = into[^1] with { Length = into[^1].Length + length };
            } else {
                into.Add(new FontSpan(font, start, length));
            }
        }
    }

    /// <summary>Whether one face can draw every code point of a stretch.</summary>
    /// <remarks>
    ///     ⚠ <b>A surrogate pair is one code point.</b> Reading a string a <c>char</c> at a time asks
    ///     the font about two halves of an emoji, neither of which any font has — so every
    ///     astral character would fall to the end of the chain and be drawn by whatever is there.
    /// </remarks>
    static bool Covers(FontFace font, string text, int start, int length) {
        var end = start + length;

        for (var i = start; i < end;) {
            if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) {
                if (!font.Supports(char.ConvertToUtf32(text[i], text[i + 1]))) {
                    return false;
                }

                i += 2;
                continue;
            }

            if (!font.Supports(text[i])) {
                return false;
            }

            i++;
        }

        return true;
    }

    static void Append(List<FontFace> into, FontFace face) {
        if (!into.Contains(face)) {
            into.Add(face);
        }
    }

    /// <summary>The variants registered under a family name.</summary>
    /// <param name="family">The name.</param>
    /// <returns>Its variants, or an empty list.</returns>
    public IReadOnlyList<FontVariant> Variants(string family) {
        ArgumentNullException.ThrowIfNull(family);
        return families.TryGetValue(family, out var variants) ? variants : [];
    }

    /// <summary>Picks the face of one family that best answers a weight and a slant.</summary>
    /// <remarks>
    ///     The slant is settled first and the weight within it, which is CSS's order: an italic at
    ///     the wrong weight is a better answer to <c>italic</c> than an upright at the right one.
    /// </remarks>
    static FontFace? Best(List<FontVariant> variants, int weight, FontStyle style) {
        if (variants.Count == 0) {
            return null;
        }

        var slanted = Slanted(variants, style);
        return Weighted(slanted, weight);
    }

    /// <summary>Narrows a family to the faces whose slant is the closest available to the one asked for.</summary>
    /// <remarks>
    ///     <c>italic</c> takes an italic, then an oblique, then an upright: a synthesised slant is
    ///     not on offer here, so an upright is the honest last resort. <c>normal</c> takes an upright
    ///     and would rather have any of the others than nothing.
    /// </remarks>
    static List<FontVariant> Slanted(List<FontVariant> variants, FontStyle style) {
        ReadOnlySpan<FontStyle> order = style switch {
            FontStyle.Italic => [FontStyle.Italic, FontStyle.Oblique, FontStyle.Normal],
            FontStyle.Oblique => [FontStyle.Oblique, FontStyle.Italic, FontStyle.Normal],
            _ => [FontStyle.Normal, FontStyle.Oblique, FontStyle.Italic]
        };

        foreach (var wanted in order) {
            var matching = variants.FindAll(variant => variant.Style == wanted);
            if (matching.Count > 0) {
                return matching;
            }
        }

        return variants;
    }

    /// <summary>Picks a weight out of one slant, by CSS's matching rules.</summary>
    /// <remarks>
    ///     <para>
    ///         Not nearest-neighbour, which is what everybody writes first and is wrong in the middle
    ///         of the scale. CSS Fonts 4 §5.2: an exact match wins; below 400 the search runs
    ///         downwards first and then upwards; above 500 it runs upwards first and then downwards;
    ///         and 400 and 500 check each other before either falls back.
    ///     </para>
    ///     <para>
    ///         The 400/500 rule is the part worth stating. Asking for 400 of a family that has 500
    ///         and 300 gives the <i>500</i> — heavier than asked, where the nearest is a tie and the
    ///         obvious implementation would pick whichever came first in the list.
    ///     </para>
    /// </remarks>
    static FontFace? Weighted(List<FontVariant> variants, int weight) {
        if (Exact(variants, weight) is { } exact) {
            return exact;
        }

        // ⚠ The one asymmetry: 400 checks 500 — a weight *above* it — before falling back to the
        // downward search. 500's mirror of this rule is in the specification too, and is left out
        // because the downward search below already reaches 400 first from 500.
        if (weight == RegularWeight && Exact(variants, 500) is { } medium) {
            return medium;
        }

        return weight <= 500
            ? Nearest(variants, weight, false) ?? Nearest(variants, weight, true)
            : Nearest(variants, weight, true) ?? Nearest(variants, weight, false);
    }

    static FontFace? Exact(List<FontVariant> variants, int weight) {
        foreach (var variant in variants) {
            if (variant.Weight == weight) {
                return variant.Font;
            }
        }

        return null;
    }

    /// <summary>The nearest face on one side of a weight.</summary>
    /// <param name="variants">The faces to search.</param>
    /// <param name="weight">The weight wanted.</param>
    /// <param name="above">Whether to search weights at or above it, or at or below it.</param>
    /// <returns>The face, or null if that side is empty.</returns>
    static FontFace? Nearest(List<FontVariant> variants, int weight, bool above) {
        FontFace? found = null;
        var best = 0;

        foreach (var variant in variants) {
            if (above ? variant.Weight < weight : variant.Weight > weight) {
                continue;
            }

            if (found is null || (above ? variant.Weight < best : variant.Weight > best)) {
                best = variant.Weight;
                found = variant.Font;
            }
        }

        return found;
    }
}
