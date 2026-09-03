// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Testing;

/// <summary>The assertions. Every one of them waits.</summary>
/// <remarks>
///     <para>
///         Named methods rather than Cypress's <c>should("be.visible")</c> strings, because C# can do
///         better than a string: the set is discoverable by typing a dot, a typo is a compile error
///         rather than a test that passes, and each one can take the type its subject actually has.
///     </para>
///     <para>
///         <b>An assertion holds for every matched element and needs at least one.</b> Both halves
///         matter. Without the second, <c>Get(".error").ShouldBeVisible()</c> passes when nothing
///         matched — vacuously true, and the single most common way a UI suite comes to assert
///         nothing at all. Without the first, an assertion over three elements would have to pick
///         one, and any rule for picking is a rule somebody will be surprised by.
///     </para>
/// </remarks>
public sealed partial class UiSubject {
    /// <summary>At least one element matches.</summary>
    /// <returns>This, so it reads as part of a chain.</returns>
    public UiSubject ShouldExist() =>
        Assert("should exist", matched => matched.Count > 0 ? null : "no elements");

    /// <summary>Nothing matches.</summary>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ Passes on the frame it is asked, without waiting, because it is already true. That is
    ///     what a negative assertion means and it is also its weakness: an element about to appear
    ///     satisfies "does not exist" right up until it does. Assert the thing that made it go away
    ///     instead when there is one — <c>Get(".dialog").ShouldNotExist()</c> after
    ///     <c>Get(".backdrop").ShouldNotExist()</c> says the same thing and is racy in the same way.
    /// </remarks>
    public UiSubject ShouldNotExist() =>
        Assert("should not exist", matched => matched.Count == 0 ? null : Describe(matched));

    /// <summary>Exactly this many match.</summary>
    /// <param name="count">How many.</param>
    /// <returns>This.</returns>
    public UiSubject ShouldHaveCount(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return Assert($"should have {count} elements", matched => matched.Count == count ? null : Describe(matched));
    }

    /// <summary>Every match would put ink on the screen.</summary>
    /// <returns>This.</returns>
    /// <remarks>
    ///     <para>
    ///         Four ways to be invisible, and it checks all four: removed from the tree, an empty
    ///         rectangle on the element or any ancestor (which is how <c>display: none</c> and a
    ///         collapsed container both arrive), <c>visibility: hidden</c>, and an <c>opacity</c> of
    ///         zero anywhere above it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It agrees with the picture because the picture agrees with it.</b> These are the
    ///         same four tests <see cref="DrawListBuilder" /> applies when deciding whether to emit
    ///         anything — an assertion that read a property the renderer ignored would be worse than
    ///         one that never checked it, because it would fail on an element that is plainly there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it still does not check</b>, said plainly: whether the element is inside the
    ///         viewport, and whether anything is on top of it. <see cref="ShouldBeHittable" /> is the
    ///         one that answers the second, and it is the one to use before a click.
    ///     </para>
    /// </remarks>
    public UiSubject ShouldBeVisible() =>
        Assert("should be visible", matched => Every(matched, IsVisible, element => Invisibility(element) ?? "not visible"));

    /// <summary>No match is visible, or nothing matches.</summary>
    /// <returns>This.</returns>
    public UiSubject ShouldNotBeVisible() =>
        Assert(
            "should not be visible",
            matched => matched.All(element => !IsVisible(element))
                ? null
                : Describe(matched.Where(IsVisible).ToList()) + " visible"
        );

    /// <summary>A click at the centre of every match would actually reach it.</summary>
    /// <returns>This.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assertion worth having, and the reason is a bug class rather than a nicety.</b>
    ///         A modal backdrop, a full-screen overlay left up by a state machine, a tooltip that
    ///         forgot <c>pointer-events: none</c> — every one of these leaves a button visible, laid
    ///         out and completely unclickable, and a suite that asserts visibility passes while the
    ///         game is unplayable. This hit-tests the centre and fails naming <i>what it hit
    ///         instead</i>, which turns a mystifying "the click did nothing" into a sentence.
    ///     </para>
    ///     <para>
    ///         Descendants count as reaching it: a button whose centre lands on its own label has
    ///         been clicked by anybody's definition, and the event routes to the button by bubbling.
    ///     </para>
    /// </remarks>
    public UiSubject ShouldBeHittable() =>
        Assert("should be hittable", matched => {
            if (matched.Count == 0) {
                return "no elements";
            }

            foreach (var element in matched) {
                if (Blocker(element) is { } blocker) {
                    return blocker;
                }
            }

            return null;
        });

