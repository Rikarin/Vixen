// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Styling;

/// <summary>Turns a declaration's text into something that can be interpolated.</summary>
/// <remarks>
///     <para>
///         It has to accept <b>both</b> of two forms of the same value, which is the consequence of
///         ADR-009 that the spike did not reach. ExCSS normalises what it can see, and it cannot see
///         through a <c>var()</c>: <c>color: red</c> arrives already rewritten as
///         <c>rgb(255, 0, 0)</c>, while <c>color: var(--c)</c> with <c>--c: red</c> arrives as
///         <c>red</c>, substituted afterwards by Vixen. Both are the same colour and a parser that
///         handled only the first would work until someone used a custom property.
///     </para>
///     <para>
///         Results are cached by interned value id. A stylesheet says <c>rgb(255, 0, 0)</c> in forty
///         places and they all intern to one id, so the parse happens once — which matters because
///         this runs on every declaration of every animated property.
///     </para>
/// </remarks>
public sealed class StyleValueParser {
    readonly NameTable values;
    readonly NameTable keywords;
    readonly Dictionary<int, StyleValue> cache = [];

    /// <summary>Creates a parser.</summary>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public StyleValueParser(NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        this.values = values;
        this.keywords = keywords;
    }

    /// <summary>Parses an interned value.</summary>
    /// <param name="value">Its id.</param>
    /// <returns>The parsed value, or <see cref="StyleValue.Unknown" />.</returns>
    public StyleValue Parse(int value) {
        if (cache.TryGetValue(value, out var cached)) {
            return cached;
        }

        var parsed = Parse(values.NameOf(value).AsSpan());
        cache[value] = parsed;
        return parsed;
    }

    /// <summary>Parses value text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The parsed value, or <see cref="StyleValue.Unknown" />.</returns>
    public StyleValue Parse(ReadOnlySpan<char> text) {
        text = text.Trim();

        if (text.IsEmpty) {
            return StyleValue.Unknown;
        }

        var parts = SplitTopLevel(text);
        if (parts.Count == 1) {
            return ParseOne(text);
        }

        var items = new StyleValue[parts.Count];
        for (var i = 0; i < parts.Count; i++) {
            items[i] = ParseOne(text[parts[i]]);

            if (items[i].Kind == StyleValueKind.Unknown) {
                return StyleValue.Unknown;
            }
        }

        return StyleValue.FromList(items);
    }

    StyleValue ParseOne(ReadOnlySpan<char> text) {
        text = text.Trim();

        if (text.IsEmpty) {
            return StyleValue.Unknown;
        }

        if (text[0] == '#') {
            return Color.TryParseHex(text, out var hex)
                ? StyleValue.FromColor(hex.ToLinear())
                : StyleValue.Unknown;
        }

        if (text[0] is '-' or '+' or '.' || char.IsAsciiDigit(text[0])) {
            return ParseNumeric(text);
        }

        var open = text.IndexOf('(');
        if (open > 0 && text[^1] == ')') {
            return ParseFunction(text[..open].Trim(), text[(open + 1)..^1]);
        }

        return NamedColors.TryGet(text, out var named)
            ? StyleValue.FromColor(named.ToLinear())
            : StyleValue.FromKeyword(keywords.Intern(text.ToString()));
    }

    StyleValue ParseFunction(ReadOnlySpan<char> name, ReadOnlySpan<char> arguments) {
        // `rgb()` and `rgba()` are the same function in CSS Color 4, and ExCSS emits the first for
        // an opaque colour and the second when there is alpha.
        var isRgb = name.Equals("rgb", StringComparison.OrdinalIgnoreCase)
            || name.Equals("rgba", StringComparison.OrdinalIgnoreCase);

        if (!isRgb) {
            return StyleValue.Unknown;
        }

        Span<Range> ranges = stackalloc Range[4];
        var count = Split(arguments, ranges);

        if (count is < 3 or > 4) {
            return StyleValue.Unknown;
        }

        Span<float> channels = [0f, 0f, 0f, 1f];
        for (var i = 0; i < count; i++) {
            var part = arguments[ranges[i]].Trim();
            var percent = part.EndsWith("%", StringComparison.Ordinal);

            if (percent) {
                part = part[..^1];
            }

            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                return StyleValue.Unknown;
            }

            // Channels are 0-255 and alpha is 0-1, which is CSS being CSS.
            channels[i] = i < 3
                ? (percent ? number / 100f : number / 255f)
                : (percent ? number / 100f : number);
        }

        // sRGB in, linear out — everything downstream of the cascade works in linear, and doing the
        // decode once here is the difference between a correct fade and one that darkens.
        return StyleValue.FromColor(
            new Color4(
                ColorSpace.SrgbToLinear(channels[0]),
                ColorSpace.SrgbToLinear(channels[1]),
                ColorSpace.SrgbToLinear(channels[2]),
                channels[3]
            )
        );
    }

    static StyleValue ParseNumeric(ReadOnlySpan<char> text) {
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+' or 'e' or 'E')) {
            end++;
        }

        if (!float.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return StyleValue.Unknown;
        }

        var suffix = text[end..].Trim();
        if (suffix.IsEmpty) {
            return StyleValue.FromNumber(number);
        }

        return suffix switch {
            _ when suffix.Equals("px", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.Pixels),
            _ when suffix.Equals("%", StringComparison.Ordinal) =>
                StyleValue.FromLength(number, StyleUnit.Percent),
            _ when suffix.Equals("s", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.Seconds),
            _ when suffix.Equals("ms", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number / 1000f, StyleUnit.Seconds),
            _ when suffix.Equals("deg", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.Degrees),
            _ => StyleValue.Unknown
        };
    }

    /// <summary>Splits on top-level whitespace, keeping bracketed groups whole.</summary>
    static List<Range> SplitTopLevel(ReadOnlySpan<char> text) {
        var ranges = new List<Range>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++) {
            switch (text[i]) {
                case '(':
                    depth++;
                    break;

                case ')':
                    depth--;
                    break;

                case ' ' or '\t' or '\r' or '\n' when depth == 0: {
                    if (i > start) {
                        ranges.Add(new Range(start, i));
                    }

                    start = i + 1;
                    break;
                }

                default:
                    break;
            }
        }

        if (start < text.Length) {
            ranges.Add(new Range(start, text.Length));
        }

        return ranges;
    }

    /// <summary>Splits function arguments on commas, or on spaces where CSS Color 4 allows them.</summary>
    static int Split(ReadOnlySpan<char> text, Span<Range> ranges) {
        var count = 0;
        var start = 0;
        var depth = 0;
        var sawComma = text.Contains(',');

        for (var i = 0; i < text.Length && count < ranges.Length; i++) {
            var c = text[i];

            if (c == '(') {
                depth++;
                continue;
            }

            if (c == ')') {
                depth--;
                continue;
            }

            if (depth != 0) {
                continue;
            }

            // `rgb(255 0 0 / 50%)` is legal CSS Color 4 and ExCSS never emits it, but a substituted
            // custom property can carry it through verbatim.
            var separator = sawComma ? c == ',' : c is ' ' or '/';
            if (!separator) {
                continue;
            }

            if (i > start) {
                ranges[count++] = new Range(start, i);
            }

            start = i + 1;
        }

        if (start < text.Length && count < ranges.Length) {
            ranges[count++] = new Range(start, text.Length);
        }

        return count;
    }
}
