// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Samples.ThirdPersonShooter.Tests;

/// <summary>The plaza: the first shipping material whose layer weights come out of a splat map.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48's M11 remainder, and what these check is that it is an asset rather than a
///         claim</b> — <a href="https://github.com/Rikarin/Vixen/issues/1073">#1073</a>.
///         <c>TexturedMaterialLayersFeature</c> and its Raven surface have been complete since M11
///         landed and photographed by a golden since 2026-09-07; what did not exist was any material
///         an artist could open that carried one, which made it a feature waiting for a caller.
///     </para>
///     <para>
///         ⚠ <b>Every failure this file is written against draws.</b> A splat map on a tiling uv
///         places nothing and looks like noise; a renamed map resolves slot zero and blends the
///         layers by the table's magenta checker; a fourth layer declared over a three-channel map
///         weighs 1 everywhere and, normalised, becomes the whole surface; a texel painted zero in
///         all three channels divides by an epsilon and comes out black. None of them fail anything,
///         which is why the assertions here are about the committed bytes rather than about counters.
///     </para>
/// </remarks>
public class PaintedLayersTests {
    /// <summary>The same registration <c>MaterialImporter</c> makes, for the same reason: a layer's
    ///     base colour is written as a plain scalar — <c>baseColor: 0.22 0.21 0.19</c> — and the
    ///     generator describes no such shape on its own.</summary>
    /// <remarks>
    ///     ⚠ Per class rather than per assembly, and this file is the second to need it. Without it
    ///     the bind throws here rather than in the game, because the runtime reads a baked chunk and
    ///     the editor's importer registers in its own static constructor — so the omission is only
    ///     ever visible from a test that parses the authored file.
    /// </remarks>
    static PaintedLayersTests() => MathScalars.Register();

    /// <summary>How wide a square of the splat map each of the pure-channel checks reads.</summary>
    /// <remarks>
    ///     The largest pure squares in the committed map are 91, 76 and 58 texels on a side, so 48
    ///     sits inside all three with room for a regenerated map to move them a little before this
    ///     goes red — and going red is the point, because these are the rectangles
    ///     <c>LayeredMaterialImageTests</c> crops to compare a painted zone against a single-layer
    ///     material. A map whose pure zones moved would otherwise leave that comparison silently
    ///     photographing a blend.
    /// </remarks>
    const int PureSide = 48;

    /// <summary>The top-left texel of a square that is pure in R, in G and in B, in that order.</summary>
    static readonly (int Row, int Column)[] PureAt = [(320, 296), (8, 416), (184, 264)];

    /// <summary>The plaza carries the painted stack, names its map the one name a host pairs, and is
    ///     the only material in the arena that does.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The file is compiled rather than inspected</b>, on
    ///         <c>The_wall_is_the_one_arena_material_that_marches_a_height_field</c>'s reasoning: the
    ///         thing that has to hold is that the content build accepts this material, and
    ///         <c>MaterialCompiler</c> is what accepts it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The compile is not what catches a fourth layer, which was worth finding out by
    ///         trying it.</b> <c>UnpaintedLayer</c> is reported at warning severity, so a fourth
    ///         layer added over this three-channel map leaves <c>compilation.Failed</c> false and the
    ///         count below is the assertion that goes red. And the inverse — four layers with
    ///         <c>paintedChannels: 4</c> over a map whose alpha is 255 everywhere — raises no
    ///         diagnostic at all, because the two numbers agree; what refuses that one is the count
    ///         here together with the alpha check in the next test.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>splatMap</c> is not a name this material may choose.</b>
    ///         <c>WorldRenderer.Paired</c> keys its <c>TextureIndices</c> entry off
    ///         <c>new TexturedMaterialLayersFeature().SplatMap</c>, so a rename resolves nothing and
    ///         takes slot zero — the magenta checker, whose channels are emphatically not zero, as
    ///         the weights of every layer. The name and the <c>textures:</c> entry are asserted
    ///         together because either alone is silent.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_plaza_is_the_one_arena_material_whose_weights_are_painted() {
        var plaza = Material("plaza");

