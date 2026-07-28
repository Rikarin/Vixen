// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;

namespace Vixen.Input;

/// <summary>What a listener is handed when an action changes phase.</summary>
/// <param name="Action">The action.</param>
/// <param name="Phase">Which change this is.</param>
/// <param name="Value">The action's value at that moment.</param>
/// <param name="Magnitude">How far from rest it is.</param>
/// <param name="Binding">Which binding caused it, or <see langword="null" />.</param>
public readonly record struct InputActionContext(
    InputAction Action,
    InputActionPhase Phase,
    Vector2 Value,
    float Magnitude,
    InputBinding? Binding
) {
    /// <summary>Reads the value as the shape the caller expects.</summary>
    /// <typeparam name="T">
    ///     <see cref="bool" />, <see cref="float" /> or <see cref="Vector2" />.
    /// </typeparam>
    /// <returns>The value.</returns>
    public T ReadValue<T>() where T : struct => InputAction.Convert<T>(Value, Magnitude);
}

/// <summary>One thing a player can do, and every way they can do it.</summary>
/// <remarks>
///     <para>
///         The unit the game codes against. A game asks <c>Move.ReadValue&lt;Vector2&gt;()</c> or
///         listens for <see cref="Performed" />; it never asks whether <c>W</c> is down, which is
///         what makes rebinding, control schemes and device hot-swap possible at all.
///     </para>
///     <para>
///         <b>Both APIs, and they are the same state.</b> The event path drives UI, where a click
///         has to be handled on the frame it happened; the polled path drives gameplay, where a
///         simulation reads what is true now. Deriving one from the other — an event queue the
///         gameplay code drains, or a poll the UI does — is where the two disagree by a frame.
///     </para>
///     <para>
///         <b>One binding wins per frame</b> for a <see cref="InputActionType.Button" /> or
///         <see cref="InputActionType.Value" /> action: the one furthest from rest. Without that, a
///         stick and WASD bound to the same action would sum to a magnitude of two when both are
///         pushed, and a player who rests a hand on the pad while using the keyboard would drift.
///     </para>
/// </remarks>
public sealed class InputAction {
    readonly InputBinding[] bindings;
    readonly IInputInteraction[] defaults;

    bool pressedLastFrame;

    internal InputAction(InputActionData data, InputActionMap map) {
        Data = data;
        Map = map;
        bindings = new InputBinding[data.Bindings.Count];
        defaults = new IInputInteraction[data.Bindings.Count];

        for (var index = 0; index < bindings.Length; index++) {
            bindings[index] = new(data.Bindings[index], this);
            defaults[index] = DefaultInteraction(data.Type);
        }
    }

    /// <summary>What the asset said.</summary>
    public InputActionData Data { get; }

    /// <summary>Its name, unique within its map.</summary>
    public string Name => Data.Name;

    /// <summary>The map it belongs to.</summary>
    public InputActionMap Map { get; }

    /// <summary>What it does with its value.</summary>
    public InputActionType Type => Data.Type;

    /// <summary>The shape of its value.</summary>
    public InputControlType ControlType => Data.ControlType;

    /// <summary>Its full name, <c>Player/Move</c>.</summary>
    public string Path => $"{Map.Name}/{Name}";

    /// <summary>Its bindings.</summary>
    public IReadOnlyList<InputBinding> Bindings => bindings;

    /// <summary>Whether it is listening.</summary>
    public bool IsEnabled => Map.IsEnabled;

    /// <summary>Where it is in its lifecycle.</summary>
    public InputActionPhase Phase { get; private set; } = InputActionPhase.Disabled;

    /// <summary>Its value, as of the last update.</summary>
    public Vector2 Value { get; private set; }

    /// <summary>How far from rest it is.</summary>
    public float Magnitude { get; private set; }

    /// <summary>Whether it is past its press point now.</summary>
    public bool IsPressed { get; private set; }

    /// <summary>Whether it crossed the press point this frame.</summary>
    public bool WasPressedThisFrame { get; private set; }

    /// <summary>Whether it fell below the press point this frame.</summary>
    public bool WasReleasedThisFrame { get; private set; }

    /// <summary>Whether an interaction was satisfied this frame.</summary>
    /// <remarks>
    ///     What a game with a <c>hold</c> or a <c>multiTap</c> reads instead of
    ///     <see cref="WasPressedThisFrame" />, which only ever means the control went down.
    /// </remarks>
    public bool WasPerformedThisFrame { get; private set; }

    /// <summary>Whether an interaction began this frame.</summary>
    public bool WasStartedThisFrame { get; private set; }

    /// <summary>Whether an interaction ended without being satisfied this frame.</summary>
    public bool WasCanceledThisFrame { get; private set; }

    /// <summary>The binding that produced the current value, or <see langword="null" />.</summary>
    public InputBinding? ActiveBinding { get; private set; }

    /// <summary>Raised when an interaction begins.</summary>
    public event Action<InputActionContext>? Started;

