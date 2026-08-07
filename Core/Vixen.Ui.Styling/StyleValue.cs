// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Styling;

/// <summary>What kind of thing a declaration's value turned out to be.</summary>
public enum StyleValueKind : byte {
    /// <summary>Nothing usable — an unparsed value, or one this engine has no reading of.</summary>
    Unknown,

    /// <summary>A bare number: <c>flex-grow: 2</c>, <c>opacity: 0.5</c>.</summary>
    Number,

    /// <summary>A length or percentage, with its unit.</summary>
    Length,

    /// <summary>A colour.</summary>
    Color,

    /// <summary>An identifier: <c>auto</c>, <c>row</c>, <c>none</c>.</summary>
    Keyword,

    /// <summary>Several values in sequence: <c>2px 4px</c>, <c>1 1 auto</c>.</summary>
    List
}

/// <summary>The units a length can carry.</summary>
/// <remarks>
///     <para>
///         <b>Relative units are carried here, and never resolved here.</b> A value of two <c>em</c>
///         means exactly that and nothing more; turning it into pixels needs a context — the
///         element's own font size, the root's, the surface's — that does not exist at parse time,
///         and that step belongs to <c>Vixen.Ui</c>. Nothing in this assembly is allowed to
///         half-resolve one, because a consumer would then have to ask which half.
///     </para>
///     <para>
///         ⚠ This type used to leave <c>em</c> and friends out entirely, on the argument that they
///         belonged downstream. That was right about resolution and wrong about representation, and
///         <b>transitions are what settled it</b>: the animator interpolates <see cref="StyleValue" />,
///         so a unit this type cannot express is a unit that cannot animate. Leaving them out meant
///         <c>width: 2em</c> parsed as <c>Unknown</c>, and a transition on it silently did nothing —
///         no diagnostic, no exception, just a property that snaps while its neighbours ease.
///     </para>
///     <para>
///         The known limit, which is older than this change: interpolation requires both endpoints to
///         share a unit, so <c>2em → 40px</c> does not animate. CSS resolves both to pixels at
///         computed-value time and Vixen animates specified values, which was already true of
///         <c>%</c> against <c>px</c>.
///     </para>
/// </remarks>
public enum StyleUnit : byte {
    /// <summary>No unit — a bare number used where a length is expected, which CSS allows only for zero.</summary>
    None,

    /// <summary>Device-independent pixels.</summary>
    Pixels,

    /// <summary>A percentage of something the property decides.</summary>
    Percent,

    /// <summary>Seconds. Durations and delays.</summary>
    Seconds,

    /// <summary>Degrees. Angles.</summary>
    Degrees,

    /// <summary>The element's own font size — except on <c>font-size</c>, where it is the parent's.</summary>
    Em,

    /// <summary>The root element's font size, however deep the element is.</summary>
    Rem,

    /// <summary>A hundredth of the viewport's width.</summary>
    ViewportWidth,

    /// <summary>A hundredth of the viewport's height.</summary>
    ViewportHeight,

    /// <summary>A hundredth of the viewport's smaller side.</summary>
    ViewportMin,

    /// <summary>A hundredth of the viewport's larger side.</summary>
    ViewportMax
}

