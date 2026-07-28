// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>What reading a <c>.vxinput</c> produced.</summary>
/// <param name="Asset">The document, or <see langword="null" /> if it could not be read at all.</param>
/// <param name="Diagnostics">Everything wrong with it.</param>
/// <remarks>
///     Both halves can be present at once, and that is the point: a document with one unusable
///     binding still yields every other action, so the generator emits a class whose one broken
///     member is an error rather than a project whose every use site is.
/// </remarks>
public readonly record struct InputAssetReadResult(
    InputActionAssetData? Asset,
    IReadOnlyList<InputAssetDiagnostic> Diagnostics
) {
    /// <summary>Whether the document is usable and nothing was wrong with it.</summary>
    public bool Succeeded => Asset is not null && Diagnostics.Count == 0;
}

/// <summary>Binds a parsed <c>.vxinput</c> tree to the asset model.</summary>
/// <remarks>
///     <para>
///         Hand-written rather than driven by <c>Vixen.Core.Yaml</c>'s object mapper, for the reason
///         <see cref="VxInputNode" /> gives: the same code has to run inside the compiler, where a
///         generated type registry does not exist. The cost is this file; the benefit is that the
///         accessor a project compiles against and the asset it loads at run time were produced by
///         one reader.
///     </para>
///     <para>
///         <b>Nothing throws.</b> Every problem is a diagnostic with a line and a column, because the
///         two callers both want to report rather than crash — a generator turns them into compiler
///         errors against the file, and the runtime loader into a log line naming the asset.
///     </para>
/// </remarks>
public static class InputActionAssetReader {
    /// <summary>Reads a document.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="defaultName">
    ///     What to call the asset when the document does not say — normally the file name.
    /// </param>
    /// <returns>The asset and everything wrong with it.</returns>
    public static InputAssetReadResult Read(string text, string defaultName = "InputActions") {
        ArgumentNullException.ThrowIfNull(text);

        var diagnostics = new List<InputAssetDiagnostic>();
        VxInputNode root;

        try {
            root = VxInputReader.Read(text);
        } catch (VxInputParseException failure) {
            diagnostics.Add(new(failure.Detail, failure.Line, failure.Column));
            return new(null, diagnostics);
        }

        if (root is not VxInputMapping mapping) {
            diagnostics.Add(new("A .vxinput is a mapping of fields at its root.", root.Line, 1));
            return new(null, diagnostics);
        }

        var name = Text(mapping, "name") ?? defaultName;
        var version = InputActionAssetData.CurrentVersion;

        if (mapping.TryGet("version", out var versionNode) && versionNode is VxInputScalar versionScalar) {
            if (int.TryParse(versionScalar.Value, out var parsed)) {
                version = parsed;

                if (parsed > InputActionAssetData.CurrentVersion) {
                    diagnostics.Add(
                        new(
                            $"This asset is version {parsed} and this build understands {InputActionAssetData.CurrentVersion}. "
                            + "It was written by a newer editor.",
                            versionScalar.Line,
                            1
                        )
                    );
                }
            } else {
                diagnostics.Add(new($"'{versionScalar.Value}' is not a version number.", versionScalar.Line, 1));
            }
        }

        var maps = ReadMaps(mapping, diagnostics);
        var schemes = ReadSchemes(mapping, diagnostics);

        return new(new(name, maps, schemes, version), diagnostics);
    }

    static List<InputActionMapData> ReadMaps(VxInputMapping root, List<InputAssetDiagnostic> diagnostics) {
        var maps = new List<InputActionMapData>();

        if (!root.TryGet("maps", out var node)) {
            diagnostics.Add(new("The asset has no 'maps'. An action asset with no maps binds nothing.", root.Line, 1));
            return maps;
        }

        foreach (var item in Sequence(node, "maps", diagnostics)) {
            if (item is not VxInputMapping entry) {
                diagnostics.Add(new("An action map is a mapping with a 'name' and 'actions'.", item.Line, 1));
                continue;
            }

            var name = Text(entry, "name");

            if (string.IsNullOrEmpty(name)) {
                diagnostics.Add(new("This action map has no 'name'.", entry.Line, 1));
                continue;
            }

            if (maps.Exists(existing => NameEquals(existing.Name, name!))) {
                diagnostics.Add(new($"There is already an action map called '{name}'.", entry.Line, 1));
                continue;
            }

            maps.Add(new(name!, ReadActions(entry, name!, diagnostics)));
        }

        return maps;
    }

