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
    int cachedRevision;

    /// <summary>Creates a parser.</summary>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public StyleValueParser(NameTable values, NameTable keywords)
        : this(values, keywords, null) { }

    /// <summary>Creates a parser that resolves system colours against a palette.</summary>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    /// <param name="palette">
    ///     What <c>CanvasText</c> and its fourteen siblings mean, or <c>null</c> for a private
    ///     palette holding the light defaults.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Sharing the palette is the point of passing one.</b> A parser given <c>null</c> gets a
    ///     palette nobody else can reach, so the system colours it resolves are frozen at the
    ///     defaults — correct, and never following the platform. Every parser that draws from one
    ///     document should be handed that document's <c>UiDocument.SystemColors</c>.
    /// </remarks>
    public StyleValueParser(NameTable values, NameTable keywords, SystemPalette? palette) {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        this.values = values;
        this.keywords = keywords;
        Palette = palette ?? new SystemPalette();
        cachedRevision = Palette.Revision;
    }

    /// <summary>What this parser resolves a CSS system colour keyword to.</summary>
    public SystemPalette Palette { get; }

    /// <summary>Parses an interned value.</summary>
    /// <param name="value">Its id.</param>
    /// <returns>The parsed value, or <see cref="StyleValue.Unknown" />.</returns>
    public StyleValue Parse(int value) {
        // ⚠ <b>The palette's revision is checked here rather than at the point a system colour is
        // resolved, because the cache is what would otherwise outlive the change.</b> `CanvasText`
        // parsed under the light palette and cached is the same interned id as `CanvasText` under
        // the dark one; without this the first appearance a document ever saw would be the last it
        // ever drew, and nothing would report it — the value is a perfectly good colour, just
        // yesterday's.
        if (Palette.Revision != cachedRevision) {
            cache.Clear();
            cachedRevision = Palette.Revision;
        }

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

        // ⚠ <b>A sign only starts a number when a digit or a point follows it, and reading it as one
        // unconditionally made every vendor-prefixed keyword in CSS unparseable.</b>
        // `-webkit-center` took the numeric path, failed it, and became
        // <see cref="StyleValueKind.Unknown" /> — so the declaration resolved to nothing and no
        // diagnostic said so, because an unparseable value is not a refused selector. Nothing caught
        // it because the only prefixed keywords the engine had were the three legacy `text-align`
        // spellings, and the bridge did not read those into a field either: two defects, each hiding
        // the other's symptom. An identifier may begin with `-` in CSS Syntax §4.3.11 and a great
        // many do.
        if (text[0] is '.'
            || char.IsAsciiDigit(text[0])
            || (text[0] is '-' or '+' && text.Length > 1 && (text[1] == '.' || char.IsAsciiDigit(text[1])))) {
            return ParseNumeric(text);
        }

        var open = text.IndexOf('(');
        if (open > 0 && text[^1] == ')') {
            return ParseFunction(text[..open].Trim(), text[(open + 1)..^1]);
        }

        if (NamedColors.TryGet(text, out var named)) {
            return StyleValue.FromColor(named.ToLinear());
        }

        // ⚠ <b>A system colour becomes an ordinary <see cref="StyleValueKind.Color" /> here and not a
        // kind of its own, and that decision is what makes the feature cost nothing downstream.</b>
        // Six consumers read a colour out of a `StyleValue` — the draw list, the gradient reader,
        // `StyleAccess`, the shadow reader, `color-mix` and the animator — and a kind they had all
        // had to learn would have been six switch arms, each of which silently *builds* when it is
        // missing and then never matches. Resolving it to the palette's current value instead means
        // `color-mix(in oklab, CanvasText 50%, transparent)` and a transition from `Canvas` to a hex
        // both work with no code anywhere that knows a system colour exists.
        //
        // ⚠ What it costs is that the resolution is frozen into the parse cache, which is why
        // `Parse(int)` above watches <see cref="SystemPalette.Revision" />.
        // ⚠ The carried spelling as well as the plain one — see `SystemPalette.Carrier`. Five of the
        // fifteen keywords never arrived here at all: ExCSS froze them into `rgb()` at sheet-parse
        // time, so `StyleSheetLoader` renames every one of them before the parser can see it.
        if (SystemPalette.TryParseCarried(text, out var system)) {
            return StyleValue.FromColor(Palette[system]);
        }

        return StyleValue.FromKeyword(keywords.Intern(FoldAsciiCase(text)));
    }

    /// <summary>Lowercases an identifier's ASCII letters, so one keyword is one interned id.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this a keyword written in the wrong case was silently unrecognised, in
    ///         every property, and the frame looked exactly like the declaration had not been
    ///         written.</b> CSS Values 4 § 3.1 makes a keyword ASCII case-insensitive; the intern
    ///         table is ordinal, so two spellings were two ids and a reader holding one of them
    ///         refused the other. <c>box-shadow: 0 0 0 2px currentcolor</c> in a <c>.vcss</c> painted
    ///         nothing while <c>ring-2</c> — the same keyword in the same position — painted, because
    ///         ExCSS canonicalises the author's spelling to <c>currentColor</c> and a <c>var()</c>
    ///         fallback, substituted after parsing, keeps whatever was written. Every fixture in the
    ///         tree reached the keyword through the <c>var()</c>, so nothing could see it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Folding here rather than in <see cref="NameTable.Intern" /> is the whole
    ///         decision.</b> The table handed here for keywords is <see cref="StyleEngine.Names" />,
    ///         which also holds tag and class names — and VXML's rule is that <c>Button</c> is a
    ///         component and <c>button</c> an intrinsic element, so folding the table would make one
    ///         selector out of two. Folding the call site touches only identifiers that reached the
    ///         end of <see cref="ParseOne" />, which is every keyword and no custom ident: an
    ///         <c>animation-name</c>, a <c>font-family</c> and a grid area name are all read out of
    ///         the <i>values</i> table as raw text and never arrive here at all.
    ///     </para>
    ///     <para>
    ///         ASCII only, and not <c>ToLowerInvariant</c>, because CSS folds exactly the twenty-six.
    ///         The common case — an identifier already lowercase — allocates the one string the
    ///         intern needed anyway, and the result is cached by value id besides.
    ///     </para>
    /// </remarks>
    static string FoldAsciiCase(ReadOnlySpan<char> text) {
        var first = -1;
        for (var i = 0; i < text.Length; i++) {
            if (char.IsAsciiLetterUpper(text[i])) {
                first = i;
                break;
            }
        }

        if (first < 0) {
            return text.ToString();
        }

        var folded = new char[text.Length];
        text.CopyTo(folded);

        for (var i = first; i < folded.Length; i++) {
            if (char.IsAsciiLetterUpper(folded[i])) {
                folded[i] = (char)(folded[i] + 32);
            }
        }

        return new(folded);
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

        if (name.Equals("color", StringComparison.OrdinalIgnoreCase)) {
            return ParsePredefined(arguments);
        }

        // ⚠ <b><c>calc()</c> folds here and leaves no trace, which is the decision worth arguing
        // rather than the arithmetic.</b> It could have been a <see cref="StyleValueKind" /> of its
        // own, carrying the expression for something downstream to resolve — and that is what a
        // browser does, because CSS lets an expression mix a percentage with a pixel and only the
        // used-value stage knows what the percentage is of. This parser cannot follow it there: a
        // <see cref="StyleValue" /> is a struct with one number and one unit, and a kind that had to
        // carry a tree would put an allocation on every declaration in the cascade. So the rule is
        // <i>fold or refuse</i>, and everything that cannot be folded to a single number or a single
        // length comes back <see cref="StyleValue.Unknown" /> — which is what it already did.
        //
        // ⚠ <b><c>calc(100% - 10px)</c> is therefore still refused, and that is a real limit and not
        // an oversight.</b> The two summands have different units, there is no third unit to hold the
        // answer, and resolving the percentage needs the containing block — the same context
        // <see cref="StyleUnit" />'s own remark says this assembly is never allowed to reach for.
        // Refusing it is the behaviour that was already there; what changes is that
        // <c>calc(2px + 2px)</c>, <c>calc(1 / 0.75)</c> and <c>calc(var(--a) + var(--b))</c> now
        // answer, and the last of those is the one that matters: substitution runs on the text before
        // this method sees it, so an expression over two custom properties arrives here as ordinary
        // arithmetic, and no generator can do that addition at build time because the two operands
        // come from two independent classes.
        if (name.Equals("calc", StringComparison.OrdinalIgnoreCase)) {
            return ParseCalc(arguments);
        }

        // ⚠ <b>The non-colour functions this parser knows, and each is spelt as a keyword and its
        // argument rather than given a <c>StyleValueKind</c> of its own.</b> A filter is a *list* of
        // functions — <c>filter: blur(4px) brightness(.5)</c> — so whatever a single one parses to
        // has to survive being an item of the list `Parse` already builds by splitting on
        // whitespace. A two-item list does; a new kind carrying a name would have meant a field on
        // `StyleValue` that only filters ever set, on a struct every declaration in the cascade is
        // one of.
        //
        // ⚠ The <i>argument</i> is refused rather than defaulted when it is not the right shape. CSS
        // says <c>blur()</c> with no argument is zero, which is a filter that does nothing and is
        // therefore indistinguishable from the declaration having been rejected — so accepting it
        // would buy a caller nothing and cost the reader the ability to tell a typo from a no-op.
        // The same rule applies to the seven below, where it matters more: an omitted argument is
        // legal CSS for all of them and means <i>one</i> for five and <i>zero</i> for two, so a
        // parser guessing would have to encode a per-function default that the layer above already
        // has to know anyway.
        if (name.Equals("blur", StringComparison.OrdinalIgnoreCase)) {
            var radius = ParseOne(arguments);

            return radius.Kind is StyleValueKind.Length
                || (radius.Kind == StyleValueKind.Number && radius.Number == 0f)
                    ? StyleValue.FromList([StyleValue.FromKeyword(keywords.Intern("blur")), radius])
                    : StyleValue.Unknown;
        }

        // ⚠ <b>An angle and not a number, and <c>hue-rotate(90)</c> is refused.</b> Filter Effects 1
        // § 8.5 takes an <c>&lt;angle&gt;</c> here, and CSS has no unitless angle outside a very few
        // legacy properties — so a bare <c>90</c> is a stylesheet bug, and one whose plausible
        // reading (degrees) is exactly what would make it invisible. Zero is the exception every
        // angle grammar makes.
        if (name.Equals("hue-rotate", StringComparison.OrdinalIgnoreCase)) {
            var angle = ParseOne(arguments);

            return (angle.Kind == StyleValueKind.Length && angle.Unit == StyleUnit.Degrees)
                || (angle.Kind == StyleValueKind.Number && angle.Number == 0f)
                    ? StyleValue.FromList([StyleValue.FromKeyword(keywords.Intern("hue-rotate")), angle])
                    : StyleValue.Unknown;
        }

        // ⚠ <b>The one filter function with more than one argument, and the reason it is the odd one
        // here is that its arguments are separated by <i>spaces</i>.</b> Every function above splits
        // into a keyword and exactly one value, which is the shape <see cref="Parse" /> already
        // produces for a whitespace-separated list — so a two-item list was enough and
        // <c>DrawListBuilder</c> could read a pair. This is a keyword and three-to-four values, and
        // the same list carries it: <c>[drop-shadow, x, y, blur?, colour?]</c>.
        //
        // ⚠ <b>Validated only for shape, and the grammar is checked one level up.</b> That is not
        // laziness, it is the arrangement <c>box-shadow</c> already has: <c>EmitShadow</c> walks a
        // flat list of items and decides what each one is, because two of the answers need the
        // <i>element</i> — <c>currentcolor</c> is the computed <c>color</c>, and a relative length is
        // relative to the element's font size. A parser that resolved either would be resolving
        // something it cannot see, and one that refused them both would refuse the default colour CSS
        // gives this function. See <c>DrawListBuilder.One</c>, which is the reader.
        //
        // ⚠ <b>The order of the arguments is not normalised either.</b> Filter Effects 1 § 8.4 writes
        // the grammar as <c>[ &lt;color&gt;? &amp;&amp; &lt;length&gt;{2,3} ]</c>, so the colour is
        // legal at either end. Reordering here would put the parser in the business of knowing which
        // item is which, which is exactly the knowledge the reader has to have anyway.
        if (name.Equals("drop-shadow", StringComparison.OrdinalIgnoreCase)) {
            var parts = SplitTopLevel(arguments);

            if (parts.Count is < 2 or > 4) {
                return StyleValue.Unknown;
            }

            var items = new StyleValue[parts.Count + 1];
            items[0] = StyleValue.FromKeyword(keywords.Intern("drop-shadow"));

            for (var i = 0; i < parts.Count; i++) {
                items[i + 1] = ParseOne(arguments[parts[i]]);

                if (items[i + 1].Kind == StyleValueKind.Unknown) {
                    return StyleValue.Unknown;
                }
            }

            return StyleValue.FromList(items);
        }

        // ⚠ <b>Six functions and one branch, because their grammar is identical and their <i>meaning</i>
        // is not this method's business.</b> Each takes a <c>&lt;number&gt;</c> or a
        // <c>&lt;percentage&gt;</c>; what one means — a ratio for <c>brightness</c>, a proportion of
        // the way to grey for <c>grayscale</c>, a range that clamps for four of them and does not for
        // two — belongs to `DrawListBuilder`, which is the only thing that reads them. Splitting the
        // interpretation across the parser and the consumer is how <c>saturate</c> would come to
        // clamp at one in a version of the parser nobody remembered to check.
        //
        // ⚠ The percentage is carried as a percentage rather than divided by a hundred here. This
        // method resolves nothing — see `StyleUnit`'s own remark, which is the rule the relative
        // lengths follow — and a value that arrived as `50%` and left as `0.5` could not be told
        // apart from one written `0.5`, which is the ambiguity the animator would inherit.
        var amount = name switch {
            _ when name.Equals("brightness", StringComparison.OrdinalIgnoreCase) => "brightness",
            _ when name.Equals("contrast", StringComparison.OrdinalIgnoreCase) => "contrast",
            _ when name.Equals("grayscale", StringComparison.OrdinalIgnoreCase) => "grayscale",
            _ when name.Equals("invert", StringComparison.OrdinalIgnoreCase) => "invert",
            _ when name.Equals("saturate", StringComparison.OrdinalIgnoreCase) => "saturate",
            _ when name.Equals("sepia", StringComparison.OrdinalIgnoreCase) => "sepia",

            // ⚠ <b>The seventh, and it is only ever legal inside a <c>backdrop-filter</c> as far as
            // this engine is concerned.</b> The grammar is identical to the six above — a number or a
            // percentage — so it belongs in this branch; which of the two properties may contain it is
            // <c>DrawListBuilder.One</c>'s decision, exactly as the *meaning* of the other six is.
            // ⚠ It cannot be confused with the <c>opacity</c> property: that one is a bare value and
            // never reaches a method whose whole job is what is in front of a bracket.
            _ when name.Equals("opacity", StringComparison.OrdinalIgnoreCase) => "opacity",
            _ => null
        };

        if (amount is not null) {
            var value = ParseOne(arguments);

            return value.Kind == StyleValueKind.Number
                || (value.Kind == StyleValueKind.Length && value.Unit == StyleUnit.Percent)
                    ? StyleValue.FromList([StyleValue.FromKeyword(keywords.Intern(amount)), value])
                    : StyleValue.Unknown;
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

    /// <summary>Parses <c>color(&lt;space&gt; c1 c2 c3 / α)</c> for the predefined RGB spaces.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing is clamped, and for this function that is the entire point.</b> CSS
    ///         Color 4 § 10 uses <c>color(display-p3 1.0844 0.43 0.1)</c> as its own worked example
    ///         of a colour outside sRGB but inside P3 — the case that exists to be shown on a wide
    ///         display, and that a clamp at parse time would destroy before anything could know what
    ///         display it was going to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the spaces whose transfer function this actually implements are accepted.</b>
    ///         <c>display-p3</c> shares sRGB's transfer curve — the specification's table says so in
    ///         as many words — so decoding it needs nothing new. <c>a98-rgb</c>, <c>prophoto-rgb</c>
    ///         and <c>rec2020</c> each have their own curve, and guessing sRGB's for them would give a
    ///         colour that is wrong by a few percent in the midtones: plausible, and invisible until
    ///         someone matches against a browser. They are refused instead.
    ///     </para>
    /// </remarks>
    static StyleValue ParsePredefined(ReadOnlySpan<char> arguments) {
        Span<Range> ranges = stackalloc Range[6];
        var count = Split(arguments, ranges);

        if (count is < 4 or > 5) {
            return StyleValue.Unknown;
        }

        var space = arguments[ranges[0]];
        ColorGamut gamut;
        bool encoded;

        if (space.Equals("srgb", StringComparison.OrdinalIgnoreCase)) {
            (gamut, encoded) = (ColorGamut.Srgb, true);
        } else if (space.Equals("srgb-linear", StringComparison.OrdinalIgnoreCase)) {
            (gamut, encoded) = (ColorGamut.Srgb, false);
        } else if (space.Equals("display-p3", StringComparison.OrdinalIgnoreCase)) {
            (gamut, encoded) = (ColorGamut.DisplayP3, true);
        } else if (space.Equals("display-p3-linear", StringComparison.OrdinalIgnoreCase)) {
            (gamut, encoded) = (ColorGamut.DisplayP3, false);
        } else {
            return StyleValue.Unknown;
        }

        Span<float> channels = stackalloc float[3];

        for (var index = 0; index < 3; index++) {
            if (!TryComponent(arguments[ranges[index + 1]], 1f, out channels[index])) {
                return StyleValue.Unknown;
            }

            if (encoded) {
                channels[index] = ColorSpace.SrgbToLinear(channels[index]);
            }
        }

        var alpha = 1f;

        if (count == 5 && !TryComponent(arguments[ranges[4]], 1f, out alpha)) {
            return StyleValue.Unknown;
        }

        // Into the engine's working space — linear, sRGB primaries, unbounded. A P3 colour that is
        // outside sRGB arrives here as a linear triple with a channel outside [0, 1], which is
        // exactly how every other out-of-gamut colour in this parser is already carried.
        var linear = GamutMap.ToLinearSrgb(new Vector3(channels[0], channels[1], channels[2]), gamut);

        return StyleValue.FromColor(
            new Color4(linear.X, linear.Y, linear.Z, Math.Clamp(alpha, 0f, 1f))
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

            // ⚠ The other three angle units are converted here rather than carried, which is the one
            // place in this switch a unit does not survive parsing — and it is deliberate. Values 1
            // § 6.1 makes `deg`, `grad`, `rad` and `turn` four spellings of one dimension with fixed
            // ratios between them, unlike `em` or `%`, which need a context this assembly does not
            // have. So there is nothing to resolve later and no reason for a consumer to carry four
            // cases. It also keeps interpolation working across them: the animator requires both
            // endpoints to share a unit, so a transition from `0.25turn` to `180deg` would otherwise
            // silently snap in exactly the way this enum's own remark warns about for `em`.
            _ when suffix.Equals("grad", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number * 0.9f, StyleUnit.Degrees),
            _ when suffix.Equals("rad", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number * (180f / MathF.PI), StyleUnit.Degrees),
            _ when suffix.Equals("turn", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number * 360f, StyleUnit.Degrees),

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

            // ⚠ `lh` and not `rlh`. Tailwind v4 spells one class in this unit — `max-h-lh`, one line
            // box — and the root-relative sibling has no class and no consumer, so adding it would be
            // a unit nothing writes and a second thing to keep resolving correctly.
            _ when suffix.Equals("lh", StringComparison.OrdinalIgnoreCase) =>
                StyleValue.FromLength(number, StyleUnit.LineHeight),

            _ => StyleValue.Unknown
        };
    }

    /// <summary>Whether what follows an <c>e</c> is an exponent rather than the rest of a unit.</summary>
    static bool HasExponentDigits(ReadOnlySpan<char> text) => text switch {
        [var digit, ..] when char.IsAsciiDigit(digit) => true,
        ['-' or '+', var digit, ..] when char.IsAsciiDigit(digit) => true,
        _ => false
    };

    /// <summary>Folds a <c>calc()</c> body to one number or one length.</summary>
    /// <param name="arguments">What was between the brackets.</param>
    /// <returns>The folded value, or <see cref="StyleValue.Unknown" /> if it does not fold.</returns>
    /// <remarks>
    ///     ⚠ <b>The whole body must be consumed, and that trailing check is the one carrying the
    ///     refusals.</b> Every rule below leaves the cursor where it stopped rather than throwing, so
    ///     an expression this evaluator does not understand parses as its own first half and returns
    ///     a value — <c>calc(2px -1px)</c> would fold to <c>2px</c>, silently, which is a shadow at
    ///     the wrong offset rather than a declaration that was refused. Requiring the cursor to reach
    ///     the end turns every one of those into <see cref="StyleValue.Unknown" />.
    /// </remarks>
    static StyleValue ParseCalc(ReadOnlySpan<char> arguments) {
        var at = 0;
        var value = Sum(arguments, ref at);

        SkipSpace(arguments, ref at);

        return at == arguments.Length ? value : StyleValue.Unknown;
    }

    /// <summary>Reads a sum, which is the lowest precedence.</summary>
    /// <remarks>
    ///     ⚠ <b><c>+</c> and <c>-</c> are operators only with whitespace on both sides, and this is
    ///     Values 4 § 10.1 rather than a house style.</b> CSS cannot tell the operator from a sign
    ///     otherwise — <c>calc(2px -1px)</c> would be a sum or a two-item list depending on who is
    ///     reading — so the grammar demands the space and calls the rest invalid. Accepting it here
    ///     would make this parser answer where a browser refuses, which is the worse of the two
    ///     disagreements: a stylesheet that works in Vixen and not on the web.
    /// </remarks>
    static StyleValue Sum(ReadOnlySpan<char> text, ref int at) {
        var left = Product(text, ref at);

        while (left.Kind != StyleValueKind.Unknown) {
            var resume = at;
            SkipSpace(text, ref at);

            if (at == resume || at >= text.Length || text[at] is not ('+' or '-')) {
                at = resume;
                return left;
            }

            var subtract = text[at] == '-';
            at++;

            if (at >= text.Length || !IsSpace(text[at])) {
                at = resume;
                return left;
            }

            left = Add(left, Product(text, ref at), subtract);
        }

        return StyleValue.Unknown;
    }

    /// <summary>Reads a product, which binds tighter than a sum.</summary>
    /// <remarks>
    ///     ⚠ <c>*</c> and <c>/</c> need no surrounding whitespace, and the asymmetry with
    ///     <see cref="Sum" /> is CSS's: neither character can begin a number, so there is nothing to
    ///     be ambiguous about.
    /// </remarks>
    static StyleValue Product(ReadOnlySpan<char> text, ref int at) {
        var left = Unary(text, ref at);

        while (left.Kind != StyleValueKind.Unknown) {
            var resume = at;
            SkipSpace(text, ref at);

            if (at >= text.Length || text[at] is not ('*' or '/')) {
                at = resume;
                return left;
            }

            var divide = text[at] == '/';
            at++;

            var right = Unary(text, ref at);
            left = divide ? Divide(left, right) : Multiply(left, right);
        }

        return StyleValue.Unknown;
    }

    /// <summary>Reads a bracketed group, a nested <c>calc()</c>, or one dimension.</summary>
    static StyleValue Unary(ReadOnlySpan<char> text, ref int at) {
        SkipSpace(text, ref at);

        if (at >= text.Length) {
            return StyleValue.Unknown;
        }

        // A nested `calc()` is exactly a bracketed group — Values 4 says the two are equivalent — so
        // the name is stepped over and the same branch reads the brackets.
        if (at + 4 < text.Length
            && text.Slice(at, 4).Equals("calc", StringComparison.OrdinalIgnoreCase)
            && text[at + 4] == '(') {
            at += 4;
        }

        if (text[at] == '(') {
            at++;
            var inner = Sum(text, ref at);
            SkipSpace(text, ref at);

            if (at >= text.Length || text[at] != ')') {
                return StyleValue.Unknown;
            }

            at++;

            return inner;
        }

        var start = at;

        if (text[at] is '-' or '+') {
            at++;
        }

        while (at < text.Length) {
            var c = text[at];

            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '%') {
                at++;
                continue;
            }

            // ⚠ The one place a sign is part of the term rather than an operator: the exponent of
            // `1e-2`. `ParseNumeric` accepts that spelling, so a scanner that stopped at the `-`
            // would hand it half a number and refuse a value the rest of this file reads.
            if (c is '-' or '+' && at > start && text[at - 1] is 'e' or 'E') {
                at++;
                continue;
            }

            break;
        }

        if (at == start) {
            return StyleValue.Unknown;
        }

        var term = ParseNumeric(text[start..at]);

        return term.Kind is StyleValueKind.Number or StyleValueKind.Length ? term : StyleValue.Unknown;
    }

    /// <summary>Adds or subtracts, which is the operation that can refuse on units.</summary>
    /// <remarks>
    ///     ⚠ <b>A bare zero is not accepted as a length here, and it is accepted everywhere else in
    ///     this file.</b> <see cref="StyleValue.CanInterpolate" /> says zero belongs to every unit,
    ///     and for interpolation it does. Values 4 § 10.2 does not extend that to arithmetic:
    ///     <c>calc(100px + 0)</c> is a length plus a number and is invalid, because the alternative is
    ///     a grammar in which the type of an expression depends on the value of a term.
    /// </remarks>
    static StyleValue Add(StyleValue left, StyleValue right, bool subtract) {
        if (left.Kind == StyleValueKind.Unknown || right.Kind == StyleValueKind.Unknown) {
            return StyleValue.Unknown;
        }

        var value = subtract ? left.Number - right.Number : left.Number + right.Number;

        if (left.Kind == StyleValueKind.Number && right.Kind == StyleValueKind.Number) {
            return Finite(StyleValue.FromNumber(value));
        }

        return left.Kind == StyleValueKind.Length && right.Kind == StyleValueKind.Length
            && left.Unit == right.Unit
                ? Finite(StyleValue.FromLength(value, left.Unit))
                : StyleValue.Unknown;
    }

    /// <summary>Multiplies, where one side has to be a plain number.</summary>
    static StyleValue Multiply(StyleValue left, StyleValue right) {
        var value = left.Number * right.Number;

        if (right.Kind == StyleValueKind.Number) {
            return left.Kind switch {
                StyleValueKind.Number => Finite(StyleValue.FromNumber(value)),
                StyleValueKind.Length => Finite(StyleValue.FromLength(value, left.Unit)),
                _ => StyleValue.Unknown
            };
        }

        return left.Kind == StyleValueKind.Number && right.Kind == StyleValueKind.Length
            ? Finite(StyleValue.FromLength(value, right.Unit))
            : StyleValue.Unknown;
    }

    /// <summary>Divides, where the divisor has to be a plain non-zero number.</summary>
    /// <remarks>
    ///     ⚠ Dividing by zero is refused rather than folded. IEEE would answer infinity, and an
    ///     infinite length is a value every consumer downstream accepts and none of them draws — a
    ///     rectangle that vanishes rather than a declaration that was rejected.
    /// </remarks>
    static StyleValue Divide(StyleValue left, StyleValue right) {
        if (right.Kind != StyleValueKind.Number || right.Number == 0f) {
            return StyleValue.Unknown;
        }

        var value = left.Number / right.Number;

        return left.Kind switch {
            StyleValueKind.Number => Finite(StyleValue.FromNumber(value)),
            StyleValueKind.Length => Finite(StyleValue.FromLength(value, left.Unit)),
            _ => StyleValue.Unknown
        };
    }

    /// <summary>Refuses a fold that overflowed, rather than letting an infinity out of the parser.</summary>
    static StyleValue Finite(StyleValue value) =>
        float.IsFinite(value.Number) ? value : StyleValue.Unknown;

    static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    static void SkipSpace(ReadOnlySpan<char> text, ref int at) {
        while (at < text.Length && IsSpace(text[at])) {
            at++;
        }
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
