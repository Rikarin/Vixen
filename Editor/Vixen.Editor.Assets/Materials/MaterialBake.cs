// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Imaging.BlockCompression;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Vixen.Rendering.Materials;

namespace Vixen.Editor.Assets.Materials;

/// <summary>One file a bake produced, before anything has decided where it goes.</summary>
/// <remarks>
///     ⚠ <b>It carries no file name, deliberately.</b> An encoded mesh map used to carry one, minted
///     before anything knew the folder it landed in or whether the name was another model's, and both
///     of that batch's write defects — <a href="https://github.com/Rikarin/Vixen/issues/680">#680</a>
///     and <a href="https://github.com/Rikarin/Vixen/issues/681">#681</a> — were reachable through
///     it. Naming is <see cref="ProjectMaterialBaker" />'s, derived from
///     <see cref="MaterialMapNaming.FileName" /> and the target each image declares.
/// </remarks>
public sealed record MaterialMapImage {
    /// <summary>Which of the seven files this is.</summary>
    public required MaterialMapTarget Target { get; init; }

    /// <summary>The encoded file.</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>Its extension, which its size chose.</summary>
    public required string Extension { get; init; }

    /// <summary>What the sidecar should say about it.</summary>
    public required TextureImportSettings Settings { get; init; }

    /// <summary>Its width in texels.</summary>
    public required int Width { get; init; }

    /// <summary>Its height in texels.</summary>
    public required int Height { get; init; }
}

/// <summary>Turns a graph's outputs into the files and the material a project can read.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § D11, and the seam is a picture rather than a plan.</b> Nothing here
///         knows about <c>TexturePlan</c>, a device or a queue: a bake evaluates the graph, reads its
///         outputs back as bitmaps, and hands them to <see cref="Encode" />. That is what lets every
///         decision that can be wrong — which channel roughness goes in, what an absent channel
///         holds, which size crosses into a container, which block format each map ships in — be
///         proved against arrays a test wrote by hand, with no adapter and no disk.
///     </para>
///     <para>
///         ⚠ <b>Two write paths, and they differ in <i>when</i> rather than in <i>what</i>.</b> Up to
///         <see cref="MaterialMapNaming.PortableLimit" /> a map is a PNG whose sidecar names the mips
///         and the block format, and <see cref="TextureImporter" /> applies both at build time. Above
///         it the same mip chain and the same block format are encoded here, into a KTX2 the importer
///         passes straight through. One table — <see cref="MaterialMapNaming.CompressionOf" /> —
///         drives both, so a 2048 bake and a 4096 bake of one graph do not ship in different formats.
///     </para>
/// </remarks>
public static class MaterialBake {
    /// <summary>Encodes a graph's outputs into the files a material samples.</summary>
    /// <param name="outputs">One bitmap per usage the graph produced.</param>
    /// <returns>One image per file, in <see cref="MaterialMapNaming.EveryTarget" /> order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outputs" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     There are no outputs, an output has no pixels, or two of them are different sizes.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>One size for the whole set, and a mismatch is refused rather than resampled.</b> A
    ///     texture set is one material's worth of maps over one atlas; two of them at different sizes
    ///     is a graph whose outputs came from different places, and quietly scaling one to meet the
    ///     other would hide that behind a filtered map nobody asked for.
    /// </remarks>
    public static IReadOnlyList<MaterialMapImage> Encode(IReadOnlyDictionary<MaterialMapUsage, Bitmap> outputs) {
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Count == 0) {
            throw new ArgumentException(
                "A material bake needs at least one output. A graph with no Output node produces no files.",
                nameof(outputs)
            );
        }

        var (width, height) = Extent(outputs);
        var made = new List<MaterialMapImage>();

        foreach (var target in MaterialMapNaming.EveryTarget) {
            var channels = MaterialMapNaming.Packed(target);
            var any = false;

            foreach (var usage in channels) {
                any |= outputs.ContainsKey(usage);
            }

            if (!any) {
                continue;
            }

            made.Add(One(target, channels, outputs, width, height));
        }

