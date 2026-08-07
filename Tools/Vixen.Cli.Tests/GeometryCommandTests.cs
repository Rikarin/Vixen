// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Vixen.Geometry;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>
///     docs/plan/41 § D16's <c>remesh</c> and docs/plan/42 § D13's <c>unwrap</c> and <c>uv pack</c>,
///     driven through the real parser on real files. Every failure path is asserted as loudly as every
///     success one, because the class of bug a batch verb has is the one where it exits zero having
///     done nothing.
/// </summary>
public sealed class GeometryCommandTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-geometry-tests", Guid.NewGuid().ToString("N"));

    public GeometryCommandTests() => Directory.CreateDirectory(root);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    [Fact]
    public async Task Remeshing_a_box_writes_quads() {
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));
        var output = Path.Combine(root, "box-quads.obj");

        var (code, said, complaint) = await Run("remesh", input, output, "--quads", "200");

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(complaint);
        Assert.True(File.Exists(output), said);
        Assert.Contains("quads", said, StringComparison.Ordinal);
    }

    /// <summary>Every output format the writer claims round-trips back through the reader.</summary>
    /// <remarks>
    ///     ⚠ <b>The GLB case is the one worth having.</b> Its chunks are length-prefixed and padded,
    ///     the JSON with spaces and the binary with zeroes, and a container that gets that wrong opens
    ///     in exactly one viewer and in none of the others — which is invisible to a test that only
    ///     checks the file exists.
    /// </remarks>
    [Theory]
    [InlineData(".obj")]
    [InlineData(".gltf")]
    [InlineData(".glb")]
    public async Task What_is_written_reads_back_as_a_model(string extension) {
        var input = Write("sphere.obj", MeshShapes.Create(ShapeParameters.Default(ShapeKind.Sphere) with { Sides = 12, Steps = 6 }));
        var output = Path.Combine(root, "sphere-quads" + extension);

        var (code, said, _) = await Run("remesh", input, output, "--quads", "120");

        Assert.Equal(ExitCode.Success, code);

        var read = ModelReader.Read(
            File.ReadAllBytes(output),
            extension,
            "round trip",
            new ModelImportSettings { GenerateTangents = false }
        );

        Assert.NotEmpty(read.Meshes);
        Assert.True(read.Meshes[0].Indices.Length > 0, said);
    }

    /// <summary>The symmetry flag reaches the remesher rather than being parsed and dropped.</summary>
    [Fact]
    public async Task The_symmetry_flag_changes_what_is_written() {
        var input = Write("cyl.obj", MeshShapes.Create(ShapeParameters.Default(ShapeKind.Cylinder) with { Sides = 12 }));
        var plain = Path.Combine(root, "plain.obj");
        var mirrored = Path.Combine(root, "mirrored.obj");

        Assert.Equal(ExitCode.Success, (await Run("remesh", input, plain, "--quads", "120")).Code);
        Assert.Equal(ExitCode.Success, (await Run("remesh", input, mirrored, "--quads", "120", "--symmetry", "x")).Code);
        Assert.NotEqual(File.ReadAllText(plain), File.ReadAllText(mirrored));
    }

    [Fact]
    public async Task An_axis_that_is_not_an_axis_is_a_usage_error() {
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));

        var (code, _, complaint) = await Run(
            "remesh", input, Path.Combine(root, "out.obj"), "--symmetry", "diagonal"
        );

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("x, y or z", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_input_is_a_usage_error_with_something_on_stderr() {
        var (code, _, complaint) = await Run(
            "remesh", Path.Combine(root, "nothing.obj"), Path.Combine(root, "out.obj")
        );

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("nothing.obj", complaint, StringComparison.Ordinal);
    }

    /// <summary>An output format nothing writes is refused before the input is read.</summary>
    [Fact]
    public async Task An_output_format_that_is_not_written_is_a_usage_error() {
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));

        var (code, _, complaint) = await Run("remesh", input, Path.Combine(root, "out.fbx"));

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains(".gltf", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_not_a_mesh_is_refused_rather_than_thrown_out_of() {
        var input = Path.Combine(root, "notamesh.obj");

        File.WriteAllText(input, "this is not a wavefront object and never was\n");

        var (code, _, complaint) = await Run("remesh", input, Path.Combine(root, "out.obj"));

        Assert.NotEqual(ExitCode.Success, code);
        Assert.NotEmpty(complaint);
    }

    [Fact]
    public async Task Unwrapping_writes_coordinates() {
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));
        var output = Path.Combine(root, "box-uv.gltf");

        var (code, said, complaint) = await Run("unwrap", input, output, "--resolution", "512", "--margin", "2");

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(complaint);
        Assert.Contains("charts", said, StringComparison.Ordinal);
        Assert.Contains("TEXCOORD_0", File.ReadAllText(output), StringComparison.Ordinal);
    }

    /// <summary>docs/plan/42's exit criterion 7: repack somebody else's islands, keeping their shapes.</summary>
    [Fact]
    public async Task Packing_repacks_the_coordinates_a_file_already_has() {
        var unwrapped = Path.Combine(root, "unwrapped.gltf");
        var repacked = Path.Combine(root, "repacked.gltf");
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));

        Assert.Equal(ExitCode.Success, (await Run("unwrap", input, unwrapped, "--resolution", "512")).Code);

        var (code, said, complaint) = await Run("uv", "pack", unwrapped, repacked, "--resolution", "512", "--margin", "8");

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(complaint);
        Assert.Contains("charts", said, StringComparison.Ordinal);
        Assert.True(File.Exists(repacked));
    }

    /// <summary>A mesh with no coordinates cannot be repacked, and the verb says which one to run.</summary>
    [Fact]
    public async Task Packing_a_mesh_with_no_coordinates_is_refused() {
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));

        var (code, _, complaint) = await Run("uv", "pack", input, Path.Combine(root, "out.obj"));

        Assert.Equal(ExitCode.Failed, code);
        Assert.Contains("vixen unwrap", complaint, StringComparison.Ordinal);
    }

    /// <summary>A guide file that is not there stops the run rather than being quietly skipped.</summary>
    /// <remarks>
    ///     ⚠ <b>Unlike the importer, which warns and carries on.</b> The two are different promises: an
    ///     import runs unattended over a project and must not fail a model because a curve was renamed,
    ///     and a person typing <c>--guide</c> at a prompt has said what they want and would rather be
    ///     told than get a result that ignored it.
    /// </remarks>
    [Fact]
    public async Task A_guide_that_is_not_there_is_a_usage_error() {
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));

        var (code, _, complaint) = await Run(
            "remesh", input, Path.Combine(root, "out.obj"), "--guide", Path.Combine(root, "spine.vxspline")
        );

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("spine.vxspline", complaint, StringComparison.Ordinal);
    }

    /// <summary>docs/plan/41 § D16's example line has a <c>--bake</c> and this verb deliberately has none.</summary>
    /// <remarks>
    ///     <b>The rule <c>VixenCommand</c>'s header states, asserted rather than trusted.</b> A flag
    ///     that parsed and wrote no maps would be discovered by a build script as a success; a flag
    ///     that is not there is discovered as a parse error, which is the one of the two anybody can
    ///     act on.
    /// </remarks>
    [Fact]
    public void There_is_no_bake_flag_until_there_is_a_bake() {
        var parsed = VixenCommand.Create().Parse(["remesh", "in.obj", "out.obj", "--bake"]);

        Assert.NotEmpty(parsed.Errors);
    }

    /// <summary>What the remesh actually wrote is quads, read back off the file rather than off the report.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § Part 4's first promise, checked against the artefact.</b>
    ///         <c>RemeshReport.QuadCount</c> counted quads all along while the file held triangles,
    ///         because everything went out through <see cref="MeshData" /> — a vertex buffer, which has
    ///         one vertex per corner and no polygon larger than a triangle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The vertex count is half the assertion and it is the half a face count misses.</b>
    ///         Splitting each quad into two triangles and giving every corner its own vertex produces a
    ///         file whose faces parse and whose surface is a heap of disconnected islands: measured on a
    ///         5 766-quad result, 23 064 positions and 23 064 boundary edges, the only two-face edges
    ///         being the diagonals inside each split quad.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task What_the_remesh_writes_is_quads_that_share_their_corners() {
        var input = Write("sphere.obj", MeshShapes.Create(ShapeParameters.Default(ShapeKind.Sphere) with { Sides = 16, Steps = 8 }));
        var output = Path.Combine(root, "sphere-quads.obj");

        Assert.Equal(ExitCode.Success, (await Run("remesh", input, output, "--quads", "200")).Code);

        var (positions, faces) = Obj(output);

        Assert.NotEmpty(faces);
        Assert.All(faces, face => Assert.Equal(4, face.Length));

        // One position per quad give or take, rather than four. A fully split result has exactly
        // 4 × faces, which is what this refuses.
        Assert.True(positions < faces.Count * 2, $"{positions} positions for {faces.Count} quads is still split.");

        var valence = Valence(faces);

        Assert.True(
            valence.GetValueOrDefault(2) > valence.GetValueOrDefault(1),
            $"more boundary edges than interior ones: {string.Join(", ", valence.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}"
        );
    }

    /// <summary>An unwrap writes a connected surface, and keeps a coordinate per corner so seams survive.</summary>
    /// <remarks>
    ///     ⚠ <b>The two halves pull against each other and both are required.</b> Sharing positions is
    ///     what makes the surface connected; sharing <i>coordinates</i> would weld every seam shut, and
    ///     an atlas with no seams is not an atlas. OBJ indexes <c>v</c> and <c>vt</c> separately for
    ///     exactly this, so the file has fewer positions than corners and one <c>vt</c> per corner.
    /// </remarks>
    [Theory]
    [InlineData("unwrap")]
    [InlineData("pack")]
    public async Task What_an_unwrap_writes_is_a_connected_surface_with_its_seams_intact(string verb) {
        var seed = Path.Combine(root, "seed.obj");
        var input = Write("sphere.obj", MeshShapes.Create(ShapeParameters.Default(ShapeKind.Sphere) with { Sides = 16, Steps = 8 }));

        Assert.Equal(ExitCode.Success, (await Run("unwrap", input, seed)).Code);

        var output = Path.Combine(root, "atlas.obj");

        var code = verb == "unwrap"
            ? (await Run("unwrap", input, output)).Code
            : (await Run("uv", "pack", seed, output)).Code;

        Assert.Equal(ExitCode.Success, code);

        var (positions, faces) = Obj(output);
        var corners = faces.Sum(face => face.Length);

        Assert.NotEmpty(faces);
        Assert.True(positions < corners, $"{positions} positions for {corners} corners is one vertex per corner.");

        var valence = Valence(faces);

        Assert.True(
            valence.GetValueOrDefault(2) > valence.GetValueOrDefault(1),
            $"more boundary edges than interior ones: {string.Join(", ", valence.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}"
        );

        // A seam is a position whose corners disagree about the coordinate. Welding the positions is
        // what connects the surface; if it had welded the coordinates with them there would be no
        // position left carrying two, and the atlas would have no seams to cut along.
        Assert.Contains(Seams(output), pair => pair.Value.Count > 1);
    }

    /// <summary>glTF cannot hold a quad, so the writer says it triangulated rather than doing it quietly.</summary>
    /// <remarks>
    ///     <b>docs/plan/41 § Part 4 wants quads and glTF 2.0's <c>mode</c> has no n-gon, so <c>.glb</c>
    ///     genuinely cannot carry one.</b> Refusing the format would take away the container the rest of
    ///     the pipeline reads; triangulating in silence is what let a triangles-only output ship as a
    ///     quad tool. The third option is the note.
    /// </remarks>
    [Theory]
    [InlineData(".glb")]
    [InlineData(".gltf")]
    public async Task A_triangles_only_format_says_that_it_triangulated(string extension) {
        var input = Write("box.obj", MeshShapes.Create(ShapeKind.Box));

        var (code, said, _) = await Run("remesh", input, Path.Combine(root, "box-quads" + extension), "--quads", "200");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("triangles only", said, StringComparison.Ordinal);
        Assert.Contains(extension, said, StringComparison.Ordinal);

        // And the OBJ, which can, says nothing.
        var quiet = (await Run("remesh", input, Path.Combine(root, "box-quads.obj"), "--quads", "200")).Output;

        Assert.DoesNotContain("triangles only", quiet, StringComparison.Ordinal);
    }

    /// <summary>An OBJ's position count and its faces, as position indices.</summary>
    static (int Positions, List<int[]> Faces) Obj(string path) {
        var positions = 0;
        var faces = new List<int[]>();

        foreach (var line in File.ReadLines(path)) {
            if (line.StartsWith("v ", StringComparison.Ordinal)) {
                positions++;
            } else if (line.StartsWith("f ", StringComparison.Ordinal)) {
                faces.Add(
                    [
                        .. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                            .Select(corner => int.Parse(corner.Split('/')[0], CultureInfo.InvariantCulture))
                    ]
                );
            }
        }

        return (positions, faces);
    }

    /// <summary>Which texture coordinates each position is used with, read off the face lines.</summary>
    static Dictionary<int, HashSet<int>> Seams(string path) {
        var seams = new Dictionary<int, HashSet<int>>();

        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith("f ", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var corner in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)) {
                var parts = corner.Split('/');

                if (parts.Length < 2 || parts[1].Length == 0) {
                    continue;
                }

                var position = int.Parse(parts[0], CultureInfo.InvariantCulture);

                if (!seams.TryGetValue(position, out var used)) {
                    seams[position] = used = [];
                }

                used.Add(int.Parse(parts[1], CultureInfo.InvariantCulture));
            }
        }

        return seams;
    }

    /// <summary>How many edges have how many faces, over the faces' position indices.</summary>
    static Dictionary<int, int> Valence(List<int[]> faces) {
        var edges = new Dictionary<(int Low, int High), int>();

        foreach (var face in faces) {
            for (var corner = 0; corner < face.Length; corner++) {
                var a = face[corner];
                var b = face[(corner + 1) % face.Length];
                var key = (Math.Min(a, b), Math.Max(a, b));

                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        var valence = new Dictionary<int, int>();

        foreach (var count in edges.Values) {
            valence[count] = valence.GetValueOrDefault(count) + 1;
        }

        return valence;
    }

    /// <summary>Writes a kernel mesh as an OBJ the reader can take.</summary>
    string Write(string name, EditMesh mesh) {
        var path = Path.Combine(root, name);

        File.WriteAllText(path, ModelWriter.Obj([ModelGeometry.ToMeshData(mesh, Path.GetFileNameWithoutExtension(name))]), Encoding.UTF8);

        return path;
    }

    static async Task<(ExitCode Code, string Output, string Error)> Run(params string[] args) {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };

        var parsed = VixenCommand.Create(output, error).Parse(args);

        if (parsed.Errors.Count > 0) {
            return (ExitCode.UsageError, output.ToString(), string.Join("\n", parsed.Errors.Select(problem => problem.Message)));
        }

        var code = await parsed.InvokeAsync(null, TestContext.Current.CancellationToken);

        return ((ExitCode) code, output.ToString(), error.ToString());
    }
}
