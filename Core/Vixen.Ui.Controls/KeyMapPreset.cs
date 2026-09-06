// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Ui.Controls;

/// <summary>A named set of bindings that sits between the shipped defaults and the user's own.</summary>
/// <remarks>
///     <para>
///         <b>The third layer, and doc 20's A5 is precise about why it has to be one.</b> A preset
///         applied by <i>copying</i> two hundred bindings into the user's file is a preset that stops
///         being a preset the moment it is chosen: the next release moves a default and reaches
///         nobody who ever picked Unreal, because their file now states every binding explicitly.
///         Choosing a preset and then rebinding one key has to leave the other two hundred following
///         the preset, which is only possible if the preset is a layer rather than an edit.
///     </para>
///     <para>
///         ⚠ <b>A preset maps the commands that exist.</b> Doc 20's own risk table says this must be
///         documented as "the bindings you know for the features we have" rather than as an emulation
///         mode — a preset cannot bind Unreal's Landscape tools, because there are none. What it can
///         do is put Play, Duplicate, Save All and the transform modes where the fingers of somebody
///         who has used that editor for a decade already are.
///     </para>
///     <para>
///         <b>The file format is a <see cref="KeyMap" />'s.</b> A preset is a <c>bindings:</c>
///         mapping of command id to chord, which is exactly what a user's keymap is — so a team can
///         ship their own by writing one file, and a user can export their bindings and hand them to
///         somebody as a preset.
///     </para>
///     <para>
///         ⚠ <b>The format is not here, and that is what let this class come down out of the
///         editor.</b> A preset is a named table of chords; writing one as YAML is a choice about
///         where an application keeps its preferences, and a control library that made it would put
///         a YAML parser in the dependency closure of every application that has a button.
///         <c>Vixen.Editor.Ui.KeyMapYaml</c> is the editor's answer, and a game with a settings
///         screen of its own can give a different one over the same <see cref="Of" />.
///     </para>
/// </remarks>
public sealed class KeyMapPreset {
    readonly Dictionary<string, KeyChord> bindings;

    KeyMapPreset(string name, Dictionary<string, KeyChord> bindings) {
        Name = name;
        this.bindings = bindings;
    }

    /// <summary>What it is called, which is what a keymap file records.</summary>
    public string Name { get; }

    /// <summary>What it binds, as command id to chord.</summary>
    /// <remarks>
    ///     ⚠ <b>A command the preset does not mention is not unbound, it is left to the default.</b>
    ///     That is the difference between a layer and a replacement, and it is what makes a
    ///     twenty-line preset a usable one: only the keys the other editor puts somewhere else need
    ///     saying.
    /// </remarks>
    public IReadOnlyDictionary<string, KeyChord> Bindings => bindings;

    /// <summary>Builds one from bindings already in hand.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="bindings">What it binds.</param>
    /// <returns>The preset.</returns>
    /// <remarks>What exporting the user's own map as a preset produces.</remarks>
    public static KeyMapPreset Of(string name, IEnumerable<KeyValuePair<string, KeyChord>> bindings) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bindings);

        return new KeyMapPreset(name, new Dictionary<string, KeyChord>(bindings, StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
