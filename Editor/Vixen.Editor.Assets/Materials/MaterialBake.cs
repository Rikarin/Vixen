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

    /// <summary>Packs a layered material's per-layer coverage into one splat map.</summary>
    /// <param name="coverage">
    ///     One picture per layer, in the material's own layer order — index 0 is the bottom layer and
    ///     therefore the red channel. Each is read for its red, 0..1.
    /// </param>
    /// <returns>The file, whose <see cref="MaterialMapImage.Target" /> is <c>Splat</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="coverage" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     There are no layers, more than four, one has no pixels, or two of them are different sizes.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>The producer <see cref="MaterialMapTarget.Splat" /> exists for</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>. Nothing a graph
    ///         emits reaches here: the inputs are one layer's <em>coverage</em> each, which only the
    ///         thing holding the layer list can resolve.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Layer 0 is red and the order is the material's, not the panel's.</b>
    ///         <c>TexturedMaterialLayersSurface.Painted</c> reads channel <c>i</c> for layer
    ///         <c>i</c>, and <c>TexturedMaterialLayersFeature.Layers</c> is innermost first — so
    ///         index 0 here is the bottom of the stack. A permuted map draws a lit, plausible surface
    ///         of the wrong layers with nothing reported, which is why the order is stated here and
    ///         asserted in the direction that fails.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The alpha is layer 3's weight, and writing <c>byte.MaxValue</c> into it — which
    ///         is what <see cref="Encode" />'s packed path does, correctly, for every other map —
    ///         would be the widest wrong picture this feature can draw.</b> The shader normalises by
    ///         the sum of the weights, so an alpha of one at every texel wins that normalisation
    ///         everywhere and the surface becomes entirely layer 3. Fewer than four layers leaves the
    ///         alpha at <em>zero</em> rather than opaque: zero is "no weight", which is what "there
    ///         is no layer 3" means, where opaque is the failure above waiting for somebody to raise
    ///         <c>PaintedChannels</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The weights are resolved by <c>over</c> rather than copied straight out of the
    ///         masks, and that is what makes the closed-form oracle true.</b> The shader divides by
    ///         the total, so a bottom layer painted 1 everywhere — the ordinary base of a stack —
    ///         would tie with every layer above it and a surface an artist painted pure gravel would
    ///         come back half sand. Resolving top-down, <c>w[i] = c[i] · Π(1 − c[j])</c> over the
    ///         layers above it, is exactly what the stack itself composited: doc 48's oracle — a map
    ///         pure red over one half and pure green over the other rendering as layer 0's material
    ///         then layer 1's — holds under this rule and fails under a raw copy of the masks.
    ///     </para>
    /// </remarks>
    public static MaterialMapImage Splat(IReadOnlyList<Bitmap> coverage) {
        ArgumentNullException.ThrowIfNull(coverage);

        if (coverage.Count is 0 or > Channels) {
            throw new ArgumentException(
                "A splat map carries one to four layers, one per channel, and this asked for "
                + $"{coverage.Count.ToString(CultureInfo.InvariantCulture)}. A material with more "
                + "layers than that has nowhere to paint them — see "
                + "TexturedMaterialLayersFeature.PaintedChannels.",
                nameof(coverage)
            );
        }

        var (width, height) = Extent(coverage);
        var pixels = new byte[width * height * 4];

        for (var at = 0; at < pixels.Length; at += 4) {
            // What is left of the texel after every layer above the one being written has taken its
            // share. One rather than zero, because the top layer competes with nothing.
            var remaining = 1f;

            for (var layer = coverage.Count - 1; layer >= 0; layer--) {
                var painted = coverage[layer].Pixels[at] / 255f;

                pixels[at + layer] = Byte(painted * remaining);
                remaining *= 1f - painted;
            }
        }

        return Encoded(MaterialMapTarget.Splat, pixels, width, height);
    }

    /// <summary>How many layers one splat map can weigh, which is how many channels it has.</summary>
    const int Channels = 4;

    /// <summary>The one size every layer's coverage has to agree on.</summary>
    /// <remarks>
    ///     <see cref="Extent(System.Collections.Generic.IReadOnlyDictionary{MaterialMapUsage,Bitmap})" />'s
    ///     rule for the same reason: two coverages at different sizes came from different places, and
    ///     scaling one to meet the other would hide that behind a filtered weight nobody asked for.
    /// </remarks>
    static (int Width, int Height) Extent(IReadOnlyList<Bitmap> coverage) {
        var width = 0;
        var height = 0;

        for (var layer = 0; layer < coverage.Count; layer++) {
            var bitmap = coverage[layer];

            if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Pixels.Length < bitmap.Width * bitmap.Height * 4) {
                throw new ArgumentException(
                    $"Layer {layer.ToString(CultureInfo.InvariantCulture)}'s coverage is "
                    + $"{bitmap.Width.ToString(CultureInfo.InvariantCulture)}×"
                    + $"{bitmap.Height.ToString(CultureInfo.InvariantCulture)} and carries "
                    + $"{bitmap.Pixels.Length.ToString(CultureInfo.InvariantCulture)} bytes, which is not a picture.",
                    nameof(coverage)
                );
            }

            if (width == 0) {
                (width, height) = (bitmap.Width, bitmap.Height);
                continue;
            }

            if (bitmap.Width != width || bitmap.Height != height) {
                throw new ArgumentException(
                    $"Layer {layer.ToString(CultureInfo.InvariantCulture)}'s coverage is "
                    + $"{bitmap.Width.ToString(CultureInfo.InvariantCulture)}×"
                    + $"{bitmap.Height.ToString(CultureInfo.InvariantCulture)} and an earlier layer's is "
                    + $"{width.ToString(CultureInfo.InvariantCulture)}×"
                    + $"{height.ToString(CultureInfo.InvariantCulture)}. Every channel of one splat map is one "
                    + "texel grid, so they are one size.",
                    nameof(coverage)
                );
            }
        }

        return (width, height);
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
    ///     <para>
    ///         ⚠ <b>Parallax is the one exception to "the features are the bake's", and it is an
    ///         authoring decision rather than a rendering one.</b> A bake that wrote a height output
    ///         could compose a <see cref="ParallaxOcclusionFeature" /> for it — and would then put a
    ///         per-pixel march on <em>every</em> material any graph ever emitted a height map from,
    ///         which is a picture change and a cost nobody asked for. Writing the file and letting
    ///         the material ask is the safer half, so a parallax feature already on the material is
    ///         the author's, like the shading model: it survives the re-bake and is re-pointed at
    ///         the height map this bake wrote. See
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is re-seated at index 0, which is what makes the material compile.</b>
    ///         The feature declares <see cref="MaterialFeatureStage.Coordinate" /> and
    ///         <c>MaterialCompiler</c> refuses one listed behind a feature that samples — so
    ///         appending the base surface first and the author's feature after it produces a
    ///         <c>CoordinateFeatureOutOfOrder</c> on a file the bake itself wrote. The ordering rule
    ///         is doing its job; this is the bake agreeing with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is dropped when this bake wrote no height map</b>, which is the same rule the
    ///         emissive feature is under and matters more here: a parallax feature whose map is
    ///         unbound leaves <c>heightIndex</c> at zero, marches the bindless table's fallback
    ///         checker as a height field, and the surface swims. Losing the feature is visible;
    ///         keeping it is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exactly this feature, and not "every coordinate-stage feature".</b> The general
    ///         rule reads better and is the wrong one: it would preserve a future coordinate feature
    ///         whose own map this bake knows nothing about, into a material naming no texture for it
    ///         — the silent failure above rather than the visible one. A second sampling feature
    ///         wanting to survive a bake adds itself here, next to the map it reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A layered material is the second exception and it is a <em>different</em> shape,
    ///         because a layered surface <em>is</em> the base surface</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1118">#1118</a>. Parallax is preserved
    ///         <em>beside</em> what the bake composes; a <see cref="TexturedMaterialLayersFeature" />
    ///         preserved that way would sit next to the <see cref="TexturedMetalRoughnessFeature" />
    ///         this method always adds, and two base surfaces in one chain is not a refusal — both are
    ///         <see cref="MaterialFeatureStage.Surface" />, both compose, and the later one writes over
    ///         the earlier one's albedo, roughness and metalness. So it replaces the base surface
    ///         rather than joining it, and the base-colour <em>file</em> the bake wrote is then left
    ///         unbound on purpose: nothing in the chain samples <c>baseColorMap</c> any more, and
    ///         binding it anyway is the resident-and-unread shape #1103 was refused over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And its splat map is carried over from the material rather than written by this
    ///         bake, which is the whole of why the preservation is owed.</b> A splat map's channels
    ///         are the <em>material's layer indices</em> — see <see cref="MaterialMapTarget" /> — so
    ///         no graph output produces one and the bake has no file to bind. Before this rule a
    ///         re-bake of a hand-authored layered material replaced <c>Features</c> and
    ///         <c>Textures</c> whole: the feature and its <c>splatMap</c> entry both went, and what
    ///         came back was a plain textured metal-roughness material that shades perfectly and is
    ///         not the material the artist made.
    ///     </para>
    /// </remarks>
    public static MaterialContent Material(
        IReadOnlyDictionary<MaterialMapTarget, AssetReference> maps,
        MaterialContent? existing = null
    ) {
        ArgumentNullException.ThrowIfNull(maps);

        var features = new List<IMaterialFeature>();
        var textures = new List<MaterialTexture>();
        var parallax = Displacement(maps, existing);
        var carried = new List<MaterialTexture>();
        var layered = Layered(existing, carried);

        if (parallax is not null) {
            features.Add(parallax);
        }

        features.Add(
            layered
            ?? (maps.ContainsKey(MaterialMapTarget.BaseColor)
                ? new TexturedMetalRoughnessFeature()
                : (IMaterialFeature)new MetalRoughnessFeature())
        );

        if (maps.ContainsKey(MaterialMapTarget.Normal)) {
            features.Add(new TexturedNormalMapFeature());
        }

        // ⚠ Not behind a layered surface, and this is the same decision the base colour's is rather
        // than a second one. `TexturedOrmFeature` *assigns* `d.perceptualRoughness` and splits the
        // albedo by the map's metalness — so composed behind `TexturedMaterialLayersSurface` it
        // writes over the author's per-layer roughness outright, and splits a second time off a
        // diffuse the layered surface has already split, leaving a fully metallic layer computing
        // `f0` from black and shading as a dielectric. The bake's ORM is a *flattened* answer to the
        // question the per-layer values already answer, so the two cannot both be right and the
        // author's is the one nothing else can supply. ⚠ The neighbouring remark on this method's
        // metalness rule states the invariant this would break as a requirement rather than a
        // preference, and it is not one a compilation can refuse: the material compiles clean.
        //
        // ⚠ The cost is the baked *occlusion*, which the layered surface does not write and which
        // therefore has no route onto one of these materials at all — #1130.
        if (maps.ContainsKey(MaterialMapTarget.Orm) && layered is null) {
            features.Add(new TexturedOrmFeature());
        }

        if (maps.ContainsKey(MaterialMapTarget.Emissive)) {
            features.Add(new TexturedEmissiveFeature());
        }

        if (maps.ContainsKey(MaterialMapTarget.Opacity)) {
            features.Add(new TexturedOpacityFeature());
        }

        foreach (var target in MaterialMapNaming.EveryTarget) {
            // ⚠ **A map's parameter name is the target's answer except where a feature this material
            // carries decides it**, which is the general rule and the reason `MaterialMapNaming.Parameter`
            // cannot be the whole of it: that table answers "what does the feature that samples this
            // file call it", and *whether* anything samples it is a fact about the chain rather than
            // about the file. Four of the eight are decided here today and the list has grown twice
            // in one batch (#1132), so the rule is written rather than the cases.
            //
            // Height: sampled only by a `ParallaxOcclusionFeature` the author put there, under that
            // instance's own name. Base colour and the packed map: unbound once a layered surface has
            // replaced `TexturedMetalRoughnessFeature` and `TexturedOrmFeature`, because nothing then
            // samples `baseColorMap` or `ormMap` — two entries the build imports, a bundle carries
            // and a pool makes resident for no reader.
            var parameter = target switch {
                MaterialMapTarget.Height => parallax?.HeightMap,
                MaterialMapTarget.BaseColor or MaterialMapTarget.Orm when layered is not null => null,

                // ⚠ Never from here, even though `MaterialMapNaming.Parameter` names it. A graph bake
                // cannot write a splat map — `MaterialMapNaming.Packed` gives it no channels, so
                // `Encode` skips it — and the entry a layered material has is the author's, carried by
                // `Layered` below. Binding it here as well would put two `splatMap` entries in one
                // material, of which the host pairs whichever it reached last. `Splatted` is the one
                // route that writes this name, and it patches rather than composes.
                MaterialMapTarget.Splat => null,
                _ => MaterialMapNaming.Parameter(target)
            };

            if (maps.TryGetValue(target, out var reference) && parameter is not null) {
                textures.Add(new(parameter, reference));
            }
        }

        // ⚠ After the loop and not before it, so the maps this bake wrote keep the order
        // `EveryTarget` gives them and the carried ones are visibly the tail. They cannot collide:
        // a layered feature's two names are `splatMap` and `heightMap`, and the only conditional
        // name the loop can emit is `parallaxHeightMap` — deliberately not `heightMap`, which is
        // taken.
        textures.AddRange(carried);

        return new() {
            Shader = existing?.Shader ?? new MaterialContent().Shader,
            Shading = existing?.Shading ?? new MaterialContent().Shading,
            Features = [.. features],
            Textures = [.. textures]
        };
    }

    /// <summary>Binds a freshly written splat map onto the material that paints from it.</summary>
    /// <param name="existing">The material as it stands, which has to carry a layered surface.</param>
    /// <param name="splat">The splat map this write produced.</param>
    /// <param name="layers">How many layers it weighs, which is how many channels are painted.</param>
    /// <returns>The material to write back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="existing" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     The material carries no <see cref="TexturedMaterialLayersFeature" />, the reference is
    ///     null, or the count does not match the feature's layer list.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A patch and not a compose, which is the whole difference between this and
    ///         <see cref="Material" />.</b> A splat map is one file added to a material that already
    ///         exists — the shading model, the features, the other maps and their provenance are all
    ///         somebody else's — where <see cref="Material" /> replaces <c>Features</c> and
    ///         <c>Textures</c> whole because a graph bake owns the whole set. Sending a splat write
    ///         through that would drop every feature the layered material was carrying and prune
    ///         every map this write did not produce.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It refuses a material with no layered surface rather than adding one.</b> The
    ///         feature needs a layer list — colours, roughnesses, metalnesses — and nothing here has
    ///         one; a feature composed with an empty list is a material that compiles, resolves
    ///         <c>LayerCount</c> to one and shades every texel from a layer nobody authored.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="TexturedMaterialLayersFeature.PaintedChannels" /> is written from the
    ///         count rather than left alone, and it is a guard rather than a tidiness.</b> Its default
    ///         is three: a four-layer material whose map this write filled would otherwise paint three
    ///         of them, and — the silent half — a material left at four over a map this write gave
    ///         three layers reads an alpha this writer set to zero everywhere, which is a layer that
    ///         never appears and nothing to say why.
    ///     </para>
    ///     <para>
    ///         <b>The name is the feature's own default</b>, which is <c>MaterialMapNaming</c>'s
    ///         standing rule: a host pairs one shader slot with one material-side name and keys that
    ///         on the default, so a material spelling it anything else leaves <c>splatIndex</c> at
    ///         nought and blends its layers by the bindless table's magenta checker.
    ///     </para>
    /// </remarks>
    public static MaterialContent Splatted(MaterialContent existing, AssetReference splat, int layers) {
        ArgumentNullException.ThrowIfNull(existing);

        if (splat == AssetReference.Null) {
            throw new ArgumentException(
                "A splat map bound to nothing leaves splatIndex at nought, which is the bindless table's "
                + "fallback checker read as weights — a hard-edged chequerboard of the first three layers, "
                + "shaded perfectly.",
                nameof(splat)
            );
        }

        var paired = new TexturedMaterialLayersFeature();
        var features = new List<IMaterialFeature>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var found = false;

        foreach (var feature in existing.Features) {
            if (feature is not TexturedMaterialLayersFeature layered) {
                features.Add(feature);

                continue;
            }

            if (layered.Layers.Count != layers) {
                throw new ArgumentException(
                    $"This material lists {layered.Layers.Count.ToString(CultureInfo.InvariantCulture)} layers and "
                    + $"the map weighs {layers.ToString(CultureInfo.InvariantCulture)}. Channel i is layer i, so a "
                    + "map written for a different list paints the wrong layers — and every one of those pictures "
                    + "draws.",
                    nameof(layers)
                );
            }

            // The author's spelling of the map is collected so that the entry under it goes: a
            // material that renamed the map resolved nothing and sampled slot zero, and leaving the
            // old entry beside the paired one would keep that texture resident for no reader.
            names.Add(layered.SplatMap);
            features.Add(layered with { SplatMap = paired.SplatMap, PaintedChannels = layers });
            found = true;
        }

        if (!found) {
            throw new ArgumentException(
                "This material carries no TexturedMaterialLayersFeature, so there is no layer list for a splat "
                + "map's channels to be the weights of. Add the feature with its layers first: composing one here "
                + "would resolve LayerCount to a list nobody authored.",
                nameof(existing)
            );
        }

        names.Add(paired.SplatMap);

        var textures = existing.Textures
            .Where(texture => !names.Contains(texture.Parameter))
            .Append(new MaterialTexture(paired.SplatMap, splat))
            .ToArray();

        return existing with { Features = [.. features], Textures = textures };
    }

    /// <summary>The author's parallax feature, where this bake can still feed it.</summary>
    /// <param name="maps">What the bake wrote.</param>
    /// <param name="existing">The material as it already stood.</param>
    /// <returns>The feature to put back at the head of the chain, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>The instance the author wrote, not a fresh one</b> — <c>HeightScale</c> is in the
    ///     surface's own UV units and is the one number in the feature a graph cannot know, since the
    ///     depth that reads correctly depends on how the material tiles its maps. Re-baking with
    ///     <c>new ParallaxOcclusionFeature()</c> would silently reset an authored relief to the
    ///     default five hundredths, which is a look change with a bake in front of it.
    /// </remarks>
    static ParallaxOcclusionFeature? Displacement(
        IReadOnlyDictionary<MaterialMapTarget, AssetReference> maps,
        MaterialContent? existing
    ) {
        if (!maps.ContainsKey(MaterialMapTarget.Height)) {
            return null;
        }

        foreach (var feature in existing?.Features ?? []) {
            if (feature is ParallaxOcclusionFeature parallax) {
                // ⚠ The author's numbers and the feature's own map name. `HeightScale` is theirs to
                // keep; `HeightMap` is not, because it is half of a *pairing*: `WorldRenderer` keys
                // the height texture index on `new ParallaxOcclusionFeature().HeightMap`. Preserving
                // a renamed map would write a texture entry no feature ever looks up, leave the index
                // at nought, and march the fallback checker. ⚠ Since 2026-09-09 the compiler refuses
                // that at import (`MaterialDiagnosticId.RenamedTextureMap`), which does not make this
                // line redundant — it makes it upstream: a bake that handed an author a file the
                // compiler will reject is not an improvement on one that quietly drew wrong.
                return parallax with { HeightMap = new ParallaxOcclusionFeature().HeightMap };
            }
        }

        return null;
    }

    /// <summary>The author's layered surface, where the material still carries the map it paints from.</summary>
    /// <param name="existing">The material as it already stood.</param>
    /// <param name="carried">The texture entries the feature needs, appended to.</param>
    /// <returns>The base surface to compose instead of this bake's own, or null.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Bound by the author's own spelling and rebound under the paired one.</b> The
    ///         feature is looked up in <c>Textures</c> by the name the <em>feature instance</em>
    ///         carries, because that is what the author wrote on both halves; what goes back is
    ///         <see cref="TexturedMaterialLayersFeature.SplatMap" />'s default, because that is the
    ///         only name <c>WorldRenderer.Paired</c> keys the texture index on. A material that
    ///         renamed both halves consistently is bound and unread today, and comes back bound and
    ///         read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unbound splat map drops the feature, which is the parallax rule and not a new
    ///         one.</b> A layered surface whose <c>splatIndex</c> stays at nought reads the bindless
    ///         table's fallback checker as its weights: magenta and black are 1 and 0 in three
    ///         channels, so the material becomes a hard-edged chequerboard of its first three layers
    ///         and shades perfectly while doing it. Losing the feature is visible; keeping it is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A missing height map costs the permutation rather than the feature</b>, which is
    ///         the same argument one level down: <see cref="TexturedMaterialLayersFeature.HeightBlended" />
    ///         is a permutation whose false variant emits no second sample at all, so turning it off
    ///         is exactly the guard the feature's own remarks describe — where dropping the whole
    ///         feature over a map that only biases a seam would throw away the layers as well.
    ///     </para>
    /// </remarks>
    static TexturedMaterialLayersFeature? Layered(MaterialContent? existing, List<MaterialTexture> carried) {
        foreach (var feature in existing?.Features ?? []) {
            if (feature is not TexturedMaterialLayersFeature layers) {
                continue;
            }

            if (Bound(existing, layers.SplatMap) is not { } splat) {
                return null;
            }

            var paired = new TexturedMaterialLayersFeature();

            carried.Add(new(paired.SplatMap, splat));

            if (layers.HeightBlended && Bound(existing, layers.HeightMap) is { } height) {
                carried.Add(new(paired.HeightMap, height));

                return layers with { SplatMap = paired.SplatMap, HeightMap = paired.HeightMap };
            }

            return layers with {
                SplatMap = paired.SplatMap,
                HeightMap = paired.HeightMap,
                HeightBlended = false
            };
        }

        return null;
    }

    /// <summary>Which texture a material bound under a name, or null where it bound none.</summary>
    /// <param name="existing">The material as it already stood.</param>
    /// <param name="parameter">What the material calls the map.</param>
    /// <returns>The reference, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="AssetReference.Null" /> counts as none.</b> It is the zero of a struct, so
    ///     an entry left at it is a texture slot that resolves to nothing and samples the fallback —
    ///     the same picture as no entry at all, and this repository's most-repeated defect shape.
    /// </remarks>
    static AssetReference? Bound(MaterialContent? existing, string parameter) {
        foreach (var texture in existing?.Textures ?? []) {
            if (string.Equals(texture.Parameter, parameter, StringComparison.Ordinal)
                && texture.Texture != AssetReference.Null) {
                return texture.Texture;
            }
        }

        return null;
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

        return Encoded(target, pixels, width, height);
    }

    /// <summary>Composed texels, encoded the way their size and their target decided.</summary>
    /// <remarks>
    ///     ⚠ <b>Shared by the graph path and the splat path rather than copied</b>, because what a
    ///     second copy forgets is the pair of settings below: the mip flag that a container's own
    ///     chain makes a statement rather than an instruction, and the alpha weighting that only the
    ///     base colour has. A splat map whose chain was built alpha-weighted would fade every layer
    ///     the last channel does not cover.
    /// </remarks>
    static MaterialMapImage Encoded(MaterialMapTarget target, byte[] pixels, int width, int height) {
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
