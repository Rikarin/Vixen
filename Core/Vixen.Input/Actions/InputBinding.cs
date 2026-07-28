// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Input;

/// <summary>One slot of a composite binding, resolved.</summary>
/// <remarks>
///     A part is always one-dimensional, even when the composite it belongs to is not: <c>up</c> is a
///     key, and what makes it vertical is which slot it sits in rather than anything about the
///     control.
/// </remarks>
public sealed class InputBindingPart {
    internal InputBindingPart(InputBindingPartData data) {
        Data = data;
        Processors = InputProcessorFactory.Parse(data.Processors);
        Resolve();
    }

    /// <summary>What the asset said.</summary>
    public InputBindingPartData Data { get; }

    /// <summary>Which slot this fills.</summary>
    public string Part => Data.Part;

    /// <summary>The path the player rebound this to, or <see langword="null" />.</summary>
    public string? OverridePath { get; private set; }

    /// <summary>The path in force: the override if there is one, the asset's otherwise.</summary>
    public string EffectivePath => OverridePath ?? Data.Path;

    /// <summary>The control this reads, or <see cref="InputControl.None" /> if the path resolved to
    /// nothing.</summary>
    public InputControl Control { get; private set; }

    /// <summary>Its processors.</summary>
    public IReadOnlyList<IInputProcessor> Processors { get; }

    internal void Override(string? path) {
        OverridePath = path;
        Resolve();
    }

    internal float Read(InputDeviceSet devices, int gamepadSlot) {
        if (!Control.IsValid) {
            return 0f;
        }

        var value = devices.ReadValue(Control, gamepadSlot);

        return Processors.Count == 0
            ? value
            : InputProcessorFactory.Apply(Processors, new(value, 0f), 1).X;
    }

    void Resolve() {
        Control = InputControlPath.TryParse(EffectivePath, out var control) ? control : InputControl.None;
    }
}

/// <summary>One way an action can be triggered.</summary>
/// <remarks>
///     <para>
///         A binding is either a control — <c>&lt;Gamepad&gt;/leftStick</c> — or a composite whose
///         parts are controls. It carries its own processors, its own interactions and the control
///         schemes it belongs to, and it is the unit a rebinding operation replaces.
///     </para>
///     <para>
///         <b>Interactions are per binding, not per action.</b> An action bound to both a tap and a
///         hold has to be able to have each half-way through its own pattern at the same time, which
///         a single state machine on the action cannot represent.
///     </para>
/// </remarks>
public sealed class InputBinding {
    readonly string[] groups;
    readonly InputBindingPart[] parts;

    InputControl x;
    InputControl y;

    internal InputBinding(InputBindingData data, InputAction action) {
        Data = data;
        Action = action;
        Processors = InputProcessorFactory.Parse(data.Processors);
        Interactions = InputInteractionFactory.Parse(data.Interactions);
        groups = SplitGroups(data.Groups);
        parts = new InputBindingPart[data.Parts.Count];

        for (var index = 0; index < parts.Length; index++) {
            parts[index] = new(data.Parts[index]);
        }

        Resolve();
    }

    /// <summary>What the asset said.</summary>
    public InputBindingData Data { get; }

    /// <summary>The action this belongs to.</summary>
    public InputAction Action { get; }

    /// <summary>Which composite this is, or <see cref="InputCompositeKind.None" />.</summary>
    public InputCompositeKind Composite => Data.Composite;

    /// <summary>Whether this binding is a composite of parts.</summary>
    public bool IsComposite => Data.Composite != InputCompositeKind.None;

    /// <summary>Its parts, empty unless it is a composite.</summary>
    public IReadOnlyList<InputBindingPart> Parts => parts;

    /// <summary>Its processors.</summary>
    public IReadOnlyList<IInputProcessor> Processors { get; }

    /// <summary>Its interactions, or empty for the default press behaviour.</summary>
    public IReadOnlyList<IInputInteraction> Interactions { get; }

    /// <summary>The control schemes it belongs to, or empty for "all of them".</summary>
    public IReadOnlyList<string> Groups => groups;

    /// <summary>The path the player rebound this to, or <see langword="null" />.</summary>
    /// <remarks>Meaningless on a composite, whose parts carry their own.</remarks>
    public string? OverridePath { get; private set; }

    /// <summary>The path in force.</summary>
    public string EffectivePath => OverridePath ?? Data.Path;

    /// <summary>The control it reads, or the horizontal half of a two-dimensional one.</summary>
    public InputControl Control => x;

    /// <summary>The vertical half, or <see cref="InputControl.None" />.</summary>
    public InputControl ControlY => y;

    /// <summary>Whether the binding resolved to anything this build can read.</summary>
    /// <remarks>
    ///     A binding pointing at a control the build does not have is <em>not</em> an error: an asset
    ///     shared between a desktop and a phone names controls neither of them both has. It reads
    ///     zero and says so here, so a settings screen can grey it out.
    /// </remarks>
    public bool IsResolved => IsComposite ? Array.Exists(parts, part => part.Control.IsValid) : x.IsValid;

