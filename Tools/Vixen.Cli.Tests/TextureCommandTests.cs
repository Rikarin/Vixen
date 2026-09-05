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
