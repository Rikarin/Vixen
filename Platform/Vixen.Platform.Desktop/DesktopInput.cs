// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Silk.NET.SDL;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Desktop;

/// <summary>Force feedback on one game controller.</summary>
/// <remarks>
///     SDL takes rumble strength as <c>[0, 65535]</c> and a duration in milliseconds, and a later
///     call replaces an earlier one rather than layering on it — so a mixing policy, if one is ever
///     wanted, belongs above this and not here.
/// </remarks>
sealed unsafe class DesktopHaptics(Sdl sdl, DesktopGamepad owner) : IHaptics {
    public bool SupportsRumble => owner.IsConnected && sdl.GameControllerHasRumble(owner.Handle) == SdlBool.True;

    public bool SupportsTriggerRumble =>
        owner.IsConnected && sdl.GameControllerHasRumbleTriggers(owner.Handle) == SdlBool.True;

    public void Rumble(float lowFrequency, float highFrequency, TimeSpan duration) {
        if (!owner.IsConnected) {
            return;
        }

        sdl.GameControllerRumble(owner.Handle, Strength(lowFrequency), Strength(highFrequency), Milliseconds(duration));
    }

    public void RumbleTriggers(float left, float right, TimeSpan duration) {
        if (!owner.IsConnected) {
            return;
        }

        sdl.GameControllerRumbleTriggers(owner.Handle, Strength(left), Strength(right), Milliseconds(duration));
    }

    public void StopRumble() {
        if (!owner.IsConnected) {
            return;
        }

        sdl.GameControllerRumble(owner.Handle, 0, 0, 0);
        sdl.GameControllerRumbleTriggers(owner.Handle, 0, 0, 0);
    }

    static ushort Strength(float value) => (ushort)(Math.Clamp(value, 0f, 1f) * ushort.MaxValue);

    static uint Milliseconds(TimeSpan duration) =>
        (uint)Math.Clamp(duration.TotalMilliseconds, 0d, uint.MaxValue);
}

/// <summary>One open SDL game controller.</summary>
sealed unsafe class DesktopGamepad : IGamepad {
    readonly Sdl sdl;

    GameController* handle;

    internal DesktopGamepad(Sdl sdl, GameController* handle, int deviceId) {
        this.sdl = sdl;
        this.handle = handle;
        DeviceId = deviceId;
        Name = sdl.GameControllerNameS(handle) ?? "Gamepad";
        Kind = SdlTranslation.ToGamepadKind(sdl.GameControllerGetType(handle));
        Haptics = new DesktopHaptics(sdl, this);
    }

    public int DeviceId { get; }

    public string Name { get; }

    public GamepadKind Kind { get; }

    public int PlayerIndex => IsConnected ? sdl.GameControllerGetPlayerIndex(handle) : -1;

    public bool IsConnected => handle is not null;

    public IHaptics? Haptics { get; }

    internal GameController* Handle => handle;

    public float GetAxis(GamepadAxis axis) {
        if (!IsConnected || axis == GamepadAxis.None) {
            return 0f;
        }

        return SdlTranslation.ToAxisValue(axis, sdl.GameControllerGetAxis(handle, SdlTranslation.ToSdl(axis)));
    }

    public bool IsButtonDown(GamepadButton button) =>
        IsConnected && button != GamepadButton.None
        && sdl.GameControllerGetButton(handle, SdlTranslation.ToSdl(button)) != 0;

    internal void Close() {
        if (handle is null) {
            return;
        }

        sdl.GameControllerClose(handle);
        handle = null;
    }
}

/// <summary>Raw devices and the state of the keyboard and pointer.</summary>
/// <remarks>
///     Key and pointer state comes from SDL's own snapshot, which is refreshed by the pump. That
///     matters more than it looks: after a window loses focus SDL knows which keys the user let go
///     of while somebody else had them, and an input layer reconstructing state from events alone
///     does not — which is the bug where a player alt-tabs mid-stride and comes back still walking.
/// </remarks>
public sealed unsafe class DesktopInputSource(Sdl sdl) : IInputSource {
    readonly Dictionary<int, DesktopGamepad> gamepads = [];
    readonly List<IGamepad> ordered = [];

    /// <inheritdoc />
    public IReadOnlyList<IGamepad> Gamepads => ordered;

    /// <inheritdoc />
    public KeyModifiers Modifiers => SdlTranslation.ToModifiers(sdl.GetModState());

    /// <inheritdoc />
    public Vector2 PointerPosition {
        get {
            int x, y;
            sdl.GetGlobalMouseState(&x, &y);
            return new(x, y);
        }
    }

    /// <inheritdoc />
    public bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out IGamepad? gamepad) {
        if (gamepads.TryGetValue(deviceId, out var found)) {
            gamepad = found;
            return true;
        }

        gamepad = null;
        return false;
    }

    /// <inheritdoc />
    public bool IsKeyDown(Key key) {
        if (key == Key.Unknown) {
            return false;
        }

        var scancode = (int)SdlTranslation.ToScancode(key);
        int count;
        var state = sdl.GetKeyboardState(&count);

        return state is not null && scancode >= 0 && scancode < count && state[scancode] != 0;
    }

    /// <inheritdoc />
    public bool IsMouseButtonDown(MouseButton button) {
        if (button == MouseButton.None) {
            return false;
        }

        // SDL's mask is 1 << (button - 1), with its own button numbering — so the engine's role
        // names have to go back through the same table the events came out of.
        var sdlButton = button switch {
            MouseButton.Primary => 1,
            MouseButton.Middle => 2,
            MouseButton.Secondary => 3,
            MouseButton.Extra1 => 4,
            _ => 5
        };

        return (sdl.GetGlobalMouseState(null, null) & (1u << (sdlButton - 1))) != 0;
    }

    internal void Add(int index) {
        if (sdl.IsGameController(index) == SdlBool.False) {
            return;
        }

        var handle = sdl.GameControllerOpen(index);

        if (handle is null) {
            return;
        }

        // Events carry the joystick *instance* id, which is stable for as long as the device is
        // plugged in; the index this was opened by is a position in the current device list and
        // changes when anything else is unplugged. Keying on the index is how a second controller
        // ends up controlling the first player.
        var joystick = sdl.GameControllerGetJoystick(handle);
        var instance = sdl.JoystickInstanceID(joystick);

        if (gamepads.ContainsKey(instance)) {
            sdl.GameControllerClose(handle);
            return;
        }

        var gamepad = new DesktopGamepad(sdl, handle, instance);
        gamepads.Add(instance, gamepad);
        ordered.Add(gamepad);
    }

    internal void Remove(int instanceId) {
        if (!gamepads.Remove(instanceId, out var gamepad)) {
            return;
        }

        ordered.Remove(gamepad);
        gamepad.Close();
    }

    internal void CloseAll() {
        foreach (var gamepad in gamepads.Values) {
            gamepad.Close();
        }

        gamepads.Clear();
        ordered.Clear();
    }
}
