// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>Turns the text between an arbitrary value's brackets into the CSS it stands for.</summary>
/// <remarks>
///     <para>
///         Two things a class attribute cannot say and CSS needs: a space, written <c>_</c>
///         (<c>grid-cols-[1fr_auto]</c>), and the whitespace CSS Values 4 § 10.1 requires around
///         <c>+</c> and <c>-</c> inside a math function, which Tailwind v4's decoder inserts on the
///         author's behalf — <c>mt-[calc(1px+2px)]</c> is <c>calc(1px + 2px)</c>.
///     </para>
///     <para>
///         ⚠ <b>Until #1467 the second half was missing and the value was emitted verbatim</b>:
///         <c>calc(1px+2px)</c> is invalid CSS, <c>StyleValueParser</c> rightly refuses it, and the
///         class resolved, emitted and computed to nothing — a spelling every Tailwind author uses
///         that silently did nothing.
///     </para>
/// </remarks>
static class ArbitraryValue {
    /// <summary>The math functions whose arguments are spaced, as Tailwind v4 lists them.</summary>
    static readonly string[] MathFunctions = [
        "calc", "min", "max", "clamp", "mod", "rem", "sin", "cos", "tan", "asin", "acos", "atan", "atan2",
        "pow", "sqrt", "hypot", "log", "exp", "round"
    ];

    /// <summary>Decodes an arbitrary value: <c>_</c> to a space, then the math operators spaced.</summary>
    /// <param name="text">The text between the brackets.</param>
    /// <returns>The CSS value.</returns>
    public static string Decode(ReadOnlySpan<char> text) => SpaceMathOperators(text.ToString().Replace('_', ' '));

    /// <summary>Puts whitespace around the operators inside math functions, the way Tailwind v4 does.</summary>
    /// <param name="input">The value, with its underscores already spaces.</param>
    /// <returns>The value with its math spaced, or <paramref name="input" /> where it has no math function.</returns>
    /// <remarks>
    ///     <para>
    ///         A port of Tailwind v4's <c>addWhitespaceAroundMathOperators</c>, rule for rule, because
    ///         the claim is parity and an independent reading of CSS would disagree with it at the
    ///         edges. Inside a math function — or a bare parenthesis nested in one — a comma gains a
    ///         space after it, runs of spaces collapse, and an operator is spaced when it is binary.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Binary is decided by the neighbours, and each exception is a real spelling.</b> An
    ///         operator after another operator, an open parenthesis or a comma is a sign
    ///         (<c>1px+-2px</c>, <c>calc(-1*x)</c>); one after a digit followed by <c>e</c> is an
    ///         exponent (<c>1e-3</c>); a hyphen inside a function that is not math, such as the name in
    ///         <c>var(--my-gap)</c>, is never looked at. Otherwise the operator is spaced when a digit,
    ///         a parenthesis or another operator is next to it or a number with its unit ended just
    ///         before it — so <c>calc(a-b)</c>, two identifiers, is left alone as Tailwind leaves it.
    ///     </para>
    /// </remarks>
    public static string SpaceMathOperators(string input) {
        var any = false;
        foreach (var function in MathFunctions) {
            if (input.Contains(function, StringComparison.Ordinal)) {
                any = true;
                break;
            }
        }

        if (!any) {
            return input;
        }

        var result = new StringBuilder(input.Length + 8);

        // Innermost first: whether the parenthesis just opened is one whose contents are formatted.
        var formattable = new Stack<bool>();
        int? valuePosition = null;
        int? lastValuePosition = null;

        for (var i = 0; i < input.Length; i++) {
            var c = input[i];

            // A number followed by its unit is a value, which is what lets `2px-1px` be told from an
            // identifier with a hyphen in it.
            if (char.IsAsciiDigit(c)) {
                valuePosition = i;
            } else if (valuePosition is not null && (c == '%' || char.IsAsciiLetter(c))) {
                valuePosition = i;
            } else {
                lastValuePosition = valuePosition;
                valuePosition = null;
            }

            var inMath = formattable.Count > 0 && formattable.Peek();

            if (c == '(') {
                result.Append(c);

                // The function's name, read backwards: lowercase letters and digits.
                var start = i;
                for (var j = i - 1; j >= 0; j--) {
                    if (char.IsAsciiDigit(input[j]) || char.IsAsciiLetterLower(input[j])) {
                        start = j;
                    } else {
                        break;
                    }
                }

                var name = input.AsSpan(start, i - start);
                formattable.Push(Array.IndexOf(MathFunctions, name.ToString()) >= 0 || (inMath && name.IsEmpty));
                continue;
            }

            if (c == ')') {
                result.Append(c);
                if (formattable.Count > 0) {
                    formattable.Pop();
                }

                continue;
            }

            if (c == ',' && inMath) {
                result.Append(", ");
                continue;
            }

            if (c == ' ' && inMath && result.Length > 0 && result[^1] == ' ') {
                continue;
            }

            if (c is '+' or '*' or '/' or '-' && inMath) {
                var trimmed = result.Length;
                while (trimmed > 0 && char.IsWhiteSpace(result[trimmed - 1])) {
                    trimmed--;
                }

                var previous = trimmed > 0 ? result[trimmed - 1] : '\0';
                var beforePrevious = trimmed > 1 ? result[trimmed - 2] : '\0';
                var next = i + 1 < input.Length ? input[i + 1] : '\0';

                if ((previous is 'e' or 'E' && char.IsAsciiDigit(beforePrevious))
                    || previous is '+' or '*' or '/' or '-'
                    || previous is '(' or ',') {
                    // An exponent, a sign after an operator, or a sign opening an argument.
                    result.Append(c);
                } else if (i > 0 && input[i - 1] == ' ') {
                    // Already spaced before, so only after.
                    result.Append(c).Append(' ');
                } else if (char.IsAsciiDigit(previous)
                           || char.IsAsciiDigit(next)
                           || previous == ')'
                           || next == '('
                           || next is '+' or '*' or '/' or '-'
                           || (lastValuePosition is { } last && last == i - 1)) {
                    result.Append(' ').Append(c).Append(' ');
                } else {
                    result.Append(c);
                }

                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
