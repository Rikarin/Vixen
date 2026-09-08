// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The step between what a graph produced and the files a material samples.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § M5, the half that needs no device.</b> Everything here is a pure
///         function of bitmaps a test wrote by hand, which is what lets the decisions that can be
///         wrong be asserted rather than assumed: which channel roughness goes in, what an absent
///         channel holds, which size crosses into a container, and — the one that fails as a picture
///         rather than as an error — what a material has to call each map.
///     </para>
///     <para>
///         ⚠ <b>A bake that named its maps freely would compile, import, draw and be wrong.</b>
///         <c>WorldRenderer.Paired</c> pairs one shader parameter with one material-side name and
///         keys that on the feature's own default, so a renamed map leaves the index at zero and the
///         surface is shaded by the bindless table's fallback checker. That is why
///         <see cref="Every_map_is_called_what_the_feature_that_samples_it_calls_it" /> reads the
///         names off the feature records instead of holding a list of its own.
///     </para>
/// </remarks>
public sealed class MaterialBakeTests {
    /// <summary>The names are the features', which is what the pairing is keyed on.</summary>
    [Fact]
    public void Every_map_is_called_what_the_feature_that_samples_it_calls_it() {
        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.BaseColor] = Reference(1),
                [MaterialMapTarget.Normal] = Reference(2),
                [MaterialMapTarget.Orm] = Reference(3),
                [MaterialMapTarget.Emissive] = Reference(4),
                [MaterialMapTarget.Opacity] = Reference(5)
            }
        );

        var named = material.Textures.Select(texture => texture.Parameter).ToArray();

        Assert.Contains(new TexturedMetalRoughnessFeature().BaseColorMap, named);
        Assert.Contains(new TexturedNormalMapFeature().NormalMap, named);
        Assert.Contains(new TexturedOrmFeature().OrmMap, named);
        Assert.Contains(new TexturedEmissiveFeature().EmissiveMap, named);
        Assert.Contains(new TexturedOpacityFeature().OpacityMap, named);

        // And every one of them is a map some feature in this same material asked for, which is the
        // other half: a texture nothing samples is as silent as a feature whose map is missing.
        Assert.Equal(5, named.Length);
        Assert.Equal(named.Length, named.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>And the material the bake writes is one the engine compiles.</summary>
    /// <remarks>
    ///     ⚠ <b><c>MaterialImporter</c> compiles a <c>.vxmat</c> to find out whether it is one</b>, so
    ///     this is the same question the content build will ask, asked here where the answer names
    ///     the bake rather than a file in somebody's project.
    /// </remarks>
    [Fact]
    public void The_material_a_bake_writes_compiles() {
        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.BaseColor] = Reference(1),
                [MaterialMapTarget.Normal] = Reference(2),
                [MaterialMapTarget.Orm] = Reference(3)
            }
        );

        Assert.True(MaterialShading.TryResolve(material.Shading, out var shading));

        var compilation = MaterialCompiler.Compile(material.ToDescriptor(shading));

        Assert.False(compilation.Failed, string.Join("; ", compilation.Diagnostics.Select(one => one.Message)));
    }

    /// <summary>A set with no base colour still has a surface for the ORM map to read out of.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="TexturedOrmFeature" /> reads the base albedo back out of the surface</b>,
    ///     so something has to have put one there — and a <see cref="TexturedMetalRoughnessFeature" />
    ///     naming a map that was never written resolves slot zero and shades the surface with the
    ///     table's fallback checker.
    /// </remarks>
    [Fact]
    public void A_set_without_a_base_colour_uses_the_untextured_workflow() {
        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> { [MaterialMapTarget.Orm] = Reference(3) }
        );

        Assert.Contains(material.Features, feature => feature is MetalRoughnessFeature);
        Assert.DoesNotContain(material.Features, feature => feature is TexturedMetalRoughnessFeature);
        Assert.Single(material.Textures);

        // ⚠ And its metalness is zero, which TexturedOrmFeature requires rather than prefers: at any
        // other value the albedo has already been split between diffuse and f0 by a factor the map
        // cannot see.
        Assert.Equal(0f, material.Features.OfType<MetalRoughnessFeature>().Single().Metalness);
    }

    /// <summary>The shading model is the author's and survives a re-bake; the features are not.</summary>
    [Fact]
    public void A_re_bake_keeps_the_shading_model_and_replaces_the_features() {
        var existing = new MaterialContent {
            Shading = "SubsurfaceShading",
            Features = [new TexturedEmissiveFeature()],
            Textures = [new("emissiveMap", Reference(9))]
        };

        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> { [MaterialMapTarget.BaseColor] = Reference(1) },
            existing
        );

        Assert.Equal("SubsurfaceShading", material.Shading);

        // The graph stopped producing an emissive output, so the feature that reads one goes with it.
        // Leaving it behind would be a material sampling a map nothing writes any more.
        Assert.DoesNotContain(material.Features, feature => feature is TexturedEmissiveFeature);
        Assert.Single(material.Textures);
    }

    /// <summary>An author's parallax feature survives a re-bake, at the head of the chain.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The authoring half of
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>.</b> A bake writes a
    ///         height file for any graph with a height output; whether anything marches it is the
    ///         material's answer, because the alternative is a per-pixel cost on every material a
    ///         height output ever came out of.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The compile is the assertion, not the index.</b>
    ///         <see cref="MaterialFeatureStage.Coordinate" /> has to be first and
    ///         <c>MaterialCompiler</c> is what says so; appending the preserved feature after the
    ///         base surface — which is what the naive re-add does, since the base surface is added
    ///         first — writes a <c>.vxmat</c> the content build then refuses. Asserting the index
    ///         alone would pass with the ordering rule deleted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the author's own <c>HeightScale</c> comes back.</b> It is in the surface's UV
    ///         units, so it depends on the material's tiling and no graph can know it; a re-bake that
    ///         handed back a fresh feature would reset an authored relief to the default and look
    ///         like the bake having changed the maps.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_re_bake_keeps_the_authors_parallax_and_binds_the_height_map_to_it() {
        var existing = new MaterialContent {
            Features = [new TexturedMetalRoughnessFeature(), new ParallaxOcclusionFeature { HeightScale = 0.02f }]
        };

        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.BaseColor] = Reference(1),
                [MaterialMapTarget.Normal] = Reference(2),
                [MaterialMapTarget.Orm] = Reference(3),
                [MaterialMapTarget.Height] = Reference(4)
            },
            existing
        );

        Assert.True(MaterialShading.TryResolve(material.Shading, out var shading));

        var compilation = MaterialCompiler.Compile(material.ToDescriptor(shading));

        Assert.False(compilation.Failed, string.Join("; ", compilation.Diagnostics.Select(one => one.Message)));

        var parallax = Assert.IsType<ParallaxOcclusionFeature>(material.Features[0]);

        Assert.Equal(0.02f, parallax.HeightScale);

        // ⚠ Against the *default* and not against `parallax.HeightMap`. Comparing the entry with the
        // instance the entry's name was copied from holds for any spelling, so it would pass against
        // a material that renamed the map — which is the one arrangement `WorldRenderer.Paired`
        // cannot key on, since it pairs on the default alone.
        var bound = Assert.Single(
            material.Textures,
            texture => texture.Parameter == new ParallaxOcclusionFeature().HeightMap
        );

        Assert.Equal(Reference(4), bound.Texture);
        Assert.Equal(4, material.Textures.Length);
    }

    /// <summary>⚠ A renamed height map is put back to the name the feature is paired on.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one thing the preservation rule may not preserve.</b> Everything else in the
    ///         author's <c>ParallaxOcclusionFeature</c> comes back untouched, because the numbers are
    ///         theirs. The map's <em>name</em> is not a number, it is half of a pairing:
    ///         <c>WorldRenderer</c> asks for the height texture index under
    ///         <c>new ParallaxOcclusionFeature().HeightMap</c>, so a material spelling it anything
    ///         else binds a texture nothing looks up, keeps <c>heightIndex</c> at nought and marches
    ///         the fallback checker.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing refuses the rename on the way in.</b> The two sampling-feature tests that
    ///         look like they would both assert over <c>new ParallaxOcclusionFeature()</c> defaults
    ///         rather than over authored content, and the guide page's "a material may not rename its
    ///         maps" is a rule with no enforcement behind it — which is why re-seating it here is the
    ///         fix and not a diagnostic.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_re_bake_puts_a_renamed_height_map_back_to_the_name_the_feature_is_paired_on() {
        var existing = new MaterialContent {
            Features = [new ParallaxOcclusionFeature { HeightScale = 0.02f, HeightMap = "heightMap" }]
        };

        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.BaseColor] = Reference(1),
                [MaterialMapTarget.Height] = Reference(4)
            },
            existing
        );

        var parallax = Assert.IsType<ParallaxOcclusionFeature>(material.Features[0]);

        // The author's number survives and their spelling does not.
        Assert.Equal(0.02f, parallax.HeightScale);
        Assert.Equal(new ParallaxOcclusionFeature().HeightMap, parallax.HeightMap);

        var bound = Assert.Single(
            material.Textures,
            texture => texture.Parameter == new ParallaxOcclusionFeature().HeightMap
        );

        Assert.Equal(Reference(4), bound.Texture);

        // And nothing was bound under the author's name, which is what would have been resident and
        // unread.
        Assert.DoesNotContain(material.Textures, texture => texture.Parameter == "heightMap");
    }

    /// <summary>A material that never asked for parallax does not get it, and keeps its file.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the half of the decision that is easy to lose.</b> Returning a name for
    ///     <see cref="MaterialMapTarget.Height" /> unconditionally would put a texture entry on every
    ///     baked material with a height output — bytes the build imports, a bundle carries and a pool
    ///     makes resident, that no feature in the material samples.
    /// </remarks>
    [Fact]
    public void A_bake_composes_no_parallax_for_a_material_that_did_not_ask() {
        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.BaseColor] = Reference(1),
                [MaterialMapTarget.Height] = Reference(4)
            }
        );

        Assert.DoesNotContain(material.Features, feature => feature is ParallaxOcclusionFeature);
        Assert.Single(material.Textures);
        Assert.Equal(new TexturedMetalRoughnessFeature().BaseColorMap, material.Textures[0].Parameter);
    }

    /// <summary>And it goes when the graph stops producing the map it marches.</summary>
    /// <remarks>
    ///     ⚠ <b>The same rule the emissive feature is under, and it matters more here.</b> A parallax
    ///     feature whose map is unbound leaves <c>heightIndex</c> at zero, reads the bindless table's
    ///     fallback checker as a height field and swims — a wrong picture with nothing to say why.
    ///     Losing the feature is a look an artist can see and act on.
    /// </remarks>
    [Fact]
    public void A_bake_that_wrote_no_height_map_drops_the_parallax_it_could_not_feed() {
        var existing = new MaterialContent {
            Features = [new ParallaxOcclusionFeature()],
            Textures = [new(new ParallaxOcclusionFeature().HeightMap, Reference(4))]
        };

        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> { [MaterialMapTarget.BaseColor] = Reference(1) },
            existing
        );

        Assert.DoesNotContain(material.Features, feature => feature is ParallaxOcclusionFeature);
        Assert.DoesNotContain(
            material.Textures,
            texture => texture.Parameter == new ParallaxOcclusionFeature().HeightMap
        );
    }

    /// <summary>Two of the nine usages bind to no feature, and are written anyway.</summary>
    /// <remarks>
    ///     A mask is § 4.10's input to another graph rather than anything a material samples, and a
    ///     bake that dropped it because nothing binds it would lose the file an artist asked for.
    ///     <para>
    ///         ⚠ <b>"Height has no textured runtime feature" is what this remark used to say, and it
    ///         stopped being true without the conclusion changing.</b>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/615">#615</a> closed on height-blended
    ///         layering, so <c>TexturedMaterialLayersFeature.HeightMap</c> is a textured runtime
    ///         feature that reads a height map — a <em>four-channel per-layer</em> one, channel
    ///         <c>i</c> being layer <c>i</c>'s height. What a bake writes is one material's single
    ///         channel. Two different textures with one English name, which is how a reader who checks
    ///         the old claim finds the layered feature and concludes the assertion below is the stale
    ///         half. It is not: <see cref="MaterialMapNaming.Parameter" /> returning null for
    ///         <see cref="MaterialMapTarget.Height" /> is still right.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it stayed right after
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1065">#1065</a> landed a feature that
    ///         reads exactly this map.</b> A height map binds when the material carries a
    ///         <see cref="ParallaxOcclusionFeature" /> and not when the bake wrote the file, so the
    ///         binding lives in <see cref="MaterialBake.Material" /> rather than in the naming table —
    ///         see <see cref="A_bake_composes_no_parallax_for_a_material_that_did_not_ask" />, which
    ///         is this test one map along.
    ///         ⚠ <a href="https://github.com/Rikarin/Vixen/issues/1067">#1067</a>, the third reading
    ///         of the word, was closed on 2026-09-08 by deciding it rather than by building it:
    ///         displacement moves vertices and no material feature can.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_maps_no_feature_samples_are_still_written() {
        var images = MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> {
                [MaterialMapUsage.Height] = Flat(4, 200),
                [MaterialMapUsage.Mask] = Flat(4, 30)
            }
        );

        Assert.Equal(2, images.Count);
        Assert.Null(MaterialMapNaming.Parameter(MaterialMapTarget.Height));
        Assert.Null(MaterialMapNaming.Parameter(MaterialMapTarget.Mask));

        var material = MaterialBake.Material(
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.Height] = Reference(1),
                [MaterialMapTarget.Mask] = Reference(2)
            }
        );

        Assert.Empty(material.Textures);
    }

    /// <summary>Occlusion, roughness and metalness are R, G and B — the order the feature reads.</summary>
    [Fact]
    public void The_packed_map_is_occlusion_roughness_metalness() {
        var images = MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> {
                [MaterialMapUsage.Occlusion] = Flat(4, 10),
                [MaterialMapUsage.Roughness] = Flat(4, 20),
                [MaterialMapUsage.Metalness] = Flat(4, 30)
            }
        );

        var orm = Assert.Single(images);

        Assert.Equal(MaterialMapTarget.Orm, orm.Target);

        var decoded = PngCodec.Decode(orm.Bytes);

        Assert.Equal(10, decoded.Pixels[0]);
        Assert.Equal(20, decoded.Pixels[1]);
        Assert.Equal(30, decoded.Pixels[2]);
        Assert.Equal(byte.MaxValue, decoded.Pixels[3]);
    }

    /// <summary>A channel the graph did not produce takes the value the engine already means by it.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is a valid-looking value for all three and the wrong one for two.</b> A graph
    ///     that outputs roughness alone and got zeros for the rest would write a fully occluded
    ///     conductor: a material that is black and shades, with nothing anywhere to say why.
    /// </remarks>
    [Fact]
    public void An_absent_packed_channel_is_not_zero() {
        var images = MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> { [MaterialMapUsage.Roughness] = Flat(4, 20) }
        );

        var decoded = PngCodec.Decode(Assert.Single(images).Bytes);

        Assert.Equal(byte.MaxValue, decoded.Pixels[0]);
        Assert.Equal(20, decoded.Pixels[1]);
        Assert.Equal(0, decoded.Pixels[2]);

        // And the values are the runtime features' own, rather than three numbers chosen here.
        Assert.Equal(new OcclusionFeature().OcclusionMap, MaterialMapNaming.Absent(MaterialMapUsage.Occlusion));
        Assert.Equal(new MetalRoughnessFeature().Roughness, MaterialMapNaming.Absent(MaterialMapUsage.Roughness));
        Assert.Equal(new MetalRoughnessFeature().Metalness, MaterialMapNaming.Absent(MaterialMapUsage.Metalness));
    }

    /// <summary>Colour is two of the seven, and the other five are read as data.</summary>
    /// <remarks>
    ///     ⚠ Applying a transfer function to a roughness map bends the whole material response, and
    ///     it is the failure that looks like a lighting bug for a week.
    /// </remarks>
    [Fact]
    public void Only_the_two_colour_maps_are_srgb() {
        foreach (var target in MaterialMapNaming.EveryTarget) {
            var expected = target is MaterialMapTarget.BaseColor or MaterialMapTarget.Emissive
                ? TextureContent.Colour
                : target is MaterialMapTarget.Normal
                    ? TextureContent.NormalMap
                    : TextureContent.Linear;

            Assert.Equal(expected, MaterialMapNaming.ContentOf(target));
        }
    }

    /// <summary>A single-channel map is grey, and its alpha is not its value.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="TexturedOpacityFeature" /> reads red and not alpha</b>, because a
    ///     one-channel texture samples alpha as 1 — a feature that read it would make every mask
    ///     fully opaque and every cutout material solid.
    /// </remarks>
    [Fact]
    public void A_one_channel_map_is_written_grey_and_opaque() {
        var images = MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> { [MaterialMapUsage.Opacity] = Flat(4, 77) }
        );

        var decoded = PngCodec.Decode(Assert.Single(images).Bytes);

        Assert.Equal(77, decoded.Pixels[0]);
        Assert.Equal(77, decoded.Pixels[1]);
        Assert.Equal(77, decoded.Pixels[2]);
        Assert.Equal(byte.MaxValue, decoded.Pixels[3]);
    }

    /// <summary>The base colour keeps its alpha, because that alpha is coverage.</summary>
    [Fact]
    public void The_base_colour_keeps_its_alpha() {
        var pixels = new byte[4 * 4 * 4];

        for (var at = 0; at < pixels.Length; at += 4) {
            pixels[at] = 1;
            pixels[at + 1] = 2;
            pixels[at + 2] = 3;
            pixels[at + 3] = 4;
        }

        var images = MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> { [MaterialMapUsage.BaseColor] = new(4, 4, pixels) }
        );

        var image = Assert.Single(images);
        var decoded = PngCodec.Decode(image.Bytes);

        Assert.Equal(4, decoded.Pixels[3]);
        Assert.True(image.Settings.AlphaIsTransparency);
    }

    /// <summary>Up to 2K is a PNG the importer mips; above it is a container that already is.</summary>
    /// <remarks>
    ///     ⚠ <b>2048 is a PNG and 2049 is a container</b>, which is doc 48 § D4's "over 2K" read as
    ///     the exclusive ceiling it is: 2K is the size most sets are authored at, and a ceiling that
    ///     caught it would put the ordinary case on the exceptional path.
    /// </remarks>
    [Fact]
    public void The_size_chooses_the_container_and_2048_is_still_a_png() {
        Assert.Equal(MaterialMapNaming.PortableExtension, MaterialMapNaming.ExtensionFor(2048, 2048));
        Assert.Equal(MaterialMapNaming.ContainerExtension, MaterialMapNaming.ExtensionFor(2049, 2048));
        Assert.Equal(MaterialMapNaming.ContainerExtension, MaterialMapNaming.ExtensionFor(2048, 4096));
    }

    /// <summary>A container carries its whole chain, block-compressed, and the importer copies it through.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument first: an uncompressed container would be decoded and re-encoded by
    ///         the importer</b>, which is the path this exists to avoid, and it would still be a KTX2 —
    ///         so the assertion is on the format and the level count rather than on the extension.
    ///     </para>
    ///     <para>
    ///         The map is a wide strip rather than a square, because what has to be over the limit is
    ///         one edge and a 2064² BC7 encode is twenty seconds of a unit test.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_map_over_the_limit_ships_mipped_and_compressed() {
        var images = MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> { [MaterialMapUsage.Opacity] = Strip(8) }
        );

        var image = Assert.Single(images);

        Assert.Equal(MaterialMapNaming.ContainerExtension, image.Extension);

        var texture = Ktx2.Read(image.Bytes);

        Assert.True(texture.Format.IsCompressed(), texture.Format.ToString());
        Assert.Equal(PixelFormat.Bc4RUNorm, texture.Format);
        Assert.True(texture.LevelCount > 1, "a container with one level is a container with no chain");
        Assert.False(image.Settings.GenerateMips);
    }

    /// <summary>A colour map's container is sRGB, so the hardware converts what a person authored.</summary>
    [Fact]
    public void A_colour_container_keeps_its_transfer_function() {
        var images = MaterialBake.Encode(
            new Dictionary<MaterialMapUsage, Bitmap> { [MaterialMapUsage.BaseColor] = Strip(128) }
        );

        Assert.True(Ktx2.Read(Assert.Single(images).Bytes).Format.IsSrgb());
    }

    /// <summary>Two outputs at two sizes are refused rather than resampled.</summary>
    [Fact]
    public void A_set_whose_outputs_disagree_about_size_is_refused() {
        var failure = Assert.Throws<ArgumentException>(
            () => MaterialBake.Encode(
                new Dictionary<MaterialMapUsage, Bitmap> {
                    [MaterialMapUsage.BaseColor] = Flat(8, 1),
                    [MaterialMapUsage.Roughness] = Flat(4, 1)
                }
            )
        );

        Assert.Contains("one size", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A bake with nothing in it is refused, rather than writing an empty material.</summary>
    [Fact]
    public void A_set_with_no_outputs_is_refused() =>
        Assert.Throws<ArgumentException>(() => MaterialBake.Encode(new Dictionary<MaterialMapUsage, Bitmap>()));

    /// <summary>Every usage names a file, and every file names its channels back.</summary>
    /// <remarks>
    ///     The instrument, and the reason it is worth a test of its own: a usage added to the enum
    ///     without a home lands in whichever switch arm is written last, silently.
    /// </remarks>
    [Fact]
    public void Every_usage_lands_in_a_file_that_claims_it() {
        foreach (var usage in MaterialMapNaming.Every) {
            Assert.Contains(usage, MaterialMapNaming.Packed(MaterialMapNaming.TargetOf(usage)));
            Assert.True(MaterialMapNaming.TryParseSuffix(MaterialMapNaming.Suffix(usage), out var read));
            Assert.Equal(usage, read);
        }

        foreach (var target in MaterialMapNaming.EveryTarget) {
            foreach (var usage in MaterialMapNaming.Packed(target)) {
                Assert.Equal(target, MaterialMapNaming.TargetOf(usage));
            }
        }
    }

    static AssetReference Reference(int seed) => new(new AssetId(Guid.Parse($"{seed:D8}-0000-0000-0000-000000000000")));

    /// <summary>A square whose every channel is one value, which is enough for a packing assertion.</summary>
    static Bitmap Flat(int side, byte value) => Filled(side, side, value);

    /// <summary>A strip one edge of which is over <see cref="MaterialMapNaming.PortableLimit" />.</summary>
    static Bitmap Strip(byte value) => Filled(MaterialMapNaming.PortableLimit + 16, 16, value);

    static Bitmap Filled(int width, int height, byte value) {
        var pixels = new byte[width * height * 4];

        Array.Fill(pixels, value);

        return new(width, height, pixels);
    }
}
