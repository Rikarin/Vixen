// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>One <c>property: value</c> a utility emits.</summary>
/// <param name="Property">The CSS property.</param>
/// <param name="Value">Its value.</param>
public readonly record struct UtilityDeclaration(string Property, string Value);

/// <summary>How a utility turns its value into declarations.</summary>
enum ValueKind : byte {
    /// <summary>No value at all: <c>flex</c>, <c>truncate</c>.</summary>
    Static,

    /// <summary>A multiple of the spacing unit: <c>p-4</c>.</summary>
    Spacing,

    /// <summary>A colour token: <c>bg-accent</c>.</summary>
    Color,

    /// <summary>A bare number: <c>grow-0</c>, <c>z-10</c>.</summary>
    Number,

    /// <summary>One of a fixed set of keywords: <c>items-center</c>.</summary>
    Keyword,

    /// <summary>A length that may also be a fraction or a keyword: <c>w-1/2</c>, <c>w-full</c>.</summary>
    Size,

    /// <summary>A radius token: <c>rounded-md</c>.</summary>
    Radius,

    /// <summary>A font size token, which also sets a line height: <c>text-lg</c>.</summary>
    FontSize,

    /// <summary>A font weight token: <c>font-semibold</c>.</summary>
    FontWeight,

    /// <summary>A duration in milliseconds: <c>duration-200</c>.</summary>
    Duration,

    /// <summary>A percentage written as a whole number: <c>opacity-50</c> is <c>0.5</c>.</summary>
    Fraction
}

/// <summary>The utilities a class name can name, and what each one emits.</summary>
/// <remarks>
///     <para>
///         Table-driven, because the interesting part of a utility system is not any individual
///         utility — it is that adding one is a line of data rather than a branch. The families are
///         the set [doc 09](../../docs/plan/09-ui-framework.md) names for 1.0: what an editor
///         actually needs, which is a good deal less than what Tailwind ships.
///     </para>
///     <para>
///         <b>Two utilities genuinely collide and the collision is resolved by the token tables.</b>
///         <c>text-</c> is font size, colour, and alignment: <c>text-lg</c>, <c>text-accent</c> and
///         <c>text-center</c> are three different properties behind one prefix. So <c>text-</c>
///         resolves in order — keyword, then font-size token, then colour — and the consequence is
///         worth knowing before someone hits it: a colour named <c>center</c> would be unreachable
///         through <c>text-</c>. The same applies to <c>border-</c>, which is width or colour.
///     </para>
/// </remarks>
public static class UtilityFamilies {
    sealed record Family(string Name, ValueKind Kind, string[] Properties, Dictionary<string, string>? Keywords = null);

    static readonly Dictionary<string, Family> Registry = new(StringComparer.Ordinal);
    static readonly List<string> Names = [];

