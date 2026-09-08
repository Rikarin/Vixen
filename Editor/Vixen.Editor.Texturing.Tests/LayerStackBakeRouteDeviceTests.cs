// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § M7's exit word one document along: a <c>.vxlayers</c> bakes, by the route a person can take.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1029">#1029</a>.</b> Both
///         material-bake callers read a <em>graph</em> — the command line's folder verb and
///         <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a>'s editor route — so an
///         artist who built a layer stack, which is the whole of M7 and M9, could not turn it into a
///         material at all. <c>LayerStackPreview</c> searches the compilation's outputs for
///         <em>one</em> usage and shows it; nothing read the rest.
///     </para>
///     <para>
///         ⚠ <b>Driven through the verb, never through <see cref="MaterialBakeRoute" />.</b> A test
///         that calls the route directly is green against a module that registers nothing, which is
///         the defect being fixed. <c>Commands.Execute</c> answers <see langword="false" /> for a
///         verb nothing registered.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip</b>, for <c>MaterialBakeRouteDeviceTests</c>' reason:
///         without one a headless run falls back to the Null device on every platform and exits 0,
///         and the maps this writes would be a black picture packed into a valid material. The
///         adapter is named in every message, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into
///         a failure.
///     </para>
/// </remarks>
public class LayerStackBakeRouteDeviceTests(ITestOutputHelper output) {
    /// <summary>What the fixture stack is called once it is an asset.</summary>
    const string Name = "Hull";

    /// <summary>A stack with two texture sets becomes two materials, through the verb.</summary>
    /// <remarks>
    ///     ⚠ <b>Two sets and not one, which is <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>
    ///     inside this assertion rather than beside it.</b> Every path in this plugin took
    ///     <c>Sets[0]</c>, and a bake that did the same would leave every other material slot of the
    ///     mesh with no material and say nothing about it — a fixture with one set could not tell
    ///     that apart from the right answer.
    /// </remarks>
    [Fact]
    public void A_committed_stack_bakes_one_material_per_texture_set_through_the_verb() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var stack = Open(fixture);

        Assert.Equal(2, stack.Document.Sets.Count);

        Assert.True(
            fixture.Shell.Commands.Execute(TexturingModule.BakeStackCommand),
            $"{adapter}: nothing answered '{TexturingModule.BakeStackCommand}'. A verb no module "
            + "registered is #1029 exactly."
        );

        var folder = Path.Combine(fixture.Paths.Assets, MaterialMapNaming.DefaultFolder);

        Assert.True(Directory.Exists(folder), $"{adapter}: {Say(fixture)}");

        var materials = Directory.GetFiles(folder, "*" + MaterialImporter.Extension)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        output.WriteLine($"{adapter}: wrote {string.Join(", ", materials)}");

        Assert.Equal([Name + "_Body", Name + "_Trim"], materials);

