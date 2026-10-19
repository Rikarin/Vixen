// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>Doc 50 § D2's Delivery service: what a player build would be, whether it can start, and starting one.</summary>
/// <remarks>
///     <para>
///         <b>The build's use cases as methods, published to plugins</b> (#1295). Build Settings,
///         Build and Run and the two radio submenus were private methods on the application closed
///         over its fields, and the device panel reached them through an <c>IDeviceDeploy</c> the
///         application implemented by forwarding to the same privates. One object does it now: the
///         Build menu and the settings window are a module's names for these methods, and Deploy is the
///         diagnostics module calling <see cref="Build" /> for the one kind of device a build can reach.
///     </para>
///     <para>
///         ⚠ <b>No device in this contract, and that is the reason it can be here.</b> What Deploy
///         means differs per kind of device — this machine is a publish and a launch, a phone is
///         <c>adb install</c> — and the kinds are the device manager's vocabulary, not the build's. So
///         the build answers "can I build and run here" and the module that owns the devices maps its
///         kinds onto that, which is what lets the application stop naming a single device type.
///     </para>
///     <para>
///         ⚠ <b>In <c>Vixen.Editor.Core</c> rather than beside <c>BuildStep</c> in
///         <c>Vixen.Editor.Assets.Content</c></b>, which is where doc 50 § D1 places the Delivery
///         context. Nothing in the contract is an asset-pipeline type — <see cref="PlayerBuildSettings" />
///         is already this assembly's — and the module that deploys is one that references this
///         assembly and not the import pipeline.
///     </para>
/// </remarks>
public interface IPlayerDelivery {
    /// <summary>The project's build settings, which is the one copy every surface reads and writes.</summary>
    PlayerBuildSettings Settings { get; }

    /// <summary>Which target a build would use: the setting, or this machine when it is empty.</summary>
    string Target { get; }

    /// <summary>Raised when something that is not an edit to the settings may have changed <see cref="Refusal" />.</summary>
    /// <remarks>
    ///     A build starting or ending, an import finishing, a <c>.csproj</c> arriving in a checkout.
    ///     Raised on the frame thread; the answer itself is asked for rather than carried, because
    ///     asking enumerates the project root and is therefore done on events and not once a frame.
    /// </remarks>
    event Action? Changed;

    /// <summary>Every scene in the project, project-relative, in path order.</summary>
    /// <returns>What could be put in a build.</returns>
    IEnumerable<string> Scenes();

    /// <summary>What would stop a listed scene shipping, or <see langword="null" /> when nothing would.</summary>
    /// <param name="path">The scene's project-relative path.</param>
    /// <returns>A one-word reason, or <see langword="null" />.</returns>
    string? SceneTrouble(string path);

    /// <summary>Why a player build cannot start, or <see langword="null" /> when it can.</summary>
    /// <returns>The sentence, or <see langword="null" />.</returns>
    string? Refusal();

    /// <summary>Builds the player, and optionally runs it.</summary>
    /// <param name="launch">Whether to start what came out.</param>
    /// <param name="finished">Told on the frame thread when a build that started has ended, however it ended.</param>
    /// <returns>
    ///     Whether a build started. <see langword="false" /> means it was refused before anything ran,
    ///     the user has been told why, and <paramref name="finished" /> will not be called.
    /// </returns>
    bool Build(bool launch, Action? finished = null);

    /// <summary>Writes the build settings, which is what a menu tick and a field both do.</summary>
    void SaveSettings();
}
