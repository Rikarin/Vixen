// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Texturing.Layers;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § M11's last verb: a painted layer stack becomes a splat map, through the editor.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>, and therefore the road
///         to <a href="https://github.com/Rikarin/Vixen/issues/1073">#1073</a>.</b> The pixels existed
///         — M9 paints per-layer masks and <c>LayerStackGraph</c> composites them — and nothing put
///         three or four of them into one RGBA file. An artist who wanted a layered material exported
///         the masks by hand, packed them in another editor and named the result in the
///         <c>.vxmat</c>'s <c>textures:</c> block.
///     </para>
///     <para>
///         ⚠ <b>Driven through the verb, never through <c>MaterialBakeRoute</c>.</b> A test that calls
///         the route directly is green against a module that registers nothing, which is the shape
///         this workstream ships most often. <c>Commands.Execute</c> answers <see langword="false" />
///         for a verb nothing registered.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip.</b> Without one a headless run falls back to the Null
///         device on every platform and exits 0 — and every coverage this reads back would be a black
///         picture packed into a valid file, which is a splat map whose weights sum to nothing and a
///         material that draws its first layer everywhere.
///     </para>
/// </remarks>
public class SplatBakeDeviceTests(ITestOutputHelper output) {
    /// <summary>What the fixture stack and its material are called.</summary>
    const string Name = "Ground";

    /// <summary>⚠ The closed form: the weights are the stack's own <c>over</c> resolution.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48's oracle, on a device, in the direction every wrong answer draws.</b> The
    ///         fixture's three layers are masked at 1, 0.25 and 0.5 bottom to top, so resolving them
    ///         top down gives snow 0.5, moss 0.25 of the half that is left, and rock the remainder:
    ///         <c>(0.375, 0.125, 0.5)</c> in red, green and blue, summing to one, with no fourth
    ///         layer and therefore no weight in alpha.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every one of those four numbers is different, which is what makes the assertion
    ///         discriminate.</b> Packed in the panel's order rather than the material's, red and blue
    ///         swap — a lit surface of the wrong layers. Packed as the masks stand, with no
    ///         resolution, the answer is <c>(1, 0.25, 0.5)</c> and the shader's normalisation blends
    ///         three layers where the artist painted snow over moss over rock. Packed with the ORM
    ///         path's alpha the fourth channel is 255 and — after that same normalisation — the
    ///         surface is a layer the material does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the one thing this could not see if the arithmetic were done twice</b>: the
    ///         coverages come off the GPU through <c>LayerStackGraph.Weights</c>, which is the
    ///         <em>picture</em> path with its colour replaced by one — the same masks, the same
    ///         folding, the same <c>Blend</c> kernel. A coverage computed in C# beside the packer
    ///         would agree with the packer and with nothing else.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_written_weights_are_the_stacks_own_resolution() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Open(fixture);
        Layered(fixture, layers: 3);

        Assert.True(
            fixture.Shell.Commands.Execute(TexturingModule.BakeSplatCommand),
            $"{adapter}: nothing answered '{TexturingModule.BakeSplatCommand}'. A verb no module "
            + "registered is the finished-thing-nothing-calls shape exactly."
        );

        var splat = Texel(fixture, Name + "_splat");

        output.WriteLine($"{adapter}: splat ({splat[0]}, {splat[1]}, {splat[2]}, {splat[3]}) · {Say(fixture)}");

        // ±3 of 255, which is what a mask folded into an opacity, evaluated in half precision and
        // quantised into a byte can carry — not a tolerance on the rule. Every wrong answer above is
        // further away than a whole channel.
        Assert.InRange(splat[0], 93, 99);
        Assert.InRange(splat[1], 29, 35);
        Assert.InRange(splat[2], 125, 131);

