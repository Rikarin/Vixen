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

    /// <summary>What a translator's template for this toolset holds.</summary>
    /// <remarks>
    ///     Spread from the family, which is what puts every member of it in the template. A family
    ///     left out of this list would hide ten strings rather than one, which is why <c>VXS0310</c>
    ///     counts a <see cref="StringFamily" /> property as a declaration.
    /// </remarks>
    public static IReadOnlyList<StringId> All { get; } = [.. Commands.All];
}
