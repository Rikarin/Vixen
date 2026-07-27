// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;

namespace Vixen.Ui;

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
///         ⚠ <b>Family names are matched exactly and case-insensitively, and nothing else about a
///         family is understood.</b> CSS's <c>font-family</c> chooses between faces of one family by
///         <c>font-weight</c>, <c>font-style</c> and <c>font-stretch</c>, and the matching algorithm
///         for that is a page of the specification. Here a name is a face. Registering
///         <c>"Inter Bold"</c> and asking for it works; registering <c>"Inter"</c> and asking for
///         <c>font-weight: bold</c> gets the regular one with nothing said. Owed, and said plainly
///         rather than half-implemented — a weight that is silently ignored is worse than one that
///         is documented as not there.
///     </para>
///     <para>
///         ⚠ This is not <b>font fallback</b>, which is the other thing the word means. The list in
///         a declaration is tried in order until a <i>registered</i> family is found; it is not tried
///         per character until one that has a glyph is found. A registered font missing the code
///         point draws <c>.notdef</c>. Per-glyph fallback is owed with the rest of the text work.
///     </para>
/// </remarks>
public sealed class FontRegistry {
    readonly Dictionary<string, FontFace> families = new(StringComparer.OrdinalIgnoreCase);

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
    /// <remarks>The first face registered also becomes <see cref="Default" />.</remarks>
    public void Register(string family, FontFace font) {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(font);

        families[family] = font;
        Default ??= font;
    }

    /// <summary>The face a <c>font-family</c> declaration resolves to.</summary>
    /// <param name="declaration">
    ///     The declaration's value — a comma-separated list, most wanted first, with quotes allowed
    ///     around a name that needs them.
    /// </param>
    /// <returns>The first registered family in the list, or <see cref="Default" />.</returns>
    public FontFace? Resolve(string? declaration) {
        if (string.IsNullOrWhiteSpace(declaration)) {
            return Default;
        }

        foreach (var range in declaration.AsSpan().Split(',')) {
            // The quotes are part of the CSS syntax rather than of the name — `"Noto Sans"` and
            // `Noto Sans` are the same family, and a registry keyed on the quoted form would match
            // whichever way the stylesheet happened to be written.
            var name = declaration.AsSpan(range).Trim().Trim("\"'");

            if (!name.IsEmpty && families.TryGetValue(name.ToString(), out var font)) {
                return font;
            }
        }

        return Default;
    }
}