    static UtilityFamilies() {
        // ── Layout ──────────────────────────────────────────────────────────────────────────
        Static("block", "display", "block");
        Static("inline", "display", "inline");
        Static("inline-block", "display", "inline-block");
        Static("flex", "display", "flex");
        Static("inline-flex", "display", "inline-flex");
        Static("grid", "display", "grid");
        Static("hidden", "display", "none");
        Static("truncate", "overflow", "hidden");

        // `flex-wrap` and `flex-col` are both values of `flex`, and they set different properties.
        // Registering `flex-wrap` as a family of its own would make the class `flex-wrap` a family
        // with no value rather than the family `flex` with the value `wrap`.
        Keywords("flex", "flex-direction", new() {
            ["row"] = "row", ["row-reverse"] = "row-reverse",
            ["col"] = "column", ["col-reverse"] = "column-reverse"
        });

        Keywords("flex", "flex-wrap", new() {
            ["wrap"] = "wrap", ["wrap-reverse"] = "wrap-reverse", ["nowrap"] = "nowrap"
        });

        Keywords("items", "align-items", new() {
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["baseline"] = "baseline", ["stretch"] = "stretch"
        });

        Keywords("self", "align-self", new() {
            ["auto"] = "auto", ["start"] = "flex-start", ["end"] = "flex-end",
            ["center"] = "center", ["baseline"] = "baseline", ["stretch"] = "stretch"
        });

        Keywords("justify", "justify-content", new() {
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["between"] = "space-between", ["around"] = "space-around", ["evenly"] = "space-evenly"
        });

        Keywords("content", "align-content", new() {
            ["start"] = "flex-start", ["end"] = "flex-end", ["center"] = "center",
            ["between"] = "space-between", ["around"] = "space-around", ["stretch"] = "stretch"
        });

        // ── Flex and grid ───────────────────────────────────────────────────────────────────
        Number("grow", "flex-grow");
        Number("shrink", "flex-shrink");
        Number("order", "order");
        Size("basis", "flex-basis");
        Number("grid-cols", "grid-template-columns");
        Number("col-span", "grid-column");

        // ── Gap and spacing ─────────────────────────────────────────────────────────────────
        Spacing("gap", "gap");
        Spacing("gap-x", "column-gap");
        Spacing("gap-y", "row-gap");

        Spacing("p", "padding");
        Spacing("px", "padding-left", "padding-right");
        Spacing("py", "padding-top", "padding-bottom");
        Spacing("pt", "padding-top");
        Spacing("pr", "padding-right");
        Spacing("pb", "padding-bottom");
        Spacing("pl", "padding-left");

        Spacing("m", "margin");
        Spacing("mx", "margin-left", "margin-right");
        Spacing("my", "margin-top", "margin-bottom");
        Spacing("mt", "margin-top");
        Spacing("mr", "margin-right");
        Spacing("mb", "margin-bottom");
        Spacing("ml", "margin-left");

        // ── Sizing ──────────────────────────────────────────────────────────────────────────
        Size("w", "width");
        Size("h", "height");
        Size("size", "width", "height");
        Size("min-w", "min-width");
        Size("min-h", "min-height");
        Size("max-w", "max-width");
        Size("max-h", "max-height");

        // ── Position ────────────────────────────────────────────────────────────────────────
        Static("static", "position", "static");
        Static("relative", "position", "relative");
        Static("absolute", "position", "absolute");
        Size("inset", "top", "right", "bottom", "left");
        Size("inset-x", "left", "right");
        Size("inset-y", "top", "bottom");
        Size("top", "top");
        Size("right", "right");
        Size("bottom", "bottom");
        Size("left", "left");
        Number("z", "z-index");

        // ── Typography ──────────────────────────────────────────────────────────────────────
        Register(new Family("text", ValueKind.FontSize, ["font-size"], new Dictionary<string, string>(StringComparer.Ordinal) {
            ["left"] = "text-align:left", ["center"] = "text-align:center",
            ["right"] = "text-align:right", ["justify"] = "text-align:justify"
        }));

        Register(new Family("font", ValueKind.FontWeight, ["font-weight"]));
        Spacing("leading", "line-height");
        Spacing("tracking", "letter-spacing");

        Keywords("whitespace", "white-space", new() {
            ["normal"] = "normal", ["nowrap"] = "nowrap", ["pre"] = "pre", ["pre-wrap"] = "pre-wrap"
        });

        Keywords("align", "vertical-align", new() {
            ["top"] = "top", ["middle"] = "middle", ["bottom"] = "bottom", ["baseline"] = "baseline"
        });

        // ── Colours ─────────────────────────────────────────────────────────────────────────
        Color("bg", "background-color");
        Color("ring", "outline-color");
        Color("fill", "fill");
        Color("stroke", "stroke");

        // ── Borders ─────────────────────────────────────────────────────────────────────────
        Register(new Family("border", ValueKind.Color, ["border-color"]));
        Register(new Family("rounded", ValueKind.Radius, ["border-radius"]));

        // ── Effects ─────────────────────────────────────────────────────────────────────────
        // `opacity-50` is half, not fifty. CSS's `opacity` runs 0 to 1 and the utility scale runs
        // 0 to 100, because nobody writes `opacity-0.5`.
        Register(new Family("opacity", ValueKind.Fraction, ["opacity"]));
        Spacing("blur", "--blur");

        // ── Transforms ──────────────────────────────────────────────────────────────────────
        Size("translate-x", "--translate-x");
        Size("translate-y", "--translate-y");
        Number("scale", "--scale");
        Number("rotate", "--rotate");

        // ── Transitions ─────────────────────────────────────────────────────────────────────
        Register(new Family("transition", ValueKind.Static, ["transition-property"], new Dictionary<string, string>(StringComparer.Ordinal) {
            [string.Empty] = "all"
        }));

        Register(new Family("duration", ValueKind.Duration, ["transition-duration"]));

        Keywords("ease", "transition-timing-function", new() {
            ["linear"] = "linear", ["in"] = "ease-in", ["out"] = "ease-out", ["in-out"] = "ease-in-out"
        });

        // ── Interactivity ───────────────────────────────────────────────────────────────────
        Keywords("cursor", "cursor", new() {
            ["auto"] = "auto", ["default"] = "default", ["pointer"] = "pointer", ["text"] = "text",
            ["move"] = "move", ["not-allowed"] = "not-allowed", ["grab"] = "grab",
            ["col-resize"] = "col-resize", ["row-resize"] = "row-resize"
        });

        Keywords("select", "user-select", new() {
            ["none"] = "none", ["text"] = "text", ["all"] = "all", ["auto"] = "auto"
        });

        Keywords("pointer-events", "pointer-events", new() { ["none"] = "none", ["auto"] = "auto" });

        Keywords("overflow", "overflow", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        Keywords("overflow-x", "overflow-x", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        Keywords("overflow-y", "overflow-y", new() {
            ["auto"] = "auto", ["hidden"] = "hidden", ["visible"] = "visible", ["scroll"] = "scroll"
        });

        Register(new Family("aspect", ValueKind.Number, ["aspect-ratio"]));

        // Longest first, so `min-w` wins over nothing and `flex-wrap` over `flex`.
        Names.Sort(static (left, right) => right.Length.CompareTo(left.Length));
    }

    /// <summary>Splits a utility into the longest registered family name and the rest.</summary>
    /// <param name="whole">The utility text, without variants or suffixes.</param>
    /// <returns>The family name and its value.</returns>
    /// <remarks>
    ///     Longest prefix rather than first hyphen, because <c>p</c> and <c>pointer-events</c> both
    ///     exist and a first-hyphen split would read the second as the family <c>pointer</c>. The
    ///     hyphen after the name has to be there, or <c>p</c> would claim <c>pointer-events</c>.
    /// </remarks>
    public static (string Name, string Value) SplitName(string whole) {
        ArgumentNullException.ThrowIfNull(whole);

        foreach (var name in Names) {
            if (whole.Equals(name, StringComparison.Ordinal)) {
                return (name, string.Empty);
            }

            if (whole.Length > name.Length
                && whole.StartsWith(name, StringComparison.Ordinal)
                && whole[name.Length] == '-') {
                return (name, whole[(name.Length + 1)..]);
            }
        }

        return (whole, string.Empty);
    }

    /// <summary>Turns a parsed candidate into the declarations it stands for.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="tokens">The theme.</param>
    /// <param name="declarations">Receives the declarations.</param>
    /// <returns>Whether it is a utility this system knows.</returns>
    public static bool TryResolve(UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(declarations);

        declarations.Clear();

        if (!Registry.TryGetValue(candidate.Name, out var family)) {
            return false;
        }

        // An arbitrary value goes straight through. That is the point of it: `w-[37px]` exists
        // precisely for the case the token scale does not cover, and second-guessing it would make
        // the escape hatch useless.
        if (candidate.Arbitrary is { } arbitrary) {
            return Emit(family, arbitrary, declarations);
        }

        // Keywords first, because `text-center` has to beat any colour or size named `center`.
        if (family.Keywords is not null && family.Keywords.TryGetValue(candidate.Value, out var keyword)) {
            return keyword.Contains(':', StringComparison.Ordinal)
                ? EmitPair(keyword, declarations)
                : Emit(family, keyword, declarations);
        }

        // `border` and `rounded` on their own mean a default width and a default radius — CSS's own
        // ambiguity rather than one invented here, and handled apart from the table so that the
        // table stays one entry per family.
        if (candidate.Value.Length == 0 && TryBareForm(candidate, declarations)) {
            return true;
        }

        return family.Kind switch {
            // A family with no value reaches its declaration through the keyword table's empty key,
            // which the branch above has already tried. Getting here means a value was given to a
            // utility that does not take one.
            ValueKind.Static => false,
            ValueKind.Spacing => TrySpacing(candidate.Value, tokens, out var spacing) && Emit(family, spacing, declarations),
            ValueKind.Size => TrySize(candidate, tokens, out var size) && Emit(family, size, declarations),
            ValueKind.Number => TryNumber(candidate.Value, out var number) && Emit(family, number, declarations),
            ValueKind.Duration => TryNumber(candidate.Value, out var ms) && Emit(family, ms + "ms", declarations),
            ValueKind.Fraction => TryFraction(candidate.Value, out var fraction) && Emit(family, fraction, declarations),
            ValueKind.Radius => TryRadius(candidate.Value, tokens, out var radius) && Emit(family, radius, declarations),
            ValueKind.FontWeight => TryFontWeight(candidate.Value, tokens, out var weight) && Emit(family, weight, declarations),
            ValueKind.FontSize => TryFontSizeOrColor(candidate, tokens, declarations),
            ValueKind.Color => TryColor(candidate, tokens, out var colour) && Emit(family, colour, declarations),
            _ => false
        };
    }

    /// <summary>The bare form of a family that also takes a value.</summary>
    /// <remarks>
    ///     <c>border</c> on its own means a one-pixel border and <c>border-accent</c> means a colour,
    ///     which is CSS's own ambiguity rather than one this system invented. Handled here so the
    ///     table stays one entry per family.
    /// </remarks>
    static bool TryBareForm(UtilityCandidate candidate, List<UtilityDeclaration> declarations) {
        if (candidate.Value.Length != 0) {
            return false;
        }

        switch (candidate.Name) {
            case "grow":
                declarations.Add(new UtilityDeclaration("flex-grow", "1"));
                return true;

            case "shrink":
                declarations.Add(new UtilityDeclaration("flex-shrink", "1"));
                return true;

            case "border":
                declarations.Add(new UtilityDeclaration("border-width", "1px"));
                return true;

            case "rounded":
                declarations.Add(new UtilityDeclaration("border-radius", "4px"));
                return true;

            default:
                return false;
        }
    }

    static bool TryFontSizeOrColor(UtilityCandidate candidate, ThemeTokens tokens, List<UtilityDeclaration> declarations) {
        // The documented resolution order for `text-`: keyword (already tried), then font size,
        // then colour. A colour named `lg` would be unreachable, which is the price of one prefix
        // meaning three things and is worth paying — `text-lg` and `text-accent` both read right.
        if (tokens.FontSize.TryGetValue(candidate.Value, out var size)) {
            declarations.Add(new UtilityDeclaration("font-size", Px(size.Size)));
            declarations.Add(new UtilityDeclaration("line-height", Px(size.LineHeight)));
            return true;
        }

        if (!TryColor(candidate, tokens, out var colour)) {
            return false;
        }

        declarations.Add(new UtilityDeclaration("color", colour));
        return true;
    }

    static bool TryColor(UtilityCandidate candidate, ThemeTokens tokens, out string value) {
        value = string.Empty;

        if (candidate.Value.Length == 0) {
            return false;
        }

        if (!tokens.TryGetColor(candidate.Value, out var colour)) {
            return false;
        }

        if (candidate.Opacity is not { } opacity) {
            value = colour;
            return true;
        }

        // `#rrggbb` plus an opacity becomes `rgba(...)`, because CSS has no way to say "this colour
        // but at half alpha" without rewriting it. Anything not a hex triple is passed through with
        // the opacity dropped rather than mangled — with a note, since it is a real limitation.
        if (!Vixen.Core.Mathematics.Color.TryParseHex(colour, out var parsed)) {
            value = colour;
            return true;
        }

        value = string.Create(
            CultureInfo.InvariantCulture,
            $"rgba({parsed.R}, {parsed.G}, {parsed.B}, {(opacity * parsed.A / 255f).ToString("0.###", CultureInfo.InvariantCulture)})"
        );

        return true;
    }

    static bool TrySpacing(string value, ThemeTokens tokens, out string result) {
        result = string.Empty;

        if (value.Length == 0) {
            return false;
        }

        if (value.Equals("px", StringComparison.Ordinal)) {
            result = "1px";
            return true;
        }

        if (value.Equals("auto", StringComparison.Ordinal)) {
            result = "auto";
            return true;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var steps)) {
            return false;
        }

        result = Px(steps * tokens.SpacingBase);
        return true;
    }

    static bool TrySize(UtilityCandidate candidate, ThemeTokens tokens, out string result) {
        result = string.Empty;

        switch (candidate.Value) {
            case "full":
                result = "100%";
                return true;
            case "screen":
                result = "100%";
                return true;
            case "auto":
                result = "auto";
                return true;
            case "min":
                result = "min-content";
                return true;
            case "max":
                result = "max-content";
                return true;
            case "fit":
                result = "fit-content";
                return true;
            default:
                break;
        }

        // `w-1/2` — the slash is a fraction here and not an opacity, which is why the suffix is kept
        // as written as well as read as one.
        if (candidate.SlashSuffix is { } denominator
            && float.TryParse(candidate.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && float.TryParse(denominator, NumberStyles.Float, CultureInfo.InvariantCulture, out var divisor)
            && divisor != 0f) {
            result = (numerator / divisor * 100f).ToString("0.####", CultureInfo.InvariantCulture) + "%";
            return true;
        }

        return TrySpacing(candidate.Value, tokens, out result);
    }

    static bool TryRadius(string value, ThemeTokens tokens, out string result) {
        if (tokens.Radius.TryGetValue(value, out var radius)) {
            result = Px(radius);
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryFontWeight(string value, ThemeTokens tokens, out string result) {
        if (tokens.FontWeight.TryGetValue(value, out var weight)) {
            result = weight.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryFraction(string value, out string result) {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) {
            result = (percent / 100f).ToString("0.####", CultureInfo.InvariantCulture);
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool TryNumber(string value, out string result) {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            result = number.ToString("0.####", CultureInfo.InvariantCulture);
            return true;
        }

        result = string.Empty;
        return false;
    }

    static bool Emit(Family family, string value, List<UtilityDeclaration> declarations) {
        foreach (var property in family.Properties) {
            declarations.Add(new UtilityDeclaration(property, value));
        }

        return true;
    }

    static bool EmitPair(string pair, List<UtilityDeclaration> declarations) {
        var colon = pair.IndexOf(':', StringComparison.Ordinal);
        declarations.Add(new UtilityDeclaration(pair[..colon], pair[(colon + 1)..]));
        return true;
    }

    static string Px(float value) => value.ToString("0.####", CultureInfo.InvariantCulture) + "px";

    static void Register(Family family) {
        // A family registered twice keeps the first, so that `flex` as a display utility is not
        // replaced by `flex` as a direction one — they are the same prefix and different values,
        // which the keyword table is what resolves.
        if (Registry.TryAdd(family.Name, family)) {
            Names.Add(family.Name);
            return;
        }

        var existing = Registry[family.Name];
        if (family.Keywords is null) {
            return;
        }

        var merged = existing.Keywords is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing.Keywords, StringComparer.Ordinal);

        foreach (var (key, value) in family.Keywords) {
            merged[key] = value.Contains(':', StringComparison.Ordinal) ? value : $"{family.Properties[0]}:{value}";
        }

        Registry[family.Name] = existing with { Keywords = merged };
    }

    static void Static(string name, string property, string value) =>
        Register(new Family(name, ValueKind.Static, [property], new Dictionary<string, string>(StringComparer.Ordinal) {
            [string.Empty] = $"{property}:{value}"
        }));

    static void Keywords(string name, string property, Dictionary<string, string> keywords) {
        var qualified = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in keywords) {
            qualified[key] = $"{property}:{value}";
        }

        Register(new Family(name, ValueKind.Keyword, [property], qualified));
    }

    static void Spacing(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Spacing, properties));

    static void Size(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Size, properties));

    static void Number(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Number, properties));

    static void Color(string name, params string[] properties) =>
        Register(new Family(name, ValueKind.Color, properties));
}
