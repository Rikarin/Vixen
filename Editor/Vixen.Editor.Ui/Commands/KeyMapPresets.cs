// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

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
    /// <remarks>
    ///     <see cref="KeyMap.NoPreset" />'s value, named again here so the dropdown's list and the
    ///     keymap file agree with the layer that resolves the name.
    /// </remarks>
    public const string Vixen = KeyMap.NoPreset;

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
    static readonly KeyMapPreset UnityPreset = KeyMapYaml.ReadPreset(Unity, UnitySource);

    /// <inheritdoc cref="UnityPreset" />
    static readonly KeyMapPreset UnrealPreset = KeyMapYaml.ReadPreset(Unreal, UnrealSource);

    /// <summary>
    ///     Unity's, for the commands this editor has.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>view.palette</c> is listed even though it matches the default.</b> The palette is
    ///     <c>Ctrl+K</c> everywhere now — the chord every editor with a palette uses, and the one this
    ///     editor's own default settled on — but Play is <c>Ctrl+P</c> in this preset, so writing the
    ///     palette's chord here is what says the two do not collide. A preset that took Play's key
    ///     while leaving the palette's implicit would be one nobody could check by reading.
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
