// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Input;

/// <summary>Writes an <see cref="InputActionAssetData" /> back out as a <c>.vxinput</c>.</summary>
/// <remarks>
///     <para>
///         The dialect [doc 08](../../../docs/plan/08-asset-pipeline-and-addressables.md) settles on:
///         two-space indent, block style, no document-start marker, keys in the order the record
///         declares them, <c>\n</c> line endings and a trailing newline.
///     </para>
///     <para>
///         <b>A key whose value is the default is left out.</b> This is the one place the engine's
///         YAML omits defaults, and it is because the file is read by people: an action list where
///         every entry carries <c>controlType: button</c> and <c>groups:</c> buries the two lines
///         that differ. What is omitted is recoverable — <see cref="InputActionAssetReader" /> fills
///         the same defaults in — so a write-read round trip is the identity, which is what the
///         round-trip test asserts.
///     </para>
/// </remarks>
public static class InputActionAssetWriter {
    /// <summary>Writes a document.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns>The document text, ending in a newline.</returns>
    public static string Write(InputActionAssetData asset) {
        ArgumentNullException.ThrowIfNull(asset);

        var builder = new StringBuilder(1024);

        Field(builder, 0, "name", asset.Name);
        Field(builder, 0, "version", asset.Version.ToString(CultureInfo.InvariantCulture));

        builder.Append("maps:\n");

        foreach (var map in asset.Maps) {
            Item(builder, 1, "name", map.Name);
            Line(builder, 2, "actions:");

            foreach (var action in map.Actions) {
                WriteAction(builder, action);
            }
        }

        if (asset.ControlSchemes.Count == 0) {
            return builder.ToString();
        }

        builder.Append("controlSchemes:\n");

        foreach (var scheme in asset.ControlSchemes) {
            Item(builder, 1, "name", scheme.Name);
            Line(builder, 2, "devices:");

            foreach (var requirement in scheme.Devices) {
                Item(builder, 3, "device", Name(requirement.Device));

                if (requirement.Optional) {
                    Field(builder, 4, "optional", "true");
                }
            }
        }

        return builder.ToString();
    }

    static void WriteAction(StringBuilder builder, InputActionData action) {
        Item(builder, 3, "name", action.Name);

        if (action.Type != InputActionType.Button) {
            Field(builder, 4, "type", Name(action.Type));
        }

        var implied = action.Type == InputActionType.Button ? InputControlType.Button : InputControlType.Axis;

        if (action.ControlType != implied) {
            Field(builder, 4, "controlType", Name(action.ControlType));
        }

        if (action.Bindings.Count == 0) {
            return;
        }

        Line(builder, 4, "bindings:");

        foreach (var binding in action.Bindings) {
            WriteBinding(builder, binding);
        }
    }

    static void WriteBinding(StringBuilder builder, InputBindingData binding) {
        if (binding.Composite == InputCompositeKind.None) {
            Item(builder, 5, "path", binding.Path);
        } else {
            Item(builder, 5, "composite", Name(binding.Composite));
        }

        Optional(builder, 6, "name", binding.Name);
        Optional(builder, 6, "groups", binding.Groups);
        Optional(builder, 6, "interactions", binding.Interactions);
        Optional(builder, 6, "processors", binding.Processors);

        if (binding.Parts.Count == 0) {
            return;
        }

        Line(builder, 6, "parts:");

        foreach (var part in binding.Parts) {
            Item(builder, 7, "part", part.Part);
            Field(builder, 8, "path", part.Path);
            Optional(builder, 8, "processors", part.Processors);
        }
    }

    static void Optional(StringBuilder builder, int depth, string key, string? value) {
        if (!string.IsNullOrEmpty(value)) {
            Field(builder, depth, key, value!);
        }
    }

    static void Field(StringBuilder builder, int depth, string key, string value) {
        Indent(builder, depth);
        builder.Append(key).Append(": ").Append(Scalar(value)).Append('\n');
    }

    /// <summary>Writes the first field of a sequence item, which carries the dash.</summary>
    static void Item(StringBuilder builder, int depth, string key, string value) {
        Indent(builder, depth - 1);
        builder.Append("  - ").Append(key).Append(": ").Append(Scalar(value)).Append('\n');
    }

    static void Line(StringBuilder builder, int depth, string text) {
        Indent(builder, depth);
        builder.Append(text).Append('\n');
    }

    static void Indent(StringBuilder builder, int depth) => builder.Append(' ', depth * 2);

    /// <summary>Quotes a value when writing it bare would not read back as itself.</summary>
    /// <remarks>
    ///     The two questions <c>Vixen.Core.Yaml</c> asks, minus the second: every value here is a
    ///     name, a path or a keyword and is read back as a string whatever it looks like, so only the
    ///     first — would this break the document — has to be answered.
    /// </remarks>
    static string Scalar(string value) {
        if (value.Length == 0) {
            return "''";
        }

        var needsQuotes = value[0] is ' ' or '-' or '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '[' or '{'
            || value[value.Length - 1] == ' '
            || value.Contains('\n')
            || value.Contains('\t')
            || value.Contains(": ", StringComparison.Ordinal)
            || value.Contains(" #", StringComparison.Ordinal)
            || value[value.Length - 1] == ':';

        if (!needsQuotes) {
            return value;
        }

        var builder = new StringBuilder(value.Length + 4);
        builder.Append('\'');

        foreach (var character in value) {
            if (character == '\'') {
                builder.Append('\'');
            }

            builder.Append(character);
        }

        return builder.Append('\'').ToString();
    }

    static string Name(InputActionType type) => type switch {
        InputActionType.Value => "value",
        InputActionType.PassThrough => "passThrough",
        _ => "button"
    };

    static string Name(InputControlType type) => type switch {
        InputControlType.Axis => "axis",
        InputControlType.Vector2 => "vector2",
        _ => "button"
    };

    static string Name(InputCompositeKind composite) => composite switch {
        InputCompositeKind.Axis1D => "axis1D",
        InputCompositeKind.Vector2 => "vector2",
        InputCompositeKind.ButtonWithModifiers => "buttonWithModifiers",
        _ => "none"
    };

    static string Name(InputDeviceKind device) => device switch {
        InputDeviceKind.Keyboard => "keyboard",
        InputDeviceKind.Mouse => "mouse",
        InputDeviceKind.Gamepad => "gamepad",
        InputDeviceKind.Touch => "touch",
        _ => "none"
    };
}
