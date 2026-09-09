// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.TextureGraph;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The editor's ask for a height march, through the verb rather than through the route.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>'s last owed half.</b>
///         <c>MaterialBake.Material</c> preserves a <c>ParallaxOcclusionFeature</c> the material
///         already carries and composes none of its own — which by construction cannot help a
///         <em>first</em> bake, so the artist's route was bake, hand-edit the <c>.vxmat</c>, bake
///         again. The command line grew <c>--parallax</c>; this is the same rule with an editor verb
///         in front of it.
///     </para>
///     <para>
///         ⚠ <b>Driven through <c>Shell.Commands.Execute</c>, never through
///         <c>MaterialBakeRoute.Bake</c>.</b> A test that called the route is green against a module
///         that registers no verb at all, which is this workstream's commonest defect shape and
///         exactly what the route it exercises was written to close.
///     </para>
/// </remarks>
/// <param name="output">Where the material's feature list goes, so a failure names what was composed.</param>
public class ParallaxBakeDeviceTests(ITestOutputHelper output) {
    /// <summary>What the graph asset is called, and therefore what the material is.</summary>
    const string Material = "Relief";

    /// <summary>The ask composes a march, seated where the compiler will take it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The position is proved by the material <em>compiling</em> rather than by an index
    ///         this test declared.</b> <c>ParallaxOcclusionFeature</c> is
    ///         <c>MaterialFeatureStage.Coordinate</c> and <c>MaterialCompiler</c> refuses one listed
    ///         behind a feature that samples — so the naive wiring, appending it to what the bake
    ///         wrote, produces a <c>.vxmat</c> the verb itself created and the importer then rejects.
    ///         Asserting an index would hold for a build whose compiler rule had gone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the map name is asserted against the feature's own default</b>, not against
    ///         the instance the entry was written from: comparing those two holds for every spelling,
    ///         including the one that leaves <c>heightIndex</c> at nought and marches the bindless
    ///         table's fallback checker.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_parallax_verb_composes_a_march_the_compiler_takes() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(
            fixture.Shell.Commands.Execute(TexturingModule.ParallaxBakeCommand),
            $"{adapter}: nothing answered '{TexturingModule.ParallaxBakeCommand}'. A verb no module "
            + "registered is the finished-thing-nothing-calls shape exactly."
        );

        var content = Baked(fixture, adapter);

        output.WriteLine($"{adapter}: {string.Join(", ", content.Features.Select(one => one.GetType().Name))}");

        var parallax = Assert.Single(content.Features.OfType<ParallaxOcclusionFeature>());

        Assert.Equal(new ParallaxOcclusionFeature().HeightMap, parallax.HeightMap);

        Assert.Contains(
            content.Textures,
            texture => texture.Parameter == new ParallaxOcclusionFeature().HeightMap
        );

        Assert.True(MaterialShading.TryResolve(content.Shading, out var shading));

        var compilation = MaterialCompiler.Compile(content.ToDescriptor(shading));

        Assert.False(
            compilation.Failed,
            $"{adapter}: the verb wrote a material the compiler refuses: "
            + string.Join("; ", compilation.Diagnostics.Select(one => one.Message))
        );
    }

    /// <summary>⚠ And the ordinary verb still composes none, which is what makes the other one the ask.</summary>
    /// <remarks>
    ///     The instrument. Every assertion above is equally true of a bake that composes a march for
    ///     <em>every</em> material with a height output — which is the picture change and the
    ///     per-pixel cost <c>MaterialBake.Material</c> was deliberately refused the power to make. The
    ///     same fixture through <c>BakeCommand</c> is where that is decidable.
    /// </remarks>
    [Fact]
    public void The_ordinary_bake_verb_still_composes_no_march() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.BakeCommand), Say(fixture));

        var content = Baked(fixture, adapter);

        Assert.DoesNotContain(content.Features, feature => feature is ParallaxOcclusionFeature);

        // The file is written either way — it is the *entry* that a material has to ask for, which is
        // what stops the height map being resident bytes with no reader.
        Assert.DoesNotContain(
            content.Textures,
            texture => texture.Parameter == new ParallaxOcclusionFeature().HeightMap
        );

        // ⚠ The bake's own warning names the second step, and it is what an artist who did not find
        // the verb reads. The tag rather than the type name: they differ by a `- !` and only one of
        // them is something a `.vxmat` accepts.
        Assert.Contains(
            MaterialBakeParallax.Tag,
            string.Join(" · ", fixture.Shell.Notifications.History.Select(one => one.Detail)),
            StringComparison.Ordinal
        );
    }

    /// <summary>⚠ Asking over a bake with no height output says so rather than composing an unfed march.</summary>
    /// <remarks>
    ///     A march whose map index stays at nought reads the bindless table's fallback checker as a
    ///     height field and the surface swims — a feature that disappears is visible, and this is not.
    ///     <c>BakeRoute.vxtexgraph</c> is the fixture with no height output, which is why the two
    ///     fixtures are separate files.
    /// </remarks>
    [Fact]
    public void Asking_over_a_bake_with_no_height_output_says_so() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        Open(fixture, "BakeRoute");

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.ParallaxBakeCommand), Say(fixture));

        var content = Baked(fixture, adapter);

        Assert.DoesNotContain(content.Features, feature => feature is ParallaxOcclusionFeature);

        var said = string.Join(" · ", fixture.Shell.Notifications.History.Select(one => one.Detail));

        output.WriteLine($"{adapter}: {said}");
        Assert.Contains("no height map", said, StringComparison.Ordinal);
    }

    /// <summary>Puts a committed graph in the project and opens it, at a size a test can afford.</summary>
    static TextureGraphDocument Open(TexturingFixture fixture, string graph = "BakeHeight") {
        fixture.Project.Selection.Set(fixture.AddGraph(Material, Fixture(graph)));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<TextureGraphDocument>());

        Assert.Empty(document.LoadDiagnostics);

        document.BaseWidth = 64;
        document.BaseHeight = 64;

        return document;
    }

    /// <summary>The material the verb wrote, read back off the disk.</summary>
    /// <remarks>
    ///     ⚠ <b>Off the file rather than off the outcome.</b> A route that composed the feature in
    ///     memory and wrote the material it had before would satisfy every in-process assertion, and
    ///     the file an artist then opens is the one without the march.
    /// </remarks>
    static MaterialContent Baked(TexturingFixture fixture, string adapter) {
        var file = Path.Combine(
            fixture.Paths.Assets,
            MaterialMapNaming.DefaultFolder,
            Material + MaterialImporter.Extension
        );

        Assert.True(File.Exists(file), $"{adapter}: there is no material at '{file}'. {Say(fixture)}");

        return YamlSerializer.Parse<MaterialContent>(File.ReadAllText(file));
    }

    /// <summary>The text of a committed graph, from beside the test assembly.</summary>
    /// <remarks>
    ///     ⚠ <see cref="AppContext.BaseDirectory" /> and not <c>[CallerFilePath]</c>: CI compiles with
    ///     <c>DeterministicSourcePaths</c>, which rewrites the repository root to <c>/_/</c>.
    /// </remarks>
    static string Fixture(string name) =>
        File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name + TextureGraphDocument.Extension)
        );

    /// <summary>What the bake said, for a message that would otherwise only say "no file".</summary>
    static string Say(TexturingFixture fixture) =>
        fixture.Shell.Notifications.History.Count > 0
            ? string.Join(
                " · ",
                fixture.Shell.Notifications.History.Select(one => one.Message + ": " + one.Detail)
            )
            : "The bake said nothing at all, which is itself the finding.";
}