        return made;
    }

    /// <summary>The material a bake's files are sampled by.</summary>
    /// <param name="maps">What each written file became, once the database had seen it.</param>
    /// <param name="existing">The material as it already stood, or null where there was none.</param>
    /// <returns>The material, ready to be written as a <c>.vxmat</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="maps" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The features are the bake's and are replaced whole; the shading model and the
    ///         pass are the author's and are kept.</b> A graph says what the surface looks like and
    ///         cannot say that it should be shaded as hair or as cel — so re-baking a material an
    ///         artist switched to <c>SubsurfaceShading</c> must not put it back to standard, and
    ///         re-baking a material whose graph dropped its emissive output must not leave the
    ///         emissive feature behind reading a map that is no longer written.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The base surface is textured only when a base-colour map was written, and
    ///         otherwise is the untextured workflow.</b> <see cref="TexturedOrmFeature" /> reads the
    ///         albedo back out of the surface, so something has to have put one there; a
    ///         <see cref="TexturedMetalRoughnessFeature" /> naming a map that does not exist would
    ///         resolve slot zero and shade the surface with the bindless table's fallback checker.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the base feature's own metalness stays at its default of zero</b>, which
    ///         <see cref="TexturedOrmFeature" />'s remarks require rather than prefer: at any other
    ///         value the albedo has already been split between diffuse and <c>f0</c> by a factor the
    ///         ORM map cannot see, and the map's metalness then multiplies whatever the split left.
    ///     </para>
    /// </remarks>
    public static MaterialContent Material(
        IReadOnlyDictionary<MaterialMapTarget, AssetReference> maps,
        MaterialContent? existing = null
    ) {
        ArgumentNullException.ThrowIfNull(maps);

        var features = new List<IMaterialFeature>();
        var textures = new List<MaterialTexture>();

        features.Add(
            maps.ContainsKey(MaterialMapTarget.BaseColor)
                ? new TexturedMetalRoughnessFeature()
                : new MetalRoughnessFeature()
        );

        if (maps.ContainsKey(MaterialMapTarget.Normal)) {
            features.Add(new TexturedNormalMapFeature());
        }

        if (maps.ContainsKey(MaterialMapTarget.Orm)) {
            features.Add(new TexturedOrmFeature());
        }

        if (maps.ContainsKey(MaterialMapTarget.Emissive)) {
            features.Add(new TexturedEmissiveFeature());
        }

        if (maps.ContainsKey(MaterialMapTarget.Opacity)) {
            features.Add(new TexturedOpacityFeature());
        }

        foreach (var target in MaterialMapNaming.EveryTarget) {
            if (maps.TryGetValue(target, out var reference) && MaterialMapNaming.Parameter(target) is { } parameter) {
                textures.Add(new(parameter, reference));
            }
        }

        return new() {
            Shader = existing?.Shader ?? new MaterialContent().Shader,
            Shading = existing?.Shading ?? new MaterialContent().Shading,
            Features = [.. features],
            Textures = [.. textures]
        };
    }

    /// <summary>The one size every output has to agree on.</summary>
    static (int Width, int Height) Extent(IReadOnlyDictionary<MaterialMapUsage, Bitmap> outputs) {
        var width = 0;
        var height = 0;
        var named = MaterialMapUsage.BaseColor;

        foreach (var usage in MaterialMapNaming.Every) {
            if (!outputs.TryGetValue(usage, out var bitmap)) {
                continue;
            }

            if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Pixels.Length < bitmap.Width * bitmap.Height * 4) {
                throw new ArgumentException(
                    $"The {MaterialMapNaming.Suffix(usage)} output is "
                    + $"{bitmap.Width.ToString(CultureInfo.InvariantCulture)}×"
                    + $"{bitmap.Height.ToString(CultureInfo.InvariantCulture)} and carries "
                    + $"{bitmap.Pixels.Length.ToString(CultureInfo.InvariantCulture)} bytes, which is not a picture.",
                    nameof(outputs)
                );
            }

            if (width == 0) {
                (width, height, named) = (bitmap.Width, bitmap.Height, usage);
                continue;
            }

            if (bitmap.Width != width || bitmap.Height != height) {
                throw new ArgumentException(
                    $"The {MaterialMapNaming.Suffix(usage)} output is "
                    + $"{bitmap.Width.ToString(CultureInfo.InvariantCulture)}×"
                    + $"{bitmap.Height.ToString(CultureInfo.InvariantCulture)} and the "
                    + $"{MaterialMapNaming.Suffix(named)} output is "
                    + $"{width.ToString(CultureInfo.InvariantCulture)}×"
                    + $"{height.ToString(CultureInfo.InvariantCulture)}. A texture set is one material's maps over "
                    + "one atlas, so they are one size — resampling one to meet the other would hide where they "
                    + "came from.",
                    nameof(outputs)
                );
            }
        }

        return (width, height);
    }

    /// <summary>One file: its texels composed, then encoded the way its size decided.</summary>
    static MaterialMapImage One(
        MaterialMapTarget target,
        IReadOnlyList<MaterialMapUsage> channels,
        IReadOnlyDictionary<MaterialMapUsage, Bitmap> outputs,
        int width,
        int height
    ) {
        var pixels = Compose(target, channels, outputs, width, height);
        var extension = MaterialMapNaming.ExtensionFor(width, height);
        var container = string.Equals(extension, MaterialMapNaming.ContainerExtension, StringComparison.Ordinal);

        var settings = new TextureImportSettings {
            Content = MaterialMapNaming.ContentOf(target),
            Compression = MaterialMapNaming.CompressionOf(target),

            // ⚠ A container already holds its chain, and this says so rather than repeating it. The
            // importer copies a compressed source through untouched, so the flag is a statement
            // about the file for anything that reads the sidecar — an inspector, a re-import after
            // the settings were edited — and not an instruction it would obey.
            GenerateMips = !container,

            // Only the base colour has an alpha that means anything: it is coverage, and it is what
            // mip generation must weight the colour by so that a cut-out's invisible texels do not
            // vote. Every other map here writes an opaque alpha it does not use.
            AlphaIsTransparency = target == MaterialMapTarget.BaseColor
        };

        return new() {
            Target = target,
            Bytes = container
                ? Ktx2.Write(Compressed(pixels, width, height, settings))
                : PngCodec.Encode(new Bitmap(width, height, pixels)),
            Extension = extension,
            Settings = settings,
            Width = width,
            Height = height
        };
    }

    /// <summary>The RGBA texels of one file, gathered from the outputs that feed its channels.</summary>
    /// <remarks>
    ///     ⚠ <b>A single-channel map is written grey rather than red.</b> Every feature that reads
    ///     one reads <c>.r</c> and BC4 keeps only that channel, so the other two cost nothing at run
    ///     time — and § D4's argument for files is that an artist opens them, which a red-on-black
    ///     opacity mask defeats.
    /// </remarks>
    static byte[] Compose(
        MaterialMapTarget target,
        IReadOnlyList<MaterialMapUsage> channels,
        IReadOnlyDictionary<MaterialMapUsage, Bitmap> outputs,
        int width,
        int height
    ) {
        var pixels = new byte[width * height * 4];

        if (channels.Count == 1) {
            var single = outputs[channels[0]];

            for (var at = 0; at < pixels.Length; at += 4) {
                if (target is MaterialMapTarget.BaseColor or MaterialMapTarget.Normal
                    or MaterialMapTarget.Emissive) {
                    pixels[at] = single.Pixels[at];
                    pixels[at + 1] = single.Pixels[at + 1];
                    pixels[at + 2] = single.Pixels[at + 2];
                } else {
                    var level = single.Pixels[at];
                    pixels[at] = level;
                    pixels[at + 1] = level;
                    pixels[at + 2] = level;
                }

                // ⚠ The base colour's alpha is the graph's; everybody else's is opaque. An opacity
                // map's own alpha is not its value — TexturedOpacityFeature reads red, because a
                // one-channel texture samples alpha as 1 and a feature that read it would make every
                // mask fully opaque and every cutout material solid.
                pixels[at + 3] = target == MaterialMapTarget.BaseColor ? single.Pixels[at + 3] : byte.MaxValue;
            }

            return pixels;
        }

        for (var channel = 0; channel < channels.Count; channel++) {
            var usage = channels[channel];

            if (outputs.TryGetValue(usage, out var source)) {
                for (var at = 0; at < pixels.Length; at += 4) {
                    pixels[at + channel] = source.Pixels[at];
                }

                continue;
            }

            var absent = Byte(MaterialMapNaming.Absent(usage));

            for (var at = 0; at < pixels.Length; at += 4) {
                pixels[at + channel] = absent;
            }
        }

        for (var at = 3; at < pixels.Length; at += 4) {
            pixels[at] = byte.MaxValue;
        }

        return pixels;
    }

    /// <summary>The mipped, block-compressed texture a container holds.</summary>
    /// <remarks>
    ///     ⚠ <b>The sRGB flag goes on the texture before the mips and before the compression, because
    ///     both consult it</b> — <see cref="TextureImporter" /> says so at the one line where it
    ///     matters, and the failure mode is not an error: half black and half white averages to 188
    ///     in linear light and to 127 on the stored bytes, so a colour map whose chain was built on
    ///     the wrong side darkens as it recedes.
    /// </remarks>
    static TextureData Compressed(byte[] pixels, int width, int height, TextureImportSettings settings) {
        var srgb = settings.Content == TextureContent.Colour;
        var texture = new TextureData(srgb ? PixelFormat.Rgba8UNormSrgb : PixelFormat.Rgba8UNorm, width, height);

        pixels.CopyTo(texture.LevelSpan(0));
        MipChain.Generate(texture, Options(settings));

        return BlockCompressor.Encode(texture, Block(settings.Compression, settings.Content));
    }

    /// <summary>How the chain is averaged, which the content decides and the format cannot.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="TextureImporter" />'s own mapping, read from the same settings the sidecar
    ///     is written from</b> — not from the content alone. The two paths have to build the same
    ///     chain for the same map, and alpha weighting is the one input that is not a function of the
    ///     content: an emissive map is colour and its alpha is not coverage.
    /// </remarks>
    static MipOptions Options(TextureImportSettings settings) => settings.Content switch {
        TextureContent.NormalMap => MipOptions.NormalMap,
        TextureContent.Colour => new() { Srgb = true, AlphaWeighted = settings.AlphaIsTransparency },
        _ => MipOptions.Linear
    };

    /// <summary>The pixel format a named compression is, on this side of the seam.</summary>
    /// <remarks>
    ///     ⚠ <b>The same switch <c>TextureImporter.Compress</c> has, and the duplication is the
    ///     honest half of the arrangement rather than an oversight.</b> That one is private to an
    ///     importer that owns a whole <see cref="TextureImportSettings" /> and a target platform;
    ///     this one answers for the seven formats this file names and refuses everything else, which
    ///     is what keeps the two from drifting into disagreeing about a format neither is asked for.
    /// </remarks>
    static PixelFormat Block(TextureCompression compression, TextureContent content) {
        var chosen = compression switch {
            TextureCompression.Bc4 => PixelFormat.Bc4RUNorm,
            TextureCompression.Bc5 => PixelFormat.Bc5RgUNorm,
            TextureCompression.Bc7 => PixelFormat.Bc7RgbaUNorm,
            _ => throw new NotSupportedException(
                $"A baked material map ships as BC4, BC5 or BC7 and this asked for {compression}. "
                + "MaterialMapNaming.CompressionOf is what names them."
            )
        };

        return content == TextureContent.Colour ? chosen.ToSrgb() : chosen.ToLinear();
    }

    static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
}
