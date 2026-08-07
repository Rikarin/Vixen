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
        // ⚠ **Everything below reaches this method as the author wrote it, `var()`s already gone.**
        // ExCSS normalises only the colour syntaxes it knows — `red` becomes `rgb(255, 0, 0)` — and
        // passes `oklch(…)` and `color-mix(…)` through untouched, including the colours *inside*
        // them: `color-mix(in srgb, red 50%, blue)` arrives with both endpoints still spelt as
        // names. Substitution then runs later still, in `StyleResolver.Substitute`, on the text and
        // before anything is parsed — so `color-mix(in oklab, var(--accent) 50%, transparent)`
        // arrives here character-for-character identical to the same mix written with the hex code
        // in place of the variable. Two consequences, and the second is the one that bites: this
        // method needs no notion of `var()` at all, and a `color-mix()` that only accepted `rgb()`
        // arguments would silently fail on every real one.
        if (name.Equals("oklab", StringComparison.OrdinalIgnoreCase)) {
            return ParseOklab(arguments, polar: false);
        }

        if (name.Equals("oklch", StringComparison.OrdinalIgnoreCase)) {
            return ParseOklab(arguments, polar: true);
        }

        if (name.Equals("color-mix", StringComparison.OrdinalIgnoreCase)) {
            return ParseColorMix(arguments);
        }

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

    /// <summary>Parses <c>oklab(L a b / α)</c> or <c>oklch(L C H / α)</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The percentage references differ per component and getting one wrong is invisible.</b>
    ///     CSS Color 4 § 9.3: lightness takes 100% as 1, but the <c>a</c>, <c>b</c> and chroma axes
    ///     take 100% as <b>0.4</b> — a number chosen because it is about the chroma of the most
    ///     saturated colours sRGB can show, not because it is a round one. Reading chroma against 1
    ///     instead makes <c>oklch(0.6 50% 260)</c> two and a half times too vivid, which reads as a
    ///     palette that is merely bolder than expected rather than as a bug.
    /// </remarks>
    static StyleValue ParseOklab(ReadOnlySpan<char> arguments, bool polar) {
        // Five, so that a five-part argument list overflows into `count == 5` and is refused rather
        // than being silently read as its first four parts.
        Span<Range> ranges = stackalloc Range[5];
        var count = Split(arguments, ranges);

        if (count is < 3 or > 4) {
            return StyleValue.Unknown;
        }

        if (!TryComponent(arguments[ranges[0]], 1f, out var lightness)
            || !TryComponent(arguments[ranges[1]], 0.4f, out var second)) {
            return StyleValue.Unknown;
        }

        float third;
        if (polar) {
            if (!TryAngle(arguments[ranges[2]], out third)) {
                return StyleValue.Unknown;
            }
        } else if (!TryComponent(arguments[ranges[2]], 0.4f, out third)) {
            return StyleValue.Unknown;
        }

        var alpha = 1f;
        if (count == 4 && !TryComponent(arguments[ranges[3]], 1f, out alpha)) {
            return StyleValue.Unknown;
        }

        alpha = Math.Clamp(alpha, 0f, 1f);

        // Lightness and chroma are clamped and the hue is not, because only the first two have a
        // meaningless side: a negative chroma is refused by CSS Color 4 § 9.3 and folded to zero, a
        // hue of -30° is 330°. ⚠ Nothing clamps the *result* into the sRGB gamut — see
        // `ColorFunctions` for why that is deliberate and whose job it is.
        return StyleValue.FromColor(
            polar
                ? ColorFunctions.FromOklch(lightness, MathF.Max(second, 0f), third, alpha)
                : ColorFunctions.FromOklab(lightness, second, third, alpha)
        );
    }

    /// <summary>Parses <c>color-mix(in &lt;space&gt;, &lt;color&gt; &lt;pct&gt;, &lt;color&gt; &lt;pct&gt;)</c>.</summary>
    /// <remarks>
    ///     The interpolation method is optional in the current CSS Color 5 grammar and defaults to
    ///     Oklab, which is also the space every mix Vixen emits names explicitly. An unrecognised one
    ///     is refused rather than defaulted — see <see cref="ColorFunctions.TrySpace" />.
    /// </remarks>
    StyleValue ParseColorMix(ReadOnlySpan<char> arguments) {
        Span<Range> ranges = stackalloc Range[4];
        var count = Split(arguments, ranges);

        var space = InterpolationSpace.Oklab;
        var hue = HueInterpolation.Shorter;
        var first = 0;

        if (count == 3) {
            if (!TryInterpolationMethod(arguments[ranges[0]].Trim(), out space, out hue)) {
                return StyleValue.Unknown;
            }

            first = 1;
        } else if (count != 2) {
            return StyleValue.Unknown;
        }

        if (!TryMixArgument(arguments[ranges[first]], out var one, out var percent1)
            || !TryMixArgument(arguments[ranges[first + 1]], out var two, out var percent2)) {
            return StyleValue.Unknown;
        }

        Span<float> weights = stackalloc float[2];
        ColorFunctions.Normalize(percent1, percent2, weights, out var alphaMultiplier);

        return StyleValue.FromColor(ColorFunctions.Mix(one, two, weights, alphaMultiplier, space, hue));
    }

    /// <summary>Reads <c>in &lt;space&gt;</c>, optionally followed by <c>&lt;method&gt; hue</c>.</summary>
    static bool TryInterpolationMethod(
        ReadOnlySpan<char> text,
        out InterpolationSpace space,
        out HueInterpolation hue
    ) {
        space = InterpolationSpace.Oklab;
        hue = HueInterpolation.Shorter;

        var parts = SplitTopLevel(text);
        if (parts.Count is not (2 or 4)) {
            return false;
        }

        if (!text[parts[0]].Equals("in", StringComparison.OrdinalIgnoreCase)
            || !ColorFunctions.TrySpace(text[parts[1]], out space)) {
            return false;
        }

        if (parts.Count == 2) {
            return true;
        }

        // ⚠ A hue-interpolation method on a rectangular space is a syntax error, not a no-op. Both
        // `in oklab shorter hue` and `in oklab longer hue` would otherwise parse and mean the same
        // thing, which would make the second look supported.
        return space == InterpolationSpace.Oklch
            && ColorFunctions.TryHue(text[parts[2]], out hue)
            && text[parts[3]].Equals("hue", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads one side of a mix: a colour, and the percentage that may sit either side of it.</summary>
    /// <remarks>
    ///     The grammar is <c>&lt;color&gt; &amp;&amp; &lt;percentage&gt;?</c>, and <c>&amp;&amp;</c>
    ///     is order-free — so <c>50% red</c> is exactly as legal as <c>red 50%</c>. Written as a
    ///     scan over the parts rather than as two positional cases so that neither order is the one
    ///     that happens to work.
    /// </remarks>
    bool TryMixArgument(ReadOnlySpan<char> text, out Color4 colour, out float? percentage) {
        colour = default;
        percentage = null;
        text = text.Trim();

        var parts = SplitTopLevel(text);
        if (parts.Count is < 1 or > 2) {
            return false;
        }

        var colourPart = new Range(0, 0);
        var sawColour = false;

        foreach (var part in parts) {
            var piece = text[part];

            if (percentage is null
                && piece.EndsWith("%", StringComparison.Ordinal)
                && float.TryParse(piece[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var written)) {
                // ⚠ Negative is invalid rather than clamped — CSS Color 5 § 3 says so explicitly,
                // and it has to, because the normalisation below divides by the sum and a negative
                // one would make a mix that brightens past both its endpoints.
                if (written < 0f) {
                    return false;
                }

                percentage = written;
                continue;
            }

            if (sawColour) {
                return false;
            }

            colourPart = part;
            sawColour = true;
        }

        if (!sawColour) {
            return false;
        }

        // Recursive, which is what makes a mix of a mix work and what makes the endpoints accept
        // every spelling the parser knows — hex, a name, `rgb()`, `oklch()`. See the note on
        // `ParseFunction` for why the second of those is not hypothetical.
        var value = ParseOne(text[colourPart]);
        if (value.Kind != StyleValueKind.Color) {
            return false;
        }

        colour = value.Color;
        return true;
    }

    /// <summary>Reads one colour component: a number, a percentage against a reference, or <c>none</c>.</summary>
    static bool TryComponent(ReadOnlySpan<char> text, float reference, out float value) {
        text = text.Trim();
        value = 0f;

        // A missing component. Vixen has no missing-component model — the concept only earns its
        // keep when interpolating two colours that are missing the same one, and carrying it through
        // every value would cost more than it pays — so `none` takes the value CSS Color 4 § 4.4
        // says a missing component behaves as everywhere else: zero.
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var percent = text.EndsWith("%", StringComparison.Ordinal);
        if (percent) {
            text = text[..^1];
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return false;
        }

        value = percent ? number / 100f * reference : number;
        return true;
    }

    /// <summary>Reads a hue: a bare number of degrees, or an angle with a unit.</summary>
    static bool TryAngle(ReadOnlySpan<char> text, out float degrees) {
        text = text.Trim();
        degrees = 0f;

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+')) {
            end++;
        }

        if (!float.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return false;
        }

        // ⚠ A bare number is degrees. `<hue>` is `<number> | <angle>` — which is why Tailwind's
        // whole palette, `oklch(62.3% 0.214 259.815)`, carries no unit on the third component and
        // why a parser that demanded one would reject every colour v4 ships.
        var suffix = text[end..];

        degrees = suffix switch {
            _ when suffix.IsEmpty || suffix.Equals("deg", StringComparison.OrdinalIgnoreCase) => number,
            _ when suffix.Equals("grad", StringComparison.OrdinalIgnoreCase) => number * 0.9f,
            _ when suffix.Equals("rad", StringComparison.OrdinalIgnoreCase) => number * (180f / MathF.PI),
            _ when suffix.Equals("turn", StringComparison.OrdinalIgnoreCase) => number * 360f,
            _ => float.NaN
        };

        return !float.IsNaN(degrees);
    }

    static StyleValue ParseNumeric(ReadOnlySpan<char> text) {
        var end = 0;

        while (end < text.Length) {
            if (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+') {
                end++;
                continue;
            }

            // ⚠ `e` belongs to the number only when a number follows it. CSS has a unit that begins
            // with the exponent character, so scanning `e` unconditionally makes `2em` scan as the
            // number `2e` — which does not parse, so the whole declaration comes back Unknown and
            // the element silently keeps its initial value. `1e2px` still works, which is the point
            // of testing rather than just dropping the exponent.
            if (text[end] is 'e' or 'E' && HasExponentDigits(text[(end + 1)..])) {
                end++;
                continue;
            }

            break;
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

            // Relative units are recognised and carried unresolved. `rem` is tested before `em`
            // because the second is a suffix of the first, and an ordinary switch on strings would
            // hide that — here the order is the correctness argument, so it is worth seeing.
            _ when suffix.Equals("rem", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.Rem),
            _ when suffix.Equals("em", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.Em),
            _ when suffix.Equals("vw", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.ViewportWidth),
            _ when suffix.Equals("vh", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.ViewportHeight),
            _ when suffix.Equals("vmin", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.ViewportMin),
            _ when suffix.Equals("vmax", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.ViewportMax),

            _ => StyleValue.Unknown
        };
    }

    /// <summary>Whether what follows an <c>e</c> is an exponent rather than the rest of a unit.</summary>
    static bool HasExponentDigits(ReadOnlySpan<char> text) => text switch {
        [var digit, ..] when char.IsAsciiDigit(digit) => true,
        ['-' or '+', var digit, ..] when char.IsAsciiDigit(digit) => true,
        _ => false
    };

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
