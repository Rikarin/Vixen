// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Textures;
using Vixen.Rendering.Materials;

namespace Vixen.Editor.Assets.Materials;

/// <summary>What one output of a texture graph is, which is how a bake knows where to put it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § 4.8's <c>Output</c> usages, verbatim.</b> An <c>Output</c> node declares
///         one of these and the bake does the rest — which file it lands in, which channel of it,
///         what the sampler is told the bytes mean, and which material feature reads it.
///     </para>
///     <para>
///         ⚠ <b>Nine usages and seven files, because three of them share one.</b>
///         <see cref="MaterialMapTarget.Orm" /> is <see cref="Occlusion" />, <see cref="Roughness" />
///         and <see cref="Metalness" /> in R, G and B — the packing
///         <see cref="TexturedOrmFeature" /> reads, and the reason a material spends one streaming
///         page on three measurements instead of three. So a usage is not a file, and the two are
///         separate types rather than one enum somebody later assumes is both.
///     </para>
/// </remarks>
public enum MaterialMapUsage {
    /// <summary>Albedo, seen by a person. Its alpha is coverage.</summary>
    BaseColor,

    /// <summary>The tangent-space normal.</summary>
    Normal,

    /// <summary>Perceptual roughness. Packed into <see cref="MaterialMapTarget.Orm" />'s green.</summary>
    Roughness,

    /// <summary>How much of a conductor the surface is. Packed into <see cref="MaterialMapTarget.Orm" />'s blue.</summary>
    Metalness,

    /// <summary>How much light reaches the point. Packed into <see cref="MaterialMapTarget.Orm" />'s red.</summary>
    Occlusion,

    /// <summary>Displacement along the normal.</summary>
    Height,

    /// <summary>Emitted radiance.</summary>
    Emissive,

    /// <summary>Coverage as its own map, read from red.</summary>
    Opacity,

    /// <summary>A mask this graph produced for another graph, or for a layer stack.</summary>
    Mask
}

/// <summary>One file a bake writes, which is not the same list as <see cref="MaterialMapUsage" />.</summary>
/// <remarks>
///     ⚠ <b>Two of these bind to no material feature, and saying so is the point of the type.</b>
///     <see cref="Height" /> has no textured runtime feature —
///     <a href="https://github.com/Rikarin/Vixen/issues/615">#615</a> is that decision, and until it
///     is made a height map is a file an artist and a future feature can read and a material cannot.
///     <see cref="Mask" /> is not a material map at all: § 4.10's mask sources read it, and a
///     material never does. <see cref="MaterialMapNaming.Parameter" /> returns null for both, and a
///     bake writes them without inventing a feature to hang them on.
/// </remarks>
public enum MaterialMapTarget {
    /// <summary>The albedo map.</summary>
    BaseColor,

    /// <summary>The tangent-space normal map.</summary>
    Normal,

    /// <summary>Occlusion, roughness and metalness in R, G and B.</summary>
    Orm,

    /// <summary>The height map. Written, and bound to nothing.</summary>
    Height,

    /// <summary>The emissive map.</summary>
    Emissive,

    /// <summary>The opacity mask.</summary>
    Opacity,

    /// <summary>A mask for another graph. Written, and bound to nothing.</summary>
    Mask
}

/// <summary>What a baked material's files are called, and what a material calls them.</summary>
/// <remarks>
///     <para>
///         <b>Two vocabularies meet here and only one of them is ours.</b> The file names are this
///         file's to choose; the <em>parameter</em> names are not — <see cref="Parameter" /> reads
///         them off the runtime feature records themselves, because <c>WorldRenderer.Paired</c> pairs
///         one shader parameter with one material-side name and keys that on the feature's default.
///         ⚠ <b>A material that renames its map resolves nothing and samples slot zero</b>, which is
///         the bindless table's fallback checker: a normal map read from it is a lit surface whose
///         shading is merely wrong, on every device, with nothing reported. So the names are read
///         rather than typed, and a rename in <c>MaterialFeatures.cs</c> moves both halves at once.
///     </para>
///     <para>
///         ⚠ <b>The suffixes are the vocabulary a graph's <c>Output</c> nodes and the CLI both write
///         against</b>, so they are fixed here rather than composed at each call site — the argument
///         <see cref="MeshMaps.MeshMapNaming" /> makes for its own, and for the same reason: renaming
///         one silently unbinds everything that asks for it.
///     </para>
/// </remarks>
public static class MaterialMapNaming {
    /// <summary>Which folder under <c>Assets/</c> a baked material goes in by default.</summary>
    public const string DefaultFolder = "Materials";

