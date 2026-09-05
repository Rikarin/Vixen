// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Input;
using Vixen.Ui.Text;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>A document with both themes in it and a way to drive it.</summary>
/// <remarks>
///     Both sheets, in order: the advanced theme is written against the base theme's tokens, and a
///     custom property nothing declared substitutes to nothing — which would leave every docked
///     surface transparent and every test about colours quietly meaningless.
/// </remarks>
sealed class AdvancedFixture : IDisposable {
    static readonly FontFace Font = LoadFont();

    TimeSpan clock;

    public AdvancedFixture(float width = 800f, float height = 600f, string? css = null) {
        Document = new UiDocument(width, height);
        Document.Fonts.Register("Test", Font);

        ControlTheme.Install(Document);
        AdvancedTheme.Install(Document);

        Document.Load($"root {{ width: {width}px; height: {height}px; }}");

        if (css is not null) {
            Document.Load(css);
        }
    }

    public UiDocument Document { get; }

    public T Add<T>() where T : UiElement, new() {
        var control = Document.Root.Add<T>();
        Update();

        return control;
    }

    public void Update() {
        Document.Update();
        Document.Draw();
    }

    public void Click(UiElement element, ModifierKeys modifiers = ModifierKeys.None) {
        var centre = Centre(element);

        Press(centre.X, centre.Y, modifiers: modifiers);
        Release(centre.X, centre.Y, modifiers: modifiers);
    }

    /// <summary>The middle of an element, in document space — where a pointer test aims.</summary>
    public static (float X, float Y) Centre(UiElement element) {
        var bounds = element.Bounds;
        return (bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
    }

    public void Press(
        float x,
        float y,
        PointerButton button = PointerButton.Primary,
        ModifierKeys modifiers = ModifierKeys.None
    ) => Send(x, y, PointerAction.Pressed, button, modifiers);

    public void Move(float x, float y, ModifierKeys modifiers = ModifierKeys.None) =>
        Send(x, y, PointerAction.Moved, PointerButton.None, modifiers);

    public void Release(
        float x,
        float y,
        PointerButton button = PointerButton.Primary,
        ModifierKeys modifiers = ModifierKeys.None
    ) => Send(x, y, PointerAction.Released, button, modifiers);

    /// <summary>Presses in the middle of an element, drags to a point and releases there.</summary>
    /// <remarks>
    ///     For the controls that act on raw pointer events rather than on the gesture recogniser's
    ///     drags — a canvas, a timeline, a curve — where there is no slop threshold to cross and a
    ///     single move is a move.
    /// </remarks>
    public void DragFrom(
        UiElement from,
        float x,
        float y,
        PointerButton button = PointerButton.Primary,
        ModifierKeys modifiers = ModifierKeys.None
    ) {
        var centre = Centre(from);

        Press(centre.X, centre.Y, button, modifiers);
        Move(x, y, modifiers);
        Release(x, y, button, modifiers);
    }

    /// <summary>Ditto, from a bare point.</summary>
    public void DragPoint(
        float fromX,
        float fromY,
        float x,
        float y,
        PointerButton button = PointerButton.Primary,
        ModifierKeys modifiers = ModifierKeys.None
    ) {
        Press(fromX, fromY, button, modifiers);
        Move(x, y, modifiers);
        Release(x, y, button, modifiers);
    }

    /// <summary>Drags from the middle of an element to a point, far enough to be a drag.</summary>
    /// <remarks>
    ///     ⚠ Two moves, not one. The gesture recogniser latches a drag on the first move past the
    ///     slop threshold and reports it as <c>Started</c>; the second is the first <c>Moved</c>, and
    ///     a control that only acts on moves would see nothing from a one-move drag.
    /// </remarks>
    public void Drag(UiElement from, float x, float y) {
        var bounds = from.Bounds;

        Press(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
        Move(x, y);
        Move(x, y);
        Release(x, y);
    }

    /// <summary>Lets enough time pass that the next tap is a first tap rather than a third.</summary>
    /// <remarks>
    ///     ⚠ The gesture recogniser counts taps in a row, so two double-clicks in the same place with
    ///     nothing between them are taps one to four — and a control acting on <c>Count == 2</c> sees
    ///     one double click, not two. Every test that clicks twice in the same place needs this in
    ///     the middle, and every user gets it for free by being slow.
    /// </remarks>
    public void Rest(int milliseconds = 500) {
        clock += TimeSpan.FromMilliseconds(milliseconds);

        Document.Gestures.Tick(clock);
        Update();
    }

    /// <summary>Moves the clock on and tells the document, the way a frame loop would.</summary>
    /// <remarks>
    ///     <c>ControlFixture.Advance</c>'s counterpart, and it exists here for the one thing
    ///     <see cref="Update" /> cannot do: <c>UiDocument.Tick</c> is what raises the coalesced
    ///     <c>AccessibilityInvalidated</c>, and <c>Update</c> is not called every frame — see that
    ///     event's own remarks. A test about a notification that never ticked would be asserting
    ///     that nothing was raised because nothing could be.
    /// </remarks>
    public void Advance(TimeSpan by) {
        clock += by;

        Document.Tick(clock);
        Update();
    }

    public void Type(InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        SendKey(key, KeyAction.Pressed, modifiers);
        SendKey(key, KeyAction.Released, modifiers);
    }

    public void TypeText(string text) {
        clock += TimeSpan.FromMilliseconds(16);
        Document.Dispatch(new TextInputEvent { Text = text, Timestamp = clock });

        Update();
    }

    /// <summary>Turns the wheel at a bare point, which is what a cursor-anchored zoom needs.</summary>
    /// <remarks>
    ///     ⚠ The element-centred overload cannot test one: the centre is the fixed point of a
    ///     zoom about the centre <i>and</i> of a zoom about the pointer, so an assertion made there
    ///     is true of the bug as well as of the fix.
    /// </remarks>
    public void WheelAt(float x, float y, float deltaY) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(new WheelEvent { X = x, Y = y, DeltaY = deltaY, Timestamp = clock });
        Update();
    }

    public void Wheel(UiElement over, float deltaY) {
        var bounds = over.Bounds;
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new WheelEvent {
                X = bounds.X + (bounds.Width * 0.5f),
                Y = bounds.Y + (bounds.Height * 0.5f),
                DeltaY = deltaY,
                Timestamp = clock
            }
        );

        Update();
    }

