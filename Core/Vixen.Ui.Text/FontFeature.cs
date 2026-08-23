// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Ui.Text;

/// <summary>One OpenType feature, switched on or off for a whole run.</summary>
/// <remarks>
///     <para>
///         CSS Fonts 4 gives two ways to ask for one and they arrive here as the same thing.
///         <c>font-feature-settings: "tnum" 1</c> names the tag directly; <c>font-variant-numeric:
///         tabular-nums</c> names a typographic intention that <i>is</i> that tag. So the high-level
///         property is a table of these and the low-level one is a parser for them, and the shaper
///         below never learns which of the two a feature came from.
///     </para>
///     <para>
///         ⚠ <b>No range.</b> CSS and OpenType both allow a feature to be switched on over part of a
///         run — <c>"liga" 1 [3:7]</c> in HarfBuzz's own syntax — and CSS's grammar does not expose
///         it, so neither does this. A feature here applies to the whole shaped paragraph, which is
///         what every declaration a stylesheet can write means.
///     </para>
/// </remarks>
/// <param name="Tag">The four-character OpenType tag, packed big-endian as HarfBuzz packs it.</param>
/// <param name="Value">
///     What to set it to. Zero is off, one is on, and a larger number selects an alternate for the
///     features that number theirs.
/// </param>
public readonly record struct FontFeature(uint Tag, uint Value) {
    /// <summary>Packs a four-character tag.</summary>
    /// <param name="tag">The tag. Exactly four characters, each of them ASCII.</param>
    /// <returns>The packed tag, or zero if it is not one.</returns>
    /// <remarks>
    ///     Zero is <c>hb_tag_none</c> and is what an unparseable tag becomes, so a malformed
    ///     declaration drops out rather than switching on whatever four bytes it happened to spell.
    /// </remarks>
    public static uint Pack(ReadOnlySpan<char> tag) {
        if (tag.Length != 4) {
            return 0u;
        }

        var packed = 0u;

        foreach (var character in tag) {
            if (character is < ' ' or > '~') {
                return 0u;
            }

            packed = (packed << 8) | (byte) character;
        }

        return packed;
    }

    /// <summary>Unpacks a tag back into its four characters.</summary>
    /// <param name="tag">The packed tag.</param>
    /// <returns>The four characters, or the empty string for <c>hb_tag_none</c>.</returns>
    public static string Unpack(uint tag) =>
        tag == 0u
            ? string.Empty
            : new string([(char) (tag >> 24), (char) ((tag >> 16) & 0xFF), (char) ((tag >> 8) & 0xFF), (char) (tag & 0xFF)]);

    /// <summary>Reads one <c>&lt;feature-tag-value&gt;</c>, as CSS Fonts 4 § 6.4 spells it.</summary>
    /// <param name="text">
    ///     The value: a quoted four-character tag, optionally followed by <c>on</c>, <c>off</c> or an
    ///     integer. <c>"tnum"</c>, <c>"tnum" 1</c>, <c>'liga' off</c>.
    /// </param>
    /// <param name="feature">The feature, when it parses.</param>
    /// <returns>Whether it did.</returns>
    /// <remarks>
    ///     ⚠ <b>The quotes are required and an unquoted tag is refused</b>, which is CSS's grammar
    ///     rather than strictness for its own sake: without them there is no way to tell the tag
    ///     <c>normal</c> — which is not four characters, but <c>calt</c> is — from the keyword that
    ///     means "no features at all".
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<char> text, out FontFeature feature) {
        feature = default;
        text = text.Trim();

        if (text.Length < 6 || (text[0] != '"' && text[0] != '\'')) {
            return false;
        }

        var quote = text[0];
        var close = text[1..].IndexOf(quote);

        if (close < 0) {
            return false;
        }

        var tag = Pack(text.Slice(1, close));

        if (tag == 0u) {
            return false;
        }

        var rest = text[(close + 2)..].Trim();

        var value = rest.Length switch {
            0 => 1u,
            _ when rest.Equals("on", StringComparison.OrdinalIgnoreCase) => 1u,
            _ when rest.Equals("off", StringComparison.OrdinalIgnoreCase) => 0u,
            _ => uint.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : uint.MaxValue
        };

        if (value == uint.MaxValue) {
            return false;
        }

        feature = new FontFeature(tag, value);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"\"{Unpack(Tag)}\" {Value}");
}

/// <summary>The OpenType features a run is shaped with, as one comparable value.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists because of <see cref="ShapingCache" />'s key, and that is the part of the
///         feature most likely to have shipped broken.</b> The cache is keyed on the font and the
///         string, because shaping is a function of those two — which stopped being true the moment a
///         feature array could differ between two callers. Without the set in the key, the first
///         paragraph shaped wins and every later one gets its glyphs: a table of tabular figures next
///         to a paragraph of proportional ones would silently share whichever was drawn first, and
///         nothing anywhere would say so.
///     </para>
///     <para>
///         ⚠ <b>Sorted and deduplicated on the way in, so two declarations that ask for the same
///         thing in a different order are one cache entry.</b> A later duplicate wins, which is CSS's
///         rule for a repeated <c>&lt;feature-tag-value&gt;</c> and is also what makes
///         <c>font-feature-settings</c> able to override <c>font-variant-numeric</c>: the high-level
///         property's features are added first and the low-level escape hatch's after them.
///     </para>
///     <para>
///         A reference type with a cached hash, and <see cref="None" /> is a singleton — so the
///         overwhelmingly common case, text with no features at all, compares by reference and hashes
///         to a constant.
///     </para>
/// </remarks>
public sealed class FontFeatureSet : IEquatable<FontFeatureSet> {
    /// <summary>No features. Shared, so the common case costs no allocation and no walk.</summary>
    public static readonly FontFeatureSet None = new([]);

    readonly int hash;

    FontFeatureSet(ImmutableArray<FontFeature> features) {
        Features = features;

        var code = new HashCode();

        foreach (var feature in features) {
            code.Add(feature.Tag);
            code.Add(feature.Value);
        }

        hash = code.ToHashCode();
    }

    /// <summary>The features, by ascending tag, one entry per tag.</summary>
    public ImmutableArray<FontFeature> Features { get; }

    /// <summary>Whether there are none, which is what almost all text asks for.</summary>
    public bool IsEmpty => Features.IsEmpty;

    /// <summary>Builds a set, sorting and deduplicating.</summary>
    /// <param name="features">The features, in the order the cascade produced them. A later duplicate wins.</param>
    /// <returns>The set, or <see cref="None" /> when there are no features.</returns>
    public static FontFeatureSet Of(ReadOnlySpan<FontFeature> features) {
        if (features.Length == 0) {
            return None;
        }

        var byTag = new SortedDictionary<uint, uint>();

        foreach (var feature in features) {
            byTag[feature.Tag] = feature.Value;
        }

        var builder = ImmutableArray.CreateBuilder<FontFeature>(byTag.Count);

        foreach (var (tag, value) in byTag) {
            builder.Add(new FontFeature(tag, value));
        }

        return new FontFeatureSet(builder.MoveToImmutable());
    }

    /// <inheritdoc />
    public bool Equals(FontFeatureSet? other) {
        if (ReferenceEquals(this, other)) {
            return true;
        }

        if (other is null || hash != other.hash || Features.Length != other.Features.Length) {
            return false;
        }

        for (var i = 0; i < Features.Length; i++) {
            if (Features[i] != other.Features[i]) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FontFeatureSet);

    /// <inheritdoc />
    public override int GetHashCode() => hash;

    /// <inheritdoc />
    public override string ToString() => Features.IsEmpty ? "none" : string.Join(", ", Features);
}
