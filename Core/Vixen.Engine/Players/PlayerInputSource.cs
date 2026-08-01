// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;

namespace Vixen.Engine.Players;

/// <summary>Where one player's intent comes from.</summary>
/// <remarks>
///     <para>
///         <b>An interface rather than a component, because what it wraps is a managed object.</b>
///         An <c>InputActions</c> asset is one object per player, read by whatever wants it, and it
///         has no business in a chunk. So <c>PlayerInputSystem</c> holds these in a side table beside
///         the world — the same shape <c>InputUpdateSystem(InputService)</c> already uses, and for
///         the same reason.
///     </para>
///     <para>
///         <b>It is also the seam an AI takes.</b> Nothing about this interface says "a device": a
///         planner, a replay, a network stream and a test all implement it, and everything downstream
///         of the controller is unable to tell which is driving. That is Unreal's <c>AController</c>
///         polymorphism, expressed as one method instead of a class hierarchy.
///     </para>
/// </remarks>
public interface IPlayerInputSource {
    /// <summary>Writes what the player is asking for this frame.</summary>
    /// <param name="rotation">The player's aim, to be turned rather than replaced.</param>
    /// <param name="intent">The intent to fill in.</param>
    /// <param name="deltaTime">How long the frame was, in seconds.</param>
    /// <remarks>
    ///     <paramref name="rotation" /> is <c>ref</c> and not an output because aim is cumulative:
    ///     a device produces a turn, not a heading. <paramref name="intent" /> is the opposite — it
    ///     describes only this frame and an implementation is expected to overwrite all of it.
    /// </remarks>
    void Sample(ref ControlRotation rotation, ref MoveIntent intent, float deltaTime);
}

/// <summary>A player driven by named actions from a <see cref="InputActionMap" />.</summary>
/// <remarks>
///     <para>
///         The default, and what makes a working player controller two calls rather than an
///         implementation. It expects a map holding a <c>Move</c> and a <c>Look</c> value action and
///         any of the button actions it knows about; the names are properties, so a game whose asset
///         calls them something else changes the names rather than the class.
///     </para>
///     <para>
///         ⚠ <b>This is the one place the engine gives up the property <c>Vixen.Input</c> is proudest
///         of.</b> A generated accessor turns a renamed action into a compiler error; a name looked
///         up at run time turns it into an action that reads zero on the frame the player presses it.
///         An engine cannot reference a game's generated class, so the default binds by name — and
///         it resolves <b>once, at construction, and throws naming the map and the action</b> rather
///         than failing quietly forever. A shipping game gets the compile-time property back by
///         implementing <see cref="IPlayerInputSource" /> over its own accessor, which is a handful
///         of lines.
///     </para>
///     <para>
///         <b>Look is two devices wearing one action.</b> A mouse produces a delta that is already
///         independent of the frame rate; a stick produces a rate that has to be multiplied by it,
///         and multiplying a mouse delta by the frame time makes the sensitivity depend on the frame
///         rate — the bug every first-person game ships once. Which device won this frame is
///         something the input system has already decided, so this asks
///         <see cref="InputAction.ActiveBinding" /> rather than guessing from the magnitude.
///     </para>
/// </remarks>
public sealed class ActionPlayerInput : IPlayerInputSource {
    readonly InputAction move;
    readonly InputAction look;
    readonly (InputAction Action, MoveButtons Button)[] buttons;
    readonly List<InputDeviceKind> devices = [];

    /// <summary>The map this reads.</summary>
    public InputActionMap Map { get; }

    /// <summary>Radians of turn per unit of a pointer's motion.</summary>
    /// <remarks>
    ///     Applied to whatever the <c>Look</c> action reads when a pointer produced it, with no frame
    ///     time anywhere — a mouse that moved ten counts moved ten counts regardless of how long the
    ///     frame took.
    /// </remarks>
    public float PointerSensitivity { get; set; } = 0.003f;

