// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Input;

/// <summary>A set of actions that are on or off together.</summary>
/// <remarks>
///     <para>
///         The unit a game switches: <c>Player</c> off and <c>Menu</c> on when the pause screen
///         opens, so that <c>Escape</c> means two different things without a single <c>if</c> in the
///         gameplay code. Disabling a map resets every interaction in it, so a hold that was half
///         finished when the menu opened does not complete on the way back.
///     </para>
/// </remarks>
public sealed class InputActionMap {
    readonly InputAction[] actions;

    internal InputActionMap(InputActionMapData data, InputActions owner) {
        Data = data;
        Asset = owner;
        actions = new InputAction[data.Actions.Count];

        for (var index = 0; index < actions.Length; index++) {
            actions[index] = new(data.Actions[index], this);
        }
    }

    /// <summary>What the asset said.</summary>
    public InputActionMapData Data { get; }

    /// <summary>Its name.</summary>
    public string Name => Data.Name;

    /// <summary>The asset it belongs to.</summary>
    public InputActions Asset { get; }

    /// <summary>Whether its actions are listening.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Its actions.</summary>
    public IReadOnlyList<InputAction> Actions => actions;

    /// <summary>Finds an action by name, case-insensitively.</summary>
    /// <param name="name">Its name.</param>
    /// <exception cref="KeyNotFoundException">There is no such action.</exception>
    public InputAction this[string name] =>
        TryFind(name, out var action)
            ? action
            : throw new KeyNotFoundException($"The action map '{Name}' has no action called '{name}'.");

    /// <summary>Finds an action by name, case-insensitively.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="action">The action.</param>
    /// <returns><see langword="false" /> if there is no such action.</returns>
    public bool TryFind(string? name, [NotNullWhen(true)] out InputAction? action) {
        if (name is not null) {
            for (var index = 0; index < actions.Length; index++) {
                if (string.Equals(actions[index].Name, name, StringComparison.OrdinalIgnoreCase)) {
                    action = actions[index];
                    return true;
                }
            }
        }

        action = null;
        return false;
    }

    /// <summary>Starts listening.</summary>
    public void Enable() {
        if (IsEnabled) {
            return;
        }

        IsEnabled = true;

        for (var index = 0; index < actions.Length; index++) {
            actions[index].Enable();
        }
    }

    /// <summary>Stops listening, and forgets everything half-finished.</summary>
    public void Disable() {
        if (!IsEnabled) {
            return;
        }

        IsEnabled = false;

        for (var index = 0; index < actions.Length; index++) {
            actions[index].Disable();
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;

    internal void Update(InputDeviceSet devices, int gamepadSlot, double time, string? scheme) {
        for (var index = 0; index < actions.Length; index++) {
            actions[index].Update(devices, gamepadSlot, time, scheme);
        }
    }

    internal void ResetState() {
        for (var index = 0; index < actions.Length; index++) {
            actions[index].ResetState();
        }
    }
}
