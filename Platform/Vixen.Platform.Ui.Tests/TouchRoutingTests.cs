// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>A finger reaches the document, and reaches it as its own pointer.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same seam <c>TextCompositionRoutingTests</c> is on, and the same shape of
///         gap.</b> <c>PlatformEventKind.TouchDown</c>, <c>TouchMoved</c> and <c>TouchUp</c> have
///         existed since the platform layer was written, are produced by <c>WebPlatform</c>, have
///         <c>TouchTracker</c> to give their fingers small stable ids and a constructor test of
///         their own — and <c>PlatformInput.Dispatch</c> had no arm for any of them, so every one
///         fell through the <c>default</c> and was dropped. Both halves tested; the join neither.
///     </para>
///     <para>
///         ⚠ <b>And the thing the gap starved is not the one it is filed under.</b> #283 names
///         <c>touch-action</c>, a styling refusal waiting on a touch pipeline. What was actually
///         sitting there unreachable is <c>GestureRecognizer</c>: tap, double tap, long press, drag
///         and two-finger transform, all implemented, all keyed by
///         <see cref="PointerEvent.PointerId" />, and never once fed two distinct ids because the
///         only producer of a pointer event left that field at its default. A pinch cannot be
///         performed with a mouse, so the recogniser's second half had no input at all.
///     </para>
///     <para>
///         ⚠ <b>Which is why the fixture below asserts a <em>pinch</em> and not just an arrival.</b>
///         "The event reached the document" is satisfied by routing every finger as pointer zero,
///         which is the plausible wrong answer: taps and drags still work, and only a gesture that
///         needs two pointers at once can tell the difference. That one comes out as a press whose
///         partner never arrives.
///     </para>
/// </remarks>
public class TouchRoutingTests {
    static UiDocument Documented() {
        var document = new UiDocument(400f, 300f);
        document.Load("root { width: 400px; height: 300px; }");
        document.Update();

        return document;
    }

    static PlatformEvent Touch(PlatformEventKind kind, int finger, float x, float y) =>
        PlatformEvent.Touch(kind, 1, 0, finger, new Vector2(x, y));

