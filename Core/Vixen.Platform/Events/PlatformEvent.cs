// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>One thing that happened: a key, a click, a resize, a suspend.</summary>
/// <remarks>
///     <para>
///         Every platform event the engine understands is this one type, discriminated by
///         <see cref="Kind" />. That is deliberate and it is what SDL, Win32 and the browser all do:
///         events arrive interleaved in a single stream, so any design that splits them into
///         several typed streams has to buffer and re-order them, and loses the ordering between a
///         key press and the resize that happened between it and the next one.
///     </para>
///     <para>
///         The payload slots are shared between kinds, so an accessor is only meaningful for the
///         kinds its documentation names. In debug builds reading the wrong one trips an assert
///         rather than returning a plausible number; in release the check is gone. Construct events
///         through the factory methods, which is the only way the pairing can be got wrong once.
///     </para>
///     <para>
///         This is a <see langword="struct" /> with no allocation of its own except
///         <see cref="Text" />, which is null for every kind that does not carry text. Draining a
///         frame's worth of input allocates nothing.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct PlatformEvent {
    readonly Vector2 first;
    readonly Vector2 second;
    readonly float value;
    readonly int code;
    readonly int device;
    readonly uint windowId;
    readonly long timestamp;
    readonly string? text;
    readonly KeyModifiers modifiers;
    readonly bool repeat;

    PlatformEvent(
        PlatformEventKind kind,
        uint windowId,
        long timestamp,
        Vector2 first = default,
        Vector2 second = default,
        float value = 0f,
        int code = 0,
        int device = 0,
        string? text = null,
        KeyModifiers modifiers = KeyModifiers.None,
        bool repeat = false
    ) {
        Kind = kind;
        this.windowId = windowId;
        this.timestamp = timestamp;
        this.first = first;
        this.second = second;
        this.value = value;
        this.code = code;
        this.device = device;
        this.text = text;
        this.modifiers = modifiers;
        this.repeat = repeat;
    }

    /// <summary>What happened.</summary>
    public PlatformEventKind Kind { get; }

    /// <summary>Which window it happened to, or <c>0</c> for events that belong to no window.</summary>
    public uint WindowId => windowId;

    /// <summary>When it happened, in <see cref="Stopwatch" /> ticks from a monotonic clock.</summary>
    /// <remarks>
    ///     Monotonic rather than wall-clock, because the difference between two of these has to stay
    ///     meaningful across a clock adjustment — <c>docs/plan/10 § Cross-platform discipline</c>
    ///     bans <see cref="DateTime" /> in the loop for the same reason. Compare with
    ///     <see cref="Stopwatch.GetTimestamp" />.
    /// </remarks>
    public long Timestamp => timestamp;

    /// <summary>The modifier keys held at the time.</summary>
    /// <remarks>Meaningful for the keyboard and pointer kinds; <see cref="KeyModifiers.None" />
    /// elsewhere.</remarks>
    public KeyModifiers Modifiers => modifiers;

    /// <summary>The key, for <see cref="PlatformEventKind.KeyDown" /> and
    /// <see cref="PlatformEventKind.KeyUp" />.</summary>
    public Key Key {
        get {
            AssertKind(PlatformEventKind.KeyDown, PlatformEventKind.KeyUp);
            return (Key)code;
        }
    }

    /// <summary>Whether a <see cref="PlatformEventKind.KeyDown" /> is auto-repeat rather than a
    /// fresh press.</summary>
    /// <remarks>A game reads presses and ignores repeats; a text field wants both.</remarks>
    public bool IsRepeat {
        get {
            AssertKind(PlatformEventKind.KeyDown, PlatformEventKind.KeyUp);
            return repeat;
        }
    }

    /// <summary>The button, for <see cref="PlatformEventKind.MouseButtonDown" /> and
    /// <see cref="PlatformEventKind.MouseButtonUp" />.</summary>
    public MouseButton MouseButton {
        get {
            AssertKind(PlatformEventKind.MouseButtonDown, PlatformEventKind.MouseButtonUp);
            return (MouseButton)code;
        }
    }

    /// <summary>How many clicks this one completes: 1 for a single click, 2 for the second click of
    /// a double-click, and so on for as far as the OS counts.</summary>
    /// <remarks>
    ///     The platform decides, using the user's configured double-click interval and slop. An
    ///     engine that re-derives this from timestamps gets a different answer from every other
    ///     application on the machine.
    /// </remarks>
    public int ClickCount {
        get {
            AssertKind(PlatformEventKind.MouseButtonDown, PlatformEventKind.MouseButtonUp);
            return device;
        }
    }

    /// <summary>The button, for <see cref="PlatformEventKind.GamepadButtonDown" /> and
    /// <see cref="PlatformEventKind.GamepadButtonUp" />.</summary>
    public GamepadButton GamepadButton {
        get {
            AssertKind(PlatformEventKind.GamepadButtonDown, PlatformEventKind.GamepadButtonUp);
            return (GamepadButton)code;
        }
    }

    /// <summary>The axis, for <see cref="PlatformEventKind.GamepadAxisMoved" />.</summary>
    public GamepadAxis GamepadAxis {
        get {
            AssertKind(PlatformEventKind.GamepadAxisMoved);
            return (GamepadAxis)code;
        }
    }

    /// <summary>An analogue reading: an axis position, or a touch's pressure.</summary>
    public float Value {
        get {
            AssertKind(
                PlatformEventKind.GamepadAxisMoved,
                PlatformEventKind.TouchDown,
                PlatformEventKind.TouchMoved,
                PlatformEventKind.TouchUp
            );

            return value;
        }
    }

    /// <summary>Which device this came from: a gamepad's id, or a finger's id for the duration of
    /// its touch.</summary>
    public int DeviceId {
        get {
            AssertKind(
                PlatformEventKind.GamepadConnected,
                PlatformEventKind.GamepadDisconnected,
                PlatformEventKind.GamepadButtonDown,
                PlatformEventKind.GamepadButtonUp,
                PlatformEventKind.GamepadAxisMoved,
                PlatformEventKind.TouchDown,
                PlatformEventKind.TouchMoved,
                PlatformEventKind.TouchUp
            );

            return device;
        }
    }

    /// <summary>Where the pointer or finger is, relative to the window's top-left, in logical
    /// points.</summary>
    /// <remarks>
    ///     Fractional on trackpads, high-resolution mice and touchscreens, so it is a
    ///     <see cref="Vector2" /> rather than an <see cref="Int2" />. Meaningless in
    ///     <see cref="CursorMode.Relative" />, where only <see cref="Delta" /> is real.
    /// </remarks>
    public Vector2 Position {
        get {
            AssertKind(
                PlatformEventKind.MouseMoved,
                PlatformEventKind.MouseButtonDown,
                PlatformEventKind.MouseButtonUp,
                PlatformEventKind.MouseWheel,
                PlatformEventKind.TouchDown,
                PlatformEventKind.TouchMoved,
                PlatformEventKind.TouchUp,
                PlatformEventKind.DropFile,
                PlatformEventKind.DropText
            );

            return first;
        }
    }

    /// <summary>How far it moved since the previous event, in logical points — or in wheel notches
    /// for <see cref="PlatformEventKind.MouseWheel" />.</summary>
    public Vector2 Delta {
        get {
            AssertKind(
                PlatformEventKind.MouseMoved,
                PlatformEventKind.MouseWheel,
                PlatformEventKind.TouchMoved
            );

            return second;
        }
    }

    /// <summary>The window's new top-left in desktop coordinates, for
    /// <see cref="PlatformEventKind.WindowMoved" />.</summary>
    public Int2 WindowPosition {
        get {
            AssertKind(PlatformEventKind.WindowMoved);
            return new((int)first.X, (int)first.Y);
        }
    }

    /// <summary>The client area's new size in logical points, for
    /// <see cref="PlatformEventKind.WindowResized" />.</summary>
    public Int2 Size {
        get {
            AssertKind(PlatformEventKind.WindowResized);
            return new((int)first.X, (int)first.Y);
        }
    }

    /// <summary>The framebuffer's new size in physical pixels, for
    /// <see cref="PlatformEventKind.WindowResized" /> — which is what a swapchain is rebuilt
    /// from.</summary>
    public Int2 PixelSize {
        get {
            AssertKind(PlatformEventKind.WindowResized);
            return new((int)second.X, (int)second.Y);
        }
    }

    /// <summary>The window's new scale factor, for
    /// <see cref="PlatformEventKind.WindowDpiChanged" />.</summary>
    public float DpiScale {
        get {
            AssertKind(PlatformEventKind.WindowDpiChanged);
            return value;
        }
    }

    /// <summary>The text: committed input, a composition string, a dropped path, or dropped
    /// text.</summary>
    public string Text {
        get {
            AssertKind(
                PlatformEventKind.TextInput,
                PlatformEventKind.TextEditing,
                PlatformEventKind.DropFile,
                PlatformEventKind.DropText
            );

            return text ?? string.Empty;
        }
    }

    /// <summary>Where the composition cursor sits within <see cref="Text" />, for
    /// <see cref="PlatformEventKind.TextEditing" />.</summary>
    public int SelectionStart {
        get {
            AssertKind(PlatformEventKind.TextEditing);
            return code;
        }
    }

    /// <summary>How much of <see cref="Text" /> the composition has selected, for
    /// <see cref="PlatformEventKind.TextEditing" />.</summary>
    public int SelectionLength {
        get {
            AssertKind(PlatformEventKind.TextEditing);
            return device;
        }
    }

    /// <summary>A window event that carries nothing but its identity.</summary>
    public static PlatformEvent Window(PlatformEventKind kind, uint windowId, long timestamp) =>
        new(kind, windowId, timestamp);

    /// <summary>A window move.</summary>
    public static PlatformEvent WindowMoved(uint windowId, long timestamp, Int2 position) =>
        new(PlatformEventKind.WindowMoved, windowId, timestamp, new(position.X, position.Y));

    /// <summary>A window resize, carrying both the logical size and the framebuffer size.</summary>
    public static PlatformEvent WindowResized(uint windowId, long timestamp, Int2 size, Int2 pixelSize) =>
        new(
            PlatformEventKind.WindowResized,
            windowId,
            timestamp,
            new(size.X, size.Y),
            new(pixelSize.X, pixelSize.Y)
        );

    /// <summary>A change of scale factor.</summary>
    public static PlatformEvent WindowDpiChanged(uint windowId, long timestamp, float dpiScale) =>
        new(PlatformEventKind.WindowDpiChanged, windowId, timestamp, value: dpiScale);

    /// <summary>A key press or release.</summary>
    public static PlatformEvent Keyboard(
        PlatformEventKind kind,
        uint windowId,
        long timestamp,
        Key key,
        KeyModifiers modifiers,
        bool isRepeat = false
    ) =>
        new(kind, windowId, timestamp, code: (int)key, modifiers: modifiers, repeat: isRepeat);

    /// <summary>Committed text.</summary>
    public static PlatformEvent TextInput(uint windowId, long timestamp, string text) =>
        new(PlatformEventKind.TextInput, windowId, timestamp, text: text);

    /// <summary>An in-progress IME composition.</summary>
    public static PlatformEvent TextEditing(
        uint windowId,
        long timestamp,
        string text,
        int selectionStart,
        int selectionLength
    ) =>
        new(
            PlatformEventKind.TextEditing,
            windowId,
            timestamp,
            code: selectionStart,
            device: selectionLength,
            text: text
        );

    /// <summary>Pointer motion.</summary>
    public static PlatformEvent MouseMoved(
        uint windowId,
        long timestamp,
        Vector2 position,
        Vector2 delta,
        KeyModifiers modifiers = KeyModifiers.None
    ) =>
        new(PlatformEventKind.MouseMoved, windowId, timestamp, position, delta, modifiers: modifiers);

    /// <summary>A mouse button press or release.</summary>
    public static PlatformEvent MouseButtonChanged(
        PlatformEventKind kind,
        uint windowId,
        long timestamp,
        MouseButton button,
        Vector2 position,
        int clickCount = 1,
        KeyModifiers modifiers = KeyModifiers.None
    ) =>
        new(
            kind,
            windowId,
            timestamp,
            position,
            code: (int)button,
            device: clickCount,
            modifiers: modifiers
        );

    /// <summary>A wheel or trackpad scroll, in notches.</summary>
    public static PlatformEvent MouseWheel(
        uint windowId,
        long timestamp,
        Vector2 position,
        Vector2 delta,
        KeyModifiers modifiers = KeyModifiers.None
    ) =>
        new(PlatformEventKind.MouseWheel, windowId, timestamp, position, delta, modifiers: modifiers);

    /// <summary>A touch down, move or up.</summary>
    public static PlatformEvent Touch(
        PlatformEventKind kind,
        uint windowId,
        long timestamp,
        int fingerId,
        Vector2 position,
        Vector2 delta = default,
        float pressure = 1f
    ) =>
        new(kind, windowId, timestamp, position, delta, pressure, device: fingerId);

    /// <summary>A gamepad arriving or leaving.</summary>
    public static PlatformEvent GamepadConnection(PlatformEventKind kind, long timestamp, int deviceId) =>
        new(kind, 0, timestamp, device: deviceId);

    /// <summary>A gamepad button press or release.</summary>
    public static PlatformEvent GamepadButtonChanged(
        PlatformEventKind kind,
        long timestamp,
        int deviceId,
        GamepadButton button
    ) =>
        new(kind, 0, timestamp, code: (int)button, device: deviceId);

    /// <summary>A gamepad axis moving.</summary>
    public static PlatformEvent GamepadAxisMoved(long timestamp, int deviceId, GamepadAxis axis, float position) =>
        new(PlatformEventKind.GamepadAxisMoved, 0, timestamp, value: position, code: (int)axis, device: deviceId);

    /// <summary>An event that belongs to the application rather than to any window: a suspend, a
    /// resume, a memory warning, a quit, a display change.</summary>
    public static PlatformEvent Application(PlatformEventKind kind, long timestamp) => new(kind, 0, timestamp);

    /// <summary>Something dropped onto a window.</summary>
    public static PlatformEvent Drop(PlatformEventKind kind, uint windowId, long timestamp, string text, Vector2 position) =>
        new(kind, windowId, timestamp, position, text: text);

    /// <inheritdoc />
    public override string ToString() => Kind switch {
        PlatformEventKind.KeyDown or PlatformEventKind.KeyUp => $"{Kind} {Key} {Modifiers}",
        PlatformEventKind.WindowResized => $"{Kind} #{WindowId} {Size} ({PixelSize} px)",
        PlatformEventKind.TextInput or PlatformEventKind.TextEditing => $"{Kind} \"{Text}\"",
        _ => WindowId == 0 ? Kind.ToString() : $"{Kind} #{WindowId}"
    };

    /// <summary>
    ///     Catches the one mistake this design makes possible — reading a payload that belongs to a
    ///     different kind — at the moment it happens rather than as a strange number downstream.
    /// </summary>
    [Conditional("DEBUG")]
    void AssertKind(params ReadOnlySpan<PlatformEventKind> valid) {
        foreach (var candidate in valid) {
            if (Kind == candidate) {
                return;
            }
        }

        Debug.Fail($"A {Kind} event does not carry this payload.");
    }
}
