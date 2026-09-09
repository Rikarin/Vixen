// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Assets.Textures;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The weights a layered material paints from, packed into one file.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § M11 and <a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>,
///         the half that needs no device.</b> Everything here is a pure function of bitmaps a test
///         wrote by hand — which matters more for this map than for any other, because every way of
///         getting it wrong <em>draws</em>: a permuted channel is a plausible surface of the wrong
///         layers, an invented alpha is a surface entirely of the last one, and raw masks copied
///         straight across are a surface of everything at once.
///     </para>
///     <para>
///         ⚠ <b>The oracle is doc 48's own, and it is closed-form rather than a picture somebody
///         looked at</b>: a map pure red over one half of a surface and pure green over the other has
///         to render as layer 0's material over the first half and layer 1's over the second. That is
///         what <see cref="Layer_zero_is_red_and_a_layer_over_another_takes_the_texel" /> asserts on
///         the writer's side of the seam.
///     </para>
/// </remarks>
public sealed class MaterialSplatTests {
    /// <summary>⚠ Layer 0 is red, and a layer above another takes the texel rather than tying with it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48's oracle, in the direction that fails.</b> Two layers: the bottom one covers
    ///         everything, as the base of a stack does, and the top one covers the right half. The
    ///         map has to come back pure red on the left and pure green on the right.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves of that are a decision the writer could have got wrong and neither
    ///         would report anything.</b> Packed in the other order the left texel is green — a lit
    ///         surface of the wrong layer. Packed as the masks stand, without resolving them by
    ///         <c>over</c>, the right texel is <c>(255, 255)</c> and the shader — which divides by the
    ///         sum of the weights — blends the two layers half and half exactly where the artist
    ///         painted one of them solid.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Layer_zero_is_red_and_a_layer_over_another_takes_the_texel() {
        var packed = Decoded(MaterialBake.Splat([Everywhere(), RightHalf()]));

