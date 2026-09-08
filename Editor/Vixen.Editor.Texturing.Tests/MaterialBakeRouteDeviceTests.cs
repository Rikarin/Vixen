// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § M5's first exit word: a graph bakes, by the route a person can take.</summary>
/// <remarks>
///     <para>
///         <b>The half <c>BakedMaterialImageTests</c> is silent about</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a>. That golden builds a
///         <c>TexturePlan</c> by hand, bakes it and photographs the material through the real frame,
///         which is an honest measurement of the evaluator and of the packer and says nothing at all
///         about whether an artist can get a material out of the tool. Every piece existed and had
///         tests; <c>new ProjectMaterialBaker</c> had one caller outside tests, a command-line verb
///         that reads a folder of PNGs, and <c>TexturingModule</c> contained the string <c>bake</c>
///         nowhere.
///     </para>
///     <para>
///         ⚠ <b>So this starts at a file and ends at a <c>.vxmat</c>, and every step between is the
///         product's.</b> A committed <c>.vxtexgraph</c> is scanned into a project, opened by the
///         module's own <c>Open Texture Graph</c> verb into a <c>TextureGraphDocument</c>, and baked
///         by the module's own <c>Bake Material</c> verb. Nothing here constructs a
///         <see cref="MaterialBakeRoute" />, a compiler or a plan.
///     </para>
///     <para>
///         ⚠ <b>What it would say if the route were removed and the evaluator went on working:</b>
///         <c>Commands.Execute</c> answers <see langword="false" /> for a verb nothing registered, and
///         the material would not be on disk — so the first two assertions are red. That question was
///         asked before the test was written, because the obvious version of it calls
///         <c>MaterialBakeRoute.Bake</c> directly and is green against a module that registers no verb
///         at all, which is precisely the defect #1009 is.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip</b>, for <c>LayerStackBakeDeviceTests</c>' reason:
///         without one a headless run falls back to the Null device on every platform and exits 0,
///         and the maps this writes would be a black picture packed into a valid material. The
///         adapter is named in every message, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a
///         failure.
///     </para>
/// </remarks>
public class MaterialBakeRouteDeviceTests(ITestOutputHelper output) {
    /// <summary>What the fixture graph is called once it is an asset, and therefore what the set is.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the fixture file's own name.</b> The material is named after the <em>graph
    ///     asset</em> — <c>TexturingModule.BakeMaterial</c> takes the stem of the document's path —
    ///     so a test whose asset and fixture shared a name could not tell the two apart, and a route
    ///     that named the set after the wrong one would pass.
    /// </remarks>
    const string Material = "Hull";

    /// <summary>The graph on disk becomes a material in the project, through the verb.</summary>
    [Fact]
    public void A_committed_graph_bakes_a_material_through_the_bake_verb() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var document = Open(fixture);

        Assert.True(
            fixture.Shell.Commands.Execute(TexturingModule.BakeCommand),
            $"{adapter}: nothing answered '{TexturingModule.BakeCommand}'. Doc 48 § M5's first exit "
            + "word is that a graph bakes, and a verb no module registered is #1009 exactly."
        );

        var folder = Path.Combine(fixture.Paths.Assets, MaterialMapNaming.DefaultFolder);
        var vxmat = Path.Combine(folder, Material + MaterialImporter.Extension);

        Assert.True(File.Exists(vxmat), $"{adapter}: {Say(fixture)} There is no material at '{vxmat}'.");

        // ⚠ Two files for three usages, and that is the arithmetic rather than a miscount: occlusion,
        // roughness and metalness are three usages and one ORM file, which is the whole of what
        // `MaterialMapNaming.Packed` does. A route that wrote one file per Output node would put
        // three here and a material naming a roughness map no feature reads.
        var files = Directory.GetFiles(folder, "*" + MaterialMapNaming.PortableExtension)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        output.WriteLine($"{adapter}: wrote {string.Join(", ", files)} beside {Path.GetFileName(vxmat)}");

        Assert.Equal([Material + "_baseColor.png", Material + "_orm.png"], files);

