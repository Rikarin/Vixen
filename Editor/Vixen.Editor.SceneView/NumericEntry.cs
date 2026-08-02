// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.SceneView;

/// <summary>What a typed transform says: a magnitude per component, and an axis it is along.</summary>
/// <param name="Values">Up to three numbers, in the order they were typed.</param>
/// <param name="Count">How many of them have been typed.</param>
/// <param name="Axis">Which world axis it is constrained to, or −1 for none.</param>
public readonly record struct TypedTransform(Vector3 Values, int Count, int Axis);

/// <summary>Typing an exact distance, angle or factor partway through a drag.</summary>
/// <remarks>
///     <para>
///         <b>Blender's <c>G X 5 ⏎</c>, and doc 24 calls it the single most-missed feature by anybody
///         coming from Blender.</b> No dialog, no field, no mouse precision: you are already dragging,
///         you type, and the drag becomes exact. Nothing in either reference editor has it.
///     </para>
///     <para>
///         ⚠ <b>It costs almost nothing here because the gizmo recomputes from mouse-down.</b> Every
///         frame of a drag is already <c>the pose at the grab</c> plus <c>a magnitude derived from the
///         pointer</c>; typing substitutes the magnitude before the same arithmetic runs. In an
///         implementation that accumulated per-frame deltas it would not be expressible at all — which
///         is why doc 24 lists the gizmo's design as one of the two most valuable things it inherited.
///     </para>
///     <para>
///         ⚠ <b>An axis letter overrides the handle rather than composing with it.</b> Pressing X
///         during a drag is Blender's "along X and nothing else", and a user who has said it has
///         said something more specific than which arrow they happened to grab. Pressing the same
///         letter again releases the constraint.
///     </para>
///     <para>
///         ⚠ <b>The buffer is text, not a float, until it is read.</b> A user typing <c>1.</c> is
///         midway through <c>1.5</c>, and a model that parsed every keystroke into a number would show
///         them 1 and then jump. <see cref="Text" /> is what a readout draws; <see cref="Typed" /> is
///         what the gizmo applies, and an unparseable component is zero rather than a refusal.
///     </para>
/// </remarks>
public sealed class NumericEntry {
    /// <summary>How many components a transform can have typed into it.</summary>
    public const int Components = 3;

    readonly StringBuilder[] parts = [new(), new(), new()];

    /// <summary>Whether anything has been typed.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Which component the next keystroke goes into.</summary>
    public int Component { get; private set; }

    /// <summary>How many components have been started.</summary>
    public int Count { get; private set; }

    /// <summary>Which world axis the entry is constrained to, or −1 for none.</summary>
    public int Axis { get; private set; } = -1;

    /// <summary>What a readout should show, including the axis letter and the caret.</summary>
    /// <remarks>
    ///     ⚠ <b>The caret is part of it.</b> A typed entry with several components has to say which one
    ///     is being typed into, and "2.5, 4|, " is the whole of what a user needs to know that Tab
    ///     moved them and that the third is still empty.
    /// </remarks>
    public string Text {
        get {
            var text = new StringBuilder();

            if (Axis >= 0) {
                text.Append("XYZ"[Axis]).Append(' ');
            }

            var shown = Math.Max(Count, Component + 1);

            for (var index = 0; index < shown; index++) {
                if (index > 0) {
                    text.Append(", ");
                }

                text.Append(parts[index]);

                if (index == Component) {
                    text.Append('|');
                }
            }

            return text.ToString();
        }
    }

    /// <summary>What the gizmo should apply, or <see langword="null" /> if nothing has been typed.</summary>
    public TypedTransform? Typed {
        get {
            if (!IsActive) {
                return null;
            }

            var values = new Vector3(Parse(0), Parse(1), Parse(2));

            return new TypedTransform(values, Math.Max(Count, 1), Axis);
        }
    }