    static List<InputActionData> ReadActions(
        VxInputMapping map,
        string mapName,
        List<InputAssetDiagnostic> diagnostics
    ) {
        var actions = new List<InputActionData>();

        if (!map.TryGet("actions", out var node)) {
            diagnostics.Add(new($"The action map '{mapName}' has no 'actions'.", map.Line, 1));
            return actions;
        }

        foreach (var item in Sequence(node, "actions", diagnostics)) {
            if (item is not VxInputMapping entry) {
                diagnostics.Add(new("An action is a mapping with a 'name'.", item.Line, 1));
                continue;
            }

            var name = Text(entry, "name");

            if (string.IsNullOrEmpty(name)) {
                diagnostics.Add(new("This action has no 'name'.", entry.Line, 1));
                continue;
            }

            if (actions.Exists(existing => NameEquals(existing.Name, name!))) {
                diagnostics.Add(new($"'{mapName}' already has an action called '{name}'.", entry.Line, 1));
                continue;
            }

            var type = ReadActionType(entry, diagnostics);
            var controlType = ReadControlType(entry, type, diagnostics);

            actions.Add(new(name!, type, controlType, ReadBindings(entry, name!, controlType, diagnostics)));
        }

        return actions;
    }

    static InputActionType ReadActionType(VxInputMapping action, List<InputAssetDiagnostic> diagnostics) {
        if (!action.TryGet("type", out var node) || node is not VxInputScalar scalar || scalar.Value.Length == 0) {
            return InputActionType.Button;
        }

        switch (scalar.Value.ToLowerInvariant()) {
            case "button": return InputActionType.Button;
            case "value": return InputActionType.Value;
            case "passthrough": return InputActionType.PassThrough;
            default:
                diagnostics.Add(
                    new($"'{scalar.Value}' is not an action type. It is 'button', 'value' or 'passThrough'.", scalar.Line, 1)
                );

                return InputActionType.Button;
        }
    }

    static InputControlType ReadControlType(
        VxInputMapping action,
        InputActionType type,
        List<InputAssetDiagnostic> diagnostics
    ) {
        if (!action.TryGet("controlType", out var node) || node is not VxInputScalar scalar || scalar.Value.Length == 0) {
            // A button action reads as a button and everything else as a single axis, which is the
            // shape most value actions have. An action that produces a vector says so.
            return type == InputActionType.Button ? InputControlType.Button : InputControlType.Axis;
        }

        switch (scalar.Value.ToLowerInvariant()) {
            case "button": return InputControlType.Button;
            case "axis": return InputControlType.Axis;
            case "vector2": return InputControlType.Vector2;
            default:
                diagnostics.Add(
                    new($"'{scalar.Value}' is not a control type. It is 'button', 'axis' or 'vector2'.", scalar.Line, 1)
                );

                return InputControlType.Button;
        }
    }

    static List<InputBindingData> ReadBindings(
        VxInputMapping action,
        string actionName,
        InputControlType controlType,
        List<InputAssetDiagnostic> diagnostics
    ) {
        var bindings = new List<InputBindingData>();

        if (!action.TryGet("bindings", out var node)) {
            // Not a diagnostic. An action authored ahead of its bindings is ordinary, and a rebinding
            // screen is exactly how the player fills it in.
            return bindings;
        }

        foreach (var item in Sequence(node, "bindings", diagnostics)) {
            if (item is not VxInputMapping entry) {
                diagnostics.Add(new("A binding is a mapping with a 'path' or a 'composite'.", item.Line, 1));
                continue;
            }

            var composite = ReadComposite(entry, diagnostics);
            var path = Text(entry, "path");

            if (composite == InputCompositeKind.None) {
                if (string.IsNullOrEmpty(path)) {
                    diagnostics.Add(
                        new($"A binding of '{actionName}' has neither a 'path' nor a 'composite'.", entry.Line, 1)
                    );

                    continue;
                }

                bindings.Add(
                    new(
                        path!,
                        Text(entry, "name"),
                        Text(entry, "groups"),
                        Text(entry, "interactions"),
                        Text(entry, "processors")
                    )
                );

                continue;
            }

            var parts = ReadParts(entry, actionName, composite, diagnostics);

            if (parts.Count == 0) {
                continue;
            }

            if (composite == InputCompositeKind.Vector2 && controlType != InputControlType.Vector2) {
                diagnostics.Add(
                    new(
                        $"'{actionName}' is bound to a vector2 composite but its controlType is "
                        + $"'{controlType.ToString().ToLowerInvariant()}'. It would read as its first component only.",
                        entry.Line,
                        1
                    )
                );
            }

            bindings.Add(
                new(
                    string.Empty,
                    Text(entry, "name"),
                    Text(entry, "groups"),
                    Text(entry, "interactions"),
                    Text(entry, "processors"),
                    composite,
                    parts
                )
            );
        }

        return bindings;
    }

