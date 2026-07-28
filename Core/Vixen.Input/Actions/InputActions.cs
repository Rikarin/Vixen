// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Vixen.Core;

namespace Vixen.Input;

/// <summary>A <c>.vxinput</c> asset, loaded and ready to read.</summary>
/// <remarks>
///     <para>
///         One instance is one player. Two players sharing a couch load the same asset twice and set
///         a different <see cref="GamepadSlot" /> on each, which is the whole of local multiplayer:
///         the bindings are identical, the device they read is not, and nothing about the asset has
///         to know how many players there are.
///     </para>
///     <para>
///         <b>Nothing here polls a device.</b> <see cref="Update(InputDeviceSet, double)" /> is called once a frame by
///         whatever owns the loop — the ECS <c>InputUpdateSystem</c> in a game, a test directly —
///         after the frame's events have been submitted to the <see cref="InputDeviceSet" />. Every
///         action then holds a value that does not change until the next update, which is what makes
///         reading input from a fixed-step simulation meaningful.
///     </para>
/// </remarks>
public sealed class InputActions {
    readonly InputActionMap[] maps;
    readonly InputControlScheme[] schemes;
    readonly List<InputControl> scratch = [];

    InputActions(InputActionAssetData data) {
        Data = data;
        maps = new InputActionMap[data.Maps.Count];
        schemes = new InputControlScheme[data.ControlSchemes.Count];

        for (var index = 0; index < maps.Length; index++) {
            maps[index] = new(data.Maps[index], this);
        }

        for (var index = 0; index < schemes.Length; index++) {
            schemes[index] = new(data.ControlSchemes[index]);
        }
    }

    /// <summary>What the asset said.</summary>
    public InputActionAssetData Data { get; }

    /// <summary>Its name, which is what the generated accessor class is called.</summary>
    public string Name => Data.Name;

    /// <summary>Its action maps.</summary>
    public IReadOnlyList<InputActionMap> Maps => maps;

    /// <summary>Its control schemes.</summary>
    public IReadOnlyList<InputControlScheme> ControlSchemes => schemes;

    /// <summary>
    ///     Which entry of <see cref="InputDeviceSet.Gamepads" /> this instance's gamepad bindings read.
    /// </summary>
    /// <remarks>
    ///     Only affects bindings whose path carries no explicit index — which is every binding an
    ///     author should write, because a <c>.vxinput</c> that named pad two would only ever work for
    ///     player two.
    /// </remarks>
    public int GamepadSlot { get; set; }

    /// <summary>The scheme in force, or <see langword="null" /> for "every binding".</summary>
    /// <remarks>
    ///     Null is the right default and not a missing value: an asset with no schemes, or a game
    ///     that wants a keyboard and a pad to work at the same moment, wants every binding live.
    /// </remarks>
    public InputControlScheme? ActiveControlScheme { get; private set; }

    /// <summary>
    ///     Whether to follow the player onto whichever device they pick up.
    /// </summary>
    /// <remarks>
    ///     Off by default. Switching schemes changes which prompts are drawn and which bindings a
    ///     rebinding screen edits, and a game that has not thought about that is better served by
    ///     every binding being live than by the scheme changing under it when someone bumps a
    ///     controller. Turn it on and the engine does what a player expects.
    /// </remarks>
    public bool AutoSwitchControlSchemes { get; set; }

    /// <summary>Raised when <see cref="ActiveControlScheme" /> changes.</summary>
    public event Action<InputControlScheme?>? ControlSchemeChanged;

    /// <summary>Loads an asset from <c>.vxinput</c> text.</summary>
    /// <param name="text">The document.</param>
    /// <param name="name">What to call it when the document does not say.</param>
    /// <returns>The asset.</returns>
    /// <exception cref="InputAssetException">The document could not be read.</exception>
    /// <remarks>
    ///     A document that read but has problems — an unknown processor, a binding with no path —
    ///     loads, and the problems are on <see cref="InputAssetException" /> only when there is no
    ///     document at all. Use <see cref="TryLoad" /> to see the diagnostics of one that did load.
    /// </remarks>
    public static InputActions Load(string text, string name = "InputActions") {
        if (!TryLoad(text, out var actions, out var diagnostics, name)) {
            throw new InputAssetException(name, diagnostics);
        }

        return actions;
    }