    /// <summary>Radians of turn per second at a stick's full deflection.</summary>
    public float StickSensitivity { get; set; } = 2.5f;

    /// <summary>Whether looking up moves the pitch down.</summary>
    public bool InvertPitch { get; set; }

    /// <summary>Creates a source over a map, resolving every action it needs at once.</summary>
    /// <param name="map">The map, already found on a loaded or generated asset.</param>
    /// <param name="moveAction">The name of the two-axis movement action.</param>
    /// <param name="lookAction">The name of the two-axis look action.</param>
    /// <exception cref="ArgumentException">The map has no action by one of those names.</exception>
    /// <remarks>
    ///     The button actions are optional and are bound by convention — <c>Jump</c>, <c>Crouch</c>,
    ///     <c>Sprint</c>, <c>Fire</c>, <c>AltFire</c>, <c>Aim</c>, <c>Interact</c>, <c>Reload</c> —
    ///     because a game without a reload button should not have to say so. <c>Move</c> and
    ///     <c>Look</c> are not optional: a player controller that silently could not move would look
    ///     like a physics problem.
    /// </remarks>
    public ActionPlayerInput(InputActionMap map, string moveAction = "Move", string lookAction = "Look") {
        ArgumentNullException.ThrowIfNull(map);

        Map = map;
        move = Require(map, moveAction);
        look = Require(map, lookAction);

        // Optional, and absence is silence rather than an exception: the set below is every verb the
        // engine has a MoveButtons bit for, and no game has all eight.
        List<(InputAction, MoveButtons)> found = [];

        Optional(map, "Jump", MoveButtons.Jump, found);
        Optional(map, "Crouch", MoveButtons.Crouch, found);
        Optional(map, "Sprint", MoveButtons.Sprint, found);
        Optional(map, "Fire", MoveButtons.Primary, found);
        Optional(map, "AltFire", MoveButtons.Secondary, found);
        Optional(map, "Aim", MoveButtons.Aim, found);
        Optional(map, "Interact", MoveButtons.Interact, found);
        Optional(map, "Reload", MoveButtons.Reload, found);

        buttons = [.. found];
    }

    /// <inheritdoc />
    public void Sample(ref ControlRotation rotation, ref MoveIntent intent, float deltaTime) {
        var looked = look.ReadVector2();

        if (looked.X != 0f || looked.Y != 0f) {
            var rate = IsStick(look) ? StickSensitivity * deltaTime : PointerSensitivity;

            // Up is negative everywhere in this engine — the screen convention, which the vector2
            // composite and the sticks both follow (Vixen.Input's README says so out loud). So a
            // look input of -1 in Y means "up", and the pitch it produces is positive.
            var pitch = -looked.Y * rate;

            rotation.Turn(-looked.X * rate, InvertPitch ? -pitch : pitch);
        }

        intent.Move = move.ReadVector2();
        intent.Yaw = rotation.Yaw;
        intent.Pitch = rotation.Pitch;
        intent.Buttons = MoveButtons.None;

        foreach (var (action, button) in buttons) {
            if (action.IsPressed) {
                intent.Buttons |= button;
            }
        }
    }

    bool IsStick(InputAction action) {
        if (action.ActiveBinding is not { } binding) {
            return false;
        }

        devices.Clear();
        binding.CollectDevices(devices);

        return devices.Contains(InputDeviceKind.Gamepad);
    }

    static InputAction Require(InputActionMap map, string name) =>
        map.TryFind(name, out var action)
            ? action
            : throw new ArgumentException(
                $"The input map '{map.Name}' has no action named '{name}'. "
                + $"It has: {string.Join(", ", map.Actions.Select(a => a.Name))}.",
                nameof(name)
            );

    static void Optional(
        InputActionMap map,
        string name,
        MoveButtons button,
        List<(InputAction, MoveButtons)> into
    ) {
        if (map.TryFind(name, out var action)) {
            into.Add((action, button));
        }
    }
}