    static InputCompositeKind ReadComposite(VxInputMapping binding, List<InputAssetDiagnostic> diagnostics) {
        if (!binding.TryGet("composite", out var node) || node is not VxInputScalar scalar || scalar.Value.Length == 0) {
            return InputCompositeKind.None;
        }

        switch (scalar.Value.ToLowerInvariant()) {
            case "axis1d":
            case "axis": return InputCompositeKind.Axis1D;
            case "vector2": return InputCompositeKind.Vector2;
            case "buttonwithmodifiers":
            case "shortcut": return InputCompositeKind.ButtonWithModifiers;
            default:
                diagnostics.Add(
                    new(
                        $"'{scalar.Value}' is not a composite. It is 'axis1D', 'vector2' or 'buttonWithModifiers'.",
                        scalar.Line,
                        1
                    )
                );

                return InputCompositeKind.None;
        }
    }

    static List<InputBindingPartData> ReadParts(
        VxInputMapping binding,
        string actionName,
        InputCompositeKind composite,
        List<InputAssetDiagnostic> diagnostics
    ) {
        var parts = new List<InputBindingPartData>();

        if (!binding.TryGet("parts", out var node)) {
            diagnostics.Add(new($"A composite binding of '{actionName}' has no 'parts'.", binding.Line, 1));
            return parts;
        }

        foreach (var item in Sequence(node, "parts", diagnostics)) {
            if (item is not VxInputMapping entry) {
                diagnostics.Add(new("A composite part is a mapping with a 'part' and a 'path'.", item.Line, 1));
                continue;
            }

            var part = Text(entry, "part");
            var path = Text(entry, "path");

            if (string.IsNullOrEmpty(part) || string.IsNullOrEmpty(path)) {
                diagnostics.Add(new("A composite part needs both a 'part' and a 'path'.", entry.Line, 1));
                continue;
            }

            if (!IsKnownPart(composite, part!)) {
                diagnostics.Add(
                    new(
                        $"'{part}' is not a part of a {composite.ToString().ToLowerInvariant()} composite. "
                        + $"It takes {PartsOf(composite)}.",
                        entry.Line,
                        1
                    )
                );

                continue;
            }

            parts.Add(new(part!.ToLowerInvariant(), path!, Text(entry, "processors")));
        }

        foreach (var required in RequiredParts(composite)) {
            if (!parts.Exists(existing => NameEquals(existing.Part, required))) {
                diagnostics.Add(
                    new($"A composite binding of '{actionName}' has no '{required}' part.", binding.Line, 1)
                );
            }
        }

        return parts;
    }

    static List<InputControlSchemeData> ReadSchemes(VxInputMapping root, List<InputAssetDiagnostic> diagnostics) {
        var schemes = new List<InputControlSchemeData>();

        if (!root.TryGet("controlSchemes", out var node)) {
            // Optional. An asset with no schemes has one implicit scheme that everything belongs to,
            // which is what a single-player keyboard game wants and should not have to write down.
            return schemes;
        }

        foreach (var item in Sequence(node, "controlSchemes", diagnostics)) {
            if (item is not VxInputMapping entry) {
                diagnostics.Add(new("A control scheme is a mapping with a 'name' and 'devices'.", item.Line, 1));
                continue;
            }

            var name = Text(entry, "name");

            if (string.IsNullOrEmpty(name)) {
                diagnostics.Add(new("This control scheme has no 'name'.", entry.Line, 1));
                continue;
            }

            if (schemes.Exists(existing => NameEquals(existing.Name, name!))) {
                diagnostics.Add(new($"There is already a control scheme called '{name}'.", entry.Line, 1));
                continue;
            }

            schemes.Add(new(name!, ReadRequirements(entry, name!, diagnostics)));
        }

        return schemes;
    }