    /// <summary>Loads an asset, reporting what was wrong with it rather than throwing.</summary>
    /// <param name="text">The document.</param>
    /// <param name="actions">The asset.</param>
    /// <param name="diagnostics">Everything wrong with it, which may be non-empty on success.</param>
    /// <param name="name">What to call it when the document does not say.</param>
    /// <returns><see langword="false" /> if there is no usable document at all.</returns>
    public static bool TryLoad(
        string text,
        [NotNullWhen(true)] out InputActions? actions,
        out IReadOnlyList<InputAssetDiagnostic> diagnostics,
        string name = "InputActions"
    ) {
        var result = InputActionAssetReader.Read(text, name);
        diagnostics = result.Diagnostics;
        actions = result.Asset is null ? null : new(result.Asset);
        return actions is not null;
    }

    /// <summary>Builds an asset from a model that is already in memory.</summary>
    /// <param name="data">The model.</param>
    /// <returns>The asset.</returns>
    public static InputActions From(InputActionAssetData data) {
        ArgumentNullException.ThrowIfNull(data);
        return new(data);
    }

    /// <summary>Finds a map by name, case-insensitively.</summary>
    /// <param name="name">Its name.</param>
    /// <exception cref="KeyNotFoundException">There is no such map.</exception>
    public InputActionMap this[string name] =>
        TryFindMap(name, out var map)
            ? map
            : throw new KeyNotFoundException($"'{Name}' has no action map called '{name}'.");

    /// <summary>Finds a map by name, case-insensitively.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="map">The map.</param>
    /// <returns><see langword="false" /> if there is no such map.</returns>
    public bool TryFindMap(string? name, [NotNullWhen(true)] out InputActionMap? map) {
        if (name is not null) {
            for (var index = 0; index < maps.Length; index++) {
                if (string.Equals(maps[index].Name, name, StringComparison.OrdinalIgnoreCase)) {
                    map = maps[index];
                    return true;
                }
            }
        }

        map = null;
        return false;
    }

    /// <summary>Finds an action by its full name, <c>Player/Move</c>.</summary>
    /// <param name="path">The name.</param>
    /// <param name="action">The action.</param>
    /// <returns><see langword="false" /> if there is no such action.</returns>
    /// <remarks>
    ///     A bare name with no slash is searched for across every map, which is what a game with one
    ///     map wants and what a rebinding screen's saved override file may contain.
    /// </remarks>
    public bool TryFindAction(string? path, [NotNullWhen(true)] out InputAction? action) {
        action = null;

        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        var separator = path!.IndexOf('/');

        if (separator < 0) {
            for (var index = 0; index < maps.Length; index++) {
                if (maps[index].TryFind(path, out action)) {
                    return true;
                }
            }

            return false;
        }

        return TryFindMap(path[..separator], out var map) && map.TryFind(path[(separator + 1)..], out action);
    }

    /// <summary>Finds an action by its full name.</summary>
    /// <param name="path">The name, <c>Player/Move</c>.</param>
    /// <exception cref="KeyNotFoundException">There is no such action.</exception>
    public InputAction FindAction(string path) =>
        TryFindAction(path, out var action)
            ? action
            : throw new KeyNotFoundException($"'{Name}' has no action called '{path}'.");

    /// <summary>Enables every map.</summary>
    public void Enable() {
        for (var index = 0; index < maps.Length; index++) {
            maps[index].Enable();
        }
    }

    /// <summary>Disables every map.</summary>
    public void Disable() {
        for (var index = 0; index < maps.Length; index++) {
            maps[index].Disable();
        }
    }

