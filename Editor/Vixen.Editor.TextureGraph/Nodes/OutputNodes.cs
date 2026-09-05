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
    public static IReadOnlyList<string> Known { get; } = [
        "baseColor",
        "normal",
        "roughness",
        "metalness",
        "occlusion",
        "height",
        "emissive",
        "opacity",
        "mask"
    ];

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
    /// <summary>Which map this is: one of <c>TextureUsages.Known</c>.</summary>
    [Setting]
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
                "TG0010",
                $"'{nameof(Usage)}' is '{typed}', which is not one of {string.Join(", ", TextureUsages.Known)}.",
                nameof(Usage)
            );

            return;
        }

        emitter.Keep(image, usage);
    }
}
