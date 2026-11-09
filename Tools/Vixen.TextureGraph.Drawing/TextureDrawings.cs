// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Ui.Text;

namespace Vixen.TextureGraph.Drawing;

/// <summary>Fills the external images a <c>Source/Text</c> or a <c>Source/Vector</c> reads — Patina #139.</summary>
/// <remarks>
///     <para>
///         <b>The fill is here and only the file is the host's</b>, which is doc 48 § 4.1's split. A compilation
///         writes what a node was told into the external image's reference; a host walks what
///         <see cref="TextureGraphExternals.Upload" /> handed back, asks <see cref="Draws" /> of each, and for a
///         drawing hands this the plan and a way to read a file by the name the node wrote. How a name becomes a
///         file — beside the graph, in a library, in a project's assets — and how that file is opened are the host's,
///         because they are the two things that differ between a headless build, an editor and Patina's worker.
///     </para>
///     <para>
///         ⚠ <b>Drawn at the bake's own size</b>, the plan's base moved by its <see cref="TexturePlan.BakeLevelOffset" />,
///         so the node's <c>Bitmap</c> op copies texel for texel and a 4K bake of a 1K graph has four times the
///         texels on every edge of every letter rather than a 1K picture stretched.
///     </para>
///     <para>
///         <b>Its own assembly</b>, because a font is <c>Vixen.Ui.Text</c>'s and a path is <c>Vixen.Ui</c>'s, and the
///         evaluator links no interface assembly (#720). A host that never draws text never loads either.
///     </para>
/// </remarks>
public static class TextureDrawings {
    /// <summary>The face a <c>Source/Text</c> that names none is drawn with: Open Sans, OFL-1.1, embedded here.</summary>
    const string DefaultFont = "Vixen.TextureGraph.Drawing.Fonts.OpenSans-Regular.ttf";

    static readonly Lazy<FontFace> Bundled = new(static () => {
        using var stream = typeof(TextureDrawings).Assembly.GetManifestResourceStream(DefaultFont)
            ?? throw new InvalidOperationException($"'{DefaultFont}' is not embedded in this assembly.");
        using MemoryStream bytes = new();

        stream.CopyTo(bytes);

        return FontFace.Load(bytes.ToArray());
    }
    );

    /// <summary>Whether a reference names a drawing this fills, rather than a file or another host's scheme.</summary>
    /// <param name="reference">An external image's <see cref="TextureGraphExternal.Asset" />.</param>
    /// <returns><see langword="true" /> for a <c>text:</c> or <c>vector:</c> reference.</returns>
    public static bool Draws(string reference) {
        ArgumentNullException.ThrowIfNull(reference);

        TextureDrawing.Parse(reference.Trim(), out var scheme);

        return scheme.Length > 0;
    }

    /// <summary>The file a drawing reads, as its node names it — a font or an SVG — or null when it reads none.</summary>
    /// <param name="reference">An external image's <see cref="TextureGraphExternal.Asset" />.</param>
    /// <returns>The name, for a host to find and watch; null for text in the bundled face, or for no drawing.</returns>
    public static string? FileOf(string reference) {
        ArgumentNullException.ThrowIfNull(reference);

        var fields = TextureDrawing.Parse(reference.Trim(), out var scheme);
        var name = fields.GetValueOrDefault(scheme == TextureDrawing.TextScheme ? "font" : "file", string.Empty);

        return name.Length > 0 ? name : null;
    }