/// <summary>A declaration's value, parsed far enough to be interpolated.</summary>
/// <remarks>
///     <para>
///         The cascade works on interned strings, and is right to: deciding <i>which</i> declaration
///         wins needs no opinion about what <c>spring(1, 100, 10)</c> means, and keeping it that way
///         is what lets a stylesheet carry a property this engine has never heard of. Animation is
///         the point at which that stops being enough — you cannot interpolate a string — so this is
///         where the typing happens, once, on the values that are actually being animated.
///     </para>
///     <para>
///         A struct with an overlapping reading rather than a class hierarchy. There is one of these
///         per animated property per element per frame, and a <c>Number</c> that costs a heap
///         allocation to represent is a design that stops being usable at exactly the point a UI
///         gets interesting.
///     </para>
/// </remarks>
public readonly struct StyleValue : IEquatable<StyleValue> {
    readonly StyleValue[]? items;

    StyleValue(StyleValueKind kind, float number, StyleUnit unit, Color4 colour, int keyword, StyleValue[]? items) {
        Kind = kind;
        Number = number;
        Unit = unit;
        Color = colour;
        Keyword = keyword;
        this.items = items;
    }

    /// <summary>What kind of value it is.</summary>
    public StyleValueKind Kind { get; }

    /// <summary>The number, for <see cref="StyleValueKind.Number" /> and <see cref="StyleValueKind.Length" />.</summary>
    public float Number { get; }

    /// <summary>The unit, for <see cref="StyleValueKind.Length" />.</summary>
    public StyleUnit Unit { get; }

    /// <summary>The colour, for <see cref="StyleValueKind.Color" />. Linear, not sRGB-encoded.</summary>
    public Color4 Color { get; }

    /// <summary>The interned identifier, for <see cref="StyleValueKind.Keyword" />.</summary>
    public int Keyword { get; }

    /// <summary>The parts, for <see cref="StyleValueKind.List" />.</summary>
    public ReadOnlySpan<StyleValue> Items => items ?? [];

    /// <summary>The value nothing could be made of.</summary>
    public static StyleValue Unknown => default;

    /// <summary>A bare number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value.</returns>
    public static StyleValue FromNumber(float value) =>
        new(StyleValueKind.Number, value, StyleUnit.None, default, NameTable.None, null);

    /// <summary>A length.</summary>
    /// <param name="value">The magnitude.</param>
    /// <param name="unit">Its unit.</param>
    /// <returns>The value.</returns>
    public static StyleValue FromLength(float value, StyleUnit unit) =>
        new(StyleValueKind.Length, value, unit, default, NameTable.None, null);

    /// <summary>A colour.</summary>
    /// <param name="colour">The colour, linear.</param>
    /// <returns>The value.</returns>
    public static StyleValue FromColor(Color4 colour) =>
        new(StyleValueKind.Color, 0f, StyleUnit.None, colour, NameTable.None, null);

    /// <summary>An identifier.</summary>
    /// <param name="keyword">Its interned name.</param>
    /// <returns>The value.</returns>
    public static StyleValue FromKeyword(int keyword) =>
        new(StyleValueKind.Keyword, 0f, StyleUnit.None, default, keyword, null);

    /// <summary>A sequence of values.</summary>
    /// <param name="parts">The parts.</param>
    /// <returns>The value.</returns>
    public static StyleValue FromList(StyleValue[] parts) {
        ArgumentNullException.ThrowIfNull(parts);
        return new StyleValue(StyleValueKind.List, 0f, StyleUnit.None, default, NameTable.None, parts);
    }

    /// <summary>Whether two values can be interpolated between.</summary>
    /// <param name="from">One.</param>
    /// <param name="to">The other.</param>
    /// <returns>Whether a midpoint between them means anything.</returns>
    /// <remarks>
    ///     <para>
    ///         Mismatched kinds, mismatched units and keywords are all <i>discrete</i>: CSS says such
    ///         a transition flips at the halfway mark rather than not happening. So this is not
    ///         "may this transition" — every transition is allowed — but "is there a value in
    ///         between", and where there is not, the animator swaps.
    ///     </para>
    ///     <para>
    ///         Mismatched units are the interesting case: <c>width: 100px</c> to <c>width: 50%</c>
    ///         has a perfectly good midpoint in a browser, which resolves both against the containing
    ///         block first. Vixen cannot, because resolution happens after the cascade — so it says
    ///         so and flips, rather than inventing a number.
    ///     </para>
    /// </remarks>
    public static bool CanInterpolate(StyleValue from, StyleValue to) {
        // ⚠ **Zero belongs to every unit**, and this line is not a special case for tidiness. CSS
        // Values 4 says a bare `0` is a valid length, so `width: 0` and `width: 0px` are the same
        // value — and "grow from nothing" is the commonest animation there is. Without it, `from {
        // width: 0 } to { width: 100px }` has no interpolation and swaps at the halfway mark, which
        // looks like an animation that does not run rather than like a units rule.
        if (Zero(from) && to.Kind is StyleValueKind.Number or StyleValueKind.Length) {
            return true;
        }

        if (Zero(to) && from.Kind is StyleValueKind.Number or StyleValueKind.Length) {
            return true;
        }

        if (from.Kind != to.Kind) {
            return false;
        }

        return from.Kind switch {
            StyleValueKind.Number or StyleValueKind.Color => true,
            StyleValueKind.Length => from.Unit == to.Unit,
            StyleValueKind.List => from.Items.Length == to.Items.Length && EveryPartCan(from, to),
            _ => false
        };

        static bool EveryPartCan(StyleValue from, StyleValue to) {
            for (var i = 0; i < from.Items.Length; i++) {
                if (!CanInterpolate(from.Items[i], to.Items[i])) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Whether a value is a zero that any unit would accept.</summary>
    static bool Zero(StyleValue value) =>
        value.Number == 0f && value.Kind is StyleValueKind.Number or StyleValueKind.Length;

    /// <summary>Interpolates between two values.</summary>
    /// <param name="from">The value at 0.</param>
    /// <param name="to">The value at 1.</param>
    /// <param name="amount">Where between them, which may leave <c>[0, 1]</c>.</param>
    /// <returns>The interpolated value.</returns>
    /// <remarks>
    ///     <paramref name="amount" /> is deliberately not clamped. A spring overshoots, and a
    ///     <c>cubic-bezier</c> with a control point outside the unit square is legal CSS and
    ///     overshoots on purpose; clamping here would quietly flatten exactly the motion someone
    ///     wrote that curve to get.
    /// </remarks>
    public static StyleValue Lerp(StyleValue from, StyleValue to, float amount) {
        if (!CanInterpolate(from, to)) {
            // Discrete: the change happens at the halfway mark, which is what CSS specifies for
            // anything with no meaningful midpoint.
            return amount < 0.5f ? from : to;
        }

        // A bare zero takes the other end's unit before the switch below reads one off it — the
        // whole point of zero being unit-agnostic is that the *other* end decides what it means.
        if (Zero(from) && to.Kind == StyleValueKind.Length) {
            from = FromLength(0f, to.Unit);
        } else if (Zero(to) && from.Kind == StyleValueKind.Length) {
            to = FromLength(0f, from.Unit);
        }

        switch (from.Kind) {
            case StyleValueKind.Number:
                return FromNumber(MathUtil.Lerp(from.Number, to.Number, amount));

            case StyleValueKind.Length:
                return FromLength(MathUtil.Lerp(from.Number, to.Number, amount), from.Unit);

            case StyleValueKind.Color:
                // Perceptually, per doc 09. A transparent endpoint keeps the other one's hue, so
                // that fading out does not travel through black — CSS's rule, and the reason it is
                // applied here rather than in `Oklab.Lerp`, which has no way to know it is a colour
                // in a stylesheet.
                return FromColor(Oklab.Lerp(Opaque(from.Color, to.Color), Opaque(to.Color, from.Color), amount));

            case StyleValueKind.List: {
                var parts = new StyleValue[from.Items.Length];
                for (var i = 0; i < parts.Length; i++) {
                    parts[i] = Lerp(from.Items[i], to.Items[i], amount);
                }

                return FromList(parts);
            }

            default:
                return amount < 0.5f ? from : to;
        }

        static Color4 Opaque(Color4 colour, Color4 other) =>
            colour.A == 0f ? new Color4(other.R, other.G, other.B, 0f) : colour;
    }

    /// <inheritdoc />
    public bool Equals(StyleValue other) {
        if (Kind != other.Kind) {
            return false;
        }

        return Kind switch {
            StyleValueKind.Number => Number.Equals(other.Number),
            StyleValueKind.Length => Number.Equals(other.Number) && Unit == other.Unit,
            StyleValueKind.Color => Color.Equals(other.Color),
            StyleValueKind.Keyword => Keyword == other.Keyword,
            StyleValueKind.List => Items.SequenceEqual(other.Items),
            _ => true
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StyleValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Kind switch {
        StyleValueKind.Number => HashCode.Combine(Kind, Number),
        StyleValueKind.Length => HashCode.Combine(Kind, Number, Unit),
        StyleValueKind.Color => HashCode.Combine(Kind, Color),
        StyleValueKind.Keyword => HashCode.Combine(Kind, Keyword),
        StyleValueKind.List => Items.Length,
        _ => 0
    };

    /// <summary>Compares two values.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are the same value.</returns>
    public static bool operator ==(StyleValue left, StyleValue right) => left.Equals(right);

    /// <summary>Compares two values.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(StyleValue left, StyleValue right) => !left.Equals(right);

    /// <summary>Writes the value back as CSS text.</summary>
    /// <param name="names">The table keywords are interned in.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     What the animator hands back to the cascade, which still works in interned strings.
    ///     Round-tripping through text rather than threading a typed value through the whole engine
    ///     is a deliberate trade: it keeps the cascade unaware of types it does not need, and an
    ///     animated property is a handful per frame rather than the whole stylesheet.
    /// </remarks>
    public string ToCss(NameTable names) {
        ArgumentNullException.ThrowIfNull(names);

        switch (Kind) {
            case StyleValueKind.Number:
                return Format(Number);

            case StyleValueKind.Length:
                return Format(Number) + Suffix(Unit);

            case StyleValueKind.Color: {
                // ⚠ **`rgba()` cannot carry a colour this engine can hold.** Its channels are eight
                // bits of encoded sRGB, so writing one costs a clamp and a round to the byte grid.
                // For the colours a stylesheet mostly contains that is a round trip nobody can see;
                // for a colour outside sRGB — which is roughly two of every three in Tailwind v4's
                // oklch palette, see docs/plan/43-web-styling-parity.md § D4 — the clamp is not a
                // rounding but a deletion. It survives parse, resolve and draw with its linear
                // channels intact and then loses them the first time the animator hands it back.
                // `color(srgb-linear …)` is the same triple written down, so that path is exact.
                if (Color.R is < 0f or > 1f || Color.G is < 0f or > 1f || Color.B is < 0f or > 1f) {
                    // Alpha is deliberately not part of that test. A spring overshoots past 1 and
                    // `rgba()` carries that through where `color()` would clamp it — see
                    // `StyleValueParser.ParsePredefined` — so an in-gamut colour mid-overshoot
                    // stays on the branch that keeps its alpha.
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"color(srgb-linear {Exact(Color.R)} {Exact(Color.G)} {Exact(Color.B)} / {Exact(Color.A)})"
                    );
                }

                var srgb = Color.ToSrgb();
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"rgba({Channel(srgb.R)}, {Channel(srgb.G)}, {Channel(srgb.B)}, {Format(Color.A)})"
                );
            }

            case StyleValueKind.Keyword:
                return Keyword == NameTable.None ? string.Empty : names.NameOf(Keyword);

            case StyleValueKind.List: {
                var parts = new string[Items.Length];
                for (var i = 0; i < parts.Length; i++) {
                    parts[i] = Items[i].ToCss(names);
                }

                return string.Join(' ', parts);
            }

            default:
                return string.Empty;
        }

        static string Format(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        static int Channel(float value) => (int) MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

        // Four decimals is enough for a length in pixels and it is not enough here: the whole reason
        // this branch exists is that the value came back different from the way it went out. A bare
        // `ToString` is the shortest text that parses back to the same `float`, so `0.5` still reads
        // `0.5` and only a channel that genuinely needs the digits spends them.
        static string Exact(float value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The CSS spelling of a unit.</summary>
    /// <remarks>
    ///     Written once because there were two of these, and the debug one printed the enum's name —
    ///     so a failing assertion said <c>50Percent</c> where the stylesheet said <c>50%</c>. Two
    ///     tables of the same thing is how a unit gets added to one and not the other.
    /// </remarks>
    static string Suffix(StyleUnit unit) => unit switch {
        StyleUnit.Pixels => "px",
        StyleUnit.Percent => "%",
        StyleUnit.Seconds => "s",
        StyleUnit.Degrees => "deg",
        StyleUnit.Em => "em",
        StyleUnit.Rem => "rem",
        StyleUnit.ViewportWidth => "vw",
        StyleUnit.ViewportHeight => "vh",
        StyleUnit.ViewportMin => "vmin",
        StyleUnit.ViewportMax => "vmax",
        _ => string.Empty
    };

    /// <inheritdoc />
    public override string ToString() => Kind switch {
        StyleValueKind.Number => Format(Number),
        StyleValueKind.Length => Format(Number) + Suffix(Unit),
        StyleValueKind.Color => Color.ToString(),
        StyleValueKind.Keyword => "keyword " + Keyword.ToString(CultureInfo.InvariantCulture),
        StyleValueKind.List => Items.Length.ToString(CultureInfo.InvariantCulture) + " parts",
        _ => "unknown"
    };

    static string Format(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
