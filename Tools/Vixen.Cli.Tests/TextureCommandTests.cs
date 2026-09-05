// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Vixen.Core.Imaging;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Materials;
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
