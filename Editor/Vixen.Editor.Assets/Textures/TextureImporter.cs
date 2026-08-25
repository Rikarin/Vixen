// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Imaging.BlockCompression;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Graphics;
using Vixen.Rendering.Sprites;

namespace Vixen.Editor.Assets.Textures;

/// <summary>Turns an image an artist saved into the KTX2 file a device uploads.</summary>
/// <remarks>
///     <para>
///         Decode, limit, label, reduce, compress, write. Every step but the first is
///         <c>Vixen.Core.Imaging</c>, and the whole reason this class is short is that the decisions
///         live where they can be tested against something: the mip filter's variants, the block
///         encoders' bounds, the container's layout.
///     </para>
///     <para>
///         <b>What it decides, and what it refuses to decide.</b> It picks a compressed format from
///         the usage when the settings say <see cref="TextureCompression.Automatic" />, and it will
///         not pick one that cannot hold what the texture is — a colour texture is not silently
///         squeezed into BC5's two channels. When the target is a phone it produces uncompressed and
///         says why, rather than shipping BC7 that most Android hardware cannot sample.
///     </para>
///     <para>
///         <b>A compressed source passes straight through.</b> A <c>.ktx2</c> an artist compressed
///         with a better encoder is copied rather than decoded and re-encoded, because a second
///         round of lossy compression only ever loses.
///     </para>
///     <para>
///         <b>A high-range source takes its own path and keeps its range.</b> A <c>.hdr</c> decodes
///         to <c>Rgba32Float</c>, which the eight-bit path cannot hold and used to fail on; it ships
///         as BC6H, or as floats when the settings say no compression. See <see cref="HighRange" />
///         for the three eight-bit decisions that are reported rather than applied.
///     </para>
/// </remarks>
[Importer(".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif", ".hdr", ".ktx2", ".dds")]
public sealed class TextureImporter : AssetImporter<TextureImportSettings> {
    readonly IReadOnlyList<IImageDecoder> decoders;

    /// <summary>Uses the decoders that ship.</summary>
    public TextureImporter() : this(ImageDecoders.BuiltIn) { }