    /// <summary>Every match has exactly this text.</summary>
    /// <param name="text">What it should say.</param>
    /// <returns>This.</returns>
    public UiSubject ShouldHaveText(string text) {
        ArgumentNullException.ThrowIfNull(text);

        return Assert(
            $"should have text \"{text}\"",
            matched => Every(
                matched,
                element => string.Equals(element.Text, text, StringComparison.Ordinal),
                element => $"says \"{element.Text}\""
            )
        );
    }

    /// <summary>Every match's text contains this.</summary>
    /// <param name="text">What it should include.</param>
    /// <returns>This.</returns>
    public UiSubject ShouldContainText(string text) {
        ArgumentNullException.ThrowIfNull(text);

        return Assert(
            $"should contain text \"{text}\"",
            matched => Every(
                matched,
                element => element.Text is { } value && value.Contains(text, StringComparison.Ordinal),
                element => $"says \"{element.Text}\""
            )
        );
    }

    /// <summary>Every match has this class.</summary>
    /// <param name="className">Which class.</param>
    /// <returns>This.</returns>
    public UiSubject ShouldHaveClass(string className) {
        ArgumentNullException.ThrowIfNull(className);

        return Assert(
            $"should have class \"{className}\"",
            matched => Every(matched, element => element.HasClass(className), $"has no .{className}")
        );
    }

    /// <summary>No match has this class.</summary>
    /// <param name="className">Which class.</param>
    /// <returns>This.</returns>
    public UiSubject ShouldNotHaveClass(string className) {
        ArgumentNullException.ThrowIfNull(className);

        return Assert(
            $"should not have class \"{className}\"",
            matched => Every(matched, element => !element.HasClass(className), $"has .{className}")
        );
    }

    /// <summary>Every match is in this state.</summary>
    /// <param name="state">Which flags must be set. Others may be too.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     The pseudo-classes, as a test can ask about them: <see cref="ElementState.Hover" />,
    ///     <see cref="ElementState.Active" />, <see cref="ElementState.Checked" />,
    ///     <see cref="ElementState.Disabled" />. What a stylesheet reads is what a test reads, so an
    ///     assertion cannot drift from the rule that draws the thing it is about.
    /// </remarks>
    public UiSubject ShouldHaveState(ElementState state) =>
        Assert(
            $"should be {state}",
            matched => Every(
                matched,
                element => (element.State & state) == state,
                element => $"is {element.State}"
            )
        );

    /// <summary>The one match has the focus.</summary>
    /// <returns>This.</returns>
    public UiSubject ShouldBeFocused() =>
        Assert(
            "should be focused",
            matched => matched.Count == 1 && matched[0].IsFocused
                ? null
                : Test.Document.Focused is { } focused
                    ? $"the focus is on {Test.Describe(focused)}"
                    : "nothing has the focus"
        );

    /// <summary>Every match resolves a property to this value, as the cascade recorded it.</summary>
    /// <param name="property">The property, as a stylesheet writes it.</param>
    /// <param name="value">The value, as the cascade interned it.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     <para>
    ///         The resolved value, which is not always the written one: ExCSS normalises as it
    ///         parses, so <c>#3b82f6</c> comes back as <c>rgba(59, 130, 246, 1)</c> and
    ///         <c>16 / 9</c> as <c>16/9</c>. That makes string comparison the wrong tool for colours
    ///         and lengths, which is why <see cref="ShouldHaveColor" /> and
    ///         <see cref="ShouldHaveLength" /> exist beside this rather than under it. Use this for
    ///         keywords — <c>flex-direction</c>, <c>overflow</c>, <c>position</c>, <c>visibility</c> —
    ///         where the value is an identifier and the comparison is exact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Shorthands do not survive to here.</b> ExCSS expands <c>margin</c>,
    ///         <c>border-color</c> and <c>border-radius</c> on parse, exactly as a browser does, so
    ///         the cascade never holds them — ask for <c>margin-left</c> and
    ///         <c>border-top-left-radius</c>. The same thing the styling-to-layout bridge found, and
    ///         a test written against the shorthand fails saying the property is absent.
    ///     </para>
    /// </remarks>
    public UiSubject ShouldHaveStyle(string property, string value) {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);