        Assert.True(MaterialShading.TryResolve(plaza.Shading, out var shading));

        var compilation = MaterialCompiler.Compile(plaza.ToDescriptor(shading));

        Assert.False(compilation.Failed, string.Join("; ", compilation.Diagnostics.Select(one => one.Message)));

        var layered = Assert.Single(plaza.Features.OfType<TexturedMaterialLayersFeature>());

        Assert.Equal(new TexturedMaterialLayersFeature().SplatMap, layered.SplatMap);

        // ⚠ Three layers and three painted channels, and the map's alpha is 255 everywhere — see the
        // texel test below. A fourth layer here would be weighted by that alpha at 1 over the whole
        // surface and would win the normalisation, which is a lit, plausible surface of the wrong
        // stuff.
        Assert.Equal(3, layered.Layers.Length);
        Assert.Equal(layered.Layers.Length, layered.PaintedChannels);

        // ⚠ And no height blend, because there is no second map. The permutation set without one
        // samples slot zero and biases every weight in the frame by the fallback.
        Assert.False(layered.HeightBlended);

        // A layer's weight is a *scale* on its painted channel rather than the weight itself, so one
        // is "the map exactly" — and zero would be a layer the author switched off, which over a
        // three-channel partition of unity is a third of the surface gone black.
        Assert.All(layered.Layers, layer => Assert.Equal(1f, layer.Weight));

        var binding = Assert.Single(plaza.Textures, entry => entry.Parameter == layered.SplatMap);

        Assert.False(binding.Texture.IsNull, "the plaza's splat map is not a reference");
        Assert.Equal(GuidOf("Textures", "plaza-splat.png"), binding.Texture.Asset.ToString());

        // No entry naming a parameter no feature declares: bytes the bundle ships, the pool makes
        // resident and nothing samples.
        Assert.Single(plaza.Textures);