    /// <summary>Draws one external image and uploads it.</summary>
    /// <param name="uploads">Where the texture is made, and what owns it.</param>
    /// <param name="plan">The plan the image belongs to, whose size and wrap the drawing takes.</param>
    /// <param name="external">The entry, which <see cref="Draws" /> said is a drawing.</param>
    /// <param name="read">The bytes of a file by the name <see cref="FileOf" /> gives. Called at most once.</param>
    /// <exception cref="ArgumentException">The entry is not a drawing.</exception>
    /// <exception cref="InvalidDataException">The font or the SVG cannot be drawn, with a sentence saying why.</exception>
    /// <remarks>
    ///     Whatever <paramref name="read" /> throws — a missing file, a refused path — is thrown on unchanged, so a
    ///     host reports it in the words it reports a missing bitmap in.
    /// </remarks>
    public static void Fill(TextureUploads uploads, TexturePlan plan, TextureGraphExternal external, Func<string, byte[]> read) {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(read);

        var fields = TextureDrawing.Parse(external.Asset.Trim(), out var scheme);
        var width = Extent(plan.BaseWidth, plan.BakeLevelOffset);
        var height = Extent(plan.BaseHeight, plan.BakeLevelOffset);
        TextureCanvas canvas = new(width, height, plan.WrapX, plan.WrapY);

        if (scheme == TextureDrawing.TextScheme) {
            // A face read for this fill is this fill's to dispose; the bundled one is kept for the process.
            using var loaded = FileOf(external.Asset) is { } file ? Face(file, read(file)) : null;
            var font = loaded ?? Bundled.Value;
            var size = Math.Max(Number(fields, "size", 0.2f) * height, 0.01f);
            var alignment = Enum.TryParse<TextureTextAlignment>(fields.GetValueOrDefault("align"), out var parsed)
                ? parsed
                : TextureTextAlignment.Centre;

            TextureTextLayout layout = new(
                fields.GetValueOrDefault("text", string.Empty),
                size,
                alignment,
                Number(fields, "tracking", 0f) * size,
                Number(fields, "x", 0.5f) * width,
                Number(fields, "y", 0.5f) * height,
                Number(fields, "leading", 1f)
            );

            uploads.AddCoverage(plan, external.Image, width, height, TextureText.Rasterize(font, layout, width, height, plan.WrapX, plan.WrapY));

            return;
        }

        if (scheme != TextureDrawing.VectorScheme) {
            throw new ArgumentException($"'{external.Asset}' is not a drawing, so there is nothing here to draw.", nameof(external));
        }

        var name = FileOf(external.Asset) ?? throw new InvalidDataException("it names no SVG file");
        var drawing = TextureVector.Read(Encoding.UTF8.GetString(read(name)));
        TextureVectorPlacement placement = new(
            Enum.TryParse<TextureVectorFit>(fields.GetValueOrDefault("fit"), out var fit) ? fit : TextureVectorFit.Contain,
            Math.Max(Number(fields, "scale", 1f), 0f),
            Number(fields, "x", 0.5f),
            Number(fields, "y", 0.5f)
        );

        if (fields.GetValueOrDefault("draw") == "Coverage") {
            uploads.AddCoverage(plan, external.Image, width, height, TextureVector.Rasterize(drawing, canvas, placement, true));
        } else {
            uploads.Add(plan, external.Image, width, height, TextureVector.Straight(TextureVector.Rasterize(drawing, canvas, placement, false)));
        }
    }

    static FontFace Face(string name, byte[] bytes) {
        try {
            var face = FontFace.Load(bytes);

            if (face.HasOutlines) {
                return face;
            }

            face.Dispose();

            throw new InvalidDataException(
                $"'{name}' has no outlines to fill: it is not a TrueType or OpenType font, or it is a bitmap or "
                + "colour-only one"
            );
        } catch (Exception failure) when (failure is ArgumentException or FormatException or IndexOutOfRangeException or EndOfStreamException) {
            throw new InvalidDataException($"'{name}' is not a TrueType or OpenType font this can read: {failure.Message}", failure);
        }
    }

    static float Number(IReadOnlyDictionary<string, string> fields, string key, float fallback) =>
        fields.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && float.IsFinite(value)
            ? value
            : fallback;

    /// <summary>One side of the bake: <c>TexturePlan.SizeOf</c>'s arithmetic for an image at the base level.</summary>
    static int Extent(int extent, int level) =>
        level >= 0
            ? Math.Max(1, extent >> Math.Min(level, 31))
            : (int)Math.Min(TexturePlan.MaxExtent, (long)extent << Math.Min(-level, 31));
}
