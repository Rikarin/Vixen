// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling;

/// <summary>One entry of a <c>transition</c> declaration.</summary>
/// <param name="Property">The interned property it transitions, or <see cref="NameTable.None" /> for <c>all</c>.</param>
/// <param name="Duration">How long, in seconds.</param>
/// <param name="Delay">How long to wait first, in seconds.</param>
/// <param name="Timing">The easing.</param>
/// <param name="AllowDiscrete">
///     Whether a property with no midpoint may transition at all — <c>transition-behavior:
///     allow-discrete</c>. False is CSS's initial value and, since this parameter landed, this
///     engine's.
/// </param>
/// <remarks>
///     ⚠ <b>The default was the opposite of CSS's until <paramref name="AllowDiscrete" /> existed,
///     and nothing said so.</b> <c>Animator.Observe</c> started a transition for any pair of unequal
///     known values and never asked <see cref="StyleValue.CanInterpolate" />, so
///     <c>transition: all 1s</c> over <c>display: none</c> → <c>display: flex</c> ran for a second
///     and flipped at the halfway mark, where a browser shows <c>flex</c> on the first frame.
///     Transitions 2 § 3 makes <c>normal</c> the initial value and <c>normal</c> means <i>not
///     transitionable</i> rather than "transitions instantly".
/// </remarks>
public readonly record struct TransitionSpec(
    int Property,
    float Duration,
    float Delay,
    TimingFunction Timing,
    bool AllowDiscrete = false
);

/// <summary>Reads <c>transition</c>, <c>animation</c> and the timing-function grammar.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Vixen has to parse the <c>transition</c> shorthand itself, and cannot rely on the
///         longhands.</b> ExCSS expands the shorthand only when it recognises every part — so
///         <c>transition: opacity 200ms ease-in</c> arrives as four longhand declarations, and
///         <c>transition: opacity 200ms spring(1, 100, 10)</c> arrives as one unknown property with
///         its text intact. Whether the longhands exist therefore depends on whether the author used
///         a Vixen extension, which is not a distinction anything downstream should have to know
///         about. Both forms are read here and produce the same thing.
///     </para>
///     <para>
///         Exactly parallel to <c>@layer</c>: the front end handles the common case, Vixen owns the
///         general one, and the seam stops here.
///     </para>
/// </remarks>
public sealed class TransitionParser {
    static readonly (string Name, Func<TimingFunction> Function)[] Keywords = [
        ("linear", () => TimingFunction.Linear),
        ("ease", () => TimingFunction.Ease),
        ("ease-in", () => TimingFunction.EaseIn),
        ("ease-out", () => TimingFunction.EaseOut),
        ("ease-in-out", () => TimingFunction.EaseInOut),
        ("step-start", () => TimingFunction.Step(1, StepPosition.Start)),
        ("step-end", () => TimingFunction.Step(1, StepPosition.End))
    ];

    readonly NameTable properties;