        // The held half. Five textured arena materials carry a base-colour map each, and putting this
        // feature on any of them would mean giving that map up — a MaterialLayerValue names no map at
        // all. The plaza is a surface that was added rather than one that was converted.
        foreach (var other in new[] { "wall", "pillar", "floor", "crate", "ramp" }) {
            Assert.Empty(Material(other).Features.OfType<TexturedMaterialLayersFeature>());
        }
    }

    /// <summary>The plaza survives the round trip the content build puts it through.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the check the whole of #1073 was missing, and it was red for a reason
    ///         nobody had had the chance to find.</b> A <c>.vxmat</c> reaching a game is YAML on the
    ///         way in and a chunk on the way out — <c>MaterialImporter</c> ends in
    ///         <c>Serializer.ToBytes(content)</c> — and the two halves are different code. The YAML
    ///         half had always worked; the binary half <em>threw</em> for any material carrying either
    ///         layer feature, because <c>Layers</c> was declared as an <c>IReadOnlyList</c>, the
    ///         generated serializer writes an interface-typed member polymorphically, and
    ///         <c>MaterialLayerValue[]</c> has no <c>[DataContract]</c> alias to write. Nothing could
    ///         see it: only an authored material is ever serialised, and until this batch there was no
    ///         authored material with layers anywhere in the tree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Compiling the descriptor is not this check and cannot become it.</b>
    ///         <c>MaterialCompiler</c> reads the features in memory and is perfectly happy with an
    ///         interface-typed collection — the test above passed against the broken build, as did the
    ///         device golden. What failed was <c>dotnet build</c> of the sample, which is not a suite
    ///         anything runs per project. So the round trip is asserted here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_plaza_survives_being_written_as_a_chunk_and_read_back() {
        var plaza = Material("plaza");
        var restored = Serializer.Read<MaterialContent>(Serializer.ToBytes(plaza));

        var before = Assert.Single(plaza.Features.OfType<TexturedMaterialLayersFeature>());
        var after = Assert.Single(restored.Features.OfType<TexturedMaterialLayersFeature>());

        // ⚠ The layers themselves and not just their count. A serializer that wrote an empty array
        // would satisfy a count of zero against a count of zero, and the values are what a splat map
        // has nothing to weight without.
        Assert.Equal(before.Layers, after.Layers);
        Assert.Equal(before.SplatMap, after.SplatMap);
        Assert.Equal(before.PaintedChannels, after.PaintedChannels);
        Assert.Equal(before.HeightBlended, after.HeightBlended);

        var binding = Assert.Single(restored.Textures);

        Assert.Equal(before.SplatMap, binding.Parameter);
        Assert.False(binding.Texture.IsNull, "the splat map's reference did not survive the round trip");
    }

    /// <summary>The plaza is unwrapped into one tile and every other arena mesh is not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the measurement the whole of #1073 turned on, asserted rather than quoted.</b>
    ///         A splat map is the one kind of map that must be unique per surface, because its content
    ///         is "where on this surface layer 2 is". Every other mesh here is box-projected by
    ///         <c>Content/boxuv.py</c> in metres of world divided by a 2 m tile, so
    ///         <c>arena-floor.obj</c> runs −16..16 and a map painted over 0..1 would repeat
    ///         thirty-two times per axis and place nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The span is asserted as well as the bound</b>, because a mesh crammed into one
    ///         corner of the tile also satisfies "inside 0..1" — and would read one corner of the map
    ///         across the whole plaza, which is a flat colour that looks like a material that failed
    ///         to load.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the other meshes are checked</b> so this cannot quietly become vacuous. If
    ///         somebody re-exported the arena at its own extent, a splat map would fit anywhere and
    ///         the reason this mesh exists would be gone — that is a finding, not a silent pass.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_plaza_is_the_one_arena_mesh_unwrapped_into_a_single_tile() {
        var plaza = TexCoords("arena-plaza.obj");

        Assert.NotEmpty(plaza);
        Assert.All(
            plaza,
            uv => Assert.True(
                uv.U is >= 0f and <= 1f && uv.V is >= 0f and <= 1f,
                $"arena-plaza.obj leaves the tile at ({uv.U}, {uv.V}), so its splat map wraps"
            )
        );

        Assert.True(plaza.Min(uv => uv.U) < 0.01f && plaza.Max(uv => uv.U) > 0.99f, "u does not span the tile");
        Assert.True(plaza.Min(uv => uv.V) < 0.01f && plaza.Max(uv => uv.V) > 0.99f, "v does not span the tile");

        foreach (var other in new[] { "arena-floor.obj", "arena-wall.obj", "arena-pillar.obj", "arena-ramp.obj" }) {
            var coordinates = TexCoords(other);

            Assert.NotEmpty(coordinates);
            Assert.True(
                coordinates.Any(uv => uv.U is < 0f or > 1f || uv.V is < 0f or > 1f),
                $"{other} now fits inside one tile, so the plaza is no longer the only mesh that could "
                + "carry a splat map — which is a decision to revisit rather than a test to relax"
            );
        }
    }

    /// <summary>The committed splat map is a three-channel partition of unity with no painted alpha.</summary>
    /// <remarks>
    ///     <para>
    ///         Three properties, each of which a map without it <em>draws</em>:
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Alpha is 255 in every texel</b>, which is what a three-channel texture's alpha
    ///         samples as anyway — so the material's <c>paintedChannels: 3</c> is the true statement
    ///         about this map rather than the lucky one, and a fourth layer added to either half
    ///         would be the wrong picture the feature's own remarks warn about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>R + G + B is exactly 255.</b>
    ///         <c>TexturedMaterialLayersSurface</c> divides by <c>max(total, epsilon)</c>, so a texel
    ///         painted zero in all three is a black surface rather than an error. An exact sum also
    ///         makes that division a no-op, which is what lets the golden compare a painted zone
    ///         against a single-layer material exactly rather than within a rescaling.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each channel is pure somewhere</b>, and at the rectangles the golden crops. A
    ///         three-layer material whose map never reaches one layer is a two-layer material that
    ///         costs three, and a pure zone that moved would leave the golden photographing a blend
    ///         while still passing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_splat_map_paints_three_channels_and_leaves_the_fourth_alone() {
        var map = PngCodec.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "plaza-splat.png"));

        Assert.Equal(512, map.Width);
        Assert.Equal(512, map.Height);

        for (var row = 0; row < map.Height; row++) {
            for (var column = 0; column < map.Width; column++) {
                var (r, g, b, a) = Texel(map, row, column);

                Assert.True(a == 255, $"({column}, {row}) has a painted alpha of {a}, which layer 3 would read");
                Assert.True(r + g + b == 255, $"({column}, {row}) sums to {r + g + b} rather than 255");
            }
        }

        for (var channel = 0; channel < PureAt.Length; channel++) {
            var (top, left) = PureAt[channel];

            for (var row = top; row < top + PureSide; row++) {
                for (var column = left; column < left + PureSide; column++) {
                    var (r, g, b, _) = Texel(map, row, column);
                    var painted = channel switch { 0 => r, 1 => g, _ => b };

                    Assert.True(
                        painted == 255 && r + g + b == 255,
                        $"({column}, {row}) is ({r}, {g}, {b}) rather than pure in channel {channel}, so the "
                        + "zone LayeredMaterialImageTests crops for its closed form is a blend"
                    );
                }
            }
        }
    }

    /// <summary>The scene places the plaza, which is what makes any of the above reach a frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect this repository ships most often is a finished thing nothing calls</b>, and
    ///     that is exactly what a <c>TexturedMaterialLayersFeature</c> was for as long as the only
    ///     constructions of it were a golden fixture's and <c>WorldRenderer.Paired</c>'s — which
    ///     builds one purely to read the map's name off. So the entity is asserted by the same guids
    ///     the <c>.meta</c> sidecars mint: a scene naming a mesh or a material that does not exist
    ///     draws nothing and says nothing.
    /// </remarks>
    [Fact]
    public void The_scene_places_the_plaza_with_the_mesh_and_material_that_were_authored_for_it() {
        var scene = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Scenes", "Arena.vxscene"));

        Assert.Contains($"vx:{GuidOf("Models", "arena-plaza.obj")}#", scene, StringComparison.Ordinal);
        Assert.Contains($"material: vx:{GuidOf("Materials", "plaza.vxmat")}", scene, StringComparison.Ordinal);
    }

    /// <summary>One material, read as the author wrote it rather than as the importer left it.</summary>
    /// <param name="name">The file's stem.</param>
    static MaterialContent Material(string name) =>
        YamlSerializer.Parse<MaterialContent>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Materials", $"{name}.vxmat"))
        );

    /// <summary>The guid an asset's committed sidecar mints for it.</summary>
    /// <param name="folder">Which folder under <c>Assets</c> it is in.</param>
    /// <param name="file">The asset's file name, without the <c>.meta</c>.</param>
    static string GuidOf(string folder, string file) {
        var meta = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", folder, $"{file}.meta"));
        var line = meta.Split('\n').First(one => one.StartsWith("guid:", StringComparison.Ordinal));

        return line["guid:".Length..].Trim();
    }

    /// <summary>Every <c>vt</c> line of a committed mesh, in the file's own order.</summary>
    /// <param name="file">The mesh's file name.</param>
    static (float U, float V)[] TexCoords(string file) =>
        [
            .. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", file))
                .Where(line => line.StartsWith("vt ", StringComparison.Ordinal))
                .Select(
                    line => {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        return (float.Parse(parts[1], CultureInfo.InvariantCulture),
                            float.Parse(parts[2], CultureInfo.InvariantCulture));
                    }
                )
        ];

    /// <summary>One texel of a decoded map.</summary>
    /// <param name="map">The picture.</param>
    /// <param name="row">Which row.</param>
    /// <param name="column">Which column.</param>
    static (int R, int G, int B, int A) Texel(Bitmap map, int row, int column) {
        var at = map.Offset(column, row);

        return (map.Pixels[at], map.Pixels[at + 1], map.Pixels[at + 2], map.Pixels[at + 3]);
    }
}