        // ⚠ Exactly zero rather than a range: an alpha this writer invented is 255, and one it left
        // as a weight for a layer that does not exist is nothing at all.
        Assert.Equal(0, splat[3]);
    }

    /// <summary>And the map is bound onto the feature under the one name a host pairs.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the written <c>.vxmat</c> rather than off the outcome.</b> A route that
    ///     resolved the GUID and never wrote the material would satisfy every assertion about the
    ///     texture — and the material left behind would name no splat map, leave <c>splatIndex</c> at
    ///     nought, and blend its layers by the bindless table's magenta checker.
    /// </remarks>
    [Fact]
    public void The_material_names_the_map_and_says_how_many_channels_it_paints() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Open(fixture);
        Layered(fixture, layers: 3);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeSplatCommand), $"{adapter}: {Say(fixture)}");

        var content = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(Material(fixture)));
        var feature = Assert.Single(content.Features.OfType<TexturedMaterialLayersFeature>());

        Assert.Equal(3, feature.PaintedChannels);
        Assert.Equal(new TexturedMaterialLayersFeature().SplatMap, feature.SplatMap);

        var bound = Assert.Single(
            content.Textures,
            texture => texture.Parameter == new TexturedMaterialLayersFeature().SplatMap
        );

        Assert.NotEqual(AssetReference.Null, bound.Texture);

        // ⚠ And the normal map the fixture's material already carried is still there. A splat write
        // that went through `MaterialBake.Material` would have replaced `Features` and `Textures`
        // whole and handed back a material with neither.
        Assert.Contains(content.Features, one => one is TexturedNormalMapFeature);
        output.WriteLine($"{adapter}: {content.Features.Length} features, {content.Textures.Length} textures");
    }

    /// <summary>⚠ And a second write refuses once somebody has painted on the map.</summary>
    /// <remarks>
    ///     <b>The guard the second write path would have forgotten.</b> § D4's digest exists so that a
    ///     file whose bytes are no longer what the tool wrote is flagged rather than overwritten,
    ///     because the commonest reason for the mismatch is that somebody painted on it — and a splat
    ///     map is exactly the kind of file an artist touches up by hand. It is inherited here rather
    ///     than reimplemented: <c>WriteSplat</c> reads the same digest key
    ///     <c>MaterialProvenance.Painted</c> reads.
    /// </remarks>
    [Fact]
    public void A_splat_map_somebody_painted_over_is_not_silently_replaced() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Open(fixture);
        Layered(fixture, layers: 3);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeSplatCommand), $"{adapter}: {Say(fixture)}");

        var file = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            Name + "_splat" + MaterialMapNaming.PortableExtension
        );

        var painted = PngCodec.Encode(new Bitmap(64, 64, new byte[64 * 64 * 4]));

        File.WriteAllBytes(file, painted);
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeSplatCommand));

        Assert.Equal(painted, File.ReadAllBytes(file));

        // ⚠ And it said so, which is the half that makes the refusal actionable rather than a bake
        // that quietly did nothing.
        Assert.Contains(ProjectMaterialBaker.Overpaint, Say(fixture), StringComparison.Ordinal);
    }

    /// <summary>A stack with no material of its name writes no texture at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Device-free deliberately, and the assertion is that nothing was written.</b> A splat
    ///     map's channels are one material's layer indices, so a file written beside no material is a
    ///     texture nothing samples and nothing explains — the resident-and-unread shape. The sentence
    ///     is asserted too, because "no folder" is equally true of the missing-device refusal this
    ///     fixture also produces.
    /// </remarks>
    [Fact]
    public void A_stack_with_no_material_to_bind_onto_writes_nothing() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeSplatCommand));

        Assert.False(
            File.Exists(
                Path.Combine(
                    fixture.Paths.Assets,
                    MaterialMapNaming.DefaultFolder,
                    Name + "_splat" + MaterialMapNaming.PortableExtension
                )
            ),
            "a stack with no material wrote a splat map, so the refusal happens after the write."
        );

        // ⚠ And what it refused *for*, which the assertion above cannot see. This fixture publishes
        // an `IEditorGraphics` with a null device, so "no file" is equally true of a stack whose
        // material is right there — the route refuses a missing device too. The sentence is the only
        // thing that says which of the two ran, and it says the project-shaped one: a missing
        // material is a fact about the project rather than about the host, so it is asked first.
        Assert.Contains(Name + MaterialImporter.Extension, Say(fixture), StringComparison.Ordinal);
    }

    /// <summary>Scans the committed fixture in and opens it through the verb.</summary>
    static LayerStackDocument Open(TexturingFixture fixture) {
        fixture.Project.Selection.Set(fixture.AddStack(Name, Fixture("SplatStack")));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());

        Assert.Empty(document.LoadDiagnostics);
        Assert.Single(document.Document.Sets);

        return document;
    }

    /// <summary>Writes the layered material the splat map is for, as an author would have one.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="layers">How many layers it lists.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Serialised through <c>YamlSerializer</c> rather than typed as YAML</b>, so that a
    ///         fixture cannot disagree with the format the editor writes — a hand-written mapping that
    ///         binds to a <em>default</em> feature would give this test a material with no layers and
    ///         the refusal it produces would look like the rule working.
    ///     </para>
    ///     <para>
    ///         <b>The normal map beside it is not decoration.</b> It is what
    ///         <see cref="The_material_names_the_map_and_says_how_many_channels_it_paints" /> reads to
    ///         tell a patch from a compose.
    ///     </para>
    /// </remarks>
    static void Layered(TexturingFixture fixture, int layers) {
        var content = new MaterialContent {
            Features = [
                new TexturedMaterialLayersFeature {
                    Layers = [
                        .. Enumerable.Range(0, layers)
                            .Select(at => new MaterialLayerValue(new Vector3(0.25f * at), 0f, 0.5f, 1f))
                    ]
                },
                new TexturedNormalMapFeature()
            ]
        };

        var file = Material(fixture);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, YamlSerializer.ToYaml(content));
        fixture.Project.Assets.Scan();

        Assert.True(
            fixture.Project.Assets.TryGetByPath(fixture.Paths.Relative(file), out _),
            "the scan did not pick the material up, so the splat write has nothing to bind onto."
        );
    }

    /// <summary>Where the fixture's material is.</summary>
    static string Material(TexturingFixture fixture) =>
        Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            Name + MaterialImporter.Extension
        );

    /// <summary>The first texel of a written map, all four channels.</summary>
    static byte[] Texel(TexturingFixture fixture, string name) {
        var file = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            name + MaterialMapNaming.PortableExtension
        );

        Assert.True(File.Exists(file), $"there is no map at '{file}': {Say(fixture)}");

        return PngCodec.Decode(File.ReadAllBytes(file)).Pixels[..4];
    }

    /// <summary>The text of a committed stack, from beside the test assembly.</summary>
    /// <remarks>
    ///     ⚠ <see cref="AppContext.BaseDirectory" /> and not <c>[CallerFilePath]</c>: CI compiles with
    ///     <c>DeterministicSourcePaths</c>, which rewrites the repository root to <c>/_/</c>.
    /// </remarks>
    static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + LayerStackDocument.Extension));

    /// <summary>What the verb said, for a message that would otherwise only say "no file".</summary>
    static string Say(TexturingFixture fixture) =>
        fixture.Shell.Notifications.History.Count > 0
            ? string.Join(" · ", fixture.Shell.Notifications.History.Select(one => one.Message + ": " + one.Detail))
            : "The verb said nothing at all, which is itself the finding.";
}
