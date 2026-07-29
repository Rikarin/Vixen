// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>Which channels of a texture the viewer is showing.</summary>
/// <remarks>
///     A flags enum rather than a single choice, because the two questions a channel viewer answers
///     are "what is in the green channel" — one at a time — and "what does this look like without
///     its alpha" — three at once. One radio group can only ask the first.
/// </remarks>
[Flags]
public enum TextureChannels {
    /// <summary>Nothing, which draws black rather than nothing at all.</summary>
    None = 0,

    /// <summary>Red.</summary>
    Red = 1,

    /// <summary>Green.</summary>
    Green = 2,

    /// <summary>Blue.</summary>
    Blue = 4,

    /// <summary>Alpha.</summary>
    Alpha = 8,

    /// <summary>Colour with its alpha applied — what the texture looks like in use.</summary>
    All = Red | Green | Blue | Alpha,

    /// <summary>Colour with the alpha ignored, which is what is actually stored.</summary>
    Colour = Red | Green | Blue
}

/// <summary>One level of a texture's mip chain, as the import will produce it.</summary>
/// <param name="Level">Which level, zero being the full-size image.</param>
/// <param name="Width">Its width in texels.</param>
/// <param name="Height">Its height in texels.</param>
/// <param name="Bytes">What it costs in the shipped format.</param>
public readonly record struct TextureMipLevel(int Level, int Width, int Height, long Bytes);

/// <summary>What a texture will ship as, worked out from its source size and its settings.</summary>
/// <remarks>
///     <para>
///         <b>Arithmetic, not an import.</b> Every number a mip inspector shows — how many levels,
///         how big each one is, what the whole chain costs — follows from the source's extent, the
///         size limit and the compressed format. Running the importer to find out would make opening
///         a texture cost a block-compression pass, and would answer a question about the artefact
///         rather than about the settings the author is editing.
///     </para>
///     <para>
///         ⚠ <b>The size limit halves rather than resamples</b>, which is the behaviour
///         <see cref="TextureImportSettings.MaxSize" /> documents and which is worth seeing: a 2048
///         under a limit of 1000 ships at 512, and the ladder here says 512 rather than 1000.
///     </para>
/// </remarks>
public static class TextureLadder {
    /// <summary>How many bytes a block-compressed or uncompressed level of a given size costs.</summary>
    /// <param name="compression">The format it ships in.</param>
    /// <param name="width">The level's width.</param>
    /// <param name="height">The level's height.</param>
    /// <returns>Its size in bytes.</returns>
    /// <remarks>
    ///     Block formats are counted in whole four-by-four blocks, which is why a 1×1 level of a BC7
    ///     texture costs sixteen bytes rather than one: the hardware reads a block whatever fraction
    ///     of it is real, and a chain that pretended otherwise would under-report every tail.
    /// </remarks>
    public static long BytesFor(TextureCompression compression, int width, int height) {
        var blocksX = (Math.Max(width, 1) + 3) / 4;
        var blocksY = (Math.Max(height, 1) + 3) / 4;

        return compression switch {
            TextureCompression.None => (long) Math.Max(width, 1) * Math.Max(height, 1) * 4,
            TextureCompression.Bc1 or TextureCompression.Bc4 => (long) blocksX * blocksY * 8,
            _ => (long) blocksX * blocksY * 16
        };
    }

    /// <summary>The format the importer will actually use, resolving <c>Automatic</c>.</summary>
    /// <param name="settings">The settings as they stand.</param>
    /// <returns>A concrete format.</returns>
    /// <remarks>
    ///     ⚠ <b>The same table <c>TextureImporter</c> uses, restated.</b> A preview that guessed
    ///     differently from the importer would show a cost the build does not produce. It is
    ///     restated rather than shared because the importer resolves per target as part of a run and
    ///     this answers for one setting object; a test compares the two.
    /// </remarks>
    public static TextureCompression Resolve(TextureImportEdits settings) {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Compression != TextureCompression.Automatic) {
            return settings.Compression;
        }

