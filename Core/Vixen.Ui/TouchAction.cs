// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What a finger that lands on an element may do to the scrollable thing around it.</summary>
/// <remarks>
///     <para>
///         <b>CSS's <c>touch-action</c>, as a set.</b> The property is a negotiation between an
///         element and the user-agent behaviour a touch would otherwise get — here a
///         <c>ScrollView</c> dragging its content under a finger. A slider inside a list says
///         <c>touch-action: none</c> so that the finger on it moves the knob and not the list; a
///         horizontal carousel says <c>pan-y</c> so that a vertical swipe still scrolls the page
///         while a horizontal one belongs to the carousel.
///     </para>
///     <para>
///         ⚠ <b>Flags, because the grammar is a union.</b> <c>pan-x pan-down pinch-zoom</c> is one
///         declaration and three permissions. <see cref="PanX" /> is both horizontal directions and
///         <see cref="PanY" /> both vertical, so a consumer asks "may this axis move" with one mask
///         whether the author wrote the axis or one of its directions.
///     </para>
///     <para>
///         ⚠ <b>The directional keywords are a test on how the gesture BEGINS, not a boundary
///         check.</b> The parity ledger recorded <c>pan-left</c>/<c>pan-right</c>/<c>pan-up</c> as
///         needing "an at-boundary check nothing computes". They do not: Pointer Events § 6 says the
///         user agent may consider the touch "only for the purposes of scrolling that starts in the
///         given direction", and Chrome implements that as a sign test on the scroll-begin hint.
///         <c>pan-left</c> admits a gesture whose first movement scrolls the content leftward — the
///         viewport moving toward the left edge, <c>ScrollLeft</c> decreasing, the finger travelling
///         right — and once admitted the whole gesture is an ordinary horizontal pan.
///     </para>
///     <para>
///         ⚠ <b>Touch and pen only.</b> The property governs touch and nothing else; a consumer
///         reads <see cref="PointerType" /> first and consults this only for a finger or a stylus,
///         because <c>touch-action: none</c> on a map must not stop the map responding to a
///         <i>mouse</i> drag, which no browser does. That is the whole reason
///         <see cref="PointerEvent.PointerType" /> exists.
///     </para>
///     <para>
///         <see cref="PinchZoom" /> and <see cref="DoubleTapZoom" /> are carried so that the value
///         round-trips — <c>manipulation</c> is <c>auto</c> minus the double-tap — but nothing in
///         this engine performs either gesture as a user-agent default today, so those two bits
///         govern nothing yet. They are here so that the consumer which grows one knows where to ask.
///     </para>
/// </remarks>
[Flags]
public enum TouchAction : byte {
    /// <summary><c>none</c>: the finger belongs to the element and nothing around it may pan.</summary>
    None = 0,

    /// <summary><c>pan-left</c>: a horizontal pan that begins by scrolling the content leftward.</summary>
    PanLeft = 1 << 0,

    /// <summary><c>pan-right</c>: a horizontal pan that begins by scrolling the content rightward.</summary>
    PanRight = 1 << 1,

    /// <summary><c>pan-up</c>: a vertical pan that begins by scrolling the content upward.</summary>
    PanUp = 1 << 2,

    /// <summary><c>pan-down</c>: a vertical pan that begins by scrolling the content downward.</summary>
    PanDown = 1 << 3,

    /// <summary><c>pinch-zoom</c>: two fingers may scale whatever performs the user-agent zoom.</summary>
    PinchZoom = 1 << 4,

    /// <summary>The double-tap zoom <c>auto</c> permits and <c>manipulation</c> withholds.</summary>
    DoubleTapZoom = 1 << 5,

    /// <summary><c>pan-x</c>: horizontal panning, either way.</summary>
    PanX = PanLeft | PanRight,

    /// <summary><c>pan-y</c>: vertical panning, either way.</summary>
    PanY = PanUp | PanDown,

    /// <summary><c>manipulation</c>: panning and pinching, without the double-tap zoom's delay.</summary>
    Manipulation = PanX | PanY | PinchZoom,

