// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>What a map is for, and therefore what a bake writes it as.</summary>
/// <remarks>
///     Doc 48 § 4.8's list, exactly. The name rather than a number, because it is what a
///     <c>.vxmat</c>'s slot is called and what an artist reads on the node.
/// </remarks>
static class TextureUsages {
    /// <summary>The nine an <c>Output</c> node may name.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read off the node's own declaration rather than written here —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1013">#1013</a>.</b> The nine were a
    ///         literal in this class and the setting that names one of them stated nothing, so the
    ///         single most-edited setting in the library — every graph has an <c>Output</c> — drew
    ///         as a text box, and the list only existed where the refusal could reach it. Moving it
    ///         onto <c>[Setting(Accepted = …)]</c> is the same trade
    ///         <c>TextureMeshMaps.Known</c> already makes: one list, read by the refusal below, by
    ///         the node inspector's dropdown, and by whatever a plugin draws.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A literal and not an <c>AcceptedFrom</c>, because these are not an enum's
    ///         names.</b> A usage is what a <c>.vxmat</c>'s slot is called — <c>baseColor</c>, in
    ///         that spelling — and the enum that mirrored it would be a second set of names with a
    ///         casing rule between them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It throws rather than answering empty when the setting cannot be found</b>, for
    ///         <see cref="TextureMeshMaps.Known" />'s reason: a renamed setting would otherwise make
    ///         <see cref="Canonical" /> refuse all nine, which is every graph in the project failing
    ///         to compile with a message about the author's spelling.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Known { get; } =
        OutputNode.Definition.Setting(OutputNode.Setting) is { Accepted.Length: > 0 } declared
            ? declared.Accepted
            : throw new InvalidOperationException(
                $"'{OutputNode.Setting}' declares no accepted values, so nothing knows what an output "
                + "may be for. The list lives on the node's [Setting] attribute."
            );

    /// <summary>The canonical spelling of a usage, or empty when it is not one of the nine.</summary>
    /// <param name="usage">What the author typed.</param>
    /// <returns>The spelling <see cref="Known" /> holds, or an empty string.</returns>
    public static string Canonical(string usage) {
        foreach (var known in Known) {
            if (string.Equals(known, usage, StringComparison.OrdinalIgnoreCase)) {
                return known;
            }
        }

        return "";
    }
}

/// <summary>One map the graph produces.</summary>
/// <remarks>
///     <para>
///         <b>The terminal node, and the only one that keeps anything.</b> An image nothing names is
///         freed the moment its last reader has run — that is what makes the pool cheap — so a graph
///         with no <c>Output</c> computes nothing anybody can look at, and
///         <c>TextureGraphCompiler</c> says so rather than producing a plan that evaluates to
///         nothing.
///     </para>
///     <para>
///         ⚠ <b>The usage is where the compiler's artefact and the plan part company.</b>
///         <c>TexturePlan.Outputs</c> is a list of indices with no names on it, so which of them is
///         the roughness map is carried by <c>TextureGraphCompiler.Outputs</c> instead — see its
///         remarks, and <a href="https://github.com/Rikarin/Vixen/issues/718">#718</a>.
///     </para>
/// </remarks>
[Node("Output/Output", Summary = "One map the graph produces, under a usage a bake writes it by.")]
sealed partial class OutputNode : TextureNode {
    /// <summary>What the setting is called, which is what reads it back off the definition.</summary>
    /// <remarks>
    ///     A constant rather than <c>nameof(Usage)</c>, for <c>MeshMapInputNode.Setting</c>'s reason:
    ///     a setting's name is the key a saved graph stores and a field's name is a C# identifier,
    ///     and <see cref="SettingAttribute.Name" /> exists so one can be renamed without orphaning
    ///     the other.
    /// </remarks>
    public const string Setting = "Usage";

    /// <summary>Which map this is: one of <c>TextureUsages.Known</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The nine live here and nowhere else — #1013.</b> They used to be a literal in
    ///     <see cref="TextureUsages" />, which the refusal below could read and no picker could, so
    ///     the setting every graph in a project sets drew as a free text box.
    /// </remarks>
    [Setting(
        Name = Setting,
        Accepted = ["baseColor", "normal", "roughness", "metalness", "occlusion", "height", "emissive", "opacity", "mask"]
    )]
    public string Usage = "baseColor";

    /// <summary>The image to keep.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var image = emitter.Read("Input");
        var typed = emitter.Text(nameof(Usage)).Trim();
        var usage = TextureUsages.Canonical(typed);

        if (usage.Length == 0) {
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                $"'{nameof(Usage)}' is '{typed}', which is not one of {string.Join(", ", TextureUsages.Known)}.",
                nameof(Usage)
            );

            return;
        }

        emitter.Keep(image, usage);
    }
}