    /// <summary>Chooses the control scheme by name.</summary>
    /// <param name="name">Its name, or <see langword="null" /> to make every binding live.</param>
    /// <returns><see langword="false" /> if there is no such scheme.</returns>
    /// <remarks>
    ///     Switching resets every action, because a control held under the old scheme is not held
    ///     under the new one and reporting it as released would be a lie the interactions would act
    ///     on.
    /// </remarks>
    public bool SetControlScheme(string? name) {
        if (name is null) {
            Apply(null);
            return true;
        }

        for (var index = 0; index < schemes.Length; index++) {
            if (string.Equals(schemes[index].Name, name, StringComparison.OrdinalIgnoreCase)) {
                Apply(schemes[index]);
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads every enabled action.</summary>
    /// <param name="devices">The devices, already given this frame's events.</param>
    /// <param name="time">The clock.</param>
    public void Update(InputDeviceSet devices, GameTime time) {
        ArgumentNullException.ThrowIfNull(devices);
        Update(devices, time.TotalSeconds);
    }

    /// <summary>Reads every enabled action.</summary>
    /// <param name="devices">The devices, already given this frame's events.</param>
    /// <param name="time">The time now, in seconds, from a clock that only goes forward.</param>
    public void Update(InputDeviceSet devices, double time) {
        ArgumentNullException.ThrowIfNull(devices);

        if (AutoSwitchControlSchemes) {
            SwitchIfNeeded(devices);
        }

        var scheme = ActiveControlScheme?.Name;

        for (var index = 0; index < maps.Length; index++) {
            maps[index].Update(devices, GamepadSlot, time, scheme);
        }
    }

    /// <summary>Every action already bound to a control.</summary>
    /// <param name="control">The control.</param>
    /// <param name="into">Where to add the conflicts.</param>
    /// <param name="except">An action to ignore — the one being rebound.</param>
    /// <param name="scheme">
    ///     Only consider bindings live under this scheme, or <see langword="null" /> for all of them.
    /// </param>
    /// <remarks>
    ///     <b>What makes rebinding usable.</b> A player who binds "crouch" to the key "jump" is
    ///     already on has to be told, and the engine is the only layer that can tell them: the game
    ///     does not know what every other action is bound to and the OS certainly does not. Whether
    ///     to refuse the binding, swap the two, or allow the clash is the game's decision, which is
    ///     why this reports rather than prevents.
    /// </remarks>
    public void FindConflicts(
        InputControl control,
        List<InputBindingConflict> into,
        InputAction? except = null,
        string? scheme = null
    ) {
        ArgumentNullException.ThrowIfNull(into);

        for (var mapIndex = 0; mapIndex < maps.Length; mapIndex++) {
            var actions = maps[mapIndex].Actions;

            for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++) {
                var action = actions[actionIndex];

                if (ReferenceEquals(action, except)) {
                    continue;
                }

                for (var bindingIndex = 0; bindingIndex < action.Bindings.Count; bindingIndex++) {
                    var binding = action.Bindings[bindingIndex];

                    if (!binding.BelongsTo(scheme)) {
                        continue;
                    }

                    if (binding.IsComposite) {
                        for (var part = 0; part < binding.Parts.Count; part++) {
                            if (binding.Parts[part].Control == control) {
                                into.Add(new(action, bindingIndex, part, control));
                            }
                        }

                        continue;
                    }

                    scratch.Clear();
                    binding.CollectControls(scratch);

                    if (scratch.Contains(control)) {
                        into.Add(new(action, bindingIndex, -1, control));
                    }
                }
            }
        }
    }

    /// <summary>Writes every binding the player has changed.</summary>
    /// <returns>
    ///     A short text document, or an empty string when nothing has been rebound.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <b>Only the overrides, never the whole asset.</b> A saved file that contained every
    ///         binding would freeze a player's controls at the version of the game they first ran:
    ///         an action added in a patch would load with no binding at all, and a default someone
    ///         improved would never reach anybody. Saving the difference means a patch's changes
    ///         arrive and the player's do not get lost.
    ///     </para>
    ///     <para>
    ///         Text rather than binary, and one line per override, so that a support request can
    ///         include it and a player can delete the one line that is wrong.
    ///     </para>
    /// </remarks>
    public string SaveBindingOverrides() {
        var builder = new StringBuilder();

        for (var mapIndex = 0; mapIndex < maps.Length; mapIndex++) {
            var actions = maps[mapIndex].Actions;

            for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++) {
                var action = actions[actionIndex];

                for (var bindingIndex = 0; bindingIndex < action.Bindings.Count; bindingIndex++) {
                    var binding = action.Bindings[bindingIndex];

                    if (binding.OverridePath is { } path) {
                        Write(builder, action.Path, bindingIndex, -1, path);
                    }

                    for (var part = 0; part < binding.Parts.Count; part++) {
                        if (binding.Parts[part].OverridePath is { } partPath) {
                            Write(builder, action.Path, bindingIndex, part, partPath);
                        }
                    }
                }
            }
        }

        return builder.ToString();

        static void Write(StringBuilder builder, string action, int binding, int part, string path) {
            builder.Append(action).Append('[').Append(binding.ToString(CultureInfo.InvariantCulture)).Append(']');

            if (part >= 0) {
                builder.Append('[').Append(part.ToString(CultureInfo.InvariantCulture)).Append(']');
            }

            builder.Append('=').Append(path).Append('\n');
        }
    }

    /// <summary>Applies a document written by <see cref="SaveBindingOverrides" />.</summary>
    /// <param name="text">The document.</param>
    /// <returns>How many overrides were applied.</returns>
    /// <remarks>
    ///     A line naming an action, a binding or a part that no longer exists is <b>skipped, not an
    ///     error</b>. The asset it was saved against is a previous version of the game's, and the
    ///     alternative to skipping is a player whose entire control configuration is refused because
    ///     one action was renamed.
    /// </remarks>
    public int LoadBindingOverrides(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return 0;
        }

        var applied = 0;

        foreach (var line in text!.Split('\n')) {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#') {
                continue;
            }

            var equals = trimmed.IndexOf('=');

            if (equals < 0) {
                continue;
            }

            var target = trimmed[..equals];
            var path = trimmed[(equals + 1)..].Trim();
            var open = target.IndexOf('[');

            if (open < 0 || !TryFindAction(target[..open], out var action)) {
                continue;
            }

            if (!TryIndices(target[open..], out var binding, out var part)) {
                continue;
            }

            if (binding >= action.Bindings.Count) {
                continue;
            }

            if (part >= 0 && part >= action.Bindings[binding].Parts.Count) {
                continue;
            }

            action.OverrideBinding(binding, path, part);
            applied++;
        }