    /// <summary>A finger down, moved and lifted arrives as press, move and release.</summary>
    [Fact]
    public void A_finger_reaches_the_document_as_a_pointer() {
        using var document = Documented();

        var seen = new List<PointerAction>();
        document.Root.AddHandler<PointerEvent>((_, args) => seen.Add(args.Action));

        Assert.True(PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchDown, 0, 20f, 20f)));
        Assert.True(PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchMoved, 0, 24f, 20f)));
        Assert.True(PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchUp, 0, 24f, 20f)));

        // ⚠ Contains rather than Equal: the document works out its own `Entered` and `Exited` from
        // where the pointer is and posts them on the same route, so pinning the whole sequence here
        // would make this a test of hover bookkeeping that a change to hover breaks.
        Assert.Contains(PointerAction.Pressed, seen);
        Assert.Contains(PointerAction.Moved, seen);
        Assert.Contains(PointerAction.Released, seen);
    }

    /// <summary>A press carries a button; a move does not, exactly as a mouse's does not.</summary>
    /// <remarks>
    ///     A finger has no buttons, so the choice is a convention rather than a reading: primary,
    ///     because every control in the set decides what a press means by asking which button it was
    ///     and <see cref="PointerButton.None" /> is the answer that means "a hover".
    /// </remarks>
    [Fact]
    public void A_finger_presses_the_primary_button_and_moves_with_none() {
        using var document = Documented();

        var buttons = new Dictionary<PointerAction, PointerButton>();
        document.Root.AddHandler<PointerEvent>((_, args) => buttons[args.Action] = args.Button);

        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchDown, 0, 20f, 20f));
        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchMoved, 0, 24f, 20f));
        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchUp, 0, 24f, 20f));

        Assert.Equal(PointerButton.Primary, buttons[PointerAction.Pressed]);
        Assert.Equal(PointerButton.None, buttons[PointerAction.Moved]);
        Assert.Equal(PointerButton.Primary, buttons[PointerAction.Released]);
    }

    /// <summary>A finger and the mouse are never the same pointer.</summary>
    /// <remarks>
    ///     ⚠ <b>The collision is real rather than theoretical.</b> <c>TouchTracker</c> hands out the
    ///     lowest free finger starting at zero, and a mouse event's <c>PointerId</c> is zero because
    ///     nothing ever set it — so the first finger on a hybrid device, or in any browser, would be
    ///     the mouse. The recogniser keys its presses by that id, so the failure is a press closed by
    ///     a release from the other device: a stylus drag that ends when the trackpad is touched.
    /// </remarks>
    [Fact]
    public void A_finger_and_the_mouse_are_different_pointers() {
        using var document = Documented();

        var pointers = new List<int>();
        document.Root.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Pressed) {
                    pointers.Add(args.PointerId);
                }
            }
        );

        PlatformInput.Dispatch(
            document,
            PlatformEvent.MouseButtonChanged(PlatformEventKind.MouseButtonDown, 1, 0, MouseButton.Primary, new Vector2(20f, 20f))
        );

        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchDown, 0, 20f, 20f));

        Assert.Equal(2, pointers.Count);
        Assert.NotEqual(pointers[0], pointers[1]);
    }

    /// <summary>And a finger says it is a finger, which no id range can be asked.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The floor under this is the assertion that the two values <i>differ</i>, not that
    ///         either is right.</b> A build in which <see cref="PointerEvent.PointerType" /> exists
    ///         and no producer sets it delivers <see cref="PointerType.Unknown" /> for both, which is
    ///         a consistent, plausible, and entirely useless answer — and it is the state this
    ///         repository was in until #699. Reading both from one route and comparing them is what
    ///         a per-arm assertion could not do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is <em>not</em> the same claim as
    ///         <see cref="A_finger_and_the_mouse_are_different_pointers" />.</b> That one says the
    ///         two are numbered apart, which is a collision-avoidance measure: it is satisfied by a
    ///         second mouse being given finger one. <c>touch-action</c> governs touch and nothing
    ///         else, so the question it has to ask at the arbitration point is what the device
    ///         <i>is</i> — applying it to a mouse would stop a map responding to a mouse drag, which
    ///         no browser does and no author expects.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_finger_and_the_mouse_arrive_as_different_kinds_of_device() {
        using var document = Documented();

        var kinds = new List<PointerType>();
        document.Root.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Pressed) {
                    kinds.Add(args.PointerType);
                }
            }
        );

        PlatformInput.Dispatch(
            document,
            PlatformEvent.MouseButtonChanged(
                PlatformEventKind.MouseButtonDown,
                1,
                0,
                MouseButton.Primary,
                new Vector2(20f, 20f)
            )
        );

        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchDown, 0, 20f, 20f));

        Assert.Equal(2, kinds.Count);
        Assert.NotEqual(kinds[0], kinds[1]);
        Assert.Equal(PointerType.Mouse, kinds[0]);
        Assert.Equal(PointerType.Touch, kinds[1]);
    }

    /// <summary>And the crossings the document invents for itself keep the device that caused them.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="PointerAction.Entered" /> and <see cref="PointerAction.Exited" /> are
    ///     never fed in from outside</b> — <c>Hover</c> works them out from where the pointer is and
    ///     posts them itself. So they are the one place in a touch sequence where the device could
    ///     be lost, and a reader that classified a sequence by its crossings would see
    ///     <see cref="PointerType.Unknown" /> for a frame with the field set correctly everywhere
    ///     else.
    /// </remarks>
    [Fact]
    public void A_synthesised_crossing_keeps_the_device_that_caused_it() {
        using var document = Documented();

        var crossings = new List<PointerType>();

        // ⚠ `RoutingStrategy.Direct`, and the default would have made this test vacuous. A crossing
        // is delivered direct rather than bubbled — that asymmetry is what `Hover.Crossed` documents
        // — so a handler registered the usual way never hears one, and `Assert.All` over an empty
        // list passes.
        document.Root.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action is PointerAction.Entered or PointerAction.Exited) {
                    crossings.Add(args.PointerType);
                }
            },
            RoutingStrategy.Direct
        );

        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchDown, 0, 20f, 20f));
        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchMoved, 0, 24f, 20f));

        Assert.NotEmpty(crossings);
        Assert.All(crossings, kind => Assert.Equal(PointerType.Touch, kind));
    }

    /// <summary>Two fingers spreading apart are a pinch, which is the gesture only touch can make.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the assertion that says the routing is <i>right</i> and not merely
    ///         present.</b> <c>GestureRecognizer.Pair</c> only considers a transform once two
    ///         distinct pointer ids are pressed at the same time, so a router that delivered every
    ///         finger as the same pointer would satisfy every other fixture in this file and leave
    ///         this one silent — the second press would overwrite the first and there would never be
    ///         two.
    ///     </para>
    ///     <para>
    ///         The separation goes from 40 to 200, which is well past the eight pixels of
    ///         <c>TouchSlop</c> that separate a pinch from two fingers resting. Expressed as the
    ///         scale it produces rather than as a distance: five times apart is a scale of five,
    ///         whatever the recogniser's threshold is set to.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_fingers_spreading_apart_are_a_pinch() {
        using var document = Documented();

        var stages = new List<TransformStage>();
        var scale = 0f;

        document.Root.AddHandler<TransformEvent>(
            (_, args) => {
                stages.Add(args.Stage);
                scale = args.Scale;
            }
        );

        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchDown, 0, 180f, 150f));
        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchDown, 1, 220f, 150f));

        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchMoved, 0, 100f, 150f));
        PlatformInput.Dispatch(document, Touch(PlatformEventKind.TouchMoved, 1, 300f, 150f));

        Assert.Contains(TransformStage.Started, stages);

        // 200 apart where they began 40 apart. ⚠ The number is the oracle: a fixture that only
        // asked "was a transform raised" would pass against a recogniser that reported every pinch
        // as a scale of one.
        Assert.Equal(5f, scale, 0.01f);
    }
}
