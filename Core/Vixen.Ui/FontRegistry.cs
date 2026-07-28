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
///         ⚠ This is not <b>font fallback</b>, which is the other thing the word means. The list in
///         a declaration is tried in order until a <i>registered</i> family is found; it is not tried
///         per character until one that has a glyph is found. A registered font missing the code
///         point draws <c>.notdef</c>. Per-glyph fallback is owed with the rest of the text work.
///     </para>
/// </remarks>
public sealed class FontRegistry {
    /// <summary>The weight <c>font-weight: normal</c> names, and what a face registered without one gets.</summary>
    public const int RegularWeight = 400;

    /// <summary>The weight <c>font-weight: bold</c> names.</summary>
    public const int BoldWeight = 700;

    readonly Dictionary<string, List<FontVariant>> families = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The face used when a declaration names nothing that is registered.</summary>
    /// <remarks>
    ///     Null until something is registered, and then the first one registered. An interface whose
    ///     stylesheet has a typo in a family name draws in some font rather than not at all, which is
    ///     both what a browser does and what makes the typo findable.
    /// </remarks>
    public FontFace? Default { get; set; }

    /// <summary>How many families are registered.</summary>
    public int Count => families.Count;

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

        Default ??= font;
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
