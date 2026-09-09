// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A material whose maps were made by the texture tool, photographed through the real frame.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 48 § M5's exit criterion 12, and until this file nothing in the tree met it.</b>
///         Every other assertion about a baked material is about bytes: the plan validates, the
///         kernels dispatch, the read-back matches a closed form, the packer puts roughness in green,
///         the <c>.vxmat</c> names three GUIDs. All true, all green, and every one of them equally
///         true of a material that draws nothing — <b>no golden anywhere rendered a <i>textured</i>
///         material at all</b> until this one, so the whole sampling half of the surface library had
///         never been in a picture. What is asserted here is a picture.
///     </para>
///     <para>
///         <b>The chain is the product's, not a stand-in for it.</b>
///         <see cref="TexturePlanEvaluator" /> makes the texels (§ M2), <see cref="MaterialBake" />
///         packs occlusion, roughness and metalness into one file and encodes every map as the PNG a
///         project ships (§ M5), <see cref="MaterialBake.Material" /> decides which features that set
///         of maps composes into, and <c>TierScene</c> draws the result through
///         <see cref="StandardFrameAsset" />. The only step this file performs itself is putting the
///         decoded bytes on the device, which is what an asset build does and what a test has no
///         project to do.
///     </para>
///     <para>
///         ⚠ <b>The oracle is a differential rather than a committed PNG</b>, for
///         <see cref="GraphMaterialImageTests" />'s reason and one more of its own. A flat map is a
///         constant, and a constant surface is <em>exactly</em> what
///         <see cref="MetalRoughnessFeature" /> spells with three numbers: base colour from the map,
///         roughness out of the ORM map's green, metalness out of its blue, occlusion out of its red
///         at one. So the whole textured path — sRGB decode, the bindless slot, the table's sampler,
///         the ORM channel order, the normal map's unfold — has to reduce to the number the untextured
///         feature carries, and nothing in that claim depends on this machine's tone map, its
///         exposure or its sun.
///     </para>
///     <para>
///         ⚠ <b>And the map levels are chosen so that the quantisation is not part of the claim.</b>
///         A map is eight bits, so a colour that is not a level lands on a neighbouring one and the
///         two spellings differ for a reason that is neither of their faults. Every value here is
///         authored as <c>k/255</c> and the number the hand-written feature carries is that level put
///         back through the same transfer function the hardware applies — see
///         <see cref="Linear(float)" />.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is a 0–1 tint that a photometric frame would render identically to a
///         pass that never ran.</b> A base colour <em>is</em> a reflectance, so 0–1 is the correct
///         range for it; the radiance comes from <c>TierScene</c>'s sun, in lux. The control
///         assertion below — two colours that must <i>not</i> agree — is what says the frame is a
///         picture of the surface rather than a clipped white one in which every material looks the
///         same.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class BakedMaterialImageTests {
    /// <summary>How large each baked map is, in texels.</summary>
    /// <remarks>
    ///     Small deliberately. The maps here are flat, so resolution buys nothing, and
    ///     <see cref="MaterialMapNaming.ExtensionFor" /> writes a PNG rather than a block-compressed
    ///     container below its portable limit — which keeps this a test of the frame rather than of
    ///     a BC7 encoder that has closed-form tests of its own.
    /// </remarks>
    const int Side = 64;

    /// <summary>The stored level of a flat tangent-space normal's x and y.</summary>
    /// <remarks>
    ///     ⚠ <b>128 and not "0.5", because 0.5 is not a level.</b> <c>Normals.Decode</c> unfolds
    ///     <c>2s − 1</c>, so this decodes to 1/255 rather than to zero and the surface is tilted by
    ///     about a fifth of a degree — far inside <see cref="Tolerance.Shaded" /> and stated here so
    ///     that a reader does not mistake the agreement below for an exact one.
    /// </remarks>
    const byte FlatNormal = 128;

    /// <summary>The stored level the ORM map's green carries, which is the surface's roughness.</summary>
    const byte RoughnessLevel = 115;

    /// <summary>The stored levels of the base-colour map: saturated, and no two alike.</summary>
    /// <remarks>
    ///     A swapped channel, a dropped one and a map read through the wrong view are three different
    ///     colours here, where a grey would absorb all three.
    /// </remarks>
    static readonly byte[] Colour = [210, 54, 28];

    /// <summary>Something else, for the runs that have to disagree.</summary>
    static readonly byte[] Other = [28, 54, 210];

    /// <summary>The frame both renderings share. <see cref="GraphMaterialImageTests" />' own.</summary>
    static StandardFrameAsset Frame => new() {
        Name = "BakedMaterial",
        Shadows = ShadowMode.Cascades,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Fxaa,
        Exposure = ExposureMode.Automatic,
        Particles = false
    };

    /// <summary>
    ///     A material baked from the tool draws the surface its maps encode.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument is checked before the claim, and here that check is load bearing
    ///     twice.</b> A frame in which the slab never drew is a frame of the floor and the sky, and
    ///     it agrees with any other frame in which the slab never drew — which is what a missing
    ///     variant, an unbound set 4 and a culled pass all look like. And a textured material whose
    ///     index never reached the table samples slot zero, which is the frame's magenta checker: an
    ///     obviously wrong picture that is nonetheless a picture, so the run has to be able to tell
    ///     "the maps arrived" from "something arrived".
    /// </remarks>
    [Fact]
    public void A_baked_material_draws_the_surface_its_maps_encode() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var baked = Bake(fixture!, Colour, RoughnessLevel);
            var handWritten = Library(Linear(Colour), Level(RoughnessLevel));

            // First: the scene is a picture of its material at all.
            var constant = Render(fixture!, _ => handWritten);
            var different = Render(fixture!, _ => Library(Linear(Other), Level(RoughnessLevel)));
            var control = GoldenImage.Compare(constant, different, Tolerance.Shaded);

            Assert.False(
                control.Matches,
                $"Two materials of different colours produced the same frame on {Adapter(fixture!)}, so this "
                + "scene is not a picture of its material and nothing below it means anything."
            );

            var textured = Render(fixture!, scene => Material(scene, baked));
            var comparison = GoldenImage.Compare(constant, textured, Tolerance.Shaded);

            Assert.True(
                comparison.Matches,
                $"A material textured by the tool drew differently from the constant surface its maps encode "
                + $"on {Adapter(fixture!)}: {comparison.DifferingPixels} of {comparison.TotalPixels} pixels differ, "
                + $"worst channel {comparison.WorstChannel} at {comparison.WorstAt}, mean "
                + $"{comparison.MeanChannel:F3}."
            );
        }
    }

    /// <summary>
    ///     A base-colour map of another colour draws another picture.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half of the pair that can be false.</b> The test above is satisfied by a renderer
    ///     that ignored the maps and shaded from the material's own <c>baseColor</c>, which is white
    ///     — no, it is not: white times a map is the map, and without the map the surface is white
    ///     rather than the colour. But it <em>is</em> satisfied by a renderer in which every textured
    ///     material samples one slot, so long as that slot holds this colour. This one is not.
    /// </remarks>
    [Fact]
    public void A_base_colour_map_of_another_colour_draws_another_picture() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var one = Bake(fixture!, Colour, RoughnessLevel);
            var two = Bake(fixture!, Other, RoughnessLevel);

            var first = Render(fixture!, scene => Material(scene, one));
            var second = Render(fixture!, scene => Material(scene, two));

            var comparison = GoldenImage.Compare(first, second, Tolerance.Shaded);

            Assert.False(
                comparison.Matches,
                $"Two base-colour maps carrying different colours drew the same frame on {Adapter(fixture!)}, "
                + "so what reached the pass was not the map."
            );
        }
    }

    /// <summary>
    ///     The ORM map's green and blue are not interchangeable.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one property a flat picture would otherwise hide, and the one
    ///     <see cref="MaterialMapNaming.Packed" /> exists to fix in exactly one place.</b> Occlusion,
    ///     roughness and metalness are three greys packed into one texel, and a packer that put them
    ///     in the wrong order still writes a valid file, still resolves, still samples and still
    ///     shades — it shades a dielectric as a conductor. So the same surface is baked twice with
    ///     roughness and metalness exchanged, and the two frames have to differ.
    /// </remarks>
    [Fact]
    public void The_orm_map_does_not_confuse_roughness_with_metalness() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            // Rough and dielectric, against smooth and fully metallic: the same two numbers, swapped.
            var packed = Bake(fixture!, Colour, RoughnessLevel, metalness: 0);
            var swapped = Bake(fixture!, Colour, 0, metalness: RoughnessLevel);

            var first = Render(fixture!, scene => Material(scene, packed));
            var second = Render(fixture!, scene => Material(scene, swapped));

            var comparison = GoldenImage.Compare(first, second, Tolerance.Shaded);

            Assert.False(
                comparison.Matches,
                $"An ORM map with roughness and metalness exchanged drew the same frame as the correctly packed "
                + $"one on {Adapter(fixture!)}, so the two channels are not reaching two different things."
            );
        }
    }

    /// <summary>
    ///     A normal map bends the light, and bending it the other way is another picture.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A map tilted one way against the same map tilted the other, rather than against a
    ///     flat one.</b> A tilt against flat can be produced by a feature that samples nothing and
    ///     merely renormalises; two opposite tilts cannot, because the only thing that separates them
    ///     is the sign of what was sampled. It is also the assertion that fails if the map is decoded
    ///     as unsigned — an unfold that dropped the <c>2s − 1</c> maps both tilts into the same
    ///     hemisphere.
    /// </remarks>
    [Fact]
    public void A_normal_map_bends_the_light() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var towards = Bake(fixture!, Colour, RoughnessLevel, normalX: 210);
            var away = Bake(fixture!, Colour, RoughnessLevel, normalX: 46);

            var first = Render(fixture!, scene => Material(scene, towards));
            var second = Render(fixture!, scene => Material(scene, away));

            var comparison = GoldenImage.Compare(first, second, Tolerance.Shaded);

            Assert.False(
                comparison.Matches,
                $"Two normal maps tilted in opposite directions drew the same frame on {Adapter(fixture!)}, so "
                + "the map is not reaching the shading normal."
            );
        }
    }

    /// <summary>
    ///     A material baked from a <em>compiled graph</em> draws the surface the graph describes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 48 § M5's exit criterion 12 says "textured entirely in the tool", and until
    ///         this the tool in the picture was the evaluator.</b> Every other assertion in this file
    ///         starts from a <see cref="TexturePlan" /> the test wrote, so all of them stay green in
    ///         a world where <c>TextureGraphCompiler</c> emits nothing anybody can bake — the graph
    ///         front end had never been in a frame at all
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1081">#1081</a>). What is in this
    ///         one is a committed <c>.vxtexgraph</c>: the document is parsed, loaded, compiled
    ///         against the node registry the editor and the CLI both build, its outputs are read by
    ///         <em>usage</em> off <c>TextureGraphCompiler.Outputs</c>, and the plan that falls out is
    ///         evaluated, packed, encoded and rendered.
    ///     </para>
    ///     <para>
    ///         <b>The oracle is the same one, deliberately.</b> The graph is five
    ///         <c>Source/Uniform</c> nodes at the same stored levels <see cref="Bake" /> fills by
    ///         hand, so the frame it produces has to be the frame
    ///         <see cref="MetalRoughnessFeature" /> spells with three numbers. A compiler that
    ///         allocated the wrong image, ordered the ORM channels differently or mapped
    ///         <c>roughness</c> to the wrong usage lands somewhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Flat, and that is a limit worth stating rather than a weakness to hide.</b> A
    ///         differential against a constant surface can only be taken over a constant surface, so
    ///         nothing here says a checker or a noise reaches the frame correctly. It says the
    ///         <em>document</em> does.
    ///         <see cref="Two_graphs_differing_in_one_node_bake_two_pictures" /> is what stops that
    ///         being satisfied by a compiler that ignored the file.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_material_baked_from_a_compiled_graph_draws_the_surface_the_graph_describes() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var baked = Graph(fixture!, "BakedGraph.vxtexgraph");
            var handWritten = Library(Linear(Colour), Level(RoughnessLevel));

            // The same instrument check the hand-built run makes, and it is not redundant: it is what
            // separates "the graph's surface agrees" from "this scene is a picture of the room".
            var constant = Render(fixture!, _ => handWritten);
            var different = Render(fixture!, _ => Library(Linear(Other), Level(RoughnessLevel)));

            Assert.False(
                GoldenImage.Compare(constant, different, Tolerance.Shaded).Matches,
                $"Two materials of different colours produced the same frame on {Adapter(fixture!)}, so this "
                + "scene is not a picture of its material and nothing below it means anything."
            );

            var textured = Render(fixture!, scene => Material(scene, baked));
            var comparison = GoldenImage.Compare(constant, textured, Tolerance.Shaded);

            Assert.True(
                comparison.Matches,
                "A material baked from a compiled .vxtexgraph drew differently from the constant surface the "
                + $"graph describes on {Adapter(fixture!)}: {comparison.DifferingPixels} of "
                + $"{comparison.TotalPixels} pixels differ, worst channel {comparison.WorstChannel} at "
                + $"{comparison.WorstAt}, mean {comparison.MeanChannel:F3}."
            );
        }
    }

    /// <summary>
    ///     Two graphs that differ in one node's value bake two pictures.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half that can be false, and the reason the test above is not an instrument that
    ///     cannot fail.</b> A compiler that never read the document — one that emitted a fixed plan,
    ///     or that took the node's declared default rather than the value in the file — would satisfy
    ///     "the baked surface equals the constant one" for as long as the fixed thing happened to be
    ///     that constant. The two fixtures here are the same five nodes, the same five edges and the
    ///     same five usages, differing only in the base-colour <c>Source/Uniform</c>'s
    ///     <c>Colour</c>; so the frames differing says that a number typed into a node reached a
    ///     texel, through the document, the compiler, the plan, the bake and the frame.
    /// </remarks>
    [Fact]
    public void Two_graphs_differing_in_one_node_bake_two_pictures() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var one = Graph(fixture!, "BakedGraph.vxtexgraph");
            var two = Graph(fixture!, "BakedGraphOther.vxtexgraph");

            var first = Render(fixture!, scene => Material(scene, one));
            var second = Render(fixture!, scene => Material(scene, two));

            var comparison = GoldenImage.Compare(first, second, Tolerance.Shaded);

            Assert.False(
                comparison.Matches,
                "Two graphs whose base-colour node carries different colours baked the same frame on "
                + $"{Adapter(fixture!)}, so what reached the plan was not what the document says."
            );
        }
    }

    /// <summary>One material's worth of encoded files, as the bake writes them.</summary>
    /// <param name="Images">What <see cref="MaterialBake.Encode" /> produced, one per target.</param>
    readonly record struct Baked(IReadOnlyList<MaterialMapImage> Images);

    /// <summary>
    ///     Runs the tool: a plan of uniform fills, evaluated on the device, packed and encoded.
    /// </summary>
    /// <param name="fixture">The device the kernels dispatch on.</param>
    /// <param name="colour">The base-colour map's three stored levels.</param>
    /// <param name="roughness">The stored level of the ORM map's green.</param>
    /// <param name="metalness">The stored level of its blue.</param>
    /// <param name="normalX">The stored level of the normal map's x.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The plan is hand-built here, and that is not a shortcut.</b> <c>TexturePlan</c>'s
    ///         own remarks say a plan is the artefact both front ends compile to and that building
    ///         one by hand is a requirement, so this is the spelling that photographs the evaluator
    ///         and the bake with no document in the way. <see cref="Graph" /> — the graph
    ///         spelling — is the other half, and the two land on the same oracle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This paragraph used to give a reason that was false, in a file that disproves
    ///         it</b> (<a href="https://github.com/Rikarin/Vixen/issues/1081">#1081</a>): it said
    ///         <c>TextureGraphOutput</c> is internal and that the graph half of § M4 therefore could
    ///         not be driven from here at all. <c>TextureGraphCompiler.cs</c> declares it
    ///         <c>public readonly record struct</c>, there is no <c>InternalsVisibleTo</c> in the
    ///         chain, three assemblies outside <c>Vixen.Editor.TextureGraph</c> already read it —
    ///         and this file named the type in its own source while saying it could not see it. A
    ///         blocker nobody re-measures is how a criterion stays open for four batches.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every value is written as a level over 255 and read back as one.</b> The plan's
    ///         images are <c>Rgba8</c>, so a kernel writing <c>k/255</c> stores exactly <c>k</c>, and
    ///         the agreement asserted above is then between two numbers rather than between two
    ///         roundings.
    ///     </para>
    /// </remarks>
    static Baked Bake(Fixture fixture, byte[] colour, byte roughness, byte metalness = 0, byte normalX = FlatNormal) {
        // The order is the image table's, and the read-back below walks the same list.
        (MaterialMapUsage Usage, Vector4 Fill)[] wanted = [
            (MaterialMapUsage.BaseColor, new(Level(colour[0]), Level(colour[1]), Level(colour[2]), 1f)),
            (MaterialMapUsage.Normal, new(Level(normalX), Level(FlatNormal), 1f, 1f)),

            // Fully lit, so the ORM map's red is the identity and the untextured spelling — which has
            // no occlusion at all — is the same surface. A value other than one would make the two
            // differ for a reason neither of them is wrong about.
            (MaterialMapUsage.Occlusion, new(1f, 1f, 1f, 1f)),
            (MaterialMapUsage.Roughness, new(Level(roughness), Level(roughness), Level(roughness), 1f)),
            (MaterialMapUsage.Metalness, new(Level(metalness), Level(metalness), Level(metalness), 1f))
        ];

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [.. wanted.Select(_ => new TextureImage(TextureFormat.Rgba8))],
            Ops = [.. wanted.Select((entry, index) => Fill(index, entry.Fill))],
            Outputs = [.. Enumerable.Range(0, wanted.Length)]
        };

        var outputs = new Dictionary<MaterialMapUsage, Bitmap>();

        using (var evaluator = new TexturePlanEvaluator(fixture.Device)) {
            using var bake = evaluator.Evaluate(plan);

            for (var image = 0; image < wanted.Length; image++) {
                outputs[wanted[image].Usage] = bake.Read(image);
            }

            // ⚠ Not a formality. A plan that dispatched nothing produces five images of whatever the
            // allocator left in them, which on this hardware is usually zero — and five black maps
            // would make the control assertion above fail rather than this one, from a message about
            // colours. One dispatch per op is what the evaluator promises.
            Assert.Equal(wanted.Length, bake.Dispatches);
        }

        return new(MaterialBake.Encode(outputs));
    }

    /// <summary>
    ///     Runs the tool from the front: a committed graph, compiled, evaluated and encoded.
    /// </summary>
    /// <param name="fixture">The device the kernels dispatch on.</param>
    /// <param name="file">Which fixture under <c>Fixtures/</c>.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The registry is built the way the two shipping hosts build it.</b>
    ///         <c>NodeTypes.Register</c> is the generated roll of every node
    ///         <c>Vixen.Editor.TextureGraph</c> declares, and it is what the texturing plugin and
    ///         <c>Vixen.Cli</c>'s <c>TextureGraphRunner</c> both start from — so a node that stopped
    ///         registering is a compile error about a missing type here rather than a graph that
    ///         quietly loses one map. No compound library is published, because these fixtures use
    ///         none: a compound that failed to read is a diagnostic elsewhere and would be noise in
    ///         a picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The resolution is the host's, and the graph's own declaration would win over
    ///         it.</b> These fixtures declare none, so <see cref="Side" /> is what
    ///         <c>TextureGraphCompiler.BaseWidth</c> supplies — which is also what
    ///         <see cref="Material" /> asserts the decoded PNG is, so a graph that started declaring
    ///         its own size fails there loudly instead of resampling silently.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The outputs are read by usage rather than by index.</b>
    ///         <c>TexturePlan.Outputs</c> is a list of image indices with no names on it —
    ///         <see cref="TextureGraphOutput" />'s own remarks say the join between "an image
    ///         survived" and "it is the roughness map" lives on the compiler — so reading them
    ///         positionally would pass whatever order the compiler emitted them in and shade a
    ///         dielectric as a conductor when it changed.
    ///     </para>
    /// </remarks>
    static Baked Graph(Fixture fixture, string file) {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

        Assert.True(File.Exists(path), $"'{path}' is not beside the assembly, so this suite tested nothing.");

        var stored = YamlSerializer.Parse<NodeGraphAsset>(File.ReadAllText(path));
        var model = NodeGraphDocument.Load(stored, out var repairs);

        // ⚠ A repair is the reader deciding what a file meant. One here is a fixture that no longer
        // says what it is read as, which is exactly the drift a committed document exists to pin.
        Assert.Empty(repairs);

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compiler = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side };
        var compilation = compiler.Compile(model);

        Assert.Empty(compilation.Diagnostics.Where(diagnostic => diagnostic.Severity == NodeSeverity.Error));
        Assert.NotNull(compilation.Value);

        var plan = compilation.Value!;
        var wanted = new Dictionary<MaterialMapUsage, int>();

        foreach (var written in compiler.Outputs) {
            Assert.True(
                MaterialMapNaming.TryParseSuffix(written.Usage, out var usage),
                $"'{file}' writes a map called '{written.Usage}', which is not a usage a bake writes."
            );

            wanted[usage] = written.Image;
        }

        // ⚠ Not a formality, and not a second list either: the oracle below is a surface with a base
        // colour, a normal, an occlusion, a roughness and a metalness, and a graph that lost one of
        // the five bakes a material missing a feature — which still draws, plausibly, in the
        // material's own defaults. The number is the arity of the comparison rather than a count of
        // anything in the folder.
        Assert.Equal(5, wanted.Count);

        var outputs = new Dictionary<MaterialMapUsage, Bitmap>();

        using (var evaluator = new TexturePlanEvaluator(fixture.Device)) {
            using var bake = evaluator.Evaluate(plan);

            foreach (var (usage, image) in wanted) {
                outputs[usage] = bake.Read(image);
            }

            // The evaluator's promise, and the same guard the hand-built run makes: a plan that
            // dispatched nothing hands back whatever the allocator left in five images.
            Assert.Equal(plan.Ops.Length, bake.Dispatches);
            Assert.NotEqual(0, bake.Dispatches);
        }

        return new(MaterialBake.Encode(outputs));
    }

    /// <summary>One op: a constant colour into one image.</summary>
    /// <remarks>
    ///     ⚠ <b>The parameter names are the kernel's and are spelled here rather than taken from
    ///     <c>TextureSources.Uniform</c>, which is internal.</b> That is safe rather than a second
    ///     list: <c>TexturePlanEvaluator</c> refuses an op that does not carry every uniform its
    ///     kernel declares, so a rename in <c>Uniform.rvn</c> fails this file loudly instead of
    ///     silently taking a default.
    /// </remarks>
    static TextureOp Fill(int output, Vector4 colour) =>
        new() {
            Kernel = "Uniform",
            Output = output,
            Parameters = [
                new("red", colour.X),
                new("green", colour.Y),
                new("blue", colour.Z),
                new("alpha", colour.W)
            ]
        };

    /// <summary>
    ///     The material a baked set of files composes into, with its maps on the device.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Which features to compose is <see cref="MaterialBake.Material" />'s decision and
    ///         not this file's</b>, and the parameter each map is named by is
    ///         <see cref="MaterialMapNaming.Parameter" />'s. A test that listed
    ///         <c>TexturedMetalRoughnessFeature</c>, <c>TexturedNormalMapFeature</c> and
    ///         <c>TexturedOrmFeature</c> itself would go on passing after a bake stopped emitting
    ///         one of them, which is the shape of half the defects doc 48 has produced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The view's format comes from the bake's own <c>Content</c> too.</b> A base-colour
    ///         map read through a linear view rather than an sRGB one is a material two stops too
    ///         dark everywhere, which reads as lighting; deriving the format from
    ///         <see cref="TextureImportSettings.Content" /> is the same join an importer makes.
    ///     </para>
    /// </remarks>
    static Material Material(TierScene scene, Baked baked) {
        var maps = new Dictionary<MaterialMapTarget, AssetReference>();
        var views = new Dictionary<string, TextureViewHandle>(StringComparer.Ordinal);

        foreach (var image in baked.Images) {
            if (MaterialMapNaming.Parameter(image.Target) is not { } parameter) {
                // Height and mask bind to no feature — MaterialMapTarget's own remarks say so.
                continue;
            }

            var decoded = PngCodec.Decode(image.Bytes);

            Assert.Equal(Side, decoded.Width);
            Assert.Equal(Side, decoded.Height);

            maps[image.Target] = new(AssetId.New());

            views[parameter] = scene.Map(
                $"Baked.{image.Target}",
                Side,
                decoded.Pixels,
                image.Settings.Content == TextureContent.Colour
                    ? PixelFormat.Rgba8UNormSrgb
                    : PixelFormat.Rgba8UNorm
            );
        }

        var content = MaterialBake.Material(maps);

        Assert.True(MaterialShading.TryResolve(content.Shading, out var shading));

        var material = Compiled(content.ToDescriptor(shading));

        // The pairing the renderer completes: a feature names a map, a host puts the view under that
        // name, and `MaterialRenderFeature` turns it into the slot the shader indexes.
        foreach (var texture in content.Textures) {
            material.Parameters.Set(ParameterKeys.New<TextureViewHandle>(texture.Parameter), views[texture.Parameter]);
        }

        return material;
    }

    /// <summary>The same surface spelled with the library's own untextured feature.</summary>
    static Material Library(Vector3 colour, float roughness) =>
        Compiled(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = colour, Metalness = 0f, Roughness = roughness }]
            }
        );

    static Material Compiled(MaterialDescriptor descriptor) {
        var compilation = MaterialCompiler.Compile(descriptor);

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>Stages one slab over a floor, renders the frame, and hands back the picture.</summary>
    /// <param name="fixture">The device.</param>
    /// <param name="slab">
    ///     What the slab is made of, given the scene — because a textured material's maps are the
    ///     scene's to own and to upload, and the material cannot be built before there is one.
    /// </param>
    static Bitmap Render(Fixture fixture, Func<TierScene, Material> slab) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device)));

        using var scene = TierScene.Open(fixture, effects, new() { Game = Frame }, QualityTier.High);

        var casters = scene.Stages.TryGetValue("Shadow", out var shadow) ? shadow.Mask : default;
        var opaque = scene.Stages["Opaque"].Mask;

        // A grey floor, so the slab is the only thing in the frame whose surface is the variable.
        scene.Box(new(0.4f, -0.25f, -0.6f), new(9f, 0.25f, 9f), Library(new(0.35f, 0.35f, 0.35f), 0.45f), opaque);

        // GraphMaterialImageTests' slab, in the same place, for the same reason: big enough that a
        // difference in it is a difference in the picture rather than a handful of pixels the mean
        // absorbs.
        scene.Box(new(0.2f, 0.9f, -0.4f), new(1.5f, 0.9f, 1.1f), slab(scene), opaque | casters);

        scene.Commit(opaque);

        // Several frames, because the automatic exposure is a filter over its own history.
        return scene.Frames(8);
    }

    /// <summary>One stored level as the number a kernel writes to produce it.</summary>
    static float Level(byte level) => level / 255f;

    /// <summary>
    ///     The linear colour an sRGB view produces from three stored levels.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The IEC 61966-2-1 transfer function, spelled here because the number on the other
    ///     side of the comparison is the hardware's.</b> A base-colour map is a colour, so the bake
    ///     marks it <see cref="TextureContent.Colour" /> and the view is <c>Rgba8UNormSrgb</c> — the
    ///     sampler therefore hands the shader this, and the untextured spelling has to carry the same
    ///     number or the two frames differ for a reason that is neither of their faults. If this ever
    ///     disagrees with a driver it is a finding about the driver and not a flake: Vulkan specifies
    ///     the conversion, and the tolerance below is far wider than the rounding it permits.
    /// </remarks>
    static Vector3 Linear(byte[] levels) =>
        new(Linear(Level(levels[0])), Linear(Level(levels[1])), Linear(Level(levels[2])));

    static float Linear(float encoded) =>
        encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    /// <summary>What ran, said in every message here so that no number is anonymous.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48's exit criterion 11 — a device is confirmed by name in every GPU test in this
    ///     area.</b> <c>TextureKernelHarness.Adapter</c> is the same line on the evaluator's side of
    ///     the fence; this suite's <c>Fixture</c> had no equivalent, so a comparison that failed on
    ///     one driver and passed on another produced two messages nothing could tell apart.
    /// </remarks>
    static string Adapter(Fixture fixture) =>
        $"{fixture.Device.Adapter.Name} ({fixture.Device.Adapter.Kind}, {fixture.Device.Adapter.DriverVersion})";

    /// <summary>
    ///     A device, or a loud skip — and a second loud skip for the capability this needs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Bindless is checked and skipped on, not assumed.</b> A textured material is a
    ///     <c>uint</c> into <c>WorldRenderer</c>'s table, and without
    ///     <c>GraphicsDeviceFeatures.HasBindless</c> there is no table — that is MoltenVK below
    ///     argument-buffer tier 2, GL and WebGL2, and ADR-011 calls it a supported configuration
    ///     rather than a degraded one, in which a project uses the untextured workflow instead.
    ///     <c>BindlessSamplingDeviceTests</c> makes exactly this call and states the reason: a bare
    ///     <c>return</c> is a pass, and a test that reports one without ever opening a table proves
    ///     nothing. <c>VIXEN_REQUIRE_VULKAN</c> does <b>not</b> turn this one into a failure, because
    ///     the capability is genuinely absent rather than a run that failed to find a device.
    /// </remarks>
    static bool TryOpen(out Fixture? fixture) {
        if (!Fixture.TryOpen(out fixture, out var reason)) {
            if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
                Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
            }

            Assert.Skip(reason ?? "no Vulkan");

            return false;
        }

        if (BindlessTable.IsSupportedBy(fixture!.Device)) {
            return true;
        }

        var without = Adapter(fixture);

        fixture.Dispose();
        fixture = null;

        Assert.Skip(
            $"{without} offers no bindless descriptor indexing (ADR-011), so no material on it can "
            + "sample a texture and there is nothing here to photograph."
        );

        return false;
    }
}