    /// <summary>Whether this binding is active under a control scheme.</summary>
    /// <param name="scheme">The scheme's name, or <see langword="null" /> for no scheme at all.</param>
    /// <remarks>
    ///     A binding with no groups belongs to every scheme. That is what makes <c>Escape</c> work in
    ///     a game with a keyboard scheme and a gamepad scheme without being written twice.
    /// </remarks>
    public bool BelongsTo(string? scheme) {
        if (groups.Length == 0 || scheme is null) {
            return true;
        }

        for (var index = 0; index < groups.Length; index++) {
            if (string.Equals(groups[index], scheme, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Which families of device this binding reads.</summary>
    /// <param name="into">Where to add them. Duplicates are not added.</param>
    public void CollectDevices(List<InputDeviceKind> into) {
        ArgumentNullException.ThrowIfNull(into);

        if (IsComposite) {
            for (var index = 0; index < parts.Length; index++) {
                Add(into, parts[index].Control.Device);
            }

            return;
        }

        Add(into, x.Device);

        static void Add(List<InputDeviceKind> into, InputDeviceKind kind) {
            if (kind != InputDeviceKind.None && !into.Contains(kind)) {
                into.Add(kind);
            }
        }
    }

    /// <summary>Every control this binding reads, for conflict detection.</summary>
    /// <param name="into">Where to add them.</param>
    public void CollectControls(List<InputControl> into) {
        ArgumentNullException.ThrowIfNull(into);

        if (IsComposite) {
            for (var index = 0; index < parts.Length; index++) {
                if (parts[index].Control.IsValid) {
                    into.Add(parts[index].Control);
                }
            }

            return;
        }

        if (x.IsValid) {
            into.Add(x);
        }

        if (y.IsValid) {
            into.Add(y);
        }
    }

    /// <summary>Replaces the path this binding, or one of its parts, reads.</summary>
    /// <param name="path">The new path, or <see langword="null" /> to go back to the asset's.</param>
    /// <param name="part">Which part, or <c>-1</c> for a non-composite binding.</param>
    internal void Override(string? path, int part = -1) {
        if (part >= 0) {
            if ((uint)part < (uint)parts.Length) {
                parts[part].Override(path);
            }

            return;
        }

        OverridePath = path;
        Resolve();
    }

    /// <summary>Reads the binding's value for this frame.</summary>
    /// <param name="devices">Where the values come from.</param>
    /// <param name="gamepadSlot">Which pad an unindexed gamepad control reads.</param>
    /// <param name="dimensions">How many components the action wants.</param>
    /// <returns>The processed value.</returns>
    internal Vector2 Read(InputDeviceSet devices, int gamepadSlot, int dimensions) {
        var value = IsComposite ? ReadComposite(devices, gamepadSlot) : ReadDirect(devices, gamepadSlot);

        return Processors.Count == 0 ? value : InputProcessorFactory.Apply(Processors, value, dimensions);
    }

    Vector2 ReadDirect(InputDeviceSet devices, int gamepadSlot) =>
        new(
            x.IsValid ? devices.ReadValue(x, gamepadSlot) : 0f,
            y.IsValid ? devices.ReadValue(y, gamepadSlot) : 0f
        );

    /// <summary>Combines the parts into the composite's value.</summary>
    /// <remarks>
    ///     <b>Up is negative</b>, in both the <see cref="InputCompositeKind.Vector2" /> composite and
    ///     the sticks, because the engine's screen convention has Y increasing downward
    ///     (<c>Vixen.Core.Mathematics/Conventions.md</c>). A composite that disagreed with the stick
    ///     it sits next to in the same action would send the player the other way depending on which
    ///     device they picked up, which is the single worst bug this file can have.
    /// </remarks>
    Vector2 ReadComposite(InputDeviceSet devices, int gamepadSlot) {
        switch (Composite) {
            case InputCompositeKind.Axis1D:
                return new(Part("positive", devices, gamepadSlot) - Part("negative", devices, gamepadSlot), 0f);

            case InputCompositeKind.Vector2:
                return new(
                    Part("right", devices, gamepadSlot) - Part("left", devices, gamepadSlot),
                    Part("down", devices, gamepadSlot) - Part("up", devices, gamepadSlot)
                );

            case InputCompositeKind.ButtonWithModifiers: {
                for (var index = 0; index < parts.Length; index++) {
                    if (string.Equals(parts[index].Part, "modifier", StringComparison.OrdinalIgnoreCase)
                        && parts[index].Read(devices, gamepadSlot) < InputSettings.DefaultPressPoint) {
                        return Vector2.Zero;
                    }
                }

                return new(Part("button", devices, gamepadSlot), 0f);
            }

            default:
                return Vector2.Zero;
        }
    }

    /// <summary>Reads the first part filling a slot, clamped to <c>[0, 1]</c>.</summary>
    /// <remarks>
    ///     Clamped because a part is a direction's <em>amount</em>: a trigger bound to <c>up</c>
    ///     contributes how far it is pulled, and a control that reported <c>-1</c> would push the
    ///     composite the opposite way from the slot it was put in.
    /// </remarks>
    float Part(string slot, InputDeviceSet devices, int gamepadSlot) {
        for (var index = 0; index < parts.Length; index++) {
            if (string.Equals(parts[index].Part, slot, StringComparison.OrdinalIgnoreCase)) {
                return Math.Clamp(parts[index].Read(devices, gamepadSlot), 0f, 1f);
            }
        }

        return 0f;
    }

    void Resolve() {
        if (IsComposite || !InputControlPath.TryParse(EffectivePath, out var horizontal, out var vertical)) {
            x = InputControl.None;
            y = InputControl.None;
            return;
        }

        x = horizontal;
        y = vertical;
    }

    static string[] SplitGroups(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