    /// <summary>Raised when an interaction is satisfied. The one most games listen to.</summary>
    public event Action<InputActionContext>? Performed;

    /// <summary>Raised when an interaction ends without being satisfied, or the control is released.</summary>
    public event Action<InputActionContext>? Canceled;

    /// <summary>Reads the value as the shape the caller expects.</summary>
    /// <typeparam name="T">
    ///     <see cref="bool" /> for pressed-or-not, <see cref="float" /> for one axis,
    ///     <see cref="Vector2" /> for two.
    /// </typeparam>
    /// <returns>The value.</returns>
    /// <exception cref="NotSupportedException"><typeparamref name="T" /> is none of the three.</exception>
    /// <remarks>
    ///     The type test is against <c>typeof(T)</c> on a value type, which both the JIT and the
    ///     ahead-of-time compiler fold away, so this costs nothing a hand-written
    ///     <c>ReadVector2()</c> would not. Nothing boxes and nothing reflects.
    /// </remarks>
    public T ReadValue<T>() where T : struct => Convert<T>(Value, Magnitude);

    /// <summary>Reads the value as one axis.</summary>
    public float ReadFloat() => Value.X;

    /// <summary>Reads the value as two axes.</summary>
    public Vector2 ReadVector2() => Value;

    /// <summary>Every control this action currently reads.</summary>
    /// <param name="into">Where to add them.</param>
    public void CollectControls(List<InputControl> into) {
        ArgumentNullException.ThrowIfNull(into);

        for (var index = 0; index < bindings.Length; index++) {
            bindings[index].CollectControls(into);
        }
    }

    /// <summary>Replaces the path a binding reads.</summary>
    /// <param name="bindingIndex">Which binding.</param>
    /// <param name="path">The new path, or <see langword="null" /> to restore the asset's.</param>
    /// <param name="partIndex">Which part of a composite, or <c>-1</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such binding.</exception>
    public void OverrideBinding(int bindingIndex, string? path, int partIndex = -1) {
        ArgumentOutOfRangeException.ThrowIfNegative(bindingIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bindingIndex, bindings.Length);
        bindings[bindingIndex].Override(path, partIndex);
    }