        // ⚠ The usages come from each set's own channels, which is what a stack has instead of Output
        // nodes. Body declares baseColor and roughness — two usages and two files, because roughness
        // is packed into an ORM whether or not occlusion and metalness are there — and Trim declares
        // baseColor alone.
        var files = Directory.GetFiles(folder, "*" + MaterialMapNaming.PortableExtension)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                Name + "_Body_baseColor.png",
                Name + "_Body_orm.png",
                Name + "_Trim_baseColor.png"
            ],
            files
        );
    }

    /// <summary>The instrument: the baked base colours are the stack's colours and not a cleared image.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What the test above cannot distinguish.</b> A route that evaluated nothing and
    ///         handed <c>MaterialBake.Encode</c> a cleared image writes the same three files with the
    ///         same names, mints the same ids and composes the same features — every assertion up
    ///         there is equally true of a material whose maps are black.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ordering of the three components rather than their values</b>, and that is
    ///         deliberate: base colour is written through an sRGB transfer, so an assertion on
    ///         <c>0.25 · 255</c> would be an assertion about the encode. A monotone transfer cannot
    ///         reorder the channels — so <c>r &lt; g &lt; b</c> for Body and <c>r &gt; g &gt; b</c>
    ///         for Trim says both that the values arrived and that the two sets did not get each
    ///         other's, which a single set could not show at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_baked_base_colours_are_each_sets_own() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeStackCommand), Say(fixture));

        var body = Texel(fixture, Name + "_Body_baseColor");
        var trim = Texel(fixture, Name + "_Trim_baseColor");

        output.WriteLine($"{adapter}: Body ({body.R}, {body.G}, {body.B}) · Trim ({trim.R}, {trim.G}, {trim.B})");

        Assert.True(
            body.R < body.G && body.G < body.B,
            $"{adapter}: Body's baked base colour is ({body.R}, {body.G}, {body.B}), and the stack "
            + "writes (0.25, 0.5, 0.75) — so either the layer's values did not reach the output or "
            + "the map beside the material is a cleared image."
        );

        Assert.True(
            trim.R > trim.G && trim.G > trim.B,
            $"{adapter}: Trim's baked base colour is ({trim.R}, {trim.G}, {trim.B}), and the stack "
            + "writes (0.75, 0.5, 0.25). Two sets baked from one stack got the same picture, which "
            + "is what a route that compiled Sets[0] twice would produce."
        );
    }

    /// <summary>⚠ The two materials record which texture set produced each, and everything else matches.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1066">#1066</a>.</b> A stack writes
    ///         one <c>.vxmat</c> per texture set and every other member of the provenance record is
    ///         identical across them — same source, same asset, same adapter, and a stack exposes no
    ///         parameters. So the two sidecars were character-identical and the only thing telling
    ///         them apart was the material's file name, which is exactly the identity
    ///         <c>MaterialBakeRecord.SourceAsset</c> exists because a file name is <em>not</em>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/681">#681</a>).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, and the second is the one that could have been broken by the fix.</b>
    ///         The sets differ — which is the finding — <em>and</em> the source keys still agree, which
    ///         is what stops a re-bake of one set adopting the other's files. Narrowing
    ///         <c>MaterialProvenance.KeyOf</c> to include the set would pass the first assertion and
    ///         change which sets a re-bake is allowed to overwrite, silently.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read off the written sidecars rather than off the records</b>, because the record
    ///         is an object this test could construct and the sidecar is the format the issue is about.
    ///         A member filled in and never written is the shape this workstream ships most often.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Each_material_records_which_texture_set_produced_it() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeStackCommand), $"{adapter}: {Say(fixture)}");

        var body = Provenance(fixture, Name + "_Body");
        var trim = Provenance(fixture, Name + "_Trim");

        output.WriteLine(
            $"{adapter}: Body set '{body.GetValueOrDefault(MaterialProvenance.SetKey)}', "
            + $"Trim set '{trim.GetValueOrDefault(MaterialProvenance.SetKey)}'"
        );

        Assert.Equal("Body", body[MaterialProvenance.SetKey]);
        Assert.Equal("Trim", trim[MaterialProvenance.SetKey]);

        // ⚠ And the sets are the *only* thing that differs about where they came from. Two sets of one
        // stack are one source, and the key that stops a different source adopting a name has to keep
        // saying so.
        Assert.Equal(MaterialProvenance.KeyIn(body), MaterialProvenance.KeyIn(trim));
        Assert.Equal(body[MaterialProvenance.SourceKey], trim[MaterialProvenance.SourceKey]);
    }

    /// <summary>A stack that does not compile bakes nothing and does not throw.</summary>
    /// <remarks>
    ///     ⚠ <b>Device-free deliberately, and it is the half that says the refusal comes back as a
    ///     sentence rather than out.</b> This runs from a command handler, and a throw out of one
    ///     takes the editor's frame with it.
    /// </remarks>
    [Fact]
    public void A_stack_that_does_not_compile_bakes_nothing_and_does_not_throw() {
        using var fixture = new TexturingFixture(graphics: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddStack(Name, Broken));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeStackCommand));

        Assert.False(
            Directory.Exists(Path.Combine(fixture.Paths.Assets, MaterialMapNaming.DefaultFolder)),
            "a stack that does not compile wrote files, so the refusal happens after the bake rather "
            + "than before it."
        );

        // ⚠ And what it refused *for*, which the assertion above cannot see. This fixture publishes
        // an `IEditorGraphics` with a null device, so "no folder was written" is equally true of a
        // stack that compiles perfectly — the route refuses a missing device too, and earlier. The
        // duplicate channel is a `LayerStackProblem` and carries no diagnostic id, so the sentence
        // itself is what says which of the two refusals ran.
        Assert.Contains("twice", Say(fixture), StringComparison.Ordinal);
    }

    /// <summary>A stack whose one set declares its channel twice, which <c>LayerStackGraph.Run</c> refuses.</summary>
    const string Broken = """
        version: 1
        name: Hull
        baseWidth: 64
        baseHeight: 64
        sets:
          - name: Body
            channels:
              - usage: baseColor
                default: [0, 0, 0, 1]
              - usage: baseColor
                default: [0, 0, 0, 1]
            layers:
              - id: base
                kind: Fill
                values:
                  baseColor: [0.5, 0.5, 0.5, 1]
        """;

    /// <summary>Scans the committed fixture in and opens it through the verb.</summary>
    /// <param name="fixture">The host.</param>
    /// <returns>The document in the layers panel.</returns>
    /// <remarks>
    ///     ⚠ <b>The resolution comes off the file rather than being set here, unlike the graph
    ///     route's fixture.</b> A <c>.vxtexgraph</c> does not carry one
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>) and a <c>.vxlayers</c>
    ///     does, so the 64² is in the committed bytes and a bake at the document's default would be
    ///     a fixture disagreeing with itself.
    /// </remarks>
    static LayerStackDocument Open(TexturingFixture fixture) {
        fixture.Project.Selection.Set(fixture.AddStack(Name, Fixture("BakeStack")));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());

        Assert.Empty(document.LoadDiagnostics);
        Assert.Equal(64, document.Document.BaseWidth);

        return document;
    }

    /// <summary>The first texel of a baked map.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="name">The map's file name, without the extension.</param>
    /// <returns>Its red, green and blue.</returns>
    static (byte R, byte G, byte B) Texel(TexturingFixture fixture, string name) {
        var file = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            name + MaterialMapNaming.PortableExtension
        );

        Assert.True(File.Exists(file), $"there is no map at '{file}': {Say(fixture)}");

        var picture = PngCodec.Decode(File.ReadAllBytes(file));

        return (picture.Pixels[0], picture.Pixels[1], picture.Pixels[2]);
    }

    /// <summary>The provenance block a baked material's sidecar carries.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="name">The material's file name, without the extension.</param>
    /// <returns>Its sidecar's extensions.</returns>
    static IReadOnlyDictionary<string, string> Provenance(TexturingFixture fixture, string name) {
        var file = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            name + MaterialImporter.Extension
        );

        Assert.True(File.Exists(file), $"no material was written at {file}: {Say(fixture)}");

        return AssetMetaFile.ReadFile(AssetMetaFile.PathFor(file)).Extensions;
    }

    /// <summary>The text of a committed stack, from beside the test assembly.</summary>
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
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name + LayerStackDocument.Extension)
        );

    /// <summary>What the bake said, for a message that would otherwise only say "no file".</summary>
    /// <param name="fixture">The host, whose notifications the verb writes into.</param>
    /// <returns>The sentences, or a note that there were none.</returns>
    static string Say(TexturingFixture fixture) =>
        fixture.Shell.Notifications.History.Count > 0
            ? string.Join(
                " · ",
                fixture.Shell.Notifications.History.Select(one => one.Message + ": " + one.Detail)
            )
            : "The bake said nothing at all, which is itself the finding.";
}
