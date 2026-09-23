// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>The order the generator writes its conditional groups in, which is what decides who wins.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Order is semantics here, not tidiness.</b> Two breakpoint utilities on one element are
///         one class each, so they tie on specificity, and where both conditions hold the rule written
///         later wins. Mobile-first — <c>sm:p-2 lg:p-4</c> meaning "2 from sm, 4 from lg" — is
///         therefore entirely a claim about which group is emitted last. This used to be an ordinal
///         sort over the at-rule text, which put <c>@media (min-width: 1024px)</c> before
///         <c>@media (min-width: 640px)</c> because <c>'1' &lt; '6'</c>, so the narrow value won on
///         the wide window for every pair whose widths differ in digit count.
///     </para>
///     <para>
///         The order is v4's: the viewport ranges first — <c>max-*</c> widest to narrowest, then
///         <c>min-*</c> and the bare breakpoints narrowest to widest, so on either side the tighter
///         condition is the later one — then the container ranges in the same shape, then every other
///         group in the ordinal order it always had. The container family after the viewport one is
///         v4's registration order, and means a component's own <c>@md:</c> refines the page's
///         <c>md:</c> rather than losing to it.
///     </para>
///     <para>
///         ⚠ <b>One known divergence, kept deliberately small.</b> v4 registers
///         <c>motion-*</c> and <c>contrast-*</c> <i>before</i> the breakpoints; here they sort with
///         the other feature queries, after them, which is where the ordinal sort already put them.
///         Only a class pairing one of those with a breakpoint on the same property can tell.
///     </para>
///     <para>
///         Widths are compared in pixels, with <c>rem</c> and <c>em</c> at the 16px a media query
///         measures them in. A width in any other unit — an arbitrary <c>min-[calc(…)]</c> — sorts
///         after the ones this can read in its band, and ties fall back to the ordinal order, so the
///         comparison is total and the file stays byte-stable.
///     </para>
/// </remarks>
sealed class AtRuleOrder : IComparer<string> {
    /// <summary>The one instance; it holds no state.</summary>
    public static readonly AtRuleOrder Instance = new();

    AtRuleOrder() { }

    enum Family : byte {
        Viewport,
        Container,
        Other
    }

    enum Direction : byte {
        // `max-*` first, so that the `min-*` side — mobile-first, the side most classes use — wins
        // where a `max-lg:` and an `md:` overlap, which is v4's answer.
        Below,
        AtLeast
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y) {
        if (ReferenceEquals(x, y)) {
            return 0;
        }

        if (x is null) {
            return -1;
        }

        if (y is null) {
            return 1;
        }

        var a = Read(x);
        var b = Read(y);

        var order = a.Family.CompareTo(b.Family);

        if (order != 0) {
            return order;
        }

        if (a.Family != Family.Other) {
            order = a.Direction.CompareTo(b.Direction);

            if (order != 0) {
                return order;
            }

            // A width this can read before one it cannot, in either direction.
            order = double.IsNaN(a.Width).CompareTo(double.IsNaN(b.Width));

            if (order != 0) {
                return order;
            }

            if (!double.IsNaN(a.Width)) {
                // Narrowest last on the `max-*` side and widest last on the `min-*` side: the later
                // group is always the tighter condition.
                order = a.Direction == Direction.Below ? b.Width.CompareTo(a.Width) : a.Width.CompareTo(b.Width);

                if (order != 0) {
                    return order;
                }
            }
        }

        return string.CompareOrdinal(x, y);
    }

    static (Family Family, Direction Direction, double Width) Read(string atRule) {
        var text = atRule.AsSpan();
        Family family;

        if (text.StartsWith("@media ", StringComparison.Ordinal)) {
            family = Family.Viewport;
        } else if (text.StartsWith("@container ", StringComparison.Ordinal)) {
            family = Family.Container;
        } else {
            return (Family.Other, default, double.NaN);
        }

        // A container's name sits between the keyword and the condition; the condition is the one
        // parenthesised feature `Variants` writes, and anything else is not a width range.
        var open = text.IndexOf('(');

        if (open < 0 || text[^1] != ')') {
            return (Family.Other, default, double.NaN);
        }

        var condition = text[(open + 1)..^1];

        if (condition.StartsWith("min-width:", StringComparison.Ordinal)) {
            return (family, Direction.AtLeast, Pixels(condition["min-width:".Length..]));
        }

        if (condition.StartsWith("width <", StringComparison.Ordinal) && !condition.StartsWith("width <=", StringComparison.Ordinal)) {
            return (family, Direction.Below, Pixels(condition["width <".Length..]));
        }

        return (Family.Other, default, double.NaN);
    }

    static double Pixels(ReadOnlySpan<char> value) {
        value = value.Trim();

        var scale = value.EndsWith("rem", StringComparison.Ordinal) ? 16d
            : value.EndsWith("px", StringComparison.Ordinal) ? 1d
            : value.EndsWith("em", StringComparison.Ordinal) ? 16d
            : double.NaN;

        if (double.IsNaN(scale)) {
            return double.NaN;
        }

        var number = value[..^(value.EndsWith("rem", StringComparison.Ordinal) ? 3 : 2)];

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed * scale
            : double.NaN;
    }
}
