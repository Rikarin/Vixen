// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Debugger;

/// <summary>What Deploy means, for whoever can actually build a player.</summary>
/// <remarks>
///     <para>
///         <b>The device manager knows what devices there are and this says what can be done to
///         one.</b> Building a player is a project, a target, a content build and a process — none of
///         which belongs beside a list of machines, and all of which is why this assembly is
///         constructible against a bare <c>UiDocument</c>.
///     </para>
///     <para>
///         ⚠ <b>Refusal is a sentence, not a boolean.</b> Doc 20's first bar is that a verb which is
///         not reachable right now is visibly not reachable — and "why not" is the half that stops
///         somebody pressing a greyed button repeatedly. A console takes its build through the
///         vendor's SDK; a phone needs a device provider that does not exist yet; each is a different
///         sentence and none of them is "no".
///     </para>
///     <para>
///         Here rather than beside the implementation because both ends need to name it: the panel
///         that offers the button and whatever in the editor can carry it out. Doc 36 § P3 — the two
///         are in different assemblies now, and a contract owned by either would be a reference back
///         to it.
///     </para>
/// </remarks>
public interface IDeviceDeploy {
    /// <summary>Why this device cannot be deployed to, or <see langword="null" /> if it can.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The sentence, or <see langword="null" />.</returns>
    string? Refuse(DeviceEntry device);

    /// <summary>Builds a player and puts it on the device.</summary>
    /// <param name="device">The device.</param>
    /// <remarks>
    ///     Asynchronous in effect and not in signature: it starts a background build and returns, and
    ///     what happens next is a notification. A method that awaited would be one the frame thread
    ///     cannot call.
    /// </remarks>
    void Deploy(DeviceEntry device);
}