    /// <summary>Uses a given set of decoders.</summary>
    /// <param name="decoders">The decoders.</param>
    public TextureImporter(IReadOnlyList<IImageDecoder> decoders) {
        ArgumentNullException.ThrowIfNull(decoders);
        this.decoders = decoders;
    }

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        TextureImportSettings settings,
        CancellationToken cancellationToken
    ) {
        var extension = Path.GetExtension(context.SourcePath.ToString());
        var decoder = ImageDecoders.For(decoders, extension)
            ?? throw new NotSupportedException(
                $"Nothing here decodes {extension}. StbImageDecoder reads the common authoring formats and "
                + "Radiance HDR, Ktx2Decoder reads what the engine already ships and DdsDecoder reads a 2D "
                + "DDS; .exr, .tif and .webp are owed."
            );

        TextureData decoded;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            decoded = decoder.Decode(source, extension);
        }

        if (decoded.Format.IsCompressed()) {
            context.Report(
                ImportSeverity.Information,
                $"{extension} is already {decoded.Format}, so it ships as it arrived. Re-encoding a compressed "
                + "texture only loses."
            );

            // ⚠ The one decision the settings lose on this path, so it is the one worth saying out
            // loud. A compressed file carries its own transfer function — BC7_UNORM and
            // BC7_UNORM_SRGB are different formats and the file picked one — and passing it through
            // means the sRGB flag comes from the exporter rather than from Content. When the two
            // disagree the texture is not wrong in a way anything downstream can see: it is an albedo
            // the hardware never converts, or a mask it converts twice, and the symptom is a scene
            // that looks washed out or crushed with nothing in the log. Saying so costs a line.
            if (decoded.Format.IsSrgb() != (settings.Content == TextureContent.Colour)) {
                context.Report(
                    ImportSeverity.Warning,
                    $"The file is {decoded.Format} and its usage is {settings.Content}, so the sampler will "
                    + (decoded.Format.IsSrgb() ? "convert bytes that are not colour. " : "not convert colour. ")
                    + "A compressed source ships with the transfer function its exporter chose; either the "
                    + "usage or the export is wrong."
                );
            }

            context.Write(SubAssetId.Main, "Texture", Ktx2.Write(decoded));
            WriteSprites(context, settings, decoded.Width, decoded.Height);

            return context.Finish();
        }

        if (IsHighRange(decoded.Format)) {
            return HighRange(context, settings, decoded);
        }

        var options = OptionsFor(settings);
        var limited = Limit(decoded, settings, options, context);

        // The sRGB flag is a statement about what the bytes mean, and only the settings know it. It
        // has to be on the texture before the mip chain and the compression, because both consult it.
        var format = settings.Content == TextureContent.Colour ? PixelFormat.Rgba8UNormSrgb : PixelFormat.Rgba8UNorm;

        var texture = new TextureData(format, limited.Width, limited.Height, settings.GenerateMips ? 0 : 1);
        limited.Level(0).CopyTo(texture.LevelSpan(0));

        if (settings.GenerateMips) {
            MipChain.Generate(texture, options);
        }

        var compressed = Compress(texture, settings, context);

        context.Write(SubAssetId.Main, "Texture", Ktx2.Write(compressed));

        // ⚠ The *decoded* extent, not the compressed one. A texture over the size limit ships halved
        // and the sprite rects are in the source's texels — see SpriteRect for why that is right
        // rather than an oversight: a UV is a region over a texture size and both halve together, so
        // rescaling the rects would round every one of them to a grid they were not drawn on.
        WriteSprites(context, settings, decoded.Width, decoded.Height);

        return context.Finish();
    }

    /// <summary>Whether the decoded pixels carry more range than a byte, and so cannot take the eight-bit path.</summary>
    static bool IsHighRange(PixelFormat format) =>
        format is PixelFormat.Rgba32Float or PixelFormat.Rgba16Float;

    /// <summary>
    ///     Ships a high-range source as high range: float in, float or BC6H out, and every decision
    ///     the eight-bit path would have made silently is named instead.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This branch exists because the eight-bit path is not merely lossy for a
    ///         <c>.hdr</c>, it is wrong.</b> It allocates a four-byte-a-texel buffer and copies a
    ///         sixteen-byte-a-texel level into it, which for every <c>.hdr</c> anybody has ever
    ///         dropped on the pipeline is a failed import saying "destination is too short" — the
    ///         importer claims <c>.hdr</c>, the guide's decoder table promises <c>Rgba32Float</c>,
    ///         and nothing between them ever ran.
    ///     </para>
    ///     <para>
    ///         <b>The range is the content, so nothing here narrows it.</b> Tone-mapping down to a
    ///         byte at import time would throw away the one thing a Radiance file exists to carry —
    ///         the sun being ten thousand times the sky — and would bake an exposure into an asset
    ///         that has no idea which scene it will be lit in. <see cref="BlockCompressor" /> already
    ///         encodes BC6H from float and nothing could ask for it; this is what asks.
    ///     </para>
    ///     <para>
    ///         <b>Three things the eight-bit path does are refused rather than approximated,</b> and
    ///         each is reported so it is a line in the import log rather than a surprise in a frame:
    ///         the mip chain and the size limit both run through <see cref="MipChain" />, which reads
    ///         eight-bit channels and has no float filter yet; and the sRGB flag does not apply,
    ///         because Radiance is linear by definition and no float format has a transfer function
    ///         for the hardware to undo.
    ///     </para>
    /// </remarks>
    static ImportResult HighRange(ImportContext context, TextureImportSettings settings, TextureData decoded) {
        if (settings.Content != TextureContent.Linear) {
            context.Report(
                ImportSeverity.Information,
                $"{decoded.Format} carries linear radiance with no upper bound, so its usage being "
                + $"{settings.Content} changes nothing: no float format has an sRGB form, and the sampler "
                + "applies no transfer function to one."
            );
        }

        if (settings.GenerateMips) {
            context.Report(
                ImportSeverity.Warning,
                "A high-range texture ships with one level. The mip filter averages eight-bit channels and "
                + "has no float form yet, and a chain built by narrowing to bytes first would throw away the "
                + "range this format exists to carry."
            );
        }

        if (settings.MaxSize > 0 && Math.Max(decoded.Width, decoded.Height) > settings.MaxSize) {
            context.Report(
                ImportSeverity.Warning,
                $"{decoded.Width}×{decoded.Height} is over the {settings.MaxSize} limit and ships at full size "
                + "anyway. Reducing runs through the same eight-bit mip filter a chain does; resize the source "
                + "until that filter has a float form."
            );
        }

        var texture = CompressHighRange(decoded, settings, context);

        context.Write(SubAssetId.Main, "Texture", Ktx2.Write(texture));
        WriteSprites(context, settings, decoded.Width, decoded.Height);

        return context.Finish();
    }

    /// <summary>Compresses a high-range texture to the one block format that can hold it, or says why it did not.</summary>
    static TextureData CompressHighRange(
        TextureData texture,
        TextureImportSettings settings,
        ImportContext context
    ) {
        if (settings.Compression == TextureCompression.None) {
            return texture;
        }

        if (settings.Compression is not (TextureCompression.Automatic or TextureCompression.Bc6H)) {
            // Refused by name rather than left to the encoder, which would say "convert to Rgba8UNorm
            // first" — advice that is exactly the mistake being made.
            throw new NotSupportedException(
                $"A {texture.Format} source cannot ship as {settings.Compression}: every BC format but BC6H "
                + "stores unsigned normalised bytes, so everything over one would clamp and the file would be "
                + "a low-range picture with a high-range name. Ask for Bc6H, or for None to ship the floats."
            );
        }

        if (IsMobile(context.Target)) {
            context.Report(
                ImportSeverity.Warning,
                $"{context.Target} does not sample BC, so this ships as {texture.Format} and sixteen times "
                + "larger than BC6H. ASTC's high-range profile needs the native encoder doc 03 calls for and "
                + "doc 01 registers as astcenc."
            );

            return texture;
        }

        return BlockCompressor.Encode(texture, PixelFormat.Bc6HRgbUFloat);
    }

    /// <summary>Writes the sheet and one sub-asset per sprite, when the texture declares any.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A sub-asset each, and the sheet as well.</b> The sheet is what a tile map or an
    ///         animation reaches for — it is the thing that holds the frames in order — and the
    ///         per-sprite sub-assets are what a single reference resolves to, so dragging one frame
    ///         into a scene does not pull a hundred others in with it. Neither is derivable from the
    ///         other at load time without reading both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sub-asset id is derived from the sprite's <i>name</i></b>, through the same
    ///         <c>DeclareSubAsset</c> every importer uses. That is what makes a re-slice survivable:
    ///         a reference points at a name, so moving a rect keeps it and renaming one breaks it
    ///         visibly — where numbering by position would silently repoint every reference after
    ///         the frame somebody inserted.
    ///     </para>
    /// </remarks>
    static void WriteSprites(ImportContext context, TextureImportSettings settings, int width, int height) {
        if (settings.SpriteMode == SpriteMode.None || settings.Sprites.Length == 0) {
            return;
        }

        var size = new Int2(width, height);
        var density = settings.PixelsPerUnit > 0f ? settings.PixelsPerUnit : Sprite.DefaultPixelsPerUnit;
        var name = Path.GetFileNameWithoutExtension(context.SourcePath.ToString());

        List<Sprite> sprites = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rect in settings.Sprites) {
            if (rect.IsEmpty) {
                context.Report(ImportSeverity.Warning, $"Sprite '{rect.Name}' has no area and was skipped.");
                continue;
            }

            if (!seen.Add(rect.Name)) {
                // Refused rather than de-duplicated, because the id is derived from the name: two
                // sprites called the same thing are one sub-asset, and whichever is written second
                // silently replaces the first.
                context.Report(
                    ImportSeverity.Error,
                    $"Two sprites are called '{rect.Name}'. A sub-asset is identified by its name, so the second "
                    + "would take the first one's place and every reference to it would follow."
                );

                continue;
            }

            var sprite = rect.ToSprite(size, density);

            sprites.Add(sprite);
            context.Write(context.DeclareSubAsset("Sprite", rect.Name), "Sprite", Serializer.ToBytes(sprite));
        }

        if (sprites.Count == 0) {
            return;
        }

        var sheet = new SpriteSheet { Name = name, TextureSize = size, Sprites = [.. sprites] };

        context.Write(context.DeclareSubAsset("SpriteSheet", name), "SpriteSheet", Serializer.ToBytes(sheet));
    }

    /// <summary>How the mip filter should treat this texture, which is the usage restated.</summary>
    static MipOptions OptionsFor(TextureImportSettings settings) => settings.Content switch {
        TextureContent.NormalMap => MipOptions.NormalMap,
        TextureContent.Colour => new() { Srgb = true, AlphaWeighted = settings.AlphaIsTransparency },
        _ => MipOptions.Linear
    };

    /// <summary>
    ///     Reduces a texture until it fits under the size limit, by halving through the same filter
    ///     that builds its mip chain.
    /// </summary>
    static TextureData Limit(
        TextureData source,
        TextureImportSettings settings,
        MipOptions options,
        ImportContext context
    ) {
        if (settings.MaxSize <= 0 || Math.Max(source.Width, source.Height) <= settings.MaxSize) {
            return source;
        }

        var chain = new TextureData(source.Format, source.Width, source.Height);
        source.Level(0).CopyTo(chain.LevelSpan(0));
        MipChain.Generate(chain, options);

        var steps = 0;

        while (steps < chain.LevelCount - 1 && Math.Max(chain.Levels[steps].Width, chain.Levels[steps].Height) > settings.MaxSize) {
            steps++;
        }

        var described = chain.Levels[steps];
        var reduced = new TextureData(source.Format, described.Width, described.Height, levelCount: 1);
        chain.Level(steps).CopyTo(reduced.LevelSpan(0));

        context.Report(
            ImportSeverity.Information,
            $"{source.Width}×{source.Height} is over the {settings.MaxSize} limit, so it ships at "
            + $"{described.Width}×{described.Height} — halved {steps} time{(steps == 1 ? "" : "s")}."
        );

        return reduced;
    }

    /// <summary>Compresses, or says why it did not.</summary>
    static TextureData Compress(TextureData texture, TextureImportSettings settings, ImportContext context) {
        if (settings.Compression == TextureCompression.None) {
            return texture;
        }

        // BC is a desktop and console format. Android's baseline is ETC2 and its modern floor is
        // ASTC; iOS is ASTC only. Shipping BC7 to either would produce a texture the driver refuses
        // to sample, so this produces something that works and names what is missing — doc 03 puts
        // the ASTC encoder in native code and doc 01 registers astcenc for it.
        if (IsMobile(context.Target)) {
            context.Report(
                ImportSeverity.Warning,
                $"{context.Target} does not sample BC, so this ships uncompressed and four times larger than it "
                + "should. ASTC needs the native encoder doc 03 calls for and doc 01 registers as astcenc."
            );

            return texture;
        }

        var chosen = settings.Compression switch {
            TextureCompression.Bc1 => PixelFormat.Bc1RgbaUNorm,
            TextureCompression.Bc3 => PixelFormat.Bc3RgbaUNorm,
            TextureCompression.Bc4 => PixelFormat.Bc4RUNorm,
            TextureCompression.Bc5 => PixelFormat.Bc5RgUNorm,
            TextureCompression.Bc7 => PixelFormat.Bc7RgbaUNorm,
            // The mirror of the refusal on the high-range path, and refused for the same reason from
            // the other side: BC6H is three channels of unbounded float, so an eight-bit source
            // shipped in it loses its alpha, gains nothing for the range it does not have, and is a
            // worse picture than BC7 at the same eight bits a texel.
            TextureCompression.Bc6H => throw new NotSupportedException(
                $"A {texture.Format} source cannot ship as BC6H: that format holds three channels of unbounded "
                + "float, so this would drop the alpha and spend its precision on values above one that an "
                + "eight-bit source cannot contain. BC7 is the eight-bit equivalent; BC6H is for a .hdr."
            ),
            _ => settings.Content switch {
                // Two channels for a normal map, because the third is reconstructed in the shader and
                // storing it costs precision the other two want.
                TextureContent.NormalMap => PixelFormat.Bc5RgUNorm,
                _ => PixelFormat.Bc7RgbaUNorm
            }
        };

        var target = texture.Format.IsSrgb() ? chosen.ToSrgb() : chosen.ToLinear();

        if (texture.Format.IsSrgb() && !target.IsSrgb()) {
            throw new NotSupportedException(
                $"A colour texture cannot ship as {chosen}: that format has no sRGB form, so the hardware would "
                + "never apply the transfer function. Either the usage or the compression setting is wrong."
            );
        }

        return BlockCompressor.Encode(texture, target);
    }

    static bool IsMobile(string target) =>
        target.Contains("android", StringComparison.OrdinalIgnoreCase)
        || target.Contains("ios", StringComparison.OrdinalIgnoreCase);
}
