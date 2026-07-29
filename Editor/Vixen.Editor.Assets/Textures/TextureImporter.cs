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
/// </remarks>
[Importer(".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif", ".hdr", ".ktx2")]
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
                + "Radiance HDR, and Ktx2Decoder reads what the engine already ships; .exr, .tif, .webp and "
                + ".dds are owed."
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

            context.Write(SubAssetId.Main, "Texture", Ktx2.Write(decoded));
            WriteSprites(context, settings, decoded.Width, decoded.Height);

            return context.Finish();
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
