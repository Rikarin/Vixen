// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;

namespace Vixen.Ui.Testing;

/// <summary>The actions. Every one of them waits for the element to be worth acting on first.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here reaches into the framework to make something happen.</b> A click is a
///         <see cref="PointerEvent" /> at a real coordinate, dispatched through
///         <see cref="UiDocument.Dispatch(PointerEvent)" />, hit-tested and routed like any other —
///         so capture, <c>pointer-events</c>, the <c>:active</c> state and the gesture recogniser are
///         all under test rather than bypassed. Calling a control's <c>OnClick</c> directly would
///         make every test pass on an interface where the button is behind a modal.
///     </para>
///     <para>
///         <b>Every action ends with a frame</b>, because that is what an application does: input
///         arrives, then the loop runs. Without it the element a handler just created has no layout,
///         and the next assertion would spend a retry discovering that.
///     </para>
/// </remarks>
public sealed partial class UiSubject {
    /// <summary>How many pointer moves a drag is broken into.</summary>
    /// <remarks>
    ///     ⚠ More than one, and not for realism. The gesture recogniser turns a press into a drag
    ///     only once the pointer has wandered past its slop threshold, and it reads that from the
    ///     moves it is given — a press followed by one move to the destination and a release is a
    ///     tap in a different place as far as anything downstream is concerned.
    /// </remarks>
    const int DragSteps = 8;

    /// <summary>Clicks the middle of it.</summary>
    /// <param name="button">Which button.</param>
    /// <param name="force">Skip the check that the click would actually land on it.</param>
    /// <returns>This, so it reads as part of a chain.</returns>
    /// <remarks>
    ///     ⚠ <paramref name="force" /> exists for the tests that are deliberately about something in
    ///     the way, and is a mistake everywhere else: it turns "the button is behind the modal" from
    ///     a failure that names the modal into a click that goes to the modal and a puzzling
    ///     assertion failure three lines later.
    /// </remarks>
    public UiSubject Click(PointerButton button = PointerButton.Primary, bool force = false) {
        var element = Actionable($"click{(button == PointerButton.Primary ? string.Empty : $" {button}")}", force);
        var (x, y) = Centre(element);

        Test.MovePointer(x, y);
        Test.PressPointer(button);
        Test.ReleasePointer(button);
        Test.Frame();
        return this;
    }

    /// <summary>Clicks it twice, close enough together to be a double tap.</summary>
    /// <param name="force">Skip the check that the click would land on it.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     ⚠ The two clicks are one frame apart, which is well inside
    ///     <see cref="GestureSettings.Default" />'s window. A test that wants them <i>outside</i> it
    ///     — two single taps rather than a double — calls <see cref="Click" /> twice with an
    ///     <see cref="UiTest.Advance" /> between them, which says what it means.
    /// </remarks>
    public UiSubject DoubleClick(bool force = false) {
        Click(PointerButton.Primary, force);
        Click(PointerButton.Primary, force);
        return this;
    }

    /// <summary>Clicks it with the right button.</summary>
    /// <param name="force">Skip the check that the click would land on it.</param>
    /// <returns>This.</returns>
    public UiSubject RightClick(bool force = false) => Click(PointerButton.Secondary, force);

    /// <summary>Moves the pointer onto the middle of it.</summary>
    /// <returns>This.</returns>
    /// <remarks>
    ///     What sets <c>:hover</c> on it and on its ancestors, and what raises <c>Entered</c> and
    ///     <c>Exited</c> on everything the pointer crossed — the document works those out itself, so
    ///     a test gets them by moving a pointer rather than by saying so.
    /// </remarks>
    public UiSubject Hover() {
        var element = Actionable("hover", force: false);
        var (x, y) = Centre(element);

        Test.MovePointer(x, y);
        Test.Frame();
        return this;
    }

    /// <summary>Presses a button on it and leaves it down.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>This.</returns>
    /// <remarks>Half of a drag, and what a long-press test presses before it advances the clock.</remarks>
    public UiSubject Press(PointerButton button = PointerButton.Primary) {
        var element = Actionable("press", force: false);
        var (x, y) = Centre(element);

        Test.MovePointer(x, y);
        Test.PressPointer(button);
        Test.Frame();
        return this;
    }

