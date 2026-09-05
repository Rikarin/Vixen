// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Vixen.Core.Imaging;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.MeshMaps;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>
///     docs/plan/48 § 4.8's binding through <c>vixen mesh-maps list</c>, on a real project.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The verb is a caller and not an implementation.</b> That
///         <see cref="MeshMapLibrary" /> agrees with what <c>ProjectMeshMapBaker</c> writes is proved
///         where both live, by baking and reading back — <c>MeshMapLibraryTests</c>. What is proved
///         here is the half that suite cannot reach: that a command line gets to the index at all,
///         and that an unresolved query is a non-zero exit rather than an empty list.
///     </para>
///     <para>
///         ⚠ <b>The sidecars here are written by hand, deliberately, and that is only sound because
///         of the suite above.</b> <c>Vixen.Cli</c> does not reference the editor application, so the
///         baker is out of reach; a fixture that also owned the reader would be a test of one file
///         against itself, which is why the agreement is asserted in the other assembly and only the
///         wiring is asserted here.
///     </para>
/// </remarks>
public sealed class MeshMapCommandTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-meshmap-cli", Guid.NewGuid().ToString("N")[..12]);

    public MeshMapCommandTests() => Directory.CreateDirectory(Path.Combine(root, "Assets", MeshMapNaming.DefaultFolder));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>The verb lists what a graph would bind, and nothing else in the project.</summary>
    [Fact]
    public async Task Listing_shows_the_maps_a_graph_would_bind() {
        Baked("Barrel_ao.png", MeshMapUsage.AmbientOcclusion, "Barrel");
        Baked("Barrel_curvature.png", MeshMapUsage.Curvature, "Barrel");

        // An ordinary picture, named exactly like one of ours and carrying no usage in its sidecar.
        Authored("Rock_normal.png");

        var (code, said, complaint) = await Run("mesh-maps", "list", "--project", root);

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(complaint);
        Assert.Contains("Barrel  ao", said, StringComparison.Ordinal);
        Assert.Contains("Barrel  curvature", said, StringComparison.Ordinal);

        // ⚠ The half that can be false: a verb over the file names would list this one too, and it is
        // a hand-authored texture rather than a measurement of anything.
        Assert.DoesNotContain("Rock", said, StringComparison.Ordinal);
    }

    /// <summary>A usage nothing measures is a failure with a message, not an empty success.</summary>
    /// <remarks>
    ///     ⚠ <b>An empty list exiting zero is the answer a build script cannot act on.</b> Only the
    ///     normal and the height map are always baked, so "this project has no thickness map" is the
    ///     ordinary state of a bake run with the ray-casting maps switched off — and a script that
    ///     read success would go on to bake a material whose generators all read the fallback.
    /// </remarks>
    [Fact]
    public async Task A_usage_nothing_measures_is_a_failure_with_a_message() {
        Baked("Barrel_ao.png", MeshMapUsage.AmbientOcclusion, "Barrel");

        var (code, said, complaint) =
            await Run("mesh-maps", "list", "--project", root, "--usage", "thickness");

        Assert.Equal(ExitCode.Failed, code);
        Assert.Empty(said);
        Assert.Contains("thickness", complaint, StringComparison.Ordinal);
    }

    /// <summary>A suffix that is not one of the nine is a usage error, and says what the nine are.</summary>
    [Fact]
    public async Task A_suffix_that_is_not_a_mesh_map_is_a_usage_error() {
        Baked("Barrel_ao.png", MeshMapUsage.AmbientOcclusion, "Barrel");

        var (code, _, complaint) =
            await Run("mesh-maps", "list", "--project", root, "--usage", "specular");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("curvature", complaint, StringComparison.Ordinal);
    }

    /// <summary>A file with a mesh-map sidecar beside it, as a bake would leave one.</summary>
    static void Meta(string file, MeshMapUsage usage, string set) =>
        AssetMetaFile.WriteFile(
            AssetMetaFile.PathFor(file),
            new() {
                Guid = Core.AssetId.New(),
                Extensions = new Dictionary<string, string>(StringComparer.Ordinal) {
                    [MeshMapNaming.UsageKey] = MeshMapNaming.Suffix(usage),
                    [MeshMapNaming.MeshKey] = set
                }
            }
        );

    void Baked(string name, MeshMapUsage usage, string set) {
        var file = Path.Combine(root, "Assets", MeshMapNaming.DefaultFolder, name);

        File.WriteAllBytes(file, PngCodec.Encode(new(2, 2, new byte[2 * 2 * 4])));
        Meta(file, usage, set);
    }

    void Authored(string name) {
        var file = Path.Combine(root, "Assets", name);

        File.WriteAllBytes(file, PngCodec.Encode(new(2, 2, new byte[2 * 2 * 4])));
        AssetMetaFile.WriteFile(AssetMetaFile.PathFor(file), new() { Guid = Core.AssetId.New() });
    }

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
