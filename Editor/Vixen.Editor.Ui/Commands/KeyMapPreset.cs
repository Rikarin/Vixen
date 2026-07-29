// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;

namespace Vixen.Editor.Ui;

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
///         <b>The file format is <see cref="KeyMap" />'s.</b> A preset is a <c>bindings:</c> mapping
///         of command id to chord, which is exactly what a user's keymap is — so a team can ship
///         their own by writing one file, and a user can export their bindings and hand them to
///         somebody as a preset.
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

    /// <summary>Reads a preset from the same YAML a keymap is written in.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="yaml">The text.</param>
    /// <returns>The preset, which is empty if the text names nothing.</returns>
    /// <remarks>
    ///     ⚠ <b>Never throws on a preset that has gone stale</b>, for <see cref="KeyMap.Load" />'s
    ///     reason: a chord that will not parse is dropped rather than taking the editor down. A
    ///     preset shipped in this assembly is asserted to be clean by a test, so a bad line here is
    ///     a third party's file rather than one of ours.
    /// </remarks>
    public static KeyMapPreset Parse(string name, string yaml) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(yaml);

        var bindings = new Dictionary<string, KeyChord>(StringComparer.Ordinal);

        if (YamlReader.Read(yaml) is YamlMapping document) {
            Read(document["bindings"], bindings);
        }

        return new KeyMapPreset(name, bindings);
    }

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

    /// <summary>Writes it back out in the format <see cref="Parse" /> reads.</summary>
    /// <returns>The text.</returns>
    public string Save() => YamlWriter.Write(new YamlMapping().Set("bindings", Write(bindings)));

    /// <summary>The <c>bindings:</c> mapping a preset file and a user's keymap both carry.</summary>
    /// <remarks>
    ///     ⚠ <b>One reader and one writer for the two files, because they are one format.</b> A
    ///     preset is exactly what a user's keymap is minus the preset name — which is what lets
    ///     somebody export their bindings and hand them to a colleague as a preset — so a second
    ///     copy in <see cref="KeyMap" /> would be a second thing to keep in step.
    /// </remarks>
    internal static YamlMapping Write(IReadOnlyDictionary<string, KeyChord> bindings) {
        var entries = new YamlMapping();

        foreach (var commandId in bindings.Keys.Order(StringComparer.Ordinal)) {
            entries.Set(commandId, new YamlScalar(bindings[commandId].Save(), YamlScalarStyle.DoubleQuoted));
        }

        return entries;
    }

    /// <inheritdoc cref="Write" />
    /// <param name="node">The mapping, or anything else for none.</param>
    /// <param name="into">Where the bindings go.</param>
    internal static void Read(YamlNode? node, Dictionary<string, KeyChord> into) {
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

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>The keymap presets the editor ships: its own, Unity's and Unreal's.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 20: "A Unity user and an Unreal user disagree about what <c>W</c> does and both
///         are certain."</b> They happen to agree about <c>W</c> — both put Translate there — and
///         disagree about almost everything around it: Play is <c>Ctrl+P</c> in one and <c>Alt+P</c>
///         in the other, Duplicate is <c>Ctrl+D</c> in one and <c>Ctrl+W</c> in the other, and Save
///         All is under two different modifiers. Shipping the three converts a week of friction into
///         a dropdown.
///     </para>
///     <para>
///         ⚠ <b>Vixen's own preset is empty, and that is not a stub.</b> The shipped defaults <i>are</i>
///         the Vixen keymap — they are declared beside the commands, where a default belongs — so a
///         preset that restated them would be a second copy of the same table, and the way that goes
///         wrong is a default moved in one place and not the other. Choosing "Vixen" means "no layer",
///         which is exactly what it should mean.
///     </para>
///     <para>
///         ⚠ <b>A preset only ever moves keys that would otherwise collide, or that the other editor
///         genuinely puts somewhere else.</b> Every entry below either matches what that editor does
///         or vacates a chord the same preset gives to something else — Unity's <c>Ctrl+P</c> is Play,
///         so the palette has to move, and Unreal's <c>Ctrl+W</c> is Duplicate, so Close Panel has to.
///         A preset that moved a key for neither reason would be a preset nobody can predict.
///     </para>
/// </remarks>
public static class KeyMapPresets {
    /// <summary>The editor's own bindings, which is the absence of a preset.</summary>
    public const string Vixen = "Vixen";

    /// <summary>Unity's, as far as the commands that exist allow.</summary>
    public const string Unity = "Unity";

    /// <summary>Unreal's, on the same terms.</summary>
    public const string Unreal = "Unreal";

    /// <summary>Every preset this assembly ships, in the order a dropdown should offer them.</summary>
    public static IReadOnlyList<string> Names { get; } = [Vixen, Unity, Unreal];

    /// <summary>The preset with a name, or <see langword="null" /> if this assembly has none.</summary>
    /// <param name="name">Its name. <see cref="Vixen" /> answers <see langword="null" />.</param>
    /// <returns>The preset, or <c>null</c>.</returns>
    /// <remarks>
    ///     <see cref="Vixen" /> answering <c>null</c> is the same statement the class's remarks make:
    ///     the editor's own keymap is no layer at all, and <see cref="KeyMap.UsePreset" /> takes
    ///     <c>null</c> to mean exactly that.
    /// </remarks>
    public static KeyMapPreset? Find(string? name) =>
        name switch {
            Unity => UnityPreset,
            Unreal => UnrealPreset,
            _ => null
        };

    /// <summary>The two shipped presets, parsed once — they are immutable and are two literals.</summary>
    /// <remarks>
    ///     ⚠ <b>Fields rather than a memoising dictionary.</b> A cache would be a mutable static
    ///     guarding the parse of two compile-time strings, and one that <see cref="Find" /> could be
    ///     called on from any thread. Two readonly fields answer the same question with no
    ///     bookkeeping and no threading question at all.
    /// </remarks>
    static readonly KeyMapPreset UnityPreset = KeyMapPreset.Parse(Unity, UnitySource);

    /// <inheritdoc cref="UnityPreset" />
    static readonly KeyMapPreset UnrealPreset = KeyMapPreset.Parse(Unreal, UnrealSource);

    /// <summary>
    ///     Unity's, for the commands this editor has.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>Ctrl+P</c> is the whole reason the palette moves.</b> It is Play in Unity and the
    ///     command palette here, and a preset that took Play's key without saying where the palette
    ///     went would leave a user of this preset with no palette at all. <c>Ctrl+K</c> is Unity's own
    ///     search, so the two land where that editor's users already reach.
    /// </remarks>
    const string UnitySource = """
        bindings:
          view.palette: "Ctrl+K"
          play.play: "Ctrl+P"
          play.pause: "Ctrl+Shift+P"
          play.step: "Ctrl+Alt+P"
          edit.duplicate: "Ctrl+D"
          scene.create-entity: "Ctrl+Shift+N"
          entity.create-child: "Alt+Shift+N"
          assets.build: "Ctrl+B"
          assets.refresh: "Ctrl+R"
          scene.translate: "W"
          scene.rotate: "E"
          scene.scale: "R"
          scene.focus: "F"
        """;

    /// <summary>
    ///     Unreal's, on the same terms.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Two chords swap and one is vacated.</b> Unreal's Save All is <c>Ctrl+Shift+S</c>,
    ///     which is Save As here, so the pair exchange; and Unreal's Duplicate is <c>Ctrl+W</c>,
    ///     which is Close Panel here, so Close Panel goes to <c>Ctrl+F4</c> — the other chord every
    ///     tabbed application on Windows answers to.
    /// </remarks>
    const string UnrealSource = """
        bindings:
          file.save-all: "Ctrl+Shift+S"
          file.save-as: "Ctrl+Alt+S"
          edit.duplicate: "Ctrl+W"
          view.close-panel: "Ctrl+F4"
          play.play: "Alt+P"
          play.stop: "Escape"
          play.step: "F10"
          entity.group: "Ctrl+G"
          scene.translate: "W"
          scene.rotate: "E"
          scene.scale: "R"
          scene.focus: "F"
          scene.frame-all: "Home"
          scene.view-front: "Alt+H"
          scene.view-top: "Alt+J"
          scene.view-left: "Alt+K"
        """;
}