    /// <summary>Creates a parser.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    public TransitionParser(NameTable properties) {
        ArgumentNullException.ThrowIfNull(properties);
        this.properties = properties;
    }

    /// <summary>The interned name meaning "every property".</summary>
    public int All { get; } = NameTable.None;

    /// <summary>Reads a <c>transition</c> shorthand.</summary>
    /// <param name="text">Its value.</param>
    /// <param name="specs">Receives one entry per comma-separated part.</param>
    /// <returns>Whether it could be read.</returns>
    public bool TryParseShorthand(string text, List<TransitionSpec> specs) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(specs);

        specs.Clear();

        // Depth-aware, and it has to be. `Split(',')` cuts `spring(2, 180, 12)` into three pieces,
        // none of them a timing function — and `spring()` is exactly the thing that got us here,
        // being both the reason ExCSS could not expand the shorthand and the only value in it with
        // commas inside. The same shape of bug as matching braces in an `@layer` body.
        foreach (var range in TopLevelSplit(text.AsSpan(), ',')) {
            var part = text.AsSpan()[range].Trim();
            if (part.IsEmpty) {
                continue;
            }

            if (!TryParseOne(part, out var spec)) {
                specs.Clear();
                return false;
            }

            specs.Add(spec);
        }

        return specs.Count > 0;
    }

    /// <summary>Splits on a separator, keeping bracketed groups whole.</summary>
    /// <param name="text">The text.</param>
    /// <param name="separator">What to split on.</param>
    /// <returns>The ranges between separators.</returns>
    public static List<Range> TopLevelSplit(ReadOnlySpan<char> text, char separator) {
        var ranges = new List<Range>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];

            if (c == '(') {
                depth++;
            } else if (c == ')') {
                depth--;
            } else if (c == separator && depth == 0) {
                ranges.Add(new Range(start, i));
                start = i + 1;
            }
        }

        ranges.Add(new Range(start, text.Length));
        return ranges;
    }

    bool TryParseOne(ReadOnlySpan<char> text, out TransitionSpec spec) {
        spec = default;

        var property = NameTable.None;
        var timing = TimingFunction.Ease;
        float? duration = null;
        float? delay = null;
        var allowDiscrete = false;

        foreach (var token in Tokens(text)) {
            var part = text[token];

            // ⚠ Before the property arm below, which would otherwise intern `allow-discrete` as a
            // property name and produce a spec matching a longhand nothing has. The shorthand's
            // grammar has no ambiguity here — Transitions 2 lists the behaviour keyword as its own
            // component — but this loop decides by what a token *parses as*, and every unrecognised
            // word falls through to the property.
            if (part.Equals("allow-discrete", StringComparison.OrdinalIgnoreCase)) {
                allowDiscrete = true;
                continue;
            }

            if (part.Equals("normal", StringComparison.OrdinalIgnoreCase)) {
                allowDiscrete = false;
                continue;
            }

            if (TryDuration(part, out var seconds)) {
                // CSS's rule, and it is positional rather than named: the first time is the
                // duration and the second is the delay.
                if (duration is null) {
                    duration = seconds;
                } else if (delay is null) {
                    delay = seconds;
                } else {
                    return false;
                }

                continue;
            }

            if (TryTimingFunction(part, out var parsed)) {
                timing = parsed;
                continue;
            }

            if (property != NameTable.None) {
                return false;
            }

            // `all` is spelled as the absence of a property rather than as a property called "all",
            // so that matching a declaration against a spec is an integer compare either way.
            property = part.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? NameTable.None
                : properties.Intern(part.ToString());
        }

        spec = new TransitionSpec(property, duration ?? 0f, delay ?? 0f, timing, allowDiscrete);
        return true;
    }

    /// <summary>Reads a timing function.</summary>
    /// <param name="text">Its text.</param>
    /// <param name="timing">Receives the function.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryTimingFunction(ReadOnlySpan<char> text, out TimingFunction timing) {
        text = text.Trim();
        timing = TimingFunction.Ease;

        // `ease-in-out` before `ease-in`, so that the prefix does not win — except these are exact
        // comparisons, so the order is only for reading.
        foreach (var (keyword, function) in Keywords) {
            if (!text.Equals(keyword, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            timing = function();
            return true;
        }

        var open = text.IndexOf('(');
        if (open <= 0 || text[^1] != ')') {
            return false;
        }

        var name = text[..open].Trim();
        var arguments = text[(open + 1)..^1];

        Span<float> numbers = stackalloc float[4];
        var words = new List<string>();
        var count = Arguments(arguments, numbers, words);

        if (name.Equals("cubic-bezier", StringComparison.OrdinalIgnoreCase) && count == 4) {
            timing = TimingFunction.Bezier(numbers[0], numbers[1], numbers[2], numbers[3]);
            return true;
        }

        if (name.Equals("steps", StringComparison.OrdinalIgnoreCase) && count >= 1) {
            var position = words.Count > 0 && words[0].StartsWith("start", StringComparison.OrdinalIgnoreCase)
                ? StepPosition.Start
                : StepPosition.End;

            timing = TimingFunction.Step((int) numbers[0], position);
            return true;
        }

        if (name.Equals("spring", StringComparison.OrdinalIgnoreCase) && count == 3) {
            timing = TimingFunction.Spring(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        return false;
    }

    /// <summary>Reads a duration.</summary>
    /// <param name="text">Its text.</param>
    /// <param name="seconds">Receives the duration in seconds.</param>
    /// <returns>Whether it is one.</returns>
    /// <remarks>
    ///     A unit is required, and that is CSS's rule rather than fussiness: <c>transition: 1</c> is
    ///     invalid because a bare number in that position would be ambiguous with an
    ///     <c>animation-iteration-count</c>.
    /// </remarks>
    public static bool TryDuration(ReadOnlySpan<char> text, out float seconds) {
        seconds = 0f;
        text = text.Trim();

        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) {
            if (!float.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)) {
                return false;
            }

            seconds = milliseconds / 1000f;
            return true;
        }

        if (!text.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    static int Arguments(ReadOnlySpan<char> text, Span<float> numbers, List<string> words) {
        var count = 0;

        foreach (var range in TopLevelSplit(text, ',')) {
            var part = text[range].Trim();
            if (part.IsEmpty) {
                continue;
            }

            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                if (count < numbers.Length) {
                    numbers[count++] = number;
                }

                continue;
            }

            words.Add(part.ToString());
        }

        return count;
    }

    /// <summary>Splits on whitespace, keeping bracketed groups whole.</summary>
    static List<Range> Tokens(ReadOnlySpan<char> text) {
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
}
