// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Graphics;
using Vixen.Terrain;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>Turns one of a project's own imported pictures into a brush alpha.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1090">#1090</a>: the shelf of four
///         was a stand-in for the thing every reference toolset has, which is an artist pointing the
///         brush at a texture in their own project.</b> <c>PaintAlphas</c> renders its shapes into
///         <c>PaintImage</c>s and wraps them in <see cref="PaintImageMask" /> precisely so that this
///         needs no new sampling path — an imported picture decodes to the Rgba8 bytes a
///         <c>PaintImage</c> already is, and the addressing, the bilinear tap and the unit-square
///         contract are the ones the shelf has been exercising.
///     </para>
///     <para>
///         ⚠ <b>Which channel carries the weight is decided by <em>inspecting the decoded image</em>
///         and never by its format, and that is the whole reason this type exists rather than a
///         two-line call at the panel.</b> Brush alphas in the wild are written both ways: some
///         carry the shape in alpha over a black or white RGB, and some are opaque greyscale with no
///         meaningful alpha at all. A picture whose alpha channel is one constant — 255 for a PNG
///         without transparency, 0 for one exported carelessly — read as an alpha mask is a brush
///         that paints a flat square or paints nothing, silently, and an artist has no way to tell
///         which of the two happened. So a constant alpha channel means the weight is in luminance,
///         and the answer is written into a copy's alpha so that the mask below has exactly one
///         contract.
///     </para>
///     <para>
///         ⚠ <b>A picture with no variation in either is refused with a sentence rather than
///         loaded.</b> A wholly transparent or wholly black image is a mask that multiplies every
///         stamp by zero, which is a brush that appears to be working and deposits nothing — the
///         defect this workstream keeps finding, one subsystem along.
///     </para>
///     <para>
///         ⚠ <b>Every failure is a returned sentence and none is an exception</b>, on
///         <c>TextureProjectImages</c>' reasoning: this runs from a panel's text box as the artist
///         types, and a throw out of one takes the editor's frame with it.
///     </para>
/// </remarks>
static class PaintAlphaSource {
    /// <summary>Reads a project asset as a brush alpha.</summary>
    /// <param name="project">Whose assets resolve the reference, and whose index has been scanned.</param>
    /// <param name="reference">The asset's project-relative path, as an artist typed it.</param>
    /// <returns>The mask and a sentence about it, or no mask and the sentence saying why.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>An empty reference is "no imported alpha" rather than a failure</b>, because that is
    ///     how an artist clears the row: the shelf selection is what the brush falls back to, and a
    ///     refusal sentence for an empty box would be an error message for doing nothing.
    /// </remarks>
    public static (IBrushMask? Mask, string Message) Resolve(EditorProject project, string? reference) {
        ArgumentNullException.ThrowIfNull(project);

        var path = reference?.Trim() ?? "";

        if (path.Length == 0) {
            return (null, "");
        }

        if (!project.Assets.TryGetByPath(path, out var asset)) {
            return (null, $"'{path}' is not in this project's assets, so there is nothing to read.");
        }

        var file = project.Paths.Absolute(asset.Path);
        var extension = Path.GetExtension(file);

        if (ImageDecoders.For(ImageDecoders.BuiltIn, extension) is not { } decoder) {
            return (null, $"nothing here decodes '{extension}'.");
        }

        TextureData decoded;

        try {
            using var stream = File.OpenRead(file);

            if (decoder.Decode(stream, extension) is not { } read) {
                return (null, $"'{path}' decoded to nothing.");
            }

            decoded = read;
        } catch (IOException failure) {
            return (null, $"'{path}' would not read: {failure.Message}");
        } catch (NotSupportedException failure) {
            return (null, $"'{path}' would not read: {failure.Message}");
        }

        if (decoded.Format != PixelFormat.Rgba8UNorm) {
            return (null,
                $"'{path}' decoded as {decoded.Format} and a brush alpha is read as Rgba8 bytes, so it "
                + "cannot be sampled. Import it as an uncompressed 8-bit picture.");
        }

        return Weighted(path, decoded);
    }

    /// <summary>Copies a decoded picture into an image whose alpha channel is the brush weight.</summary>
    /// <param name="path">What the artist typed, for the sentence.</param>
    /// <param name="decoded">The picture, Rgba8.</param>
    /// <returns>The mask and a sentence about it, or no mask and the sentence saying why.</returns>
    /// <remarks>
    ///     ⚠ <b>The luminance weights are the Rec. 709 ones and not a third each</b>, because a
    ///     picture authored as a greyscale alpha is grey — the three channels agree and every set of
    ///     weights summing to one gives the same answer — while a picture that is <em>coloured</em>
    ///     is being read for its brightness, and an even average reads a saturated blue as far
    ///     brighter than an eye does.
    /// </remarks>
    static (IBrushMask? Mask, string Message) Weighted(string path, TextureData decoded) {
        var source = decoded.Level(0);
        var texels = decoded.Width * decoded.Height;

        if (texels <= 0 || source.Length < texels * PaintImage.BytesPerTexel) {
            return (null, $"'{path}' decoded to {decoded.Width}×{decoded.Height}, which is not a picture.");
        }

        byte lowest = 255;
        byte highest = 0;

        for (var texel = 0; texel < texels; texel++) {
            var alpha = source[(texel * PaintImage.BytesPerTexel) + 3];

            lowest = Math.Min(lowest, alpha);
            highest = Math.Max(highest, alpha);
        }

        var opaque = lowest == highest;

        PaintImage image = new(decoded.Width, decoded.Height);

        byte weakest = 255;
        byte strongest = 0;

        for (var texel = 0; texel < texels; texel++) {
            var at = texel * PaintImage.BytesPerTexel;

            var weight = opaque
                ? (byte)Math.Clamp(
                    (int)MathF.Round(
                        (source[at] * 0.2126f) + (source[at + 1] * 0.7152f) + (source[at + 2] * 0.0722f)
                    ),
                    0,
                    255
                )
                : source[at + 3];

            image.Texels[at + 3] = weight;
            weakest = Math.Min(weakest, weight);
            strongest = Math.Max(strongest, weight);
        }

        if (strongest == 0) {
            return (null,
                $"'{path}' is {(opaque ? "black" : "wholly transparent")} everywhere, so it would be a "
                + "brush that paints nothing. Pick a picture whose shape is in "
                + (opaque ? "its brightness" : "its alpha channel") + ".");
        }

        var read = opaque ? "brightness" : "alpha channel";
        var flat = weakest == strongest ? ", which is flat, so it stamps a plain square" : "";

        return (new PaintImageMask(image),
            $"'{path}' at {decoded.Width}×{decoded.Height}, weight from its {read}{flat}.");
    }
}