        return Assert(
            $"should have {property}: {value}",
            matched => Every(
                matched,
                element => string.Equals(Test.StyleOf(element, property), value, StringComparison.Ordinal),
                element => Test.StyleOf(element, property) is { } resolved
                    ? $"has {property}: {resolved}"
                    : $"has no {property}"
            )
        );
    }

    /// <summary>Every match resolves a property to this colour.</summary>
    /// <param name="property">The property, such as <c>background-color</c> or <c>color</c>.</param>
    /// <param name="expected">The colour, in the linear space the cascade resolves to.</param>
    /// <param name="tolerance">How far each channel may be out.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     Parsed rather than compared as text, so a test may write the colour it means rather than
    ///     the spelling ExCSS happened to normalise it to.
    /// </remarks>
    public UiSubject ShouldHaveColor(string property, Color4 expected, float tolerance = 0.004f) {
        ArgumentNullException.ThrowIfNull(property);

        return Assert(
            $"should have {property} ≈ {expected}",
            matched => Every(
                matched,
                element => Test.ColorOf(element, property) is { } actual && Near(actual, expected, tolerance),
                element => Test.ColorOf(element, property) is { } actual
                    ? $"has {property} {actual}"
                    : $"has no {property}, or it is not a colour"
            )
        );
    }

    /// <summary>Every match resolves a property to this many pixels.</summary>
    /// <param name="property">The property, such as <c>border-top-left-radius</c>.</param>
    /// <param name="expected">The length in pixels.</param>
    /// <param name="tolerance">How far it may be out.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ Absolute lengths only. A percentage resolves against a containing block the cascade
    ///     cannot see and an <c>em</c> against a font size that lives on the element — a test that
    ///     wants either is asking about the <i>layout</i>, and should read <see cref="UiElement.Width" />
    ///     and its neighbours, which have had both resolved.
    /// </remarks>
    public UiSubject ShouldHaveLength(string property, float expected, float tolerance = 0.01f) {
        ArgumentNullException.ThrowIfNull(property);

        return Assert(
            $"should have {property} ≈ {expected:0.##}px",
            matched => Every(
                matched,
                element => Test.LengthOf(element, property) is { } actual
                    && MathF.Abs(actual - expected) <= tolerance,
                element => Test.LengthOf(element, property) is { } actual
                    ? $"has {property} {actual:0.##}px"
                    : $"has no {property}, or it is not an absolute length"
            )
        );
    }

    /// <summary>Every match is laid out this size.</summary>
    /// <param name="width">The border box's width, in points.</param>
    /// <param name="height">The border box's height, in points.</param>
    /// <param name="tolerance">How far either may be out.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The layout box, not the declaration</b>, and that is the whole of what this buys
    ///         over <see cref="ShouldHaveLength" />. A percentage, an <c>em</c>, <c>flex-grow</c>,
    ///         <c>min-width</c> and a flex line's free space are all invisible to the cascade and all
    ///         decided by the layout pass — so the one question a stylesheet cannot answer is how big
    ///         the box came out, which is this one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it waits, which is why reading <see cref="UiElement.Width" /> is not the same
    ///         thing.</b> Layout runs inside <see cref="UiTest.Frame" />, so an element created or
    ///         restyled by the line above has a width of zero until a frame runs; a test reading the
    ///         property directly either sees the previous arrangement or has a hand-written
    ///         <c>Frames(n)</c> in front of it, guessing at <i>n</i>. This runs frames until the box
    ///         is the size claimed, and fails saying what size it settled at.
    ///     </para>
    /// </remarks>
    public UiSubject ShouldHaveSize(float width, float height, float tolerance = 0.01f) =>
        Assert(
            $"should be {width:0.##}×{height:0.##}",
            matched => Every(
                matched,
                element => MathF.Abs(element.Width - width) <= tolerance
                    && MathF.Abs(element.Height - height) <= tolerance,
                element => $"is {element.Width:0.##}×{element.Height:0.##}"
            )
        );

    /// <summary>Every match's border box starts at this point in document space.</summary>
    /// <param name="left">The distance from the document's left edge, in points.</param>
    /// <param name="top">The distance from the document's top edge, in points.</param>
    /// <param name="tolerance">How far either may be out.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ <b>Absolute, not <see cref="UiElement.Left" />.</b> The relative pair is an offset within
    ///     whatever the parent turned out to be, so an assertion on it passes on a panel that moved
    ///     and took the element with it. Document space is the space a click, a hit test and a
    ///     screenshot are all in — <see cref="Centre" /> is computed from these two — so an assertion
    ///     here is an assertion about where the thing actually is.
    /// </remarks>
    public UiSubject ShouldHavePosition(float left, float top, float tolerance = 0.01f) =>
        Assert(
            $"should be at ({left:0.##}, {top:0.##})",
            matched => Every(
                matched,
                element => MathF.Abs(element.AbsoluteLeft - left) <= tolerance
                    && MathF.Abs(element.AbsoluteTop - top) <= tolerance,
                element => $"is at ({element.AbsoluteLeft:0.##}, {element.AbsoluteTop:0.##})"
            )
        );

    /// <summary>Every match satisfies a predicate.</summary>
    /// <param name="predicate">What must hold.</param>
    /// <param name="description">How it reads in a log line and a failure.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     The extension point. A control with a notion this assembly has never heard of is still
    ///     waitable, and the waiting is the part that is hard to write by hand.
    /// </remarks>
    public UiSubject ShouldMatch(Func<UiElement, bool> predicate, string description) {
        ArgumentNullException.ThrowIfNull(predicate);

        return Assert($"should {description}", matched => Every(matched, predicate, $"does not {description}"));
    }

    /// <summary>Whether an element would put ink on the screen.</summary>
    internal bool IsVisible(UiElement element) => Invisibility(element) is null;

    /// <summary>Why an element would not put ink on the screen, or <c>null</c> if it would.</summary>
    /// <remarks>
    ///     One method rather than a predicate and a separate explanation, so the reason in the
    ///     failure message cannot drift from the test that produced it. "Not visible" is a true
    ///     statement that sends somebody to read a stylesheet; "its .panel ancestor has opacity 0" is
    ///     the answer.
    /// </remarks>
    internal string? Invisibility(UiElement element) {
        if (element.IsRemoved) {
            return "has been removed from the tree";
        }

        // `visibility` is inherited, so an element's own computed value already carries whatever an
        // ancestor declared — which is why this is not a walk and `opacity` below is.
        if (string.Equals(Test.StyleOf(element, "visibility"), Hidden, StringComparison.Ordinal)) {
            return "has visibility: hidden";
        }

        for (var candidate = element; candidate is not null; candidate = candidate.Parent) {
            // The root is allowed to be the viewport and nothing else; every other ancestor holding a
            // zero rectangle has taken this element off the screen with it. `display: none` arrives
            // here as a zero size, because the layout tree is where it is honoured.
            if (candidate.Width <= 0f || candidate.Height <= 0f) {
                return ReferenceEquals(candidate, element)
                    ? $"has an empty rectangle ({candidate.Width:0.#}×{candidate.Height:0.#})"
                    : $"is inside {Test.Describe(candidate)}, which has an empty rectangle";
            }

            // ⚠ Zero only, not "very small". Opacity multiplies down the tree, so any zero above the
            // element makes the product zero and the draw list emits nothing — which is exactly the
            // test, and the reason this walks where `visibility` does not.
            if (Test.NumberOf(candidate, Opacity) is <= 0f) {
                return ReferenceEquals(candidate, element)
                    ? "has opacity 0"
                    : $"is inside {Test.Describe(candidate)}, which has opacity 0";
            }
        }

        return null;
    }

    const string Hidden = "hidden";

    const string Opacity = "opacity";

    /// <summary>Where a click at an element's centre would land.</summary>
    internal static (float X, float Y) Centre(UiElement element) =>
        (element.AbsoluteLeft + (element.Width / 2f), element.AbsoluteTop + (element.Height / 2f));

    /// <summary>What is in the way of an element, or <c>null</c> if nothing is.</summary>
    internal string? Blocker(UiElement element) {
        if (Invisibility(element) is { } why) {
            return $"{Test.Describe(element)} {why}";
        }

        var (x, y) = Centre(element);
        var hit = Test.Document.HitTest(x, y);

        if (hit is null) {
            return $"nothing is at ({x:0.#}, {y:0.#}), the centre of {Test.Describe(element)}";
        }

        for (var candidate = hit; candidate is not null; candidate = candidate.Parent) {
            if (ReferenceEquals(candidate, element)) {
                return null;
            }
        }

        return $"{Test.Describe(hit)} is on top of {Test.Describe(element)} at its centre "
            + $"({x:0.#}, {y:0.#})";
    }

    static bool Near(Color4 actual, Color4 expected, float tolerance) =>
        MathF.Abs(actual.R - expected.R) <= tolerance
        && MathF.Abs(actual.G - expected.G) <= tolerance
        && MathF.Abs(actual.B - expected.B) <= tolerance
        && MathF.Abs(actual.A - expected.A) <= tolerance;

    UiSubject Assert(string command, Func<List<UiElement>, string?> condition) {
        Await(command, condition);
        return this;
    }

    string? Every(List<UiElement> matched, Func<UiElement, bool> predicate, string complaint) =>
        Every(matched, predicate, _ => complaint);

    string? Every(List<UiElement> matched, Func<UiElement, bool> predicate, Func<UiElement, string> complaint) {
        if (matched.Count == 0) {
            return "no elements";
        }

        foreach (var element in matched) {
            if (!predicate(element)) {
                return $"{Test.Describe(element)} {complaint(element)}";
            }
        }

        return null;
    }
}