        // Two channels for a normal map, because the third is reconstructed in the shader and
        // storing it costs precision the other two want. Everything else is BC7.
        return settings.Content == TextureContent.NormalMap ? TextureCompression.Bc5 : TextureCompression.Bc7;
    }

    /// <summary>The extent a texture ships at, after the size limit has halved it into range.</summary>
    /// <param name="width">The source's width.</param>
    /// <param name="height">The source's height.</param>
    /// <param name="maxSize">The limit, or zero for none.</param>
    /// <returns>The shipped extent.</returns>
    public static (int Width, int Height) Fit(int width, int height, int maxSize) {
        if (maxSize <= 0) {
            return (Math.Max(width, 1), Math.Max(height, 1));
        }

        var fittedWidth = Math.Max(width, 1);
        var fittedHeight = Math.Max(height, 1);

        while ((fittedWidth > maxSize || fittedHeight > maxSize) && (fittedWidth > 1 || fittedHeight > 1)) {
            fittedWidth = Math.Max(fittedWidth / 2, 1);
            fittedHeight = Math.Max(fittedHeight / 2, 1);
        }

        return (fittedWidth, fittedHeight);
    }

    /// <summary>The whole chain a texture will ship as.</summary>
    /// <param name="width">The source's width.</param>
    /// <param name="height">The source's height.</param>
    /// <param name="settings">The settings as they stand.</param>
    /// <returns>The levels, largest first. One level when mips are off.</returns>
    public static IReadOnlyList<TextureMipLevel> Build(int width, int height, TextureImportEdits settings) {
        ArgumentNullException.ThrowIfNull(settings);

        var compression = Resolve(settings);
        var (levelWidth, levelHeight) = Fit(width, height, settings.MaxSize);

        List<TextureMipLevel> levels = [new(0, levelWidth, levelHeight, BytesFor(compression, levelWidth, levelHeight))];

        if (!settings.GenerateMips) {
            return levels;
        }

        var level = 0;

        while (levelWidth > 1 || levelHeight > 1) {
            level++;
            levelWidth = Math.Max(levelWidth / 2, 1);
            levelHeight = Math.Max(levelHeight / 2, 1);

            levels.Add(new(level, levelWidth, levelHeight, BytesFor(compression, levelWidth, levelHeight)));
        }

        return levels;
    }

    /// <summary>What the whole chain costs.</summary>
    /// <param name="levels">The levels.</param>
    /// <returns>Their total size in bytes.</returns>
    public static long TotalBytes(IReadOnlyList<TextureMipLevel> levels) {
        ArgumentNullException.ThrowIfNull(levels);

        long total = 0;

        for (var index = 0; index < levels.Count; index++) {
            total += levels[index].Bytes;
        }

        return total;
    }

    /// <summary>Decodes a texture's source file, or answers why it could not.</summary>
    /// <param name="path">Where the file is.</param>
    /// <param name="reason">What went wrong, when nothing came back.</param>
    /// <returns>The pixels, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>A format nothing can decode is a message, not an exception.</b> An <c>.exr</c> has no
    ///     decoder yet and still has import settings worth editing, so the panel opens and says it
    ///     cannot show the pixels rather than refusing to open.
    /// </remarks>
    public static TextureData? TryDecode(string path, out string? reason) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var extension = Path.GetExtension(path);
        var decoder = ImageDecoders.For(ImageDecoders.BuiltIn, extension);

        if (decoder is null) {
            reason = $"Nothing in this build decodes '{extension}', so there is no preview. "
                + "The import settings below still apply.";

            return null;
        }

        try {
            using var stream = File.OpenRead(path);

            reason = null;
            return decoder.Decode(stream, extension);
        } catch (Exception exception) when (exception is IOException or InvalidDataException
            or NotSupportedException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or IndexOutOfRangeException) {
            // ⚠ A wide net, at a boundary that earns one. The decoder is a third-party codec reading
            // a file an artist may have half-written, renamed from another format, or truncated on
            // the way out of a DCC tool — and what it throws for those is not a documented set.
            // A settings panel that would not open because the pixels were unreadable is the one
            // outcome that helps nobody, since the settings are how the file gets fixed.
            reason = exception.Message;
            return null;
        }
    }
}