    /// <summary>Releases a button where the pointer is.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     Does not move the pointer and does not check what is under it. A release belongs to the
    ///     press that started it — the document sends it to whatever captured the pointer — so a
    ///     release that first walked back to this element's centre would be a different gesture.
    /// </remarks>
    public UiSubject Release(PointerButton button = PointerButton.Primary) {
        Test.ReleasePointer(button);
        Test.Frame();
        return this;
    }

    /// <summary>Drags from the middle of it to a point.</summary>
    /// <param name="x">Where to, in document pixels.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>This.</returns>
    public UiSubject DragTo(float x, float y) {
        var element = Actionable($"drag to ({x:0.#}, {y:0.#})", force: false);
        var (fromX, fromY) = Centre(element);

        Test.MovePointer(fromX, fromY);
        Test.PressPointer();
        Test.Frame();

        for (var step = 1; step <= DragSteps; step++) {
            var t = (float)step / DragSteps;
            Test.MovePointer(fromX + ((x - fromX) * t), fromY + ((y - fromY) * t));
            Test.Frame();
        }

        Test.ReleasePointer();
        Test.Frame();
        return this;
    }

    /// <summary>Drags from the middle of it by an offset.</summary>
    /// <param name="deltaX">How far sideways.</param>
    /// <param name="deltaY">How far down.</param>
    /// <returns>This.</returns>
    public UiSubject DragBy(float deltaX, float deltaY) {
        var (x, y) = Centre(Await("drag", Exactly(1))[0]);
        return DragTo(x + deltaX, y + deltaY);
    }

    /// <summary>Puts two fingers on it and pinches, rotates, or both.</summary>
    /// <param name="scale">
    ///     How far apart the fingers end up, as a multiple of how far apart they started. Two spreads
    ///     them to twice the distance; a half brings them to half.
    /// </param>
    /// <param name="rotation">How far to turn the line between them, in degrees, clockwise.</param>
    /// <param name="separation">How far apart to put them to begin with, in pixels.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     <para>
    ///         The fingers start either side of the element's centre and move symmetrically, so the
    ///         midpoint stays put and the gesture is a pure scale and rotation — which is what a test
    ///         asserting on <see cref="TransformEvent.Scale" /> means, and what a hand-written version
    ///         gets wrong by moving one finger and wondering why the centroid drifted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Degrees here, radians on the event.</b> The event is what a transform matrix
    ///         consumes and radians are what the trigonometry wants; a test is written by a person,
    ///         and nobody means <c>MathF.PI / 6</c> when they mean thirty degrees.
    ///     </para>
    ///     <para>
    ///         ⚠ Broken into steps, for the reason <see cref="DragTo" /> is: the recogniser decides a
    ///         pinch has begun by watching the separation change, and one jump from the start to the
    ///         end gives it a single sample to notice with.
    ///     </para>
    /// </remarks>
    public UiSubject Pinch(float scale, float rotation = 0f, float separation = 40f) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(separation);

