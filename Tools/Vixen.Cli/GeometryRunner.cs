// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;
using Vixen.Geometry.Uv;
using Vixen.Rendering;

namespace Vixen.Cli;

/// <summary>`vixen remesh`, `vixen unwrap` and `vixen uv pack` — a mesh file in, a mesh file out.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D16's CLI row and docs/plan/42 § D13's, which exist to be batchable over a
///         directory.</b> Neither reads a project: a file on disk is the unit, because the case both
///         documents were written for is a pile of generated GLBs that are not assets yet and a build
///         script that has to turn them into ones.
///     </para>
///     <para>
///         ⚠ <b>The input goes through Assimp — <see cref="ModelReader" /> — and the output through
///         <see cref="ModelWriter" />, which is why this reaches into <c>Vixen.Editor.Assets</c>.</b>
///         <c>Tools/</c> is the top of the layering and already links two editor assemblies; writing a
///         second glTF reader here so that the CLI could pretend not to would be the wrong kind of
///         independence.
///     </para>
///     <para>
///         ⚠ <b>There is no <c>--bake</c>, and docs/plan/41 § D16's example line has one.</b> The
///         normal and displacement bake is R5's and nothing in the pipeline performs it yet —
///         <see cref="RemeshSettings.TransferAttributes" /> is declared and unread. A flag that parsed
///         and then wrote no maps is exactly the verb <c>VixenCommand</c>'s own remarks refuse: "a verb
///         that parses and then apologises is worse than one that is not there, because a build script
///         can only discover the second kind." It arrives with the bake.
///     </para>
/// </remarks>
public static class GeometryRunner {
    /// <summary>Retopologises every mesh in a file.</summary>
    /// <param name="input">The file to read.</param>
    /// <param name="output">The file to write. Its extension chooses the format.</param>
    /// <param name="settings">What the remesh should do.</param>
    /// <param name="guides">Guide curve files, as <c>.vxspline</c> paths.</param>
    /// <param name="progress">Where to write progress.</param>
    /// <param name="error">Where to complain.</param>
    /// <returns>The exit code.</returns>
    public static ExitCode Remesh(
        string input,
        string output,
        RemeshSettings settings,
        IReadOnlyList<(string Path, float Strength)> guides,
        TextWriter progress,
        TextWriter error
    ) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(guides);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(error);

        if (Open(input, output, error) is not { } read) {
            return ExitCode.UsageError;
        }

        var curves = new List<RemeshGuide>();

        foreach (var (path, strength) in guides) {
            if (!Guide(path, strength, curves, error)) {
                return ExitCode.UsageError;
            }
        }

        var written = new List<MeshData>();
        var refused = 0;

        foreach (var mesh in read.Meshes) {
            if (mesh.Indices.Length == 0) {
                continue;
            }

            var quads = Remesher.Remesh(
                ModelGeometry.ToEditMesh(mesh),
                settings with { Guides = curves },
                out var report
            );

            foreach (var warning in report.Warnings) {
                progress.WriteLine($"  note    {mesh.Name}: {warning}");
            }

            if (quads.IsEmpty) {
                error.WriteLine($"'{mesh.Name}' could not be retopologised. See the notes above for the stage that refused.");
                refused++;

                continue;
            }

            progress.WriteLine(
                $"  {mesh.Name}: {report.QuadCount} quads, "
                + $"deviation {report.MaxDeviation:0.#####} worst and {report.MeanDeviation:0.#####} mean "
                + $"of the diagonal, feature error {report.FeatureReproductionError:0.#####}."
            );

            // ⚠ Printed here rather than left in the report, because docs/plan/41 § Part 4's whole claim
            // is that a remesh that went wrong says so. A result that comes back not watertight with no
            // line naming a defect is indistinguishable from one that is fine.
            if (report.Mesh.Describe() is { } defects) {
                progress.WriteLine($"  note    {mesh.Name}: the result is not a closed solid — {defects}.");
            }

            written.Add(ModelGeometry.ToMeshData(quads, mesh.Name));
        }

