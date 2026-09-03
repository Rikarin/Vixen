// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A material authored as a shader graph, photographed through the real forward pass.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>"The tests pass" is not evidence that a material draws, and every other test on this
///         path is a test that passes.</b> The graph compiles, the source binds against the library,
///         the composition resolves, the variant reaches SPIR-V, the predicted parameter names match
///         Raven's reflection — all true, all green, and all of them equally true of a shader that
///         puts nothing on the screen. What is asserted here is a picture.
///     </para>
///     <para>
///         <b>The oracle is a differential rather than a committed PNG, and that is the stronger
///         claim.</b> <c>Master/Surface</c> writes exactly the channels <c>MetalRoughnessSurface</c>
///         writes, from exactly the same three numbers, through the same <c>Brdf.F0FromMetalness</c>
///         — so a graph carrying a colour and a hand-written feature carrying the same colour are two
///         spellings of one surface, and the frames must agree. Nothing in that depends on a
///         reference image, on this machine's tone map, on the sun's intensity or on the exposure the
///         frame settled at: whatever those do, they do identically to both.
///     </para>
///     <para>
///         <b>Two frames of one scene rather than two boxes in one frame.</b> Two boxes are lit from
///         different angles and seen from different ones, so a specular lobe alone would separate
///         them; the same box rendered twice differs in nothing but the material, which is the
///         variable under test.
///     </para>
///     <para>
///         ⚠ <b>And a second colour, because "they agree" is a predicate a broken renderer satisfies
///         easily.</b> Two frames that are both the clear colour agree perfectly. So the same
///         comparison is run against a graph of a <em>different</em> colour and has to fail —
///         which is what says the picture is of the material rather than of the room.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class GraphMaterialImageTests {
    /// <summary>The colour both spellings of the surface carry.</summary>
    /// <remarks>
    ///     Saturated and asymmetric, so that a channel swap or a dropped channel is visible as a
    ///     difference rather than absorbed by a grey.
    /// </remarks>
    static readonly Vector3 Authored = new(0.82f, 0.21f, 0.11f);

    /// <summary>Something else, for the run that has to disagree.</summary>
    static readonly Vector3 Other = new(0.11f, 0.21f, 0.82f);

    const float Metalness = 0f;
    const float Roughness = 0.45f;

    /// <summary>The frame the two renderings share. Deliberately the tier suite's own.</summary>
    static StandardFrameAsset Frame => new() {
        Name = "GraphMaterial",
        Shadows = ShadowMode.Cascades,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Fxaa,
        Exposure = ExposureMode.Automatic,
        Particles = false
    };

    /// <summary>
    ///     A graph-authored material draws, and draws the surface the graph describes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument is checked before the claim.</b> A frame in which the slab never drew
    ///     is a frame of the floor and the sky, and it would agree with another frame in which the
    ///     slab never drew — so the run first asserts that replacing the material changes the picture
    ///     at all. Only then does it assert that the graph's spelling and the library's spelling of
    ///     one surface agree.
    /// </remarks>
    [Fact]
    public void A_graph_authored_material_draws_what_a_hand_written_one_does() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var handWritten = Render(fixture!, Library(Authored), null);
            var graph = Compile(Authored);
            var authored = Render(fixture!, Material(graph), graph);

            // ⚠ First: the material is visible at all. A pair of frames that both drew nothing would
            // satisfy every assertion below, and "nothing drew" is what a missing variant, an
            // unfilled slot or a culled pass all look like.
            var different = Render(fixture!, Library(Other), null);
            var control = GoldenImage.Compare(handWritten, different, Tolerance.Shaded);

            Assert.False(
                control.Matches,
                "Two materials of different colours produced the same frame, so this scene is not a "
                + "picture of its material and nothing below it means anything."
            );

            // And then the claim: one surface, two spellings.
            var comparison = GoldenImage.Compare(handWritten, authored, Tolerance.Shaded);

            Assert.True(
                comparison.Matches,
                $"A graph-authored material drew differently from the hand-written feature it spells: "
                + $"{comparison.DifferingPixels} of {comparison.TotalPixels} pixels differ, worst "
                + $"channel {comparison.WorstChannel} at {comparison.WorstAt}, mean "
                + $"{comparison.MeanChannel:F3}."
            );
        }
    }

    /// <summary>
    ///     A graph of a different colour draws a different picture, which is the sabotage held in the
    ///     suite.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half of the pair that can be false.</b> The test above would pass on a renderer
    ///     that ignored the graph entirely and drew the hand-written material twice; this one would
    ///     not. Together they say the picture came from the graph.
    /// </remarks>
    [Fact]
    public void A_graph_of_another_colour_draws_another_picture() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using (fixture) {
            var one = Compile(Authored);
            var two = Compile(Other);

            var first = Render(fixture!, Material(one), one);
            var second = Render(fixture!, Material(two), two);

            var comparison = GoldenImage.Compare(first, second, Tolerance.Shaded);

            Assert.False(
                comparison.Matches,
                "Two graphs carrying different colours drew the same frame, so what reached the pass "
                + "was not the graph's value."
            );
        }
    }

    /// <summary>Compiles a graph whose surface is one colour, one metalness and one roughness.</summary>
    /// <remarks>
    ///     ⚠ <b>Values on the master rather than property nodes, deliberately.</b> A property is a
    ///     uniform a <em>material</em> supplies, which is a second thing that can be wrong; a
    ///     constant on the master reaches the shader as a literal, so a difference in the picture is
    ///     the emission and not the parameter path. The parameter path has its own oracle in
    ///     <c>GraphMaterialTests</c>, held against Raven's reflection.
    /// </remarks>
    static ShaderGraphSource Compile(Vector3 colour) {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        var graph = new NodeGraphModel { Name = "Painted" };
        var master = graph.Add("Master/Surface");

        master.SetValue("BaseColour", colour.X, colour.Y, colour.Z);
        master.SetValue("Metallic", Metalness);
        master.SetValue("Roughness", Roughness);

        var result = new ShaderGraphCompiler(registry).Compile(graph);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(ShaderGraphKind.Surface, result.Value.Kind);

        return result.Value;
    }

    /// <summary>The material a graph composes into.</summary>
    static Material Material(ShaderGraphSource source) => Composed([ShaderGraphMaterial.Feature(source)]);

    /// <summary>The same surface, spelled with the library's own feature.</summary>
    static Material Library(Vector3 colour) => Composed([
        new MetalRoughnessFeature { BaseColor = colour, Metalness = Metalness, Roughness = Roughness }
    ]);

    static Material Composed(List<IMaterialFeature> features) {
        var compilation = MaterialCompiler.Compile(new() { ShaderName = "ForwardPlus", Features = features });

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>Stages one slab over a floor, renders the frame, and hands back the picture.</summary>
    /// <param name="fixture">The device.</param>
    /// <param name="slab">What the slab is made of.</param>
    /// <param name="source">
    ///     The graph whose text has to be in the compilation, or null when the material names only
    ///     shaders the library already holds.
    /// </param>
    static Bitmap Render(Fixture fixture, Material slab, ShaderGraphSource? source) {
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(new(fixture.Device), _ => Compiler(source)));

        using var scene = TierScene.Open(fixture, effects, new() { Game = Frame }, QualityTier.High);

        var casters = scene.Stages.TryGetValue("Shadow", out var shadow) ? shadow.Mask : default;
        var opaque = scene.Stages["Opaque"].Mask;

        // A grey floor, so the slab is the only thing in the frame whose colour is the variable.
        scene.Box(new(0.4f, -0.25f, -0.6f), new(9f, 0.25f, 9f), Grey, opaque);

        // And the slab, big enough in frame that a difference in it is a difference in the picture
        // rather than a handful of pixels the tolerance's mean absorbs.
        scene.Box(new(0.2f, 0.9f, -0.4f), new(1.5f, 0.9f, 1.1f), slab, opaque | casters);

        scene.Commit(opaque);

        // Several frames, because the automatic exposure is a filter over its own history and a
        // single frame is whatever it started at — which would differ between two runs for a reason
        // that is not the material.
        return scene.Frames(8);
    }

    static Material Grey => Library(new(0.35f, 0.35f, 0.35f));

    /// <summary>The library, plus the graph's own text when there is one.</summary>
    /// <remarks>
    ///     ⚠ <b>The one function this test genuinely adds.</b> <c>RavenEffects.Everything</c> builds a
    ///     compiler from <em>paths</em>, and a graph's shader is not a file and must not become one —
    ///     which is the same argument <c>ShaderGraphSources</c> makes for the editor and the build.
    ///     <c>RavenEffectCompiler.FromSources</c> is the seam, and it exists because the graph
    ///     previews needed it first.
    /// </remarks>
    static RavenEffectCompiler Compiler(ShaderGraphSource? source) {
        List<(string Name, string Text)> sources = [
            .. Directory.GetFiles(RavenEffects.Library, "*.rvn", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => (Path.GetFileName(file), File.ReadAllText(file)))
        ];

        if (source is not null) {
            sources.Add((source.Name + ".rvn", source.Source));
        }

        return RavenEffectCompiler.FromSources(sources);
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