        var element = Actionable($"pinch ×{scale:0.##}{(rotation == 0f ? string.Empty : $" {rotation:0.#}°")}", force: false);
        var (x, y) = Centre(element);
        var half = separation / 2f;

        Test.MovePointer(x - half, y, UiTest.PointerId);
        Test.PressPointer(pointer: UiTest.PointerId);
        Test.MovePointer(x + half, y, UiTest.SecondPointerId);
        Test.PressPointer(pointer: UiTest.SecondPointerId);
        Test.Frame();

        for (var step = 1; step <= DragSteps; step++) {
            var t = (float)step / DragSteps;
            var reach = half * (1f + ((scale - 1f) * t));
            var angle = float.DegreesToRadians(rotation) * t;

            var dx = MathF.Cos(angle) * reach;
            var dy = MathF.Sin(angle) * reach;

            Test.MovePointer(x - dx, y - dy, UiTest.PointerId);
            Test.MovePointer(x + dx, y + dy, UiTest.SecondPointerId);
            Test.Frame();
        }

        Test.ReleasePointer(pointer: UiTest.SecondPointerId);
        Test.ReleasePointer(pointer: UiTest.PointerId);
        Test.Frame();
        return this;
    }

    /// <summary>Turns the wheel over it.</summary>
    /// <param name="deltaX">How far sideways.</param>
    /// <param name="deltaY">How far down.</param>
    /// <returns>This.</returns>
    public UiSubject Scroll(float deltaX, float deltaY) {
        var element = Actionable($"scroll ({deltaX:0.#}, {deltaY:0.#})", force: false);
        var (x, y) = Centre(element);

        Test.MovePointer(x, y);
        Test.Wheel(deltaX, deltaY);
        Test.Frame();
        return this;
    }

    /// <summary>Gives it the focus.</summary>
    /// <returns>This.</returns>
    /// <exception cref="UiTestException">It would not take the focus.</exception>
    public UiSubject Focus() {
        var element = Await("focus", Exactly(1))[0];

        if (!Test.Document.Focus(element)) {
            throw UiTestException.Build(
                $"{Description} · focus",
                $"{Test.Describe(element)} refused the focus — Focusable is false, or it is not in the tree",
                0,
                Test.Log,
                Test.Tree()
            );
        }

        Test.Frame();
        return this;
    }

    /// <summary>Takes the focus away from everything.</summary>
    /// <returns>This.</returns>
    public UiSubject Blur() {
        Test.Document.Focus(null);
        Test.Frame();
        return this;
    }

    /// <summary>Focuses it and types.</summary>
    /// <param name="text">What to type.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One event per character, not one for the string.</b> A text box that appends what
    ///         it is given passes either way; one that maintains a caret, a selection or an undo
    ///         stack does not, and the per-character path is the one a keyboard produces.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Text only. No <c>{enter}</c> escape.</b> Cypress packs key names into the string
    ///         because JavaScript has nothing better; here <see cref="PressKey" /> takes an
    ///         <see cref="InputKey" />, which the compiler checks and which cannot be confused with
    ///         somebody genuinely wanting to type the six characters <c>{enter}</c> into a search box.
    ///     </para>
    ///     <para>
    ///         Runes rather than chars, so an emoji or anything else outside the basic plane arrives
    ///         as one input rather than as two halves of a surrogate pair.
    ///     </para>
    /// </remarks>
    public UiSubject Type(string text) {
        ArgumentNullException.ThrowIfNull(text);

        Focus();
        var index = Test.Log.Begin($"{Description} · type \"{text}\"");

        foreach (var rune in text.EnumerateRunes()) {
            Test.TypeText(rune.ToString());
            Test.Frame();
        }

        Test.Log.Complete(index, null, 0);
        return this;
    }

    /// <summary>Focuses it and presses a key.</summary>
    /// <param name="key">Which physical key, by its US-QWERTY legend.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     Whatever <see cref="UiTest.Hold" /> is holding travels with it, so a shortcut reads as
    ///     <c>ui.Hold(ModifierKeys.Control); ui.Get("#editor").PressKey(InputKey.S);</c>.
    /// </remarks>
    public UiSubject PressKey(InputKey key) {
        Focus();
        var index = Test.Log.Begin($"{Description} · press {key}");
        Test.PressKey(key);
        Test.Frame();
        Test.Log.Complete(index, null, 0);
        return this;
    }

    /// <summary>Waits until there is exactly one element and something could be done to it.</summary>
    /// <param name="command">What is about to be done, as it reads in the log.</param>
    /// <param name="force">Whether to skip the check that a click would land on it.</param>
    /// <returns>The element.</returns>
    /// <remarks>
    ///     ⚠ <b>One element, not the first of several.</b> Cypress makes the same choice and it is
    ///     worth the friction: a selector that matched three buttons and clicked the first is a test
    ///     that keeps passing after somebody adds a fourth in front, having silently changed what it
    ///     tests. <c>First()</c> says so out loud when that is genuinely what is wanted.
    /// </remarks>
    UiElement Actionable(string command, bool force) {
        var matched = Await(command, matched => {
            if (matched.Count != 1) {
                return Describe(matched);
            }

            return force ? null : Blocker(matched[0]);
        });

        return matched[0];
    }
}
