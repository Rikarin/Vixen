// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>Doc 48 § M8's selection kernels, by the name a <see cref="TextureOp" /> gives.</summary>
/// <remarks>
///     ⚠ <b>A surface of its own rather than a line in <c>TextureAnalysisKernels</c>, and the reason
///     is the batch rather than the taxonomy.</b> A selection measures a picture and belongs beside
///     § 4.5's three by any reading — but the file those are declared in is another slice's this
///     batch, and <see cref="TextureKernelSurfaceAttribute" /> exists exactly so that a new surface
///     costs nothing: the roll calls read the attribute, so this joins the inventory by existing and
///     no list anywhere gains a line. A later batch that merges the two files loses nothing.
/// </remarks>
[TextureKernelSurface]
internal static class TextureSelectionKernels {
    /// <summary>One colour of an image as a grey mask, matched in colour space. ⚠ Never by index.</summary>
    public const string ColourSelect = "ColourSelect";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [ColourSelect];
}

/// <summary>The ops doc 48 § M8's selection nodes are.</summary>
/// <remarks>
///     <b>A builder rather than an op at the call site</b>, for <see cref="TextureSurfaces" />'s
///     reason: <c>TexturePlanEvaluator.Uniforms</c> refuses an op that leaves out one of the
///     parameters its kernel declares, so writing one out by hand is a chance to produce an exception
///     at bake time and — worse — a chance to name the wrong one and get a plausible picture.
/// </remarks>
[TextureKernelSurface]
internal static class TextureSelections {
    /// <summary>Doc 48 § M8's colour / ID selection mask.</summary>
    /// <param name="output">The mask to write.</param>
    /// <param name="source">The picture to select out of, read nearest.</param>
    /// <param name="red">The colour to match, red channel, linear.</param>
    /// <param name="green">Its green.</param>
    /// <param name="blue">Its blue.</param>
    /// <param name="tolerance">
    ///     How far from that colour still counts, as a Euclidean distance in linear RGB. ⚠ Colour
    ///     space and never an index: the <c>id</c> bake stores <c>MapBaker.IdColour</c>'s hue and the
    ///     index is not in the file, so a <c>±</c> on a decoded number would compare in a space that
    ///     is not there — and the golden-angle palette makes the island one index away the one whose
    ///     colour is furthest.
    /// </param>
    /// <param name="softness">How much further the mask takes to reach zero. ⚠ Zero is a hard step.</param>
    /// <returns>The op.</returns>
    public static TextureOp ColourSelect(
        int output,
        int source,
        float red = 1f,
        float green = 0f,
        float blue = 0f,
        float tolerance = 0.02f,
        float softness = 0f
    ) =>
        new() {
            Kernel = TextureSelectionKernels.ColourSelect,
            Output = output,
            Inputs = [source],
            Parameters = [
                new("red", red),
                new("green", green),
                new("blue", blue),
                // ⚠ Neither of these is a `TexelsAtBase`, and that is the § D8 question asked and
                // answered rather than skipped: both are distances in *colour*, so a bake at four
                // times the size selects exactly the same islands. A length that lived in texels
                // here would be a mask that changed shape with the resolution.
                new("tolerance", tolerance),
                new("softness", softness)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what a test over the builders prints on the day one of them is forgotten.</b> A
    ///     theory with an <c>InlineData</c> per builder passes silently when a second is added and
    ///     not listed; this list is what the parameter-agreement test walks, so a builder reaches it
    ///     by existing.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [ColourSelect(0, 1)];
}
