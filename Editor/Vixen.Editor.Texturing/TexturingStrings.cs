// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Texturing;

/// <summary>Every word the texturing toolset shows, declared once.</summary>
/// <remarks>
///     <para>
///         <b>A family rather than ten properties, because the ids were computed.</b> Every one of
///         this module's commands built its label at the call site —
///         <c>new StringId("editor.command." + OpenCommand, "Open Texture Graph")</c> — which reads
///         as a declaration and is not one: <c>Strings.Template</c> exports <c>All</c> lists, an id
///         assembled from a constant is in no <c>All</c> list, and so none of these ten words was
///         ever in a translator's template.
///     </para>
///     <para>
///         ⚠ <b>The prefix is the only part that was ever written out</b>, and the key is the
///         command id the call site already holds, so a registration reads
///         <c>TexturingStrings.Commands[TexturingModule.OpenCommand]</c> where it used to build one.
///         A key the family does not carry throws while the module registers, which is deterministic
///         and covered by the module's own activation.
///     </para>
/// </remarks>
public static class TexturingStrings {
    /// <summary>Every command the module registers, by its command id.</summary>
    /// <remarks>
    ///     The prefix is the convention the call sites were spelling out: a command's label is
    ///     <c>editor.command.</c> followed by the command's own id.
    /// </remarks>
    public static StringFamily Commands { get; } = new(
        "editor.command.",
        [
            new(TexturingModule.OpenCommand, "Open Texture Graph"),
            new(TexturingModule.OpenStackCommand, "Open Layer Stack"),
            new(TexturingModule.PaintCommand, "Paint on Layer"),
            new(TexturingModule.BakeCommand, "Bake Material"),
            new(TexturingModule.BakeStackCommand, "Bake Material from Layers"),
            new(TexturingModule.BakeSplatCommand, "Bake Splat Map from Layers"),
            new(TexturingModule.ForceBakeCommand, "Bake Material (Force)"),
            new(TexturingModule.ParallaxBakeCommand, "Bake Material with Parallax"),
            new(TexturingModule.SaveSmartCommand, "Save as Smart Material"),
            new(TexturingModule.ApplySmartCommand, "Apply Smart Material")
        ]
    );

    // ── The names the shell used to keep a copy of ─────────────────────────
    //
    // ⚠ Declared in EditorStrings until #1301, one assembly up from the only code that reads them.
    // The shell cannot name this class (StringContributions.cs says why), so the ids it held for
    // this toolset were a copy the toolset could not own — a translator saw them under the
    // editor's own words, and a toolset shipped out of tree would have had no way to add its own.
    // The ids themselves are unchanged, so a catalogue written against the old table still finds
    // every one of them.

    /// <summary>The <c>Texture Graph</c> panel.</summary>
    public static StringId PanelTextureGraph { get; } = new("editor.panel.texture-graph", "Texture Graph");

    /// <summary>The <c>Layer Stack</c> panel.</summary>
    public static StringId PanelLayerStack { get; } = new("editor.panel.layer-stack", "Layer Stack");

    /// <summary>The <c>Paint</c> panel.</summary>
    public static StringId PanelTexturePaint { get; } = new("editor.panel.texture-paint", "Paint");

    /// <summary>The <c>Paint (3D)</c> panel.</summary>
    public static StringId PanelTexturePaint3d { get; } = new("editor.panel.texture-paint-3d", "Paint (3D)");

    /// <summary>What a translator's template for this toolset holds.</summary>
    /// <remarks>
    ///     Spread from the family, which is what puts every member of it in the template. A family
    ///     left out of this list would hide ten strings rather than one, which is why <c>VXS0310</c>
    ///     counts a <see cref="StringFamily" /> property as a declaration.
    /// </remarks>
    public static IReadOnlyList<StringId> All { get; } = [
        .. Commands.All,
        PanelTextureGraph,
        PanelLayerStack,
        PanelTexturePaint,
        PanelTexturePaint3d
    ];
}