    /// <summary><c>auto</c>: everything, which is what an element that says nothing gets.</summary>
    Auto = Manipulation | DoubleTapZoom
}

/// <summary>Reads <c>touch-action</c> off a computed style.</summary>
/// <remarks>
///     <para>
///         <see cref="ContainmentReader" />'s shape, for the same reason: the value is a LIST of
///         keywords that interns as one string, so it is parsed from the text once per distinct
///         spelling and cached on the value id.
///     </para>
///     <para>
///         ⚠ <b>An unreadable declaration is <see cref="TouchAction.Auto" /> rather than
///         <see cref="TouchAction.None" /></b>, because CSS drops a declaration it cannot parse and
///         the property's initial value is <c>auto</c>. <c>contain</c>'s initial is <c>none</c>, so
///         that reader falls the other way; the two are the same rule applied to different
///         initials, not different rules.
///     </para>
/// </remarks>
sealed class TouchActionReader {
    readonly int touchAction;
    readonly NameTable values;
    readonly Dictionary<int, TouchAction> cache = [];

    /// <summary>Interns the property name and keeps the table its values are interned in.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    public TouchActionReader(NameTable properties, NameTable values) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);

        touchAction = properties.Intern("touch-action");
        this.values = values;
    }

    /// <summary>What a style allows a touch to do.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <returns>The flags, or <see cref="TouchAction.Auto" /> when nothing is declared.</returns>
    public TouchAction Of(ComputedStyle style) {
        if (!style.TryGet(touchAction, out var id)) {
            return TouchAction.Auto;
        }

        if (cache.TryGetValue(id, out var cached)) {
            return cached;
        }

        var parsed = Parse(values.NameOf(id));
        cache[id] = parsed;

        return parsed;
    }

    /// <summary>The keywords of one declaration, folded together.</summary>
    /// <param name="text">The declaration's value.</param>
    /// <returns>The flags it grants.</returns>
    /// <remarks>
    ///     <para>
    ///         The grammar is
    ///         <c>auto | none | [ [ pan-x | pan-left | pan-right ] || [ pan-y | pan-up | pan-down ] || pinch-zoom ] | manipulation</c>.
    ///         The three standalone words are accepted alone; the list may name each axis at most
    ///         once and <c>pinch-zoom</c> at most once, in any order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unrecognised or repeated word drops the whole declaration to
    ///         <see cref="TouchAction.Auto" /></b>, which is what CSS does with a value it cannot
    ///         parse. Reading <c>pan-x pan-left</c> as the union would silently accept a value every
    ///         browser rejects, and a page written against this engine would then break in one.
    ///     </para>
    /// </remarks>
    internal static TouchAction Parse(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return TouchAction.Auto;
        }

        var result = TouchAction.None;
        var words = 0;
        var horizontal = false;
        var vertical = false;
        var pinch = false;

        foreach (var range in text.AsSpan().Split(' ')) {
            var word = text.AsSpan()[range].Trim();
            if (word.IsEmpty) {
                continue;
            }

            words++;

            // Alone it is `auto`; beside anything it is invalid, which CSS also reads as `auto`.
            if (word.Equals("auto", StringComparison.OrdinalIgnoreCase)) {
                return TouchAction.Auto;
            }

            if (word.Equals("none", StringComparison.OrdinalIgnoreCase)) {
                return IsAlone(text) ? TouchAction.None : TouchAction.Auto;
            }

            if (word.Equals("manipulation", StringComparison.OrdinalIgnoreCase)) {
                return IsAlone(text) ? TouchAction.Manipulation : TouchAction.Auto;
            }

            if (word.Equals("pinch-zoom", StringComparison.OrdinalIgnoreCase)) {
                if (pinch) {
                    return TouchAction.Auto;
                }

                pinch = true;
                result |= TouchAction.PinchZoom;
                continue;
            }

            if (TryHorizontal(word, out var x)) {
                if (horizontal) {
                    return TouchAction.Auto;
                }

                horizontal = true;
                result |= x;
                continue;
            }

            if (TryVertical(word, out var y)) {
                if (vertical) {
                    return TouchAction.Auto;
                }

                vertical = true;
                result |= y;
                continue;
            }

            return TouchAction.Auto;
        }

        return words == 0 ? TouchAction.Auto : result;

        // The three standalone keywords are the whole value or nothing: `none pan-x` is invalid CSS.
        static bool IsAlone(string text) {
            var seen = 0;

            foreach (var range in text.AsSpan().Split(' ')) {
                if (!text.AsSpan()[range].Trim().IsEmpty) {
                    seen++;
                }
            }

            return seen == 1;
        }

        static bool TryHorizontal(ReadOnlySpan<char> word, out TouchAction flag) {
            if (word.Equals("pan-x", StringComparison.OrdinalIgnoreCase)) {
                flag = TouchAction.PanX;
                return true;
            }

            if (word.Equals("pan-left", StringComparison.OrdinalIgnoreCase)) {
                flag = TouchAction.PanLeft;
                return true;
            }

            if (word.Equals("pan-right", StringComparison.OrdinalIgnoreCase)) {
                flag = TouchAction.PanRight;
                return true;
            }

            flag = TouchAction.None;
            return false;
        }

        static bool TryVertical(ReadOnlySpan<char> word, out TouchAction flag) {
            if (word.Equals("pan-y", StringComparison.OrdinalIgnoreCase)) {
                flag = TouchAction.PanY;
                return true;
            }

            if (word.Equals("pan-up", StringComparison.OrdinalIgnoreCase)) {
                flag = TouchAction.PanUp;
                return true;
            }

            if (word.Equals("pan-down", StringComparison.OrdinalIgnoreCase)) {
                flag = TouchAction.PanDown;
                return true;
            }

            flag = TouchAction.None;
            return false;
        }
    }
}

