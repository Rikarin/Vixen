// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Assets.MeshMaps;

namespace Vixen.Cli;

/// <summary>`vixen mesh-maps list` — what a graph would bind, resolved the way a graph resolves it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § 4.8's binding, made visible.</b> A Mesh Map Input node asks for a usage
///         and is handed whichever file this project's bake produced; the whole of that lookup is
///         <see cref="MeshMapLibrary" />, and the whole of this verb is running it and printing what
///         came back. So the question an artist actually asks — <i>why is my Curvature Edge Wear
///         binding nothing on this mesh</i> — has an answer that does not require opening the panel
///         that is failing.
///     </para>
///     <para>
///         ⚠ <b>The point of it being a verb is that it is a second caller.</b> The read side landed
///         with no consumer at all —
///         <a href="https://github.com/Rikarin/Vixen/issues/702">#702</a> — and a resolver whose only
///         caller is a node that does not exist yet is the finished-thing-nothing-calls shape this
///         repository produces most often. This runs the same index the node will, so a project in
///         which the verb finds nothing is a project in which the node would bind nothing.
///     </para>
///     <para>
///         ⚠ <b>It scans before it indexes.</b> A library is a snapshot over the asset database, and
///         a database loaded from its committed index knows only what the last editor session saw —
///         so a bake run from another process would be invisible, which for a verb whose whole job is
///         "what is there" is the wrong answer rather than a stale one.
///     </para>
/// </remarks>
public static class MeshMapRunner {
    /// <summary>Lists a project's baked mesh maps, or resolves one.</summary>
    /// <param name="project">The project to read.</param>
    /// <param name="set">
    ///     Only the set of this name, or empty for all of them. The set's name is the stem every file
    ///     in it is named from, which is not always the mesh's own name — see
    ///     <c>ProjectMeshMapBaker.SetName</c>.
    /// </param>
    /// <param name="usage">Only this usage's suffix — <c>ao</c>, <c>curvature</c> — or empty for all.</param>
    /// <param name="output">Where to write what was found.</param>
    /// <param name="error">Where to complain.</param>
    /// <returns>The exit code. <see cref="ExitCode.Failed" /> when a named query resolved nothing.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ExitCode List(
        Project project,
        string set,
        string usage,
        TextWriter output,
        TextWriter error
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var wanted = default(MeshMapUsage?);

        if (usage.Length > 0) {
            if (!MeshMapNaming.TryParseSuffix(usage, out var parsed)) {
                error.WriteLine(
                    $"'{usage}' is not a mesh map. The nine are "
                    + $"{string.Join(", ", MeshMapNaming.Every.Select(MeshMapNaming.Suffix))}."
                );

                return ExitCode.UsageError;
            }

            wanted = parsed;
        }

        project.Database.Scan();

        var library = MeshMapLibrary.Index(project.Database);

        // ⚠ Ordered here rather than by the index, which enumerates the database. A verb whose output
        // a build script diffs cannot have a file system's ordering in it.
        var found = library.Maps
            .Where(map => set.Length == 0 || string.Equals(map.Set, set, StringComparison.Ordinal))
            .Where(map => wanted is null || map.Usage == wanted)
            .OrderBy(map => map.Set, StringComparer.Ordinal)
            .ThenBy(map => map.Usage)
            .ToList();

        if (found.Count == 0) {
            error.WriteLine(Nothing(library, set, usage));

            // ⚠ A failure and not a success with an empty list, because the caller asked a question
            // whose answer decides whether a graph binds. A build script that treated "no maps" as
            // success would bake a material whose generators all read the fallback.
            return ExitCode.Failed;
        }

        foreach (var map in found) {
            output.WriteLine(
                $"{map.Set}  {MeshMapNaming.Suffix(map.Usage)}  {map.Map.Asset}  {map.Path}"
                + (map.Scale > 0f ? $"  scale={map.Scale.ToString("R", CultureInfo.InvariantCulture)}" : "")
            );
        }

        return ExitCode.Success;
    }

    /// <summary>What to say when the query matched nothing, which is not one message.</summary>
    /// <remarks>
    ///     ⚠ <b>"No maps in this project" and "that set has no curvature map" are different problems
    ///     and the second is the common one.</b> Only the normal and the displacement are always
    ///     baked; the seven that cost rays are baked when the settings asked for them, so a generator
    ///     binding nothing is usually a bake that was run with occlusion switched off rather than a
    ///     project with no bake at all.
    /// </remarks>
    static string Nothing(MeshMapLibrary library, string set, string usage) {
        if (library.Maps.Count == 0) {
            return "This project holds no baked mesh maps. Bake them from Assets ▸ Bake Mesh Maps…, "
                + "which is what writes the sidecar keys this reads.";
        }

        if (set.Length > 0 && !library.Sets.Contains(set, StringComparer.Ordinal)) {
            return $"There is no set called '{set}'. This project has "
                + $"{string.Join(", ", library.Sets)}.";
        }

        return usage.Length > 0
            ? $"Nothing here measures '{usage}'. Only the normal and the height map are always baked; "
            + "the rest are baked when the bake was asked for them."
            : "Nothing matched.";
    }
}
