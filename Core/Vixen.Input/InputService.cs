// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Input;

/// <summary>The devices, the assets reading them, and the one call that advances both.</summary>
/// <remarks>
///     <para>
///         What goes in the <see cref="ServiceRegistry" /> under <c>InputService</c>, and what the
///         frame loop calls twice: <see cref="BeginFrame" /> before the platform's events are
///         submitted, <see cref="Update(double)" /> after. Splitting it in two is what makes the deltas
///         right — the clear has to happen before the frame's motion arrives, not after.
///     </para>
///     <para>
///         It holds a list of assets rather than one, because local multiplayer is several
///         <see cref="InputActions" /> over the same file with different
///         <see cref="InputActions.GamepadSlot" />s, and a rebinding screen is a fourth one nobody
///         wants to remember to update by hand.
///     </para>
/// </remarks>
public sealed class InputService {
    readonly List<InputActions> assets = [];

    /// <summary>Creates the service with a device set of its own.</summary>
    public InputService() : this(new()) { }

    /// <summary>Creates the service over an existing device set.</summary>
    /// <param name="devices">The devices.</param>
    public InputService(InputDeviceSet devices) {
        ArgumentNullException.ThrowIfNull(devices);
        Devices = devices;
    }

    /// <summary>The devices everything reads.</summary>
    public InputDeviceSet Devices { get; }

    /// <summary>The assets this service updates.</summary>
    public IReadOnlyList<InputActions> Assets => assets;

    /// <summary>
    ///     A rebinding in progress, which this service will drive, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     Cleared automatically once the operation reaches a terminal state, so a screen that
    ///     forgets to unset it does not leave the service driving a finished operation forever.
    /// </remarks>
    public InputRebindingOperation? Rebinding { get; set; }

    /// <summary>Adds an asset to update each frame.</summary>
    /// <param name="actions">The asset.</param>
    /// <returns>The asset, for chaining.</returns>
    public InputActions Add(InputActions actions) {
        ArgumentNullException.ThrowIfNull(actions);

        if (!assets.Contains(actions)) {
            assets.Add(actions);
        }

        return actions;
    }

    /// <summary>Stops updating an asset.</summary>
    /// <param name="actions">The asset.</param>
    /// <returns><see langword="false" /> if it was not being updated.</returns>
    public bool Remove(InputActions actions) => assets.Remove(actions);

    /// <summary>Clears the frame's motion deltas. Call before submitting the frame's events.</summary>
    public void BeginFrame() => Devices.BeginFrame();

    /// <summary>Reads every asset. Call after submitting the frame's events.</summary>
    /// <param name="time">The clock.</param>
    public void Update(GameTime time) => Update(time.TotalSeconds);

    /// <summary>Reads every asset.</summary>
    /// <param name="time">The time now, in seconds, from a clock that only goes forward.</param>
    public void Update(double time) {
        // Before the assets, so that the frame a rebinding completes on is a frame the newly bound
        // action can already be read on rather than the one after it.
        if (Rebinding is { } operation) {
            operation.Update(Devices, time);

            if (operation.State is InputRebindingState.Completed or InputRebindingState.Canceled) {
                Rebinding = null;
            }
        }

        for (var index = 0; index < assets.Count; index++) {
            assets[index].Update(Devices, time);
        }
    }

    /// <summary>Releases everything held and forgets every half-finished interaction.</summary>
    /// <remarks>What a host calls on focus loss and on suspend.</remarks>
    public void ResetHeldState() {
        Devices.ResetHeldState();

        for (var index = 0; index < assets.Count; index++) {
            var maps = assets[index].Maps;

            for (var map = 0; map < maps.Count; map++) {
                if (maps[map].IsEnabled) {
                    maps[map].Disable();
                    maps[map].Enable();
                }
            }
        }
    }
}
