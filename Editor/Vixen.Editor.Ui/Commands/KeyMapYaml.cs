// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>The YAML a keymap and a preset are both written in.</summary>
/// <remarks>
///     <para>
///         <b>One reader and one writer for the two files, because they are one format.</b> A preset
///         is exactly what a user's keymap is minus the preset name — which is what lets somebody
///         export their bindings and hand them to a colleague as a preset — so a second copy would
///         be a second thing to keep in step with a format whose whole point is that there is one.
///     </para>
///     <para>
///         ⚠ <b>This is where <see cref="KeyMap" /> stopped, and the stopping place is the layering
///         (#650).</b> The keymap's mechanism — three layers, conflicts, contexts, reservation — is
///         what any application with an accelerator has, and it is <c>Vixen.Ui.Controls</c>'s now.
///         Where the bindings are kept between runs is not: this file reaches
///         <c>Vixen.Core.Yaml</c>, which reaches YamlDotNet, and a control library that did the same
///         would put a YAML parser in the dependency closure of every application that has a button.
///         A game that keeps its settings in JSON writes the other half of
///         <see cref="KeyMap.Overrides" /> and <see cref="KeyMap.Restore" /> and owes this assembly
///         nothing.
///     </para>
///     <para>
///         Only what the user did is written: the preset's name, and the bindings they moved
///         themselves — including a command deliberately unbound, which is an empty chord rather
///         than an omission, because omitting it would mean "use the layer underneath" and the user
///         said the opposite.
///     </para>
/// </remarks>
public static class KeyMapYaml {
    /// <summary>The user's overrides and their chosen preset, as YAML.</summary>
    /// <param name="keys">The keymap.</param>
    /// <returns>The text.</returns>
    public static string Write(KeyMap keys) {
        ArgumentNullException.ThrowIfNull(keys);

        var document = new YamlMapping();

        if (keys.Preset is { } preset) {
            document.Set("preset", new YamlScalar(preset.Name, YamlScalarStyle.DoubleQuoted));
        }

        return YamlWriter.Write(document.Set("bindings", Bindings(keys.Overrides)));
    }

    /// <summary>Applies a user keymap over the defaults.</summary>
    /// <param name="keys">The keymap.</param>
    /// <param name="yaml">What <see cref="Write(KeyMap)" /> wrote.</param>
    /// <remarks>
    ///     ⚠ <b>Never throws on a keymap that has gone stale.</b> A chord that will not parse is
    ///     dropped — the alternative is an editor that will not start because somebody mistyped a
    ///     line in a preferences file, and the binding they lose is the one they can see is missing.
    ///     A <c>preset:</c> naming something <see cref="KeyMap.PresetSource" /> does not know is
    ///     dropped on the same terms, which is what happens to a team preset on a machine that has
    ///     not got it.
    /// </remarks>
    public static void Read(KeyMap keys, string yaml) {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(yaml);

        if (YamlReader.Read(yaml) is not YamlMapping document) {
            keys.Restore(null, []);

            return;
        }

        var overrides = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
        Bindings(document["bindings"], overrides);

        keys.Restore((document["preset"] as YamlScalar)?.Value, overrides);
    }

    /// <summary>Writes a preset back out in the format <see cref="ReadPreset" /> reads.</summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The text.</returns>
    public static string Write(KeyMapPreset preset) {
        ArgumentNullException.ThrowIfNull(preset);

        return YamlWriter.Write(new YamlMapping().Set("bindings", Bindings(preset.Bindings)));
    }

    /// <summary>Reads a preset from the same YAML a keymap is written in.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="yaml">The text.</param>
    /// <returns>The preset, which is empty if the text names nothing.</returns>
    /// <remarks>
    ///     ⚠ <b>Never throws on a preset that has gone stale</b>, for <see cref="Read" />'s reason: a
    ///     chord that will not parse is dropped rather than taking the editor down. A preset shipped
    ///     in this assembly is asserted to be clean by a test, so a bad line here is a third party's
    ///     file rather than one of ours.
    /// </remarks>
    public static KeyMapPreset ReadPreset(string name, string yaml) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(yaml);

        var bindings = new Dictionary<string, KeyChord>(StringComparer.Ordinal);

        if (YamlReader.Read(yaml) is YamlMapping document) {
            Bindings(document["bindings"], bindings);
        }

        return KeyMapPreset.Of(name, bindings);
    }

    /// <summary>The <c>bindings:</c> mapping a preset file and a user's keymap both carry.</summary>
    /// <param name="bindings">What to write.</param>
    /// <returns>The mapping.</returns>
    static YamlMapping Bindings(IReadOnlyDictionary<string, KeyChord> bindings) {
        var entries = new YamlMapping();

        foreach (var commandId in bindings.Keys.Order(StringComparer.Ordinal)) {
            entries.Set(commandId, new YamlScalar(bindings[commandId].Save(), YamlScalarStyle.DoubleQuoted));
        }

        return entries;
    }

    /// <inheritdoc cref="Bindings(System.Collections.Generic.IReadOnlyDictionary{string,Vixen.Ui.KeyChord})" />
    /// <param name="node">The mapping, or anything else for none.</param>
    /// <param name="into">Where the bindings go.</param>
    static void Bindings(YamlNode? node, Dictionary<string, KeyChord> into) {
        if (node is not YamlMapping entries) {
            return;
        }

        foreach (var (commandId, value) in entries) {
            if (value is not YamlScalar scalar) {
                continue;
            }

            // An empty value is a command deliberately unbound, which is how a preset takes a key
            // away without giving it to something else — and how a user says "not this one".
            if (string.IsNullOrEmpty(scalar.Value)) {
                into[commandId] = KeyChord.None;
            } else if (KeyChord.TryParse(scalar.Value, out var chord)) {
                into[commandId] = chord;
            }
        }
    }
}
