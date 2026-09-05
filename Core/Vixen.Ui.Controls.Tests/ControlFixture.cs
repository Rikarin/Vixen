// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Input;
using Vixen.Ui.Text;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A document with the theme in it and a way to drive it.</summary>
/// <remarks>
///     <para>
///         <b>The real theme, not a stub.</b> Half of what a control does is arrange for a
///         stylesheet to be able to say something — <c>:checked</c> on a switch, <c>display: none</c>
///         on a collapsed panel — and a test against an unstyled document cannot tell a control that
///         does that correctly from one that does not do it at all.
///     </para>
///     <para>
///         <b>And a real font</b>, for the same reason <c>Vixen.Ui.Tests</c> uses one: a label
///         measured without a face is a label of zero width, and every test about where something
///         went would pass against a control that put it nowhere.
///     </para>
/// </remarks>
sealed class ControlFixture : IDisposable {
    static readonly FontFace Font = LoadFont();

    TimeSpan clock;

    /// <summary>Which pointer the fixture pretends to be. One mouse, so one id.</summary>
    const int Pointer = 0;

    public ControlFixture(float width = 800f, float height = 600f, string? css = null) {
        Document = new UiDocument(width, height);
        Document.Fonts.Register("Test", Font);

        ControlTheme.Install(Document);

        // ⚠ **Pinned rather than left to the machine.** `EditingCommands.Current` is macOS on a
        // Mac and Windows everywhere else, so a suite that took the default would assert one
        // keyboard on a developer's laptop and another in CI — a red build whose cause is in neither
        // the diff nor the test. `EditingKeymapTests` drives both tables directly, which is where
        // the platform question belongs.
        Document.EditingKeymap = EditingKeymap.Windows;

        Document.Load("root { width: 800px; height: 600px; }");

        if (css is not null) {
            Document.Load(css);
        }
    }

    public UiDocument Document { get; }

    /// <summary>Adds a control to the root and lays the document out.</summary>
    public T Add<T>() where T : UiElement, new() {
        var control = Document.Root.Add<T>();
        Update();

        return control;
    }

    public void Update() {
        Document.Update();
        Document.Draw();
    }

    /// <summary>Moves the clock on and tells the document, the way a frame loop would.</summary>
    /// <param name="by">How far.</param>
    /// <remarks>
    ///     One tick rather than a frame per step: nothing here is animating, and the timed behaviour
    ///     this drives — a tooltip's delay, a toast's lifetime — asks how long it has been rather
    ///     than how many frames. <c>UiTest.Advance</c> runs real frames, for the tests that need
    ///     them.
    /// </remarks>
    public void Advance(TimeSpan by) {
        clock += by;
        Document.Tick(clock);
        Update();
    }

    /// <summary>Clicks in the middle of an element, the way a pointer would.</summary>
    /// <remarks>
    ///     ⚠ <b>A press and a release, not a synthesised tap.</b> The tap comes out of the gesture
    ///     recogniser, which is fed by <c>Dispatch</c> — so a control that listens for taps is only
    ///     exercised end to end if the test sends the two events that make one. A test that raised a
    ///     <c>TapEvent</c> directly would pass against a control that never receives real input.
    /// </remarks>
    public void Click(UiElement element, ModifierKeys modifiers = ModifierKeys.None, PointerButton button = PointerButton.Primary) {
        var bounds = element.Bounds;
        Click(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f), modifiers, button);
    }

    public void Click(float x, float y, ModifierKeys modifiers = ModifierKeys.None, PointerButton button = PointerButton.Primary) {
        Press(x, y, modifiers, button);
        Release(x, y, modifiers, button);
    }

    /// <summary>Puts a pointer down, optionally saying what kind of device it is.</summary>
    /// <remarks>
    ///     ⚠ The device defaults to <see cref="PointerType.Unknown" /> rather than to
    ///     <see cref="PointerType.Mouse" />, deliberately matching the event's own default: a fixture
    ///     that quietly claimed to be a mouse would make a control's touch branch untestable and its
    ///     mouse branch untestably right. A test that means a finger says so.
    /// </remarks>
    public void Press(
        float x,
        float y,
        ModifierKeys modifiers = ModifierKeys.None,
        PointerButton button = PointerButton.Primary,
        PointerType type = PointerType.Unknown
    ) =>
        Send(x, y, PointerAction.Pressed, button, modifiers, type);

    public void Release(
        float x,
        float y,
        ModifierKeys modifiers = ModifierKeys.None,
        PointerButton button = PointerButton.Primary,
        PointerType type = PointerType.Unknown
    ) =>
        Send(x, y, PointerAction.Released, button, modifiers, type);

    /// <summary>Moves the pointer, optionally with something held on the keyboard.</summary>
    /// <remarks>
    ///     The modifiers are on the move rather than only on the press because a control is allowed
    ///     to read them mid-gesture — <c>NumericInput</c> changes its scrub rate when Shift goes down
    ///     part way through a drag — and a fixture that could only state them at the press would make
    ///     that untestable.
    /// </remarks>
    public void MovePointer(
        float x,
        float y,
        ModifierKeys modifiers = ModifierKeys.None,
        PointerType type = PointerType.Unknown
    ) =>
        Send(x, y, PointerAction.Moved, PointerButton.None, modifiers, type);

    public void MoveOver(UiElement element) {
        var bounds = element.Bounds;
        MovePointer(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
    }

    void Send(
        float x,
        float y,
        PointerAction action,
        PointerButton button,
        ModifierKeys modifiers,
        PointerType type = PointerType.Unknown
    ) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new PointerEvent {
                PointerId = Pointer,
                PointerType = type,
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

    /// <summary>Presses and releases a key, the way a keyboard would.</summary>
    public void Type(InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        KeyDown(key, modifiers);
        KeyUp(key, modifiers);
    }

    public void KeyDown(InputKey key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false) =>
        SendKey(key, KeyAction.Pressed, modifiers, repeat);

    public void KeyUp(InputKey key, ModifierKeys modifiers = ModifierKeys.None) =>
        SendKey(key, KeyAction.Released, modifiers, false);

    void SendKey(InputKey key, KeyAction action, ModifierKeys modifiers, bool repeat) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new KeyEvent {
                Key = key,
                Action = action,
                Modifiers = modifiers,
                IsRepeat = repeat,
                Timestamp = clock
            }
        );

        Update();
    }

    /// <summary>Types text, the way an input method would.</summary>
    public void TypeText(string text) {
        clock += TimeSpan.FromMilliseconds(16);
        Document.Dispatch(new TextInputEvent { Text = text, Timestamp = clock });

        Update();
    }

    /// <summary>Sends an input method's pre-edit, the way a platform head does.</summary>
    /// <param name="text">The pre-edit string. Empty abandons the composition.</param>
    /// <param name="caret">Where the input method's own cursor sits inside it.</param>
    public void Compose(string text, int caret = -1) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new TextCompositionEvent {
                Text = text,
                Start = caret < 0 ? text.Length : caret,
                Timestamp = clock
            }
        );

        Update();
    }

    public void Wheel(UiElement over, float deltaY, float deltaX = 0f) {
        var bounds = over.Bounds;
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new WheelEvent {
                X = bounds.X + (bounds.Width * 0.5f),
                Y = bounds.Y + (bounds.Height * 0.5f),
                DeltaX = deltaX,
                DeltaY = deltaY,
                Timestamp = clock
            }
        );

        Update();
    }

    public void Dispose() => Document.Dispose();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }
}
