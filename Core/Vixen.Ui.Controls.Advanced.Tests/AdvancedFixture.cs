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

    public void Click(UiElement element) {
        var bounds = element.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        Press(x, y);
        Release(x, y);
    }

    public void Press(float x, float y) => Send(x, y, PointerAction.Pressed, PointerButton.Primary);

    public void Move(float x, float y) => Send(x, y, PointerAction.Moved, PointerButton.None);

    public void Release(float x, float y) => Send(x, y, PointerAction.Released, PointerButton.Primary);

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

    public void Type(InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        SendKey(key, KeyAction.Pressed, modifiers);
        SendKey(key, KeyAction.Released, modifiers);
    }

    public void TypeText(string text) {
        clock += TimeSpan.FromMilliseconds(16);
        Document.Dispatch(new TextInputEvent { Text = text, Timestamp = clock });

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

    void Send(float x, float y, PointerAction action, PointerButton button) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new PointerEvent { X = x, Y = y, Action = action, Button = button, Timestamp = clock }
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