        return applied;
    }

    /// <summary>Puts every binding back to what the asset says.</summary>
    public void RemoveAllBindingOverrides() {
        for (var mapIndex = 0; mapIndex < maps.Length; mapIndex++) {
            var actions = maps[mapIndex].Actions;

            for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++) {
                actions[actionIndex].RemoveBindingOverrides();
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>Follows the player onto whichever device they last used.</summary>
    void SwitchIfNeeded(InputDeviceSet devices) {
        var used = devices.LastUsedDevice;

        if (used == InputDeviceKind.None || ActiveControlScheme?.Uses(used) == true) {
            return;
        }

        for (var index = 0; index < schemes.Length; index++) {
            if (schemes[index].Uses(used) && schemes[index].IsSupportedBy(devices)) {
                Apply(schemes[index]);
                return;
            }
        }
    }

    void Apply(InputControlScheme? scheme) {
        if (ReferenceEquals(scheme, ActiveControlScheme)) {
            return;
        }

        ActiveControlScheme = scheme;

        for (var index = 0; index < maps.Length; index++) {
            maps[index].ResetState();
        }

        ControlSchemeChanged?.Invoke(scheme);
    }

    /// <summary>Reads <c>[3]</c> or <c>[3][1]</c> off the front of a saved override's target.</summary>
    static bool TryIndices(string text, out int binding, out int part) {
        binding = -1;
        part = -1;

        var close = text.IndexOf(']');

        if (text.Length == 0 || text[0] != '[' || close < 0) {
            return false;
        }

        if (!int.TryParse(text[1..close], NumberStyles.None, CultureInfo.InvariantCulture, out binding)) {
            return false;
        }

        var rest = text[(close + 1)..];

        if (rest.Length == 0) {
            return true;
        }

        var second = rest.IndexOf(']');

        return rest[0] == '['
            && second > 0
            && int.TryParse(rest[1..second], NumberStyles.None, CultureInfo.InvariantCulture, out part);
    }
}

/// <summary>One action already bound to a control a rebinding is about to take.</summary>
/// <param name="Action">The action that has it.</param>
/// <param name="BindingIndex">Which of its bindings.</param>
/// <param name="PartIndex">Which composite part, or <c>-1</c>.</param>
/// <param name="Control">The control they share.</param>
public readonly record struct InputBindingConflict(
    InputAction Action,
    int BindingIndex,
    int PartIndex,
    InputControl Control
) {
    /// <inheritdoc />
    public override string ToString() => $"{Action.Path} is already bound to {InputControlPath.Describe(Control)}";
}

/// <summary>A <c>.vxinput</c> that could not be read at all.</summary>
public sealed class InputAssetException : Exception {
    /// <summary>Creates the exception.</summary>
    /// <param name="asset">What the asset is called.</param>
    /// <param name="diagnostics">Everything wrong with it.</param>
    public InputAssetException(string asset, IReadOnlyList<InputAssetDiagnostic> diagnostics)
        : base(Describe(asset, diagnostics)) =>
        Diagnostics = diagnostics;

    /// <summary>Creates the exception.</summary>
    public InputAssetException() : base("An input asset could not be read.") => Diagnostics = [];

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public InputAssetException(string message) : base(message) => Diagnostics = [];

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public InputAssetException(string message, Exception innerException) : base(message, innerException) =>
        Diagnostics = [];

    /// <summary>Everything wrong with the document.</summary>
    public IReadOnlyList<InputAssetDiagnostic> Diagnostics { get; }

    static string Describe(string asset, IReadOnlyList<InputAssetDiagnostic> diagnostics) {
        var builder = new StringBuilder($"'{asset}' is not a readable .vxinput.");

        for (var index = 0; index < diagnostics.Count; index++) {
            builder.Append('\n').Append("  ").Append(diagnostics[index].ToString());
        }

        return builder.ToString();
    }
}