    /// <summary>Offers a key to the entry.</summary>
    /// <param name="key">Which key.</param>
    /// <param name="modifiers">What was held with it.</param>
    /// <returns>Whether the entry took it.</returns>
    /// <remarks>
    ///     ⚠ <b>Only offered while a drag is in flight, and only a key that <i>means</i> something here
    ///     is taken.</b> A drag is not a text field: W still flies, Escape still cancels, and a key
    ///     this returns false for has to reach whatever would have had it. That is why the digits are
    ///     tested rather than "anything printable".
    /// </remarks>
    public bool Key(InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        // ⚠ A chord is somebody else's. Ctrl+Z during a drag is undo and Ctrl+S is save; taking them
        // because they contain a letter would make a drag a place shortcuts stop working.
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Meta | ModifierKeys.Alt)) != 0) {
            return false;
        }

        if (Digit(key) is { } digit) {
            Append(digit);
            return true;
        }

        switch (key) {
            case InputKey.Period or InputKey.KeypadPeriod:
                // One point per component, and a second is ignored rather than making the text
                // unparseable — the same rule a numeric text field follows.
                if (!parts[Component].ToString().Contains('.', StringComparison.Ordinal)) {
                    Append('.');
                }

                return true;

            case InputKey.Minus or InputKey.KeypadMinus:
                Negate();
                return true;

            case InputKey.Backspace:
                Erase();
                return true;

            case InputKey.Tab:
                Advance((modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                return IsActive;

            case InputKey.X or InputKey.Y or InputKey.Z:
                return Constrain(key - InputKey.X);

            default:
                return false;
        }
    }

    /// <summary>Throws everything typed away.</summary>
    public void Clear() {
        foreach (var part in parts) {
            part.Clear();
        }

        IsActive = false;
        Component = 0;
        Count = 0;
        Axis = -1;
    }

    /// <summary>Constrains the entry to an axis, or releases it if it is already on that one.</summary>
    /// <param name="axis">0, 1 or 2.</param>
    /// <returns>Whether the key was taken.</returns>
    /// <remarks>
    ///     ⚠ <b>Only once something is being typed.</b> X on its own during a drag is not a constraint
    ///     in this editor — the arms are what constrain a drag, and taking the letter would silently
    ///     eat a key some other tool wants. It becomes a constraint once the user has said, by typing
    ///     a digit, that they mean an exact transform.
    /// </remarks>
    bool Constrain(int axis) {
        if (!IsActive) {
            return false;
        }

        Axis = Axis == axis ? -1 : axis;
        return true;
    }

    void Append(char character) {
        IsActive = true;
        Count = Math.Max(Count, Component + 1);

        parts[Component].Append(character);
    }

    void Negate() {
        IsActive = true;
        Count = Math.Max(Count, Component + 1);

        var part = parts[Component];

        // A toggle rather than a character, because a minus in the middle of a number is not a number
        // and because Blender's `-` flips the sign however far in you are.
        if (part.Length > 0 && part[0] == '-') {
            part.Remove(0, 1);
        } else {
            part.Insert(0, '-');
        }
    }

    void Erase() {
        var part = parts[Component];

        if (part.Length > 0) {
            part.Remove(part.Length - 1, 1);
        } else if (Component > 0) {
            Component--;
            Count = Component + 1;
        }

        // Backspacing the last character out is backing out of the entry altogether, so the drag goes
        // back to following the pointer rather than sitting frozen at zero.
        if (Count <= 1 && parts[0].Length == 0) {
            Clear();
        }
    }

    void Advance(int direction) {
        if (!IsActive) {
            return;
        }

        Component = Math.Clamp(Component + direction, 0, Components - 1);
        Count = Math.Max(Count, Component + 1);
    }

    float Parse(int index) =>
        float.TryParse(parts[index].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0f;

    /// <remarks>
    ///     ⚠ <b>Zero is not where counting would put it, on either row.</b> <c>Number0</c> follows
    ///     <c>Number9</c> and <c>Keypad0</c> follows <c>Keypad9</c> — which is the order the keys are in
    ///     on a keyboard and not the order the digits are — so a range test from zero maps every digit
    ///     one place out.
    /// </remarks>
    static char? Digit(InputKey key) =>
        key switch {
            InputKey.Number0 or InputKey.Keypad0 => '0',
            >= InputKey.Number1 and <= InputKey.Number9 => (char) ('1' + (key - InputKey.Number1)),
            >= InputKey.Keypad1 and <= InputKey.Keypad9 => (char) ('1' + (key - InputKey.Keypad1)),
            _ => null
        };
}
