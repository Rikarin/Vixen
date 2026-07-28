// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>One way to play: which devices, and therefore which bindings.</summary>
/// <remarks>
///     <para>
///         A scheme filters the bindings an action considers. "Keyboard&amp;Mouse" and "Gamepad" are
///         the usual two, and the reason they exist is not the filtering itself but everything
///         downstream of knowing which one is active: which glyph a prompt draws, which rebinding
///         screen a player is on, and which device a second player may not take.
///     </para>
///     <para>
///         A scheme is <em>supported</em> when every device it requires is present. An optional
///         device is one the scheme can do without — a gamepad scheme that would like a second stick
///         but does not need one.
///     </para>
/// </remarks>
public sealed class InputControlScheme {
    internal InputControlScheme(InputControlSchemeData data) => Data = data;

    /// <summary>What the asset said.</summary>
    public InputControlSchemeData Data { get; }

    /// <summary>Its name, which is what a binding's <c>groups</c> names.</summary>
    public string Name => Data.Name;

    /// <summary>What it needs.</summary>
    public IReadOnlyList<InputDeviceRequirementData> Devices => Data.Devices;

    /// <summary>Whether every device this scheme requires is present.</summary>
    /// <param name="devices">The devices.</param>
    public bool IsSupportedBy(InputDeviceSet devices) {
        ArgumentNullException.ThrowIfNull(devices);

        for (var index = 0; index < Devices.Count; index++) {
            if (!Devices[index].Optional && !devices.Has(Devices[index].Device)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether this scheme uses a family of device at all.</summary>
    /// <param name="kind">The family.</param>
    public bool Uses(InputDeviceKind kind) {
        for (var index = 0; index < Devices.Count; index++) {
            if (Devices[index].Device == kind) {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
