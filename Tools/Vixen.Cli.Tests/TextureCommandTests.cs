// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Vixen.Core.Imaging;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>
///     docs/plan/48 § M5's <c>vixen texture bake</c>, driven through the real parser on a real
///     project, and asserted on the one property the exit criterion names: a re-bake on the same
///     machine writes the same bytes.
/// </summary>
/// <remarks>
///     ⚠ <b>The verb is a caller and not an implementation, and that is what these check.</b> The
///     packing, the compression, the GUID dance and the provenance block are
///     <c>Vixen.Editor.Assets</c>'s and are tested there; what is proved here is that the command
///     line reaches them, and that a build script gets a material out of a folder of maps.
/// </remarks>
public sealed class TextureCommandTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-texture-cli", Guid.NewGuid().ToString("N")[..12]);

    public TextureCommandTests() {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Maps);
    }

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    string Maps => Path.Combine(root, "authored");

    /// <summary>A folder of maps becomes a material the engine reads.</summary>
    [Fact]
    public async Task Baking_a_folder_of_maps_writes_a_material() {
        Authored("hull_baseColor.png", 10);
        Authored("hull_roughness.png", 20);
        Authored("hull_normal.png", 30);

        var (code, said, complaint) = await Run("texture", "bake", "--project", root, "--from", Maps, "--name", "Hull");

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(complaint);
        Assert.Contains("Hull", said, StringComparison.Ordinal);

        var material = Path.Combine(root, "Assets", MaterialMapNaming.DefaultFolder, "Hull.vxmat");

        Assert.True(File.Exists(material), said);

        var content = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(material));

        // The three inputs became three files, because roughness alone still packs an ORM map.
        Assert.Equal(3, content.Textures.Length);
        Assert.Contains(content.Textures, texture => texture.Parameter == new TexturedOrmFeature().OrmMap);
        Assert.True(MaterialShading.TryResolve(content.Shading, out var shading));
        Assert.False(MaterialCompiler.Compile(content.ToDescriptor(shading)).Failed);
    }

    /// <summary>A re-bake on the same machine is byte-identical.</summary>
    /// <remarks>
    ///     ⚠ <b>The sidecar is excluded and § D4 says why</b>: the provenance block carries the time
    ///     the bake ran, so two runs differ there by construction. What the criterion is about is the
    ///     outputs and the material — what a build consumes and what a reviewer diffs.
    /// </remarks>
    [Fact]
    public async Task A_re_bake_writes_the_same_bytes() {
        Authored("hull_baseColor.png", 10);
        Authored("hull_occlusion.png", 40);

        Assert.Equal(ExitCode.Success, (await Bake()).Code);

        var directory = Path.Combine(root, "Assets", MaterialMapNaming.DefaultFolder);
        var before = Files(directory);

        Assert.NotEmpty(before);
        Assert.Equal(ExitCode.Success, (await Bake()).Code);

        var after = Files(directory);

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));

        foreach (var (file, bytes) in before) {
            Assert.Equal(bytes, after[file]);
        }
    }

    /// <summary>An output somebody painted over stops the verb and says what to do.</summary>
    [Fact]
    public async Task A_painted_over_map_is_refused_and_forcing_is_offered() {
        Authored("hull_baseColor.png", 10);

        Assert.Equal(ExitCode.Success, (await Bake()).Code);

        var painted = Path.Combine(
            root,
            "Assets",
            MaterialMapNaming.DefaultFolder,
            MaterialMapNaming.FileName("Hull", MaterialMapTarget.BaseColor, MaterialMapNaming.PortableExtension)
        );

        File.WriteAllBytes(painted, PngCodec.Encode(Flat(99)));

        var (code, _, complaint) = await Bake();

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("painted", complaint, StringComparison.Ordinal);
        Assert.Contains("--force", complaint, StringComparison.Ordinal);

        // And the work is still there, which is the whole of the guard.
        Assert.Equal(99, PngCodec.Decode(File.ReadAllBytes(painted)).Pixels[0]);

        var (forced, _, _) = await Bake("--force");

        Assert.Equal(ExitCode.Success, forced);
        Assert.Equal(10, PngCodec.Decode(File.ReadAllBytes(painted)).Pixels[0]);
    }

    /// <summary>The adapter reaches the sidecar and nothing compares it.</summary>
    [Fact]
    public async Task The_adapter_is_recorded() {
        Authored("hull_baseColor.png", 10);

        Assert.Equal(ExitCode.Success, (await Bake("--adapter", "Apple M4 Max")).Code);

        var meta = AssetMetaFile.ReadFile(
            AssetMetaFile.PathFor(Path.Combine(root, "Assets", MaterialMapNaming.DefaultFolder, "Hull.vxmat"))
        );

        Assert.Equal("Apple M4 Max", meta.Extensions[MaterialProvenance.AdapterKey]);

        // A second bake claiming another card is not refused, which is § D4's decision rather than a
        // gap: a re-bake elsewhere is not byte-identical and asserting the adapter would say so as a
        // failure.
        Assert.Equal(ExitCode.Success, (await Bake("--adapter", "AMD Radeon RX 7900 XT")).Code);
    }

    /// <summary>A folder with nothing this reads in it says so rather than writing an empty material.</summary>
    [Fact]
    public async Task A_folder_with_no_maps_is_a_usage_error() {
        Authored("hull_albedo.png", 10);

        var (code, _, complaint) = await Bake();

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("baseColor", complaint, StringComparison.Ordinal);
    }

    /// <summary>Two files claiming one usage is refused rather than resolved by enumeration order.</summary>
    [Fact]
    public async Task Two_files_of_one_usage_are_refused() {
        Authored("hull_roughness.png", 10);
        Authored("old_roughness.png", 20);

        var (code, _, complaint) = await Bake();

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("roughness", complaint, StringComparison.Ordinal);
    }

    /// <summary>⚠ Two <c>--from</c> folders under one <c>--name</c> get a set each.</summary>
    /// <remarks>
    ///     <para>
    ///         <see href="https://github.com/Rikarin/Vixen/issues/725" />, and the point of asserting it
    ///         <i>here</i> is that this is the caller the guard did not reach. The baker keyed a set on
    ///         <c>MaterialBakeRecord.SourceAsset</c>, this verb has no asset to put there, and the only
    ///         test of the guard built the record by hand — so every command-line bake adopted whatever
    ///         set was under the name and two folders shared one material's GUIDs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A folder is the source a folder bake has</b>, so that is what it is keyed on.
    ///         <c>A_re_bake_writes_the_same_bytes</c> is the other direction of the same guard: one
    ///         folder baked twice keeps its set rather than collecting a <c>_2</c> per run.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Two_source_folders_under_one_name_do_not_share_a_material() {
        var second = Path.Combine(root, "generated");

        Directory.CreateDirectory(second);
        Authored("hull_baseColor.png", 10);
        File.WriteAllBytes(Path.Combine(second, "moss_baseColor.png"), PngCodec.Encode(Flat(60)));

        Assert.Equal(ExitCode.Success, (await Bake()).Code);

        var (code, said, complaint) = await Run(
            "texture", "bake", "--project", root, "--from", second, "--name", "Hull"
        );

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Hull_2", said, StringComparison.Ordinal);
        Assert.Contains("rename", complaint, StringComparison.Ordinal);

        var directory = Path.Combine(root, "Assets", MaterialMapNaming.DefaultFolder);

        Assert.True(File.Exists(Path.Combine(directory, "Hull_2.vxmat")));

        // And the first folder's material still names the first folder's pixels, which is the whole
        // of the defect: it used to be handed the second's, GUIDs and all.
        var first = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(Path.Combine(directory, "Hull.vxmat")));
        var again = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(Path.Combine(directory, "Hull_2.vxmat")));

        Assert.NotEqual(first.Textures[0].Texture, again.Textures[0].Texture);
        Assert.Equal(
            10,
            PngCodec.Decode(
                File.ReadAllBytes(
                    Path.Combine(
                        directory,
                        MaterialMapNaming.FileName(
                            "Hull",
                            MaterialMapTarget.BaseColor,
                            MaterialMapNaming.PortableExtension
                        )
                    )
                )
            ).Pixels[0]
        );
    }

    /// <summary>A map the database did not pick up fails the verb rather than reporting success.</summary>
    /// <remarks>
    ///     ⚠ <b><see href="https://github.com/Rikarin/Vixen/issues/724" />, at the exit code.</b> The
    ///     write recorded a null reference, the material named a texture resolving to nothing, and this
    ///     verb printed the file list and returned <c>Success</c> — so a build script shipped a set
    ///     whose surfaces shade from the bindless table's fallback. ⚠ <b>And <c>--force</c> is
    ///     deliberately not offered</b>: forcing cannot make the database name a file it would not read.
    /// </remarks>
    [Fact]
    public async Task A_map_the_database_did_not_pick_up_fails_the_verb() {
        Authored("hull_baseColor.png", 10);

        var directory = Path.Combine(root, "Assets", MaterialMapNaming.DefaultFolder);

        Directory.CreateDirectory(directory);

        // A sidecar with no readable GUID, which a scan refuses to replace rather than mint a new id
        // and break every reference through the old one.
        File.WriteAllText(
            Path.Combine(directory, "Hull_baseColor.png" + AssetMetaFile.Extension),
            "\0not a meta"
        );

        var (code, _, complaint) = await Bake();

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("Hull_baseColor.png", complaint, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", complaint, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "Hull.vxmat")), "a material naming nothing was written");
    }

    /// <summary>
    ///     ⚠ The bake's own "nothing samples this height map" warning still names the tag
    ///     <see cref="BakeParallax" /> matches on.
    /// </summary>
    /// <remarks>
    ///     <b>The anchor for a match across an assembly seam, and it is here because there is no
    ///     shared constant to match on.</b> <c>ProjectMaterialBaker.Overpaint</c> is a public const
    ///     for exactly this reason one refusal over — a caller has to tell one of that type's
    ///     messages from the others — and the unfed-height warning has none, so
    ///     <c>BakeParallax.Requested</c> drops it by looking for the YAML tag in its text. A reword
    ///     in <c>Vixen.Editor.Assets</c> would otherwise leave <c>--parallax</c> printing a sentence
    ///     telling an artist to do the thing the flag has just done, silently and for ever.
    /// </remarks>
    [Fact]
    public async Task The_bakers_unfed_height_warning_still_names_the_tag() {
        Authored("hull_baseColor.png", 10);
        Authored("hull_height.png", 80);

        var (code, _, complaint) = await Bake();

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains(BakeParallax.Tag, complaint, StringComparison.Ordinal);
    }

    /// <summary>⚠ A first bake can ask for the march, and what it writes compiles.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>'s remainder.</b>
    ///         The bake preserves and re-seats a parallax feature the material already carries, which
    ///         cannot help a material that does not exist yet — so the route was bake, paste the tag,
    ///         bake again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The compile is the assertion that matters and it is not a formality.</b>
    ///         <c>ParallaxOcclusionFeature</c> declares <c>MaterialFeatureStage.Coordinate</c> and
    ///         <c>MaterialCompiler</c> refuses a coordinate feature listed behind one that samples, so
    ///         the obvious implementation — append the feature to what the bake wrote — produces a
    ///         <c>.vxmat</c> this verb itself created and the importer then rejects. A test that only
    ///         looked for the feature in the list would pass on that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the texture entry is asserted against the feature's <em>default</em> name.</b>
    ///         <c>WorldRenderer.Paired</c> keys the height index on
    ///         <c>new ParallaxOcclusionFeature().HeightMap</c>, so an entry written under any other
    ///         spelling leaves that index at nought and marches the bindless table's fallback checker:
    ///         a surface that swims, on every device, with nothing reported.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task A_first_bake_can_ask_for_the_parallax_march() {
        Authored("hull_baseColor.png", 10);
        Authored("hull_height.png", 80);

        var (code, _, complaint) = await Bake("--parallax");

        Assert.Equal(ExitCode.Success, code);

        // The bake's warning has become false and is not printed beside the thing that made it false.
        Assert.DoesNotContain(BakeParallax.Tag, complaint, StringComparison.Ordinal);

        var content = Material();
        var parallax = Assert.IsType<ParallaxOcclusionFeature>(content.Features[0]);

        Assert.Contains(content.Textures, texture => texture.Parameter == new ParallaxOcclusionFeature().HeightMap);
        Assert.Equal(new ParallaxOcclusionFeature().HeightMap, parallax.HeightMap);
        Assert.True(MaterialShading.TryResolve(content.Shading, out var shading));
        Assert.False(MaterialCompiler.Compile(content.ToDescriptor(shading)).Failed);
    }

    /// <summary>⚠ <c>--parallax</c> drops the one warning it made false and keeps the others.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half the case above cannot see.</b> It asserts the tag is absent from what the
    ///         verb printed — which is equally true of a
    ///         <see cref="BakeParallax.Requested" /> that returned nothing at all. Sabotaged to
    ///         <c>return []</c>, every other test in this file stays green: the two that assert the
    ///         tag's presence never pass <c>--parallax</c>, and the one that does only asks for an
    ///         absence. Selectivity is the property, and nothing held it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second warning is a name clash, because it is the only other one a command
    ///         line can provoke.</b> A differently-sourced set asking for a name already taken is
    ///         renamed and told so, and that sentence is still true after a parallax feature is
    ///         composed — where "nothing samples this height map" is not, which is the whole reason
    ///         <see cref="BakeParallax" /> filters at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the filter matches on prose</b>, because <c>ProjectMaterialBaker</c> has no
    ///         shared constant for that warning the way it has <c>Overpaint</c> for a refusal
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1127">#1127</a>). Until it does,
    ///         this and <see cref="The_bakers_unfed_height_warning_still_names_the_tag" /> are the
    ///         two halves that turn a reword in another assembly into a red test rather than into
    ///         either a lost warning or a sentence telling an artist to do what the flag just did.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task A_parallax_bake_drops_only_the_warning_it_made_false() {
        Authored("hull_baseColor.png", 10);

        Assert.Equal(ExitCode.Success, (await Bake()).Code);

        var second = Path.Combine(root, "authored-again");

        Directory.CreateDirectory(second);
        File.WriteAllBytes(Path.Combine(second, "hull_baseColor.png"), PngCodec.Encode(Flat(40)));
        File.WriteAllBytes(Path.Combine(second, "hull_height.png"), PngCodec.Encode(Flat(80)));

        var (code, _, complaint) = await Run(
            "texture", "bake", "--project", root, "--from", second, "--name", "Hull", "--parallax"
        );

        Assert.Equal(ExitCode.Success, code);
        Assert.DoesNotContain(BakeParallax.Tag, complaint, StringComparison.Ordinal);
        Assert.Contains("has already baked a material called", complaint, StringComparison.Ordinal);
    }

    /// <summary>Without the flag a bake that wrote a height map still composes no march.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes the case above a claim about the flag.</b> Both bakes write the
    ///     same height file; what differs is whether anything samples it, and a <c>--parallax</c> that
    ///     had accidentally become the default would put a per-pixel march on every material any graph
    ///     ever emitted a height output from — a picture change and a cost nobody authored.
    /// </remarks>
    [Fact]
    public async Task A_bake_that_did_not_ask_grows_no_march() {
        Authored("hull_baseColor.png", 10);
        Authored("hull_height.png", 80);

        Assert.Equal(ExitCode.Success, (await Bake()).Code);

        var content = Material();

        Assert.DoesNotContain(content.Features, feature => feature is ParallaxOcclusionFeature);
        Assert.DoesNotContain(content.Textures, texture => texture.Parameter == new ParallaxOcclusionFeature().HeightMap);
    }

    /// <summary>Asking for a march this bake cannot feed says so rather than composing one.</summary>
    /// <remarks>
    ///     ⚠ <b>A parallax feature whose map is unbound is not a smaller effect.</b> Its height index
    ///     stays at nought, it marches the bindless table's fallback checker as a height field, and
    ///     the surface swims — so the honest answer to "turn it on" over a bake with no height output
    ///     is a sentence naming what is missing upstream.
    /// </remarks>
    [Fact]
    public async Task Asking_for_a_march_with_no_height_map_says_so() {
        Authored("hull_baseColor.png", 10);

        var (code, _, complaint) = await Bake("--parallax");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("no height map", complaint, StringComparison.Ordinal);
        Assert.DoesNotContain(Material().Features, feature => feature is ParallaxOcclusionFeature);
    }

    MaterialContent Material() =>
        YamlSerializer.Parse<MaterialContent>(
            File.ReadAllText(Path.Combine(root, "Assets", MaterialMapNaming.DefaultFolder, "Hull.vxmat"))
        );

    Task<(ExitCode Code, string Output, string Error)> Bake(params string[] extra) =>
        Run([.. new[] { "texture", "bake", "--project", root, "--from", Maps, "--name", "Hull" }, .. extra]);

    void Authored(string name, byte value) =>
        File.WriteAllBytes(Path.Combine(Maps, name), PngCodec.Encode(Flat(value)));

    static Bitmap Flat(byte value) {
        var pixels = new byte[4 * 4 * 4];

        Array.Fill(pixels, value);

        return new(4, 4, pixels);
    }

    static Dictionary<string, byte[]> Files(string directory) =>
        Directory.EnumerateFiles(directory)
            .Where(file => !file.EndsWith(AssetMetaFile.Extension, StringComparison.Ordinal))
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>⚠ A `.vxtexgraph` compiled and evaluated on a real GPU becomes a material.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/48 § M5's CLI row read literally</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1020">#1020</a>. Until this the graph
    ///         route was the editor's alone, because nothing in this CLI created an
    ///         <c>IGraphicsDevice</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is a colour and not an exit code, because a bake that produced
    ///         nothing also exits 0 on the device that draws nothing.</b> The graph is a uniform
    ///         colour whose red channel is 0.25, so the base-colour map's first texel must be 64 —
    ///         a black picture, which is what a Null-device fallback would write, fails it. That is
    ///         the same reason this file's neighbours read pixels rather than counting files.
    ///     </para>
    ///     <para>
    ///         ⚠ Skips loudly with no adapter, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into
    ///         a failure — the harness the texture-graph suites use, spelled here because this
    ///         assembly does not reference their test project. Without that, a CI leg with no driver
    ///         would report this as passing.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Baking_a_graph_evaluates_it_on_a_device_and_writes_a_material() {
        RequireDevice();

        Graph("Flat.vxtexgraph", 0.25f);

        var (code, said, complaint) = await Run(
            "texture", "bake", "--project", root, "--graph", Path.Combine(root, "Assets", "Flat.vxtexgraph"),
            "--name", "Flat"
        );

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Flat", said, StringComparison.Ordinal);

        // ⚠ The adapter is in the line the verb prints, which is doc 48's exit criterion 11 as a
        // property of the output rather than of somebody's memory: a run that fell back would have
        // to name the device that draws nothing here.
        Assert.Contains("baked on", said, StringComparison.Ordinal);

        var map = Path.Combine(
            root, "Assets", MaterialMapNaming.DefaultFolder,
            "Flat_" + MaterialMapNaming.Suffix(MaterialMapUsage.BaseColor) + MaterialMapNaming.PortableExtension
        );

        Assert.True(File.Exists(map), said + complaint);

        var picture = PngCodec.Decode(File.ReadAllBytes(map));

        // 0.25 in a linear graph, written to an 8-bit map. A black picture — the Null device's answer
        // — is 0, and every other kernel this graph could have run gives another number.
        Assert.Equal(64, picture.Pixels[0]);
    }

    /// <summary>An adapter cannot be typed for a bake this tool ran itself.</summary>
    /// <remarks>
    ///     ⚠ <b>A provenance block disagreeing with the run that wrote it is undetectable</b>: § D4
    ///     records the adapter and never compares it, so a typed name would sit in the sidecar
    ///     forever. Refused at the parse rather than ignored, because silently dropping an option
    ///     somebody passed is how a script comes to believe it did something.
    /// </remarks>
    [Fact]
    public async Task An_adapter_cannot_be_named_for_a_graph_bake() {
        Graph("Flat.vxtexgraph", 0.5f);

        var (code, _, complaint) = await Run(
            "texture", "bake", "--project", root, "--graph", Path.Combine(root, "Assets", "Flat.vxtexgraph"),
            "--name", "Flat", "--adapter", "Somebody's card"
        );

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("--adapter", complaint, StringComparison.Ordinal);
    }

    /// <summary>Exactly one source, and neither is guessed.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because the interesting failure is the one that picks a winner.</b> A
    ///     parser that preferred whichever option it saw first would satisfy the empty case and bake
    ///     the wrong thing in the other.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Exactly_one_of_from_and_graph(bool folder, bool graph) {
        Authored("hull_baseColor.png", 10);
        Graph("Flat.vxtexgraph", 0.5f);

        List<string> args = ["texture", "bake", "--project", root, "--name", "Hull"];

        if (folder) {
            args.AddRange(["--from", Maps]);
        }

        if (graph) {
            args.AddRange(["--graph", Path.Combine(root, "Assets", "Flat.vxtexgraph")]);
        }

        var (code, _, complaint) = await Run([.. args]);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.NotEmpty(complaint);
    }

    /// <summary>A graph with no Output node is refused rather than writing an empty material.</summary>
    [Fact]
    public async Task A_graph_that_writes_no_map_is_refused() {
        NodeGraphModel model = new();

        model.Add("Source/Uniform");

        File.WriteAllText(
            Path.Combine(root, "Assets", "Nothing.vxtexgraph"),
            YamlWriter.Write(YamlSerializer.Serialize(NodeGraphDocument.Save(model)))
        );

        var (code, _, complaint) = await Run(
            "texture", "bake", "--project", root, "--graph", Path.Combine(root, "Assets", "Nothing.vxtexgraph"),
            "--name", "Nothing"
        );

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("Output node", complaint, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The tool does not link the device that draws nothing, which is what makes "it refuses
    ///     rather than falling back" a fact rather than a branch.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The refusal <a href="https://github.com/Rikarin/Vixen/issues/1020">#1020</a> is
    ///         about is one <c>if</c>, and an <c>if</c> is deleted by the next person in a hurry.</b>
    ///         What cannot be deleted in a hurry is an assembly that is not there: with no
    ///         <c>Vixen.Graphics.Null</c> in the closure there is nothing for a fallback to fall back
    ///         to, and adding one is a reference somebody has to write down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The Vulkan half is the anchor.</b> A directory listing that came back empty —
    ///         the shape in which this kind of roll call reports success on the day it stops running
    ///         — fails that assertion first.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_tool_links_a_real_backend_and_not_the_one_that_draws_nothing() {
        var beside = Directory.GetFiles(AppContext.BaseDirectory, "Vixen.Graphics.*.dll")
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Contains("Vixen.Graphics.Vulkan.dll", beside);
        Assert.DoesNotContain("Vixen.Graphics.Null.dll", beside);
    }

    /// <summary>⚠ A graph reading an imported picture bakes, and the picture is the project's.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1087">#1087</a>: the whole of what
    ///         <c>--graph</c> could not do.</b> Every generator, pattern and noise graph baked from
    ///         the day the verb landed; a graph with one <c>Source/Bitmap</c> pointing at an imported
    ///         PNG was refused, because the resolver that turns an asset reference into texels was
    ///         313 <see langword="internal" /> lines inside the texturing plugin.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is the imported picture's own value and not merely a success
    ///         code.</b> An exit code cannot tell this apart from a bake that filled the external
    ///         with black and wrote a plausible material over an artist's; 137 is a number no kernel
    ///         in this graph could have produced from anywhere else, and it can only have come off
    ///         the disk.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the scan is what it is really testing, one layer down.</b>
    ///         <c>AssetDatabase.TryGetByPath</c> reads an index, and an index nothing has scanned is
    ///         empty — so a run that resolved before scanning would refuse a file that is sitting
    ///         right there, with a sentence about the project rather than about the order the verb
    ///         does things in.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Baking_a_graph_that_reads_an_imported_picture_fills_it_from_the_project() {
        RequireDevice();

        Imported("Rust.png", 137);
        BitmapGraph("Rusty.vxtexgraph", "Assets/Rust.png");

        var (code, said, complaint) = await Run(
            "texture", "bake", "--project", root, "--graph", Path.Combine(root, "Assets", "Rusty.vxtexgraph"),
            "--name", "Rusty"
        );

        Assert.Equal(ExitCode.Success, code);

        var picture = PngCodec.Decode(File.ReadAllBytes(BaseColourOf("Rusty")));

        Assert.Equal(137, picture.Pixels[0]);
        Assert.Contains("baked on", said, StringComparison.Ordinal);
        Assert.DoesNotContain("1087", complaint, StringComparison.Ordinal);
    }

    /// <summary>A reference naming a live editor session is named and refused, one sentence.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of #1087, and the half that says the move was a split rather than a
    ///     copy.</b> <c>meshmap:</c> and <c>vxpaint:</c> stayed in the plugin because what they name
    ///     is a session — one of them reads strokes that are still under the pointer. A resolver that
    ///     had taken them too would look them up as project paths and answer with a missing-file
    ///     message about a file nobody named.
    /// </remarks>
    [Fact]
    public async Task A_graph_reading_a_mesh_map_is_named_and_refused() {
        RequireDevice();

        MeshMapGraph("Worn.vxtexgraph");

        var (code, _, complaint) = await Run(
            "texture", "bake", "--project", root, "--graph", Path.Combine(root, "Assets", "Worn.vxtexgraph"),
            "--name", "Worn"
        );

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("meshmap:curvature", complaint, StringComparison.Ordinal);

        // ⚠ Not the sentence a missing file gets. That one would send somebody looking in `Assets/`
        // for a picture called `meshmap:curvature`, which is the failure the split exists to avoid.
        Assert.DoesNotContain("is not in this project's assets", complaint, StringComparison.Ordinal);
    }

    /// <summary>⚠ A project's own compound is a node type this route compiles.</summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/1088">#1088</a>, end to end.</b>
    ///     <c>Assets/Compounds</c> was spelled twice — once in the texturing plugin, which owns the
    ///     join, and once here because <c>Tools/</c> cannot reference that plugin. Two spellings of
    ///     one convention is how a project's compounds become visible to one host and invisible to
    ///     the other: this route would report <c>TG0001</c>, "no node type is registered", which
    ///     reads as a broken graph rather than as a folder nobody looked in. Nothing but a bake that
    ///     actually reads the folder can tell the two apart, which is why this is a device test and
    ///     not a comparison of two constants.
    /// </remarks>
    [Fact]
    public async Task A_projects_own_compound_is_a_node_type_this_route_compiles() {
        RequireDevice();

        Compound("Tint", 0.75f);
        UsingCompound("Wall.vxtexgraph", "Tint");

        var (code, _, complaint) = await Run(
            "texture", "bake", "--project", root, "--graph", Path.Combine(root, "Assets", "Wall.vxtexgraph"),
            "--name", "Wall"
        );

        Assert.Equal(ExitCode.Success, code);
        Assert.DoesNotContain("TG0001", complaint, StringComparison.Ordinal);

        // 0.75 linear into an 8-bit map. A compound the registry never saw does not compile at all,
        // so a wrong number here would have to be a compound that inlined something else.
        Assert.Equal(191, PngCodec.Decode(File.ReadAllBytes(BaseColourOf("Wall"))).Pixels[0]);
    }

    /// <summary>⚠ The refusal leads with the driver's own words rather than with this verb's guess.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>It used to read "there is no GPU here: " and then the driver's sentence</b>, and
    ///         the commonest way for a bake to be refused is not a missing adapter at all:
    ///         <c>VulkanLoader.TryLoad</c> fails when <c>libvulkan</c> is not on the search path,
    ///         which is the ordinary state of a <c>dotnet</c> container image on a machine with a
    ///         perfectly good card in it. The two halves then contradicted each other in one line —
    ///         "there is no GPU here" immediately followed by "install the loader, it was not on the
    ///         dynamic linker's search path".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The property is that the driver's sentence comes first, which is falsifiable in
    ///         both directions.</b> Asserting the absence of the old words would be satisfied by the
    ///         same commit that deleted them and by nothing else; this fails for any prefix anybody
    ///         puts back, whatever it says. The second assertion holds the other half — a refusal
    ///         trimmed to the driver's words alone would stop saying that the fall-back exists and is
    ///         refused, which is the part the driver cannot know.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_refusal_leads_with_the_drivers_own_cause() {
        const string Loader = "Vulkan could not be loaded. Silk.NET.Vulkan ships bindings only, so the loader "
            + "has to come from the system: apt install libvulkan1 mesa-vulkan-drivers (or the equivalent).";

        var said = HeadlessGraphics.Refusal(Loader);

        Assert.StartsWith(Loader, said, StringComparison.Ordinal);
        Assert.Contains("refuses to run it on the one that draws nothing", said, StringComparison.Ordinal);
    }

    /// <summary>A device, or a loud skip — or, when one was required, a failure.</summary>
    /// <remarks>
    ///     ⚠ Without a real adapter a headless run falls back to the Null device on every platform
    ///     and prints identical healthy counters, so a graph bake asserted there would be comparing
    ///     two black images. <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure, which is
    ///     what a CI leg that is meant to be watching sets.
    /// </remarks>
    static void RequireDevice() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            TestContext.Current.TestOutputHelper?.WriteLine($"adapter: {device!.Adapter.Name}");
            device.Dispose();

            return;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");
    }

    /// <summary>A one-node graph writing a flat base colour, saved where a project keeps its assets.</summary>
    void Graph(string file, float red) {
        NodeGraphModel model = new();

        var colour = model.Add("Source/Uniform");
        var output = model.Add("Output/Output");

        colour.SetValue("Colour", [red, 0f, 0f, 1f]);
        output.SetText("Usage", MaterialMapNaming.Suffix(MaterialMapUsage.BaseColor));
        model.Connect(new(colour.Id, "Out"), new(output.Id, "Input"));

        File.WriteAllText(
            Path.Combine(root, "Assets", file),
            YamlWriter.Write(YamlSerializer.Serialize(NodeGraphDocument.Save(model)))
        );
    }

    /// <summary>Where a bake of this name put its base-colour map.</summary>
    string BaseColourOf(string name) =>
        Path.Combine(
            root, "Assets", MaterialMapNaming.DefaultFolder,
            name + "_" + MaterialMapNaming.Suffix(MaterialMapUsage.BaseColor) + MaterialMapNaming.PortableExtension
        );

    /// <summary>An imported picture in the project's own assets, flat and opaque.</summary>
    void Imported(string name, byte value) {
        var pixels = new byte[8 * 8 * 4];

        for (var texel = 0; texel < 8 * 8; texel++) {
            pixels[texel * 4] = value;
            pixels[(texel * 4) + 1] = value;
            pixels[(texel * 4) + 2] = value;
            pixels[(texel * 4) + 3] = 255;
        }

        File.WriteAllBytes(Path.Combine(root, "Assets", name), PngCodec.Encode(new(8, 8, pixels)));
    }

    /// <summary>A graph whose base colour is an imported picture, read point-sampled and linear.</summary>
    /// <remarks>
    ///     ⚠ <b>Point and linear, so that the number that comes out is the number that went in.</b>
    ///     A bilinear read of a flat picture is the same value, but an sRGB one is not — and a test
    ///     asserting a transfer function would be asserting <c>Bitmap.rvn</c> rather than whether the
    ///     picture was found.
    /// </remarks>
    void BitmapGraph(string file, string reference) {
        NodeGraphModel model = new();

        var bitmap = model.Add("Source/Bitmap");
        var output = model.Add("Output/Output");

        bitmap.SetText("Source", reference);
        bitmap.SetText("Space", "Linear");
        bitmap.SetText("Filter", "Point");
        output.SetText("Usage", MaterialMapNaming.Suffix(MaterialMapUsage.BaseColor));
        model.Connect(new(bitmap.Id, "Out"), new(output.Id, "Input"));

        Write(file, model);
    }

    /// <summary>A graph whose base colour is a mesh map nothing here has baked.</summary>
    void MeshMapGraph(string file) {
        NodeGraphModel model = new();

        var map = model.Add("Source/Mesh Map");
        var output = model.Add("Output/Output");

        map.SetText("Map", "curvature");
        output.SetText("Usage", MaterialMapNaming.Suffix(MaterialMapUsage.BaseColor));
        model.Connect(new(map.Id, "Out"), new(output.Id, "Input"));

        Write(file, model);
    }

    /// <summary>A one-node compound in the project's own <c>Assets/Compounds</c>.</summary>
    void Compound(string name, float grey) {
        var folder = Path.Combine(root, "Assets", TextureCompoundLibrary.Folder);

        Directory.CreateDirectory(folder);

        NodeGraphModel model = new();

        model.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var colour = model.Add("Source/Uniform");
        var exit = model.Add(SubGraphs.OutputType);

        colour.SetValue("Colour", [grey, grey, grey, 1f]);
        model.Connect(new(colour.Id, "Out"), new(exit.Id, "Out"));

        File.WriteAllText(
            Path.Combine(folder, name + TextureCompoundLibrary.Extension),
            YamlWriter.Write(YamlSerializer.Serialize(NodeGraphDocument.Save(model)))
        );
    }

    /// <summary>A graph whose base colour is a published compound's output.</summary>
    void UsingCompound(string file, string published) {
        NodeGraphModel model = new();

        var used = model.Add(published);
        var output = model.Add("Output/Output");

        output.SetText("Usage", MaterialMapNaming.Suffix(MaterialMapUsage.BaseColor));
        model.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        Write(file, model);
    }

    /// <summary>Saves a graph where a project keeps its assets.</summary>
    void Write(string file, NodeGraphModel model) =>
        File.WriteAllText(
            Path.Combine(root, "Assets", file),
            YamlWriter.Write(YamlSerializer.Serialize(NodeGraphDocument.Save(model)))
        );

    static async Task<(ExitCode Code, string Output, string Error)> Run(params string[] args) {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };

        var parsed = VixenCommand.Create(output, error).Parse(args);

        if (parsed.Errors.Count > 0) {
            return (
                ExitCode.UsageError, output.ToString(),
                string.Join("\n", parsed.Errors.Select(problem => problem.Message))
            );
        }

        var code = await parsed.InvokeAsync(null, TestContext.Current.CancellationToken);

        return ((ExitCode)code, output.ToString(), error.ToString());
    }
}