        Assert.Equal([255, 0, 0, 0], At(packed, Left));
        Assert.Equal([0, 255, 0, 0], At(packed, Right));
    }

    /// <summary>⚠ The alpha is layer 3's weight, and copying the packed path would make it padding.</summary>
    /// <remarks>
    ///     <b>The widest wrong picture this map can draw.</b> <c>MaterialBake.Encode</c> writes
    ///     <c>byte.MaxValue</c> into the alpha of every map it makes and is right to: no feature
    ///     reads one. A splat map written that way weights layer 3 at one over the whole surface, and
    ///     the shader's <c>1/total</c> normalisation then makes the material entirely layer 3 — see
    ///     <see cref="TexturedMaterialLayersFeature.PaintedChannels" />, which documents the same
    ///     failure arriving through the map's channel count instead of through the writer.
    /// </remarks>
    [Fact]
    public void The_alpha_is_layer_threes_weight_and_never_padding() {
        var painted = Decoded(MaterialBake.Splat([Everywhere(), Nowhere(), Nowhere(), RightHalf()]));

        // Layer 3 covers the right half, so the alpha is its weight there and nothing on the left —
        // where an alpha of 255 would have been the whole surface after normalisation.
        Assert.Equal(0, At(painted, Left)[3]);
        Assert.Equal(255, At(painted, Right)[3]);

        // And a stack that does not reach the fourth channel leaves it at no weight rather than at
        // opaque, so raising PaintedChannels by hand is a layer that never shows rather than a layer
        // that takes the surface.
        var three = Decoded(MaterialBake.Splat([Everywhere(), RightHalf(), Nowhere()]));

        Assert.Equal(0, At(three, Left)[3]);
        Assert.Equal(0, At(three, Right)[3]);
    }

    /// <summary>The channels of a texel sum to what was painted, not to that plus an invented alpha.</summary>
    /// <remarks>
    ///     ⚠ <b>The property the <c>over</c> resolution buys, stated as arithmetic rather than as a
    ///     colour.</b> The shader divides by the sum, so the sum is the one number that decides
    ///     whether the normalised blend is the picture the stack composited. A bottom layer covering
    ///     everything makes it exactly one texel's worth, whatever the layers above it do.
    /// </remarks>
    [Fact]
    public void The_weights_of_a_covered_texel_sum_to_one() {
        var packed = Decoded(MaterialBake.Splat([Everywhere(), RightHalf(), Half(), Nowhere()]));

        foreach (var texel in new[] { Left, Right }) {
            var weights = At(packed, texel);

            // ±2 of 255, which is the rounding four bytes can carry between them and not a tolerance
            // on the arithmetic: every weight is quantised once, on the way into a byte.
            Assert.InRange(weights[0] + weights[1] + weights[2] + weights[3], 253, 257);
        }
    }

    /// <summary>A partly painted layer keeps its own weight and leaves the rest to what is under it.</summary>
    /// <remarks>
    ///     The middle of the range, where a rule that merely clamped or replaced would still look
    ///     right at 0 and 1. Layer 1 painted at half over a base that covers everything is half its
    ///     own and half the base's.
    /// </remarks>
    [Fact]
    public void A_half_painted_layer_splits_the_texel_with_the_one_beneath_it() {
        var weights = At(Decoded(MaterialBake.Splat([Everywhere(), Half()])), Left);

        Assert.InRange(weights[0], 126, 129);
        Assert.InRange(weights[1], 126, 129);
    }

    /// <summary>A splat map carries one to four layers, and anything else is refused.</summary>
    [Fact]
    public void A_stack_with_no_layers_or_more_than_four_is_refused() {
        Assert.Throws<ArgumentException>(() => MaterialBake.Splat([]));
        Assert.Throws<ArgumentException>(
            () => MaterialBake.Splat([Everywhere(), Everywhere(), Everywhere(), Everywhere(), Everywhere()])
        );
    }

    /// <summary>And two coverages at different sizes are refused rather than resampled.</summary>
    [Fact]
    public void Two_coverages_of_different_sizes_are_refused() =>
        Assert.Throws<ArgumentException>(
            () => MaterialBake.Splat([Everywhere(), new Bitmap(4, 4, new byte[4 * 4 * 4])])
        );

    /// <summary>⚠ No graph bake can write a splat map, which is #1118's decision as code.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument for the decision rather than a restatement of it.</b>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1118">#1118</a> resolved that a splat
    ///         map is not a bake output because its channels are one material's layer indices and no
    ///         <see cref="MaterialMapUsage" /> can name an ordinal.
    ///         <see cref="MaterialMapTarget.Splat" /> is nonetheless a member of the file vocabulary,
    ///         so what has to stay true is that a graph — however many <c>Output</c> nodes it has —
    ///         cannot fill one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted over <em>every</em> usage rather than over a plausible set</b>, because
    ///         the way this would break is a usage added later whose <c>TargetOf</c> arm names the
    ///         splat map: <see cref="MaterialMapNaming.Packed" /> would then be non-empty for it and
    ///         <see cref="MaterialBake.Encode" /> would start writing one, silently, from weights no
    ///         layer list ever ordered.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_graph_bake_cannot_write_a_splat_map() {
        Assert.Empty(MaterialMapNaming.Packed(MaterialMapTarget.Splat));

        var every = MaterialMapNaming.Every.ToDictionary(usage => usage, _ => Everywhere());
        var written = MaterialBake.Encode(every);

        // ⚠ The instrument first: `DoesNotContain` is true of an empty list, so an `Encode` that
        // stopped producing anything would satisfy the assertion below while proving nothing. Every
        // target but the splat map is written when every usage is supplied.
        Assert.Equal(MaterialMapNaming.EveryTarget.Count - 1, written.Count);
        Assert.DoesNotContain(written, image => image.Target == MaterialMapTarget.Splat);
        Assert.DoesNotContain(MaterialMapNaming.Every, usage => MaterialMapNaming.TargetOf(usage) is
            MaterialMapTarget.Splat);
    }

    /// <summary>The splat map ships as four linear channels, which is neither of the neighbours' answer.</summary>
    /// <remarks>
    ///     ⚠ <c>Mask</c>'s BC4 would throw three of the four weights away and <c>BaseColor</c>'s sRGB
    ///     would bend every one of them through a transfer function — the failure that looks like a
    ///     lighting bug for a week.
    /// </remarks>
    [Fact]
    public void The_splat_map_is_linear_and_ships_in_four_channels() {
        Assert.Equal(TextureContent.Linear, MaterialMapNaming.ContentOf(MaterialMapTarget.Splat));
        Assert.Equal(TextureCompression.Bc7, MaterialMapNaming.CompressionOf(MaterialMapTarget.Splat));
        Assert.Equal("splat", MaterialMapNaming.Suffix(MaterialMapTarget.Splat));

        // Read off the feature, never typed — a material spelling it anything else leaves splatIndex
        // at nought and blends its layers by the bindless table's magenta checker.
        Assert.Equal(
            new TexturedMaterialLayersFeature().SplatMap,
            MaterialMapNaming.Parameter(MaterialMapTarget.Splat)
        );
    }

    /// <summary>Binding one onto a material keeps everything the material already had.</summary>
    /// <remarks>
    ///     ⚠ <b>The difference between <c>Splatted</c> and <c>Material</c>, which is the whole reason
    ///     the splat write is a second path.</b> A graph bake replaces <c>Features</c> and
    ///     <c>Textures</c> whole because it owns the set; a splat write adds one file to a material
    ///     somebody else authored, so the normal map, the emissive and the shading model all have to
    ///     survive it.
    /// </remarks>
    [Fact]
    public void Binding_a_splat_map_keeps_the_rest_of_the_material() {
        var patched = MaterialBake.Splatted(Layered(3), Reference(9), 3);

        Assert.Contains(patched.Features, feature => feature is TexturedNormalMapFeature);
        Assert.Contains(patched.Textures, texture => texture.Parameter == new TexturedNormalMapFeature().NormalMap);

        var splat = Assert.Single(
            patched.Textures,
            texture => texture.Parameter == new TexturedMaterialLayersFeature().SplatMap
        );

        Assert.Equal(Reference(9), splat.Texture);
    }

    /// <summary>⚠ And it writes the painted-channel count, which is the guard rather than a tidiness.</summary>
    /// <remarks>
    ///     <see cref="TexturedMaterialLayersFeature.PaintedChannels" /> defaults to three. A material
    ///     with four layers left at that default paints three of them and is told so; the silent half
    ///     is a material at four over a map this writer gave three layers, whose alpha is zero
    ///     everywhere and whose fourth layer therefore never appears with nothing to say why.
    /// </remarks>
    [Fact]
    public void Binding_a_splat_map_says_how_many_channels_it_painted() {
        foreach (var count in new[] { 1, 2, 3, 4 }) {
            var feature = Assert.Single(
                MaterialBake.Splatted(Layered(count), Reference(9), count).Features
                    .OfType<TexturedMaterialLayersFeature>()
            );

            Assert.Equal(count, feature.PaintedChannels);
        }
    }

    /// <summary>A material renaming the map is rebound under the one name a host pairs.</summary>
    /// <remarks>
    ///     ⚠ <b>And the author's entry goes rather than staying beside the new one.</b> Two entries
    ///     under two names is a texture the build imports, a bundle carries and a pool makes resident
    ///     for a reader that does not exist — and the compiler refuses the rename anyway
    ///     (<see cref="MaterialDiagnosticId.RenamedTextureMap" />), so leaving it would hand the
    ///     author a file that fails at import.
    /// </remarks>
    [Fact]
    public void A_renamed_splat_map_is_rebound_under_the_paired_name() {
        var renamed = Layered(2) with {
            Features = [new TexturedMaterialLayersFeature { Layers = [new(), new()], SplatMap = "weights" }],
            Textures = [new MaterialTexture("weights", Reference(4))]
        };

        var patched = MaterialBake.Splatted(renamed, Reference(9), 2);

        Assert.Equal(new TexturedMaterialLayersFeature().SplatMap, Assert.Single(patched.Textures).Parameter);
        Assert.Equal(Reference(9), Assert.Single(patched.Textures).Texture);
    }

    /// <summary>A material with no layered surface has no layer list, so there is nothing to weigh.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than composed, because composing one needs a layer list nothing here
    ///     has.</b> A <see cref="TexturedMaterialLayersFeature" /> with an empty list compiles,
    ///     resolves <c>LayerCount</c> to one, and shades every texel from a layer nobody authored.
    /// </remarks>
    [Fact]
    public void A_material_with_no_layered_surface_is_refused() =>
        Assert.Throws<ArgumentException>(
            () => MaterialBake.Splatted(new MaterialContent(), Reference(9), 2)
        );

    /// <summary>And a map written for a different number of layers is refused.</summary>
    /// <remarks>
    ///     Channel <c>i</c> is layer <c>i</c>, so a three-channel map on a four-layer material paints
    ///     the first three and leaves the fourth to an alpha that is zero — and a four-channel map on
    ///     a three-layer material paints a layer the material does not have. Both draw.
    /// </remarks>
    [Fact]
    public void A_map_written_for_a_different_layer_list_is_refused() =>
        Assert.Throws<ArgumentException>(() => MaterialBake.Splatted(Layered(3), Reference(9), 2));

    /// <summary>A splat map bound to nothing is the fallback checker read as weights.</summary>
    [Fact]
    public void A_splat_map_bound_to_nothing_is_refused() =>
        Assert.Throws<ArgumentException>(() => MaterialBake.Splatted(Layered(2), AssetReference.Null, 2));

    /// <summary>The texel this test's pictures call the left half.</summary>
    const int Left = 1;

    /// <summary>And the right.</summary>
    const int Right = 6;

    /// <summary>How wide every coverage here is. Eight, so the halves are whole texels.</summary>
    const int Side = 8;

    /// <summary>The bytes of one texel of a row of the packed map.</summary>
    static byte[] At(Bitmap packed, int column) =>
        packed.Pixels[(column * 4)..(column * 4 + 4)];

    /// <summary>The written file, read back as pixels.</summary>
    /// <remarks>
    ///     Through the PNG rather than off the pixels the packer made, so that what is asserted is
    ///     the file a project gets — an encoder that dropped the alpha would otherwise pass every
    ///     assertion here.
    /// </remarks>
    static Bitmap Decoded(MaterialMapImage image) {
        Assert.Equal(MaterialMapTarget.Splat, image.Target);
        Assert.Equal(MaterialMapNaming.PortableExtension, image.Extension);

        return PngCodec.Decode(image.Bytes);
    }

    /// <summary>A layer painted over the whole surface — the base of an ordinary stack.</summary>
    static Bitmap Everywhere() => Painted(_ => 255);

    /// <summary>One painted nowhere.</summary>
    static Bitmap Nowhere() => Painted(_ => 0);

    /// <summary>One painted over the right half.</summary>
    static Bitmap RightHalf() => Painted(column => column >= Side / 2 ? (byte)255 : (byte)0);

    /// <summary>And one painted at half strength everywhere.</summary>
    static Bitmap Half() => Painted(_ => 128);

    /// <summary>A coverage picture, whose red is what the packer reads.</summary>
    /// <remarks>
    ///     ⚠ The other three channels are deliberately <em>not</em> the same value: a packer reading
    ///     green, or reading the alpha, would be indistinguishable from one reading red on a grey
    ///     picture, which is the fixture that cannot tell a defect from the thing.
    /// </remarks>
    static Bitmap Painted(Func<int, byte> red) {
        var pixels = new byte[Side * Side * 4];

        for (var row = 0; row < Side; row++) {
            for (var column = 0; column < Side; column++) {
                var at = (row * Side + column) * 4;

                pixels[at] = red(column);
                pixels[at + 1] = 17;
                pixels[at + 2] = 33;
                pixels[at + 3] = 49;
            }
        }

        return new(Side, Side, pixels);
    }

    /// <summary>A layered material with a normal map beside it, as an author would have one.</summary>
    static MaterialContent Layered(int layers) =>
        new() {
            Features = [
                new TexturedMaterialLayersFeature {
                    Layers = [.. Enumerable.Range(0, layers).Select(_ => new MaterialLayerValue())]
                },
                new TexturedNormalMapFeature()
            ],
            Textures = [new MaterialTexture(new TexturedNormalMapFeature().NormalMap, Reference(2))]
        };

    static AssetReference Reference(int seed) => new(new AssetId(Guid.Parse($"{seed:D8}-0000-0000-0000-000000000000")));
}