        return Finish(written, refused, output, progress, error);
    }

    /// <summary>Unwraps every mesh in a file: charts, flattening and packing.</summary>
    /// <param name="input">The file to read.</param>
    /// <param name="output">The file to write.</param>
    /// <param name="settings">Where to cut and how flat to make it.</param>
    /// <param name="packing">Where the islands go.</param>
    /// <param name="progress">Where to write progress.</param>
    /// <param name="error">Where to complain.</param>
    /// <returns>The exit code.</returns>
    public static ExitCode Unwrap(
        string input,
        string output,
        UvSettings settings,
        PackSettings packing,
        TextWriter progress,
        TextWriter error
    ) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(packing);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(error);

        if (Open(input, output, error) is not { } read) {
            return ExitCode.UsageError;
        }

        var written = new List<MeshData>();
        var refused = 0;

        foreach (var mesh in read.Meshes) {
            if (mesh.Indices.Length == 0) {
                continue;
            }

            var kernel = ModelGeometry.ToEditMesh(mesh);

            try {
                var charts = UvUnwrap.Charts(kernel, settings);
                var islands = UvUnwrap.Flatten(kernel, charts, settings);
                var placements = UvUnwrap.Pack(islands, packing, out var report);

                Say(progress, mesh.Name, report);
                written.Add(ModelGeometry.ToMeshData(kernel, mesh.Name, ModelGeometry.Atlas(kernel, islands, placements)));
            } catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) {
                error.WriteLine($"'{mesh.Name}' could not be unwrapped: {failure.Message}");
                refused++;
            }
        }

        return Finish(written, refused, output, progress, error);
    }

    /// <summary>Repacks the coordinates a file already carries, without touching a seam.</summary>
    /// <param name="input">The file to read.</param>
    /// <param name="output">The file to write.</param>
    /// <param name="packing">Where the islands go.</param>
    /// <param name="progress">Where to write progress.</param>
    /// <param name="error">Where to complain.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/42's seventh exit criterion, as a verb: "islands unwrapped in Blender,
    ///         imported, repacked: seams untouched, island shapes untouched, better efficiency".</b>
    ///         That is the UV-Packer comparison and it is the one the request asked for, so the third
    ///         stage has to be reachable without the first two — which is why <c>UvUnwrap.Pack</c>
    ///         takes islands rather than a mesh.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The islands come off the file's drawing vertices rather than through
    ///         <see cref="EditMesh" />, and that is the correct side of the split.</b> A seam is two
    ///         drawing vertices at one position with different coordinates, so welding into the kernel
    ///         first and then trying to recover the seams would be throwing the answer away and looking
    ///         for it. <see cref="ModelGeometry.Islands" /> is where that reading lives, and its remarks
    ///         carry the trap: a component over shared vertex <i>indices</i> is an island only in a file
    ///         that shares an index wherever the position and the coordinate agree, and a generated GLB
    ///         shares nothing at all.
    ///     </para>
    /// </remarks>
    public static ExitCode Pack(
        string input,
        string output,
        PackSettings packing,
        TextWriter progress,
        TextWriter error
    ) {
        ArgumentNullException.ThrowIfNull(packing);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(error);

        if (Open(input, output, error) is not { } read) {
            return ExitCode.UsageError;
        }

        var written = new List<MeshData>();
        var refused = 0;

        foreach (var mesh in read.Meshes) {
            if (mesh.Indices.Length == 0) {
                continue;
            }

            if (mesh.TexCoords.Length != mesh.Positions.Length) {
                error.WriteLine(
                    $"'{mesh.Name}' has no texture coordinates, so there is nothing to repack. "
                    + "`vixen unwrap` is the verb that makes some."
                );

                refused++;

                continue;
            }

            var islands = ModelGeometry.Islands(mesh);

            try {
                var placements = UvUnwrap.Pack(islands, packing, out var report);

                Say(progress, mesh.Name, report);
                written.Add(Repacked(mesh, islands, placements));
            } catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) {
                error.WriteLine($"'{mesh.Name}' could not be repacked: {failure.Message}");
                refused++;
            }
        }

        return Finish(written, refused, output, progress, error);
    }

    /// <summary>The mesh with its coordinates moved to where the packer put them.</summary>
    static MeshData Repacked(MeshData mesh, IReadOnlyList<UvIsland> islands, IReadOnlyList<UvPlacement> placements) {
        var coordinates = (Vector2[]) mesh.TexCoords.Clone();

        foreach (var placement in placements) {
            if (placement.Island < 0 || placement.Island >= islands.Count) {
                continue;
            }

            var island = islands[placement.Island];

            for (var corner = 0; corner < island.Corners.Count; corner++) {
                coordinates[mesh.Indices[island.Corners[corner]]] = placement.Apply(island, island.Coordinates[corner]);
            }
        }

        return mesh with { TexCoords = coordinates };
    }

    /// <summary>Reads the input, having checked that both paths are usable.</summary>
    static ReadModel? Open(string input, string output, TextWriter error) {
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) {
            error.WriteLine($"There is no file at '{input}'.");

            return null;
        }

        // Checked before the read rather than after, because reading and remeshing a four-million
        // triangle blob and only then discovering the output is a .fbx is a minute of somebody's time
        // spent on an answer that was available immediately.
        if (!ModelWriter.CanWrite(output)) {
            error.WriteLine(
                $"'{Path.GetExtension(output)}' is not a format this writes. It writes "
                + string.Join(", ", ModelWriter.Extensions) + "."
            );

            return null;
        }

        try {
            return ModelReader.Read(
                File.ReadAllBytes(input),
                Path.GetExtension(input),
                Path.GetFileNameWithoutExtension(input),
                new ModelImportSettings { GenerateTangents = false }
            );
        } catch (Exception failure) when (failure is ModelFormatException or IOException or UnauthorizedAccessException) {
            error.WriteLine($"'{input}' could not be read as a model: {failure.Message}");

            return null;
        }
    }

    /// <summary>Reads one guide curve asset into the list, or complains and says no.</summary>
    static bool Guide(string path, float strength, List<RemeshGuide> into, TextWriter error) {
        if (!File.Exists(path)) {
            error.WriteLine($"There is no guide curve at '{path}'.");

            return false;
        }

        try {
            var asset = Core.Yaml.YamlSerializer.Deserialize<SplineAsset>(Core.Yaml.YamlReader.Read(File.ReadAllText(path)));

            if (!asset.CanBuild) {
                error.WriteLine($"The guide '{path}' has fewer than two control points, so it is not a curve.");

                return false;
            }

            into.Add(ModelRetopology.ToGuide(asset.Build(), strength));

            return true;
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            error.WriteLine($"The guide '{path}' could not be read: {failure.Message}");

            return false;
        }
    }

    static void Say(TextWriter output, string name, UvReport report) =>
        output.WriteLine(
            $"  {name}: {report.ChartCount} charts, "
            + $"{report.EffectiveEfficiency:P1} of the atlas used after margin, "
            + $"{report.Distortion.Flipped} flipped triangles."
        );

    /// <summary>Writes what survived, and decides what that means for the exit code.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing written is a failure and a partial result is one too.</b> A build script that
    ///     saw a zero exit code and an output file with three of its five meshes in it would ship the
    ///     three, and the two that refused would be discovered by somebody looking at the level.
    /// </remarks>
    static ExitCode Finish(
        List<MeshData> written,
        int refused,
        string output,
        TextWriter progress,
        TextWriter error
    ) {
        if (written.Count == 0) {
            error.WriteLine("Nothing was produced, so nothing was written.");

            return ExitCode.Failed;
        }

        try {
            ModelWriter.Write(output, written, Path.GetFileNameWithoutExtension(output));
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException) {
            error.WriteLine($"'{output}' could not be written: {failure.Message}");

            return ExitCode.Failed;
        }

        progress.WriteLine($"  {written.Count} mesh(es) written to {output}");

        return refused > 0 ? ExitCode.Failed : ExitCode.Success;
    }
}