        // The material names its maps by the ids the scan minted, which is the property the
        // scan-then-read-back dance exists for and the one a bake writing files and no assets breaks.
        var material = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(vxmat));

        Assert.Equal(2, material.Textures.Length);

        foreach (var texture in material.Textures) {
            Assert.NotEqual(AssetReference.Null, texture.Texture);
            Assert.True(
                fixture.Project.Assets.TryGetByGuid(texture.Texture.Asset, out _),
                $"{adapter}: '{texture.Parameter}' names an id the asset database does not have, so the "
                + "material resolves the bindless fallback and shades a magenta checker."
            );
        }

        // ⚠ The features are what say the usages reached the material rather than only the files.
        // A base colour makes the surface textured and the ORM file adds the second feature; a bake
        // that wrote both files and composed neither is a material that draws white.
        Assert.Contains(material.Features, feature => feature is TexturedMetalRoughnessFeature);
        Assert.Contains(material.Features, feature => feature is TexturedOrmFeature);

        Assert.True(MaterialShading.TryResolve(material.Shading, out var shading));
        Assert.False(MaterialCompiler.Compile(material.ToDescriptor(shading)).Failed);
    }

    /// <summary>The instrument: the baked base colour is the graph's picture and not a constant.</summary>
    /// <remarks>
    ///     ⚠ <b>What the test above cannot distinguish.</b> A route that evaluated nothing and handed
    ///     <see cref="MaterialBake.Encode" /> a cleared image writes the same two files, mints the
    ///     same ids, composes the same two features and compiles — every assertion up there is
    ///     equally true of a material whose maps are black. The fixture's base colour is a four-cell
    ///     checker for exactly this reason, and this is what says the variation survived the
    ///     evaluator, the PNG encode and the write.
    /// </remarks>
    [Fact]
    public void The_baked_base_colour_carries_the_graphs_checker() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand));

        var file = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            Material + "_baseColor" + MaterialMapNaming.PortableExtension
        );

        Assert.True(File.Exists(file), $"{adapter}: {Say(fixture)}");

        var picture = Vixen.Core.Imaging.PngCodec.Decode(File.ReadAllBytes(file));
        byte low = 255;
        byte high = 0;

        for (var index = 0; index < picture.Pixels.Length; index += 4) {
            low = Math.Min(low, picture.Pixels[index]);
            high = Math.Max(high, picture.Pixels[index]);
        }

        output.WriteLine($"{adapter}: baked baseColor red runs {low}…{high} over {picture.Width}×{picture.Height}");

        // A checker is 0 against 255 before anything encodes it. A spread rather than a count of
        // distinct values, for `LayerStackBakeDeviceTests`' reason: a dithered flat grey produces
        // many distinct texels and no picture.
        Assert.True(
            high - low >= 128,
            $"{adapter}: the baked base colour runs only {low}…{high}, so the file beside the material "
            + "is a near-constant. The fixture graph's Source/Checker did not reach the output."
        );
    }

    /// <summary>A graph that does not compile refuses, and writes nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Device-free deliberately, and it is the half that says the refusal comes back as a
    ///     sentence rather than out.</b> This runs from a command handler, and a throw out of one
    ///     takes the editor's frame with it — so the assertion is that the verb <em>answered</em> and
    ///     that the project has no material, not that an exception had a good message.
    /// </remarks>
    [Fact]
    public void A_graph_that_does_not_compile_bakes_nothing_and_does_not_throw() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddGraph(Material));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<TextureGraphDocument>());
        var target = document.Graph.Nodes.Single(node => node.Type == "Output/Output");

        // A blur wired to nothing, replacing the starter graph's colour. The starter graph compiles,
        // so this is what makes the run a claim about a refusal rather than about an empty document.
        foreach (var node in document.Graph.Nodes.Where(node => node.Type == "Source/Uniform").ToArray()) {
            document.Graph.Remove(node.Id, out _);
        }

        var blur = document.Graph.Add("Filters/Blur");

        document.Graph.Connect(new(blur.Id, "Out"), new(target.Id, "Input"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand));

        Assert.False(
            Directory.Exists(Path.Combine(fixture.Paths.Assets, MaterialMapNaming.DefaultFolder)),
            "a graph that does not compile wrote files, so the refusal happens after the bake rather "
            + "than before it."
        );

        // ⚠ And what it refused *for*, which the assertion above cannot see. This fixture publishes
        // an `IEditorGraphics` with a null device, so "no folder was written" is equally true of the
        // untouched starter graph — the route refuses a missing device too, and earlier. Without
        // this line the broken graph the test builds is not load bearing and the case is green on a
        // route that never compiled anything.
        // `Refused` writes each error as "<id>: <message>", and no device refusal carries an id —
        // so one positive assertion is enough to say which of the two refusals ran.
        Assert.Contains("TG0002", Say(fixture), StringComparison.Ordinal);
    }

    /// <summary>Every usage an Output node accepts is one the baker can write.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two nine-item lists in two assemblies, and nothing joined them.</b>
    ///         <c>TextureUsages.Known</c> is what an <c>Output</c> node's <c>Usage</c> setting
    ///         accepts, in <c>Vixen.Editor.TextureGraph</c>; <see cref="MaterialMapNaming.Every" />
    ///         is what a bake can write, in <c>Vixen.Editor.Assets</c>. They agree today and neither
    ///         reads the other — so a tenth usage added to one is a map the artist can name and the
    ///         bake silently drops, or a map the baker packs that no node can produce.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked through the compiler rather than by comparing two arrays</b>, because
    ///         <c>TextureUsages</c> is internal to the other assembly and because the compiler is
    ///         what actually decides: it canonicalises the author's text and reports
    ///         <c>SettingNotAccepted</c> for anything else. A usage that reaches
    ///         <c>TextureGraphCompilation.Outputs</c> is one an artist can really author.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_usage_an_output_node_accepts_is_one_the_baker_writes() {
        using var fixture = new TexturingFixture();

        fixture.Project.Selection.Set(fixture.AddGraph("Usages"));

        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.Project.Selection.Primary,
            Path.Combine(fixture.Paths.Assets, "Usages" + TextureGraphDocument.Extension)
        );

        var target = document.Graph.Nodes.Single(node => node.Type == "Output/Output");

        foreach (var usage in MaterialMapNaming.Every) {
            var suffix = MaterialMapNaming.Suffix(usage);

            target.SetText("Usage", suffix);

            var compilation = document.Compile();

            Assert.DoesNotContain(compilation.Diagnostics, one => one.Severity == NodeSeverity.Error);
            Assert.Equal(
                suffix,
                Assert.Single(compilation.Outputs).Usage
            );
        }

        // ⚠ The other direction, and this test asserted only the first one until a reviewer read
        // its name against its body. Walking the *baker's* list and asking the compiler to accept
        // each proves "every usage the baker writes is one a node accepts" — which is silent on the
        // failure the remark above names first: a tenth entry in `TextureUsages.Known` is a map an
        // artist can author and the bake drops, and every case above would stay green.
        //
        // Read out of the refusal rather than off the list, for the reason the remark gives — the
        // list is internal to the other assembly, and the refusal is what actually decides.
        target.SetText("Usage", "nothing-is-called-this");

        var refusal = Assert.Single(
            document.Compile().Diagnostics.Where(one => one.Severity == NodeSeverity.Error)
        );

        const string marker = "which is not one of ";

        var at = refusal.Message.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(at >= 0, "the refusal no longer enumerates what it accepts: " + refusal.Message);

        var list = refusal.Message[(at + marker.Length)..];
        var stop = list.IndexOf('.', StringComparison.Ordinal);

        Assert.Equal(
            MaterialMapNaming.Every.Select(MaterialMapNaming.Suffix).Order(StringComparer.Ordinal),
            (stop < 0 ? list : list[..stop])
                .Split(", ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.Ordinal)
        );
    }

    /// <summary>Scans the committed fixture in, opens it through the verb, and shrinks it.</summary>
    /// <returns>The document on the canvas.</returns>
    /// <remarks>
    ///     ⚠ <b>64² rather than the document's 1024² default, and it is set on the document because
    ///     that is the only place it lives.</b> The base resolution is not stored in a
    ///     <c>.vxtexgraph</c> — <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a> owns
    ///     the file half — so a fixture cannot carry it, and a bake at the default would spend the
    ///     run on a mip chain this asserts nothing about.
    /// </remarks>
    static TextureGraphDocument Open(TexturingFixture fixture) {
        fixture.Project.Selection.Set(fixture.AddGraph(Material, Fixture("BakeRoute")));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<TextureGraphDocument>());

        Assert.Empty(document.LoadDiagnostics);

        document.BaseWidth = 64;
        document.BaseHeight = 64;

        return document;
    }

    /// <summary>The text of a committed graph, from beside the test assembly.</summary>
    /// <param name="name">Its file name, without the extension.</param>
    /// <returns>The YAML.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="AppContext.BaseDirectory" /> and not <c>[CallerFilePath]</c></b>: CI
    ///     compiles with <c>DeterministicSourcePaths</c>, which rewrites the repository root to
    ///     <c>/_/</c> — so a fixture found through the compiled path is missing on all three runners
    ///     at once and present on every developer machine.
    /// </remarks>
    static string Fixture(string name) =>
        File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name + TextureGraphDocument.Extension)
        );

    /// <summary>What the last bake said, for a message that would otherwise only say "no file".</summary>
    /// <param name="fixture">The host, whose notifications the verb writes into.</param>
    /// <returns>The sentence, or a note that there was none.</returns>
    /// <remarks>
    ///     ⚠ <b>The refusal is the whole diagnosis and it is not on any exception.</b> Every way this
    ///     bake can decline — a graph that did not compile, a host with no device, a bitmap the
    ///     project cannot resolve, a map somebody painted over — comes back as a notification, so a
    ///     failure that only reported "there is no file at …" would name none of them.
    /// </remarks>
    static string Say(TexturingFixture fixture) =>
        fixture.Shell.Notifications.History.Count > 0
            ? string.Join(
                " · ",
                fixture.Shell.Notifications.History.Select(one => one.Message + ": " + one.Detail)
            )
            : "The bake said nothing at all, which is itself the finding.";
}