    /// <summary>Puts every binding back to what the asset says.</summary>
    public void RemoveBindingOverrides() {
        for (var index = 0; index < bindings.Length; index++) {
            bindings[index].Override(null);

            for (var part = 0; part < bindings[index].Parts.Count; part++) {
                bindings[index].Override(null, part);
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    internal static T Convert<T>(Vector2 value, float magnitude) where T : struct {
        if (typeof(T) == typeof(Vector2)) {
            return Unsafe.As<Vector2, T>(ref value);
        }

        if (typeof(T) == typeof(float)) {
            var scalar = value.X;
            return Unsafe.As<float, T>(ref scalar);
        }

        if (typeof(T) == typeof(bool)) {
            var pressed = magnitude >= InputSettings.DefaultPressPoint;
            return Unsafe.As<bool, T>(ref pressed);
        }

        throw new NotSupportedException(
            $"An action reads as bool, float or Vector2, not as {typeof(T).Name}. A value of another shape "
            + "belongs in a composite binding, not in a cast."
        );
    }

    internal void Enable() {
        Phase = InputActionPhase.Waiting;
        ResetState();
    }

    internal void Disable() {
        Phase = InputActionPhase.Disabled;
        ResetState();
    }

    /// <summary>Clears everything an interaction was half-way through.</summary>
    internal void ResetState() {
        Value = Vector2.Zero;
        Magnitude = 0f;
        IsPressed = false;
        pressedLastFrame = false;
        ActiveBinding = null;
        ClearFrameFlags();

        for (var index = 0; index < bindings.Length; index++) {
            defaults[index].Reset();

            for (var interaction = 0; interaction < bindings[index].Interactions.Count; interaction++) {
                bindings[index].Interactions[interaction].Reset();
            }
        }
    }

    internal void ClearFrameFlags() {
        WasPressedThisFrame = false;
        WasReleasedThisFrame = false;
        WasPerformedThisFrame = false;
        WasStartedThisFrame = false;
        WasCanceledThisFrame = false;
    }

    /// <summary>Reads every binding and moves the action to the phase they imply.</summary>
    /// <param name="devices">Where the values come from.</param>
    /// <param name="gamepadSlot">Which pad an unindexed gamepad control reads.</param>
    /// <param name="time">The time now, in seconds.</param>
    /// <param name="scheme">The active control scheme, or <see langword="null" /> for all bindings.</param>
    internal void Update(InputDeviceSet devices, int gamepadSlot, double time, string? scheme) {
        ClearFrameFlags();

        if (!IsEnabled) {
            return;
        }

        var dimensions = ControlType == InputControlType.Vector2 ? 2 : 1;
        var results = InputInteractionResult.None;
        var best = Vector2.Zero;
        var bestMagnitude = 0f;
        InputBinding? winner = null;
        InputBinding? cause = null;

        for (var index = 0; index < bindings.Length; index++) {
            var binding = bindings[index];

            if (!binding.BelongsTo(scheme)) {
                // Not merely skipped. A scheme switch mid-hold must not leave the abandoned binding's
                // interaction able to complete when the player switches back an hour later.
                defaults[index].Reset();
                continue;
            }

            var value = binding.Read(devices, gamepadSlot, dimensions);
            var magnitude = dimensions >= 2 ? value.Length() : Math.Abs(value.X);
            var sample = new InputInteractionSample(magnitude >= InputSettings.DefaultPressPoint, magnitude, time);
            var result = Process(binding, index, sample);

            if (result != InputInteractionResult.None) {
                results |= result;
                cause ??= binding;
            }

            if (winner is not null && magnitude <= bestMagnitude) {
                continue;
            }

            bestMagnitude = magnitude;
            best = value;
            winner = binding;
        }

        Value = best;
        Magnitude = bestMagnitude;
        ActiveBinding = winner;
        IsPressed = bestMagnitude >= InputSettings.DefaultPressPoint;
        WasPressedThisFrame = IsPressed && !pressedLastFrame;
        WasReleasedThisFrame = !IsPressed && pressedLastFrame;
        pressedLastFrame = IsPressed;

        Raise(results, cause);
    }

    InputInteractionResult Process(InputBinding binding, int index, in InputInteractionSample sample) {
        if (binding.Interactions.Count == 0) {
            return defaults[index].Process(sample);
        }

        var result = InputInteractionResult.None;

        for (var interaction = 0; interaction < binding.Interactions.Count; interaction++) {
            result |= binding.Interactions[interaction].Process(sample);
        }

        return result;
    }

    /// <summary>Moves the phase and calls the listeners, in the order the phases happen in.</summary>
    void Raise(InputInteractionResult results, InputBinding? cause) {
        if ((results & InputInteractionResult.Started) != 0) {
            WasStartedThisFrame = true;
            Phase = InputActionPhase.Started;
            Started?.Invoke(Context(InputActionPhase.Started, cause));
        }

        if ((results & InputInteractionResult.Performed) != 0) {
            WasPerformedThisFrame = true;
            Phase = InputActionPhase.Performed;
            Performed?.Invoke(Context(InputActionPhase.Performed, cause));
        }

        // A cancel while something else is still actuated is not the action ending: two bindings, one
        // released and one still held, must leave the action performing rather than blinking off.
        if ((results & InputInteractionResult.Canceled) == 0 || IsPressed) {
            return;
        }

        WasCanceledThisFrame = true;
        Phase = InputActionPhase.Waiting;
        Canceled?.Invoke(Context(InputActionPhase.Canceled, cause));
    }

    InputActionContext Context(InputActionPhase phase, InputBinding? binding) =>
        new(this, phase, Value, Magnitude, binding);

    static IInputInteraction DefaultInteraction(InputActionType type) => type switch {
        InputActionType.Value => new ValueInteraction(),
        InputActionType.PassThrough => new PassThroughInteraction(),
        _ => new PressInteraction()
    };

    /// <summary>
    ///     What a <see cref="InputActionType.Value" /> action does when no interaction is written.
    /// </summary>
    /// <remarks>
    ///     Starts when the control leaves its rest position, performs on every change while it is
    ///     away from it, and cancels when it returns. A press interaction would be wrong here: a
    ///     stick that is pushed and then moved has to report the movement, and a press only ever
    ///     reports the first frame.
    /// </remarks>
    sealed class ValueInteraction : IInputInteraction {
        float previous;
        bool active;

        public string Name => "value";

        public InputInteractionResult Process(in InputInteractionSample sample) {
            var changed = Math.Abs(sample.Magnitude - previous) > float.Epsilon;
            previous = sample.Magnitude;

            if (sample.Magnitude > 0f) {
                if (active) {
                    return changed ? InputInteractionResult.Performed : InputInteractionResult.None;
                }

                active = true;
                return InputInteractionResult.Started | InputInteractionResult.Performed;
            }

            if (!active) {
                return InputInteractionResult.None;
            }

            active = false;
            return InputInteractionResult.Canceled;
        }

        public void Reset() {
            active = false;
            previous = 0f;
        }
    }

    /// <summary>
    ///     What a <see cref="InputActionType.PassThrough" /> action does: reports every change and
    ///     never disambiguates.
    /// </summary>
    sealed class PassThroughInteraction : IInputInteraction {
        float previous;

        public string Name => "passThrough";

        public InputInteractionResult Process(in InputInteractionSample sample) {
            if (Math.Abs(sample.Magnitude - previous) <= float.Epsilon) {
                return InputInteractionResult.None;
            }

            var wasRest = previous == 0f;
            previous = sample.Magnitude;

            if (sample.Magnitude == 0f) {
                return InputInteractionResult.Canceled;
            }

            return wasRest
                ? InputInteractionResult.Started | InputInteractionResult.Performed
                : InputInteractionResult.Performed;
        }

        public void Reset() => previous = 0f;
    }
}