    /// <summary>What a map is written as up to and including <see cref="PortableLimit" />.</summary>
    /// <remarks>
    ///     PNG, because doc 48 § D4's whole argument for baking to files is that the result is
    ///     "a file the artist can open in Photoshop and a reviewer can diff". A container nothing but
    ///     the engine reads would give that up for a texture whose mip chain the importer is going to
    ///     build anyway.
    /// </remarks>
    public const string PortableExtension = ".png";

    /// <summary>And above it.</summary>
    /// <remarks>
    ///     ⚠ <b>KTX2 carrying its own mip chain and its own block compression</b>, which is what
    ///     <see cref="TextureImporter" /> passes straight through — "a compressed source is copied
    ///     rather than decoded and re-encoded, because a second round of lossy compression only ever
    ///     loses". So above the limit the bake pays the encode once, at bake time, instead of every
    ///     content build paying it for a 4K PNG.
    /// </remarks>
    public const string ContainerExtension = ".ktx2";

    /// <summary>The largest edge, in texels, that still ships as a PNG.</summary>
    /// <remarks>
    ///     ⚠ <b>An exclusive ceiling: 2048 is a PNG and 2049 is a container.</b> Doc 48 § D4 says
    ///     "PNGs, or KTX2 for anything over 2K", and 2K is the size most texture sets are authored
    ///     at — a ceiling that caught it would put the ordinary case on the exceptional path.
    /// </remarks>
    public const int PortableLimit = 2048;

    /// <summary>Every usage, in the order an <c>Output</c> list is written.</summary>
    public static IReadOnlyList<MaterialMapUsage> Every { get; } = Enum.GetValues<MaterialMapUsage>();

    /// <summary>Every file a bake can write, in the order it writes them.</summary>
    public static IReadOnlyList<MaterialMapTarget> EveryTarget { get; } = Enum.GetValues<MaterialMapTarget>();