    /// <summary>Presses in one of the document's other windows.</summary>
    /// <remarks>
    ///     ⚠ <b>The coordinates are that surface's, not the main window's.</b> Two windows do not
    ///     share a coordinate space, and an event sent to the wrong one lands at the right numbers in
    ///     the wrong place — which is the routing mistake a torn-off panel makes easy and which reads
    ///     as a hit-testing bug.
    /// </remarks>
    public void Press(UiSurface surface, float x, float y, PointerButton button = PointerButton.Primary) =>
        Send(surface, x, y, PointerAction.Pressed, button, ModifierKeys.None);

    /// <inheritdoc cref="Press(UiSurface,float,float,PointerButton)" />
    public void Move(UiSurface surface, float x, float y) =>
        Send(surface, x, y, PointerAction.Moved, PointerButton.None, ModifierKeys.None);

    /// <inheritdoc cref="Press(UiSurface,float,float,PointerButton)" />
    public void Release(UiSurface surface, float x, float y, PointerButton button = PointerButton.Primary) =>
        Send(surface, x, y, PointerAction.Released, button, ModifierKeys.None);

    void Send(float x, float y, PointerAction action, PointerButton button, ModifierKeys modifiers) =>
        Send(Document.Primary, x, y, action, button, modifiers);

    void Send(UiSurface surface, float x, float y, PointerAction action, PointerButton button, ModifierKeys modifiers) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            surface,
            new PointerEvent {
                X = x,
                Y = y,
                Action = action,
                Button = button,
                Modifiers = modifiers,
                Timestamp = clock
            }
        );

        Update();
    }

    void SendKey(InputKey key, KeyAction action, ModifierKeys modifiers) {
        clock += TimeSpan.FromMilliseconds(16);
        Document.Dispatch(new KeyEvent { Key = key, Action = action, Modifiers = modifiers, Timestamp = clock });

        Update();
    }

    public void Dispose() => Document.Dispose();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Advanced.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }
}