public sealed partial class UiDocument {
    readonly TouchActionReader touchActions = null!;

    /// <summary>What a touch that landed on one element may do to an ancestor that would scroll it.</summary>
    /// <param name="target">The element the finger landed on.</param>
    /// <param name="ancestor">The element asking — the one whose user-agent behaviour is at stake.</param>
    /// <returns>The intersection of <c>touch-action</c> over the chain from the target up to and including the ancestor.</returns>
    /// <remarks>
    ///     <para>
    ///         Pointer Events § 6: <i>"examine the touch-action property of each element between the
    ///         hit tested element and the element with the default touch behavior (including both).
    ///         If the touch-action property of any of those elements disallows the default touch
    ///         behavior, do nothing."</i> An intersection is that sentence as a number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Including both ends.</b> A <c>ScrollView</c> that says <c>touch-action: none</c>
    ///         on itself has declined its own default, and a finger landing on the view's own padding
    ///         has a chain of exactly one element. Excluding either end would make the property
    ///         silently ignored in the two commonest places it is written.
    ///     </para>
    ///     <para>
    ///         A target that is not under the ancestor — a drag whose source was re-parented, or a
    ///         capture that outlived its subtree — is walked to the root and the ancestor's own
    ///         declaration is intersected on top, so the answer is never more permissive than either
    ///         side alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One element's own declaration is this method with the same element twice</b>, and
    ///         there is deliberately no second method that spells it. <c>touch-action</c> does not
    ///         inherit, so a chain of one is exactly the value the cascade resolved on that element —
    ///         which is why <c>TouchActionOf</c>, a public reader that did only that, was a strict
    ///         special case of this one and went the way every finished thing with no caller should.
    ///     </para>
    /// </remarks>
    public TouchAction TouchActionBetween(UiElement target, UiElement ancestor) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ancestor);

        var allowed = TouchAction.Auto;
        var met = false;

        for (var element = target; element is not null; element = element.Parent) {
            allowed &= touchActions.Of(element.Style);

            if (ReferenceEquals(element, ancestor)) {
                met = true;
                break;
            }

            if (allowed == TouchAction.None) {
                break;
            }
        }

        if (!met) {
            allowed &= touchActions.Of(ancestor.Style);
        }

        return allowed;
    }
}