    static List<InputDeviceRequirementData> ReadRequirements(
        VxInputMapping scheme,
        string schemeName,
        List<InputAssetDiagnostic> diagnostics
    ) {
        var requirements = new List<InputDeviceRequirementData>();

        if (!scheme.TryGet("devices", out var node)) {
            diagnostics.Add(
                new($"The control scheme '{schemeName}' names no 'devices', so nothing can activate it.", scheme.Line, 1)
            );

            return requirements;
        }

        foreach (var item in Sequence(node, "devices", diagnostics)) {
            switch (item) {
                case VxInputScalar scalar when TryReadDevice(scalar.Value, out var only):
                    requirements.Add(new(only));
                    break;

                case VxInputScalar scalar:
                    diagnostics.Add(new(UnknownDevice(scalar.Value), scalar.Line, 1));
                    break;

                case VxInputMapping entry: {
                    var device = Text(entry, "device");

                    if (string.IsNullOrEmpty(device) || !TryReadDevice(device!, out var kind)) {
                        diagnostics.Add(new(UnknownDevice(device ?? string.Empty), entry.Line, 1));
                        break;
                    }

                    requirements.Add(new(kind, Flag(entry, "optional")));
                    break;
                }

                default:
                    diagnostics.Add(new("A device requirement is a name or a mapping.", item.Line, 1));
                    break;
            }
        }

        return requirements;
    }

    static IEnumerable<VxInputNode> Sequence(VxInputNode node, string key, List<InputAssetDiagnostic> diagnostics) {
        if (node is VxInputSequence sequence) {
            return sequence.Items;
        }

        // An empty value is what a key with nothing under it reads as, and an editor writes exactly
        // that for a map whose actions have all been deleted. Not news.
        if (node is VxInputScalar { Value.Length: 0 }) {
            return [];
        }

        diagnostics.Add(new($"'{key}' is a list.", node.Line, 1));
        return [];
    }

    static bool TryReadDevice(string text, out InputDeviceKind kind) {
        switch (text.ToLowerInvariant()) {
            case "keyboard": kind = InputDeviceKind.Keyboard; return true;
            case "mouse": kind = InputDeviceKind.Mouse; return true;
            case "gamepad": kind = InputDeviceKind.Gamepad; return true;
            case "touch":
            case "touchscreen": kind = InputDeviceKind.Touch; return true;
            default: kind = InputDeviceKind.None; return false;
        }
    }

    static string UnknownDevice(string text) =>
        $"'{text}' is not a device. It is 'keyboard', 'mouse', 'gamepad' or 'touch'.";

    static bool IsKnownPart(InputCompositeKind composite, string part) {
        switch (composite) {
            case InputCompositeKind.Axis1D:
                return NameEquals(part, "negative") || NameEquals(part, "positive");

            case InputCompositeKind.Vector2:
                return NameEquals(part, "up")
                    || NameEquals(part, "down")
                    || NameEquals(part, "left")
                    || NameEquals(part, "right");

            case InputCompositeKind.ButtonWithModifiers:
                return NameEquals(part, "button") || NameEquals(part, "modifier");

            default:
                return false;
        }
    }

    static string PartsOf(InputCompositeKind composite) => composite switch {
        InputCompositeKind.Axis1D => "'negative' and 'positive'",
        InputCompositeKind.Vector2 => "'up', 'down', 'left' and 'right'",
        InputCompositeKind.ButtonWithModifiers => "'button' and one or more 'modifier'",
        _ => "nothing"
    };

    static IReadOnlyList<string> RequiredParts(InputCompositeKind composite) => composite switch {
        InputCompositeKind.Axis1D => ["negative", "positive"],
        InputCompositeKind.Vector2 => ["up", "down", "left", "right"],
        InputCompositeKind.ButtonWithModifiers => ["button", "modifier"],
        _ => []
    };

    static string? Text(VxInputMapping mapping, string key) =>
        mapping.TryGet(key, out var node) && node is VxInputScalar { Value.Length: > 0 } scalar ? scalar.Value : null;

    static bool Flag(VxInputMapping mapping, string key) =>
        mapping.TryGet(key, out var node)
        && node is VxInputScalar scalar
        && (NameEquals(scalar.Value, "true") || NameEquals(scalar.Value, "yes"));

    static bool NameEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