    /// <summary>The suffix a usage is named by, in a file name and in the provenance block.</summary>
    /// <param name="usage">The usage.</param>
    /// <returns>The suffix, with no separator and no dot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a usage.</exception>
    public static string Suffix(MaterialMapUsage usage) => usage switch {
        MaterialMapUsage.BaseColor => "baseColor",
        MaterialMapUsage.Normal => "normal",
        MaterialMapUsage.Roughness => "roughness",
        MaterialMapUsage.Metalness => "metalness",
        MaterialMapUsage.Occlusion => "occlusion",
        MaterialMapUsage.Height => "height",
        MaterialMapUsage.Emissive => "emissive",
        MaterialMapUsage.Opacity => "opacity",
        MaterialMapUsage.Mask => "mask",
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "There is no such output usage.")
    };

    /// <summary>The suffix a file is named by.</summary>
    /// <param name="target">Which file.</param>
    /// <returns>The suffix, with no separator and no dot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a file a bake writes.</exception>
    public static string Suffix(MaterialMapTarget target) => target switch {
        MaterialMapTarget.BaseColor => "baseColor",
        MaterialMapTarget.Normal => "normal",
        MaterialMapTarget.Orm => "orm",
        MaterialMapTarget.Height => "height",
        MaterialMapTarget.Emissive => "emissive",
        MaterialMapTarget.Opacity => "opacity",
        MaterialMapTarget.Mask => "mask",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "There is no such baked map.")
    };

    /// <summary>The usage a suffix names.</summary>
    /// <param name="suffix">The suffix, without a separator.</param>
    /// <param name="usage">What it names.</param>
    /// <returns>Whether it names one.</returns>
    /// <remarks>Ordinal and case-sensitive: these are written by a tool, not typed into a dialog.</remarks>
    public static bool TryParseSuffix(string? suffix, out MaterialMapUsage usage) {
        foreach (var candidate in Every) {
            if (string.Equals(Suffix(candidate), suffix, StringComparison.Ordinal)) {
                usage = candidate;
                return true;
            }
        }

        usage = default;
        return false;
    }

    /// <summary>Which file a usage lands in.</summary>
    /// <param name="usage">The usage.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a usage.</exception>
    public static MaterialMapTarget TargetOf(MaterialMapUsage usage) => usage switch {
        MaterialMapUsage.BaseColor => MaterialMapTarget.BaseColor,
        MaterialMapUsage.Normal => MaterialMapTarget.Normal,
        MaterialMapUsage.Roughness or MaterialMapUsage.Metalness or MaterialMapUsage.Occlusion =>
            MaterialMapTarget.Orm,
        MaterialMapUsage.Height => MaterialMapTarget.Height,
        MaterialMapUsage.Emissive => MaterialMapTarget.Emissive,
        MaterialMapUsage.Opacity => MaterialMapTarget.Opacity,
        MaterialMapUsage.Mask => MaterialMapTarget.Mask,
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "There is no such output usage.")
    };

    /// <summary>What each of a file's channels holds, in R, G, B order.</summary>
    /// <param name="target">Which file.</param>
    /// <returns>One usage per channel it carries.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a file a bake writes.</exception>
    /// <remarks>
    ///     ⚠ <b>The order is <see cref="TexturedOrmFeature" />'s and not a preference.</b> That
    ///     feature reads occlusion from red, roughness from green and metalness from blue; a bake
    ///     that packed them in the order the enum happens to list them would produce a material that
    ///     is shiny where it should be occluded, and nothing anywhere would say so.
    /// </remarks>
    public static IReadOnlyList<MaterialMapUsage> Packed(MaterialMapTarget target) => target switch {
        MaterialMapTarget.BaseColor => [MaterialMapUsage.BaseColor],
        MaterialMapTarget.Normal => [MaterialMapUsage.Normal],
        MaterialMapTarget.Orm => [MaterialMapUsage.Occlusion, MaterialMapUsage.Roughness, MaterialMapUsage.Metalness],
        MaterialMapTarget.Height => [MaterialMapUsage.Height],
        MaterialMapTarget.Emissive => [MaterialMapUsage.Emissive],
        MaterialMapTarget.Opacity => [MaterialMapUsage.Opacity],
        MaterialMapTarget.Mask => [MaterialMapUsage.Mask],
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "There is no such baked map.")
    };

    /// <summary>What a material calls the map, or null where no feature samples it.</summary>
    /// <param name="target">Which file.</param>
    /// <returns>The parameter name, or <see langword="null" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a file a bake writes.</exception>
    /// <remarks>
    ///     ⚠ <b>Read off the feature record rather than written down.</b> The default is what
    ///     <c>WorldRenderer.Paired</c> keys the pairing on, so a literal here would be a second copy
    ///     of a name whose disagreement with the first is invisible — the index stays zero, the map
    ///     is read from the bindless table's fallback, and the surface shades.
    /// </remarks>
    public static string? Parameter(MaterialMapTarget target) => target switch {
        MaterialMapTarget.BaseColor => new TexturedMetalRoughnessFeature().BaseColorMap,
        MaterialMapTarget.Normal => new TexturedNormalMapFeature().NormalMap,
        MaterialMapTarget.Orm => new TexturedOrmFeature().OrmMap,
        MaterialMapTarget.Emissive => new TexturedEmissiveFeature().EmissiveMap,
        MaterialMapTarget.Opacity => new TexturedOpacityFeature().OpacityMap,

        // Both deliberate, and both are files the bake still writes. See MaterialMapTarget.
        MaterialMapTarget.Height or MaterialMapTarget.Mask => null,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "There is no such baked map.")
    };

    /// <summary>What the sampler is told the bytes mean.</summary>
    /// <param name="target">Which file.</param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a file a bake writes.</exception>
    /// <remarks>
    ///     ⚠ <b>Colour is exactly two of the seven.</b> Applying sRGB to a roughness map bends the
    ///     whole material response, and it is the failure that looks like a lighting bug for a week —
    ///     <see cref="TextureContent.Linear" />'s own remarks say so.
    /// </remarks>
    public static TextureContent ContentOf(MaterialMapTarget target) => target switch {
        MaterialMapTarget.BaseColor or MaterialMapTarget.Emissive => TextureContent.Colour,
        MaterialMapTarget.Normal => TextureContent.NormalMap,
        MaterialMapTarget.Orm or MaterialMapTarget.Height or MaterialMapTarget.Opacity or MaterialMapTarget.Mask =>
            TextureContent.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "There is no such baked map.")
    };

    /// <summary>Which block format it ships in.</summary>
    /// <param name="target">Which file.</param>
    /// <returns>The compression.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a file a bake writes.</exception>
    /// <remarks>
    ///     ⚠ <b>Named rather than left to <see cref="TextureCompression.Automatic" />, and the
    ///     one-channel maps are why.</b> The importer's automatic choice is BC5 for a normal map and
    ///     BC7 for everything else, so an opacity mask would ship as four channels of BC7 where BC4
    ///     holds the one channel it has at half the size. Naming the format here is also what makes
    ///     the two write paths agree: above <see cref="PortableLimit" /> this is the format the bake
    ///     encodes into the container itself, and below it the same value goes in the sidecar for the
    ///     importer to apply. A bake whose 2048 and 4096 outputs shipped in different formats would
    ///     be a resolution slider that changes the compression artefacts.
    /// </remarks>
    public static TextureCompression CompressionOf(MaterialMapTarget target) => target switch {
        MaterialMapTarget.BaseColor or MaterialMapTarget.Emissive or MaterialMapTarget.Orm => TextureCompression.Bc7,
        MaterialMapTarget.Normal => TextureCompression.Bc5,
        MaterialMapTarget.Height or MaterialMapTarget.Opacity or MaterialMapTarget.Mask => TextureCompression.Bc4,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "There is no such baked map.")
    };

    /// <summary>What a packed channel holds when the graph produced no output for it.</summary>
    /// <param name="usage">One of <see cref="MaterialMapTarget.Orm" />'s three.</param>
    /// <returns>The value, 0..1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a channel of a packed map.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero is a valid-looking value for all three and the wrong one for two of them.</b>
    ///         A graph that outputs roughness alone still needs an occlusion and a metalness channel,
    ///         and leaving them at zero writes a fully occluded conductor — a material that is black
    ///         and shades, which is this repository's most-repeated defect shape.
    ///     </para>
    ///     <para>
    ///         So the values are taken from the runtime features' own defaults rather than chosen
    ///         here: <see cref="OcclusionFeature.OcclusionMap" /> is what "no occlusion was measured"
    ///         means, and <see cref="MetalRoughnessFeature" />'s roughness and metalness are what an
    ///         unauthored surface already is everywhere else in the engine.
    ///     </para>
    /// </remarks>
    public static float Absent(MaterialMapUsage usage) => usage switch {
        MaterialMapUsage.Occlusion => new OcclusionFeature().OcclusionMap,
        MaterialMapUsage.Roughness => new MetalRoughnessFeature().Roughness,
        MaterialMapUsage.Metalness => new MetalRoughnessFeature().Metalness,
        _ => throw new ArgumentOutOfRangeException(
            nameof(usage),
            usage,
            "Only a packed map's channels have an absent value; every other usage is either written or not."
        )
    };

    /// <summary>What one map of a material's set is called.</summary>
    /// <param name="material">The material's name, already safe for a file name.</param>
    /// <param name="target">Which file.</param>
    /// <param name="extension">Its extension, which the size chose.</param>
    /// <returns>The file name.</returns>
    /// <exception cref="ArgumentException">The material name or the extension is null or empty.</exception>
    public static string FileName(string material, MaterialMapTarget target, string extension) {
        ArgumentException.ThrowIfNullOrEmpty(material);
        ArgumentException.ThrowIfNullOrEmpty(extension);

        return material + "_" + Suffix(target) + extension;
    }

    /// <summary>Which extension a map of this size is written with.</summary>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <returns>The extension.</returns>
    public static string ExtensionFor(int width, int height) =>
        width > PortableLimit || height > PortableLimit ? ContainerExtension : PortableExtension;
}
