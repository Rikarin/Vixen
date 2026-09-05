// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Core;

namespace Vixen.Cli;

/// <summary>`vixen texture bake` — a folder of authored maps in, a material asset out.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § M5's CLI row, and it runs the same code a panel will.</b> Everything
///         below the argument parsing is <see cref="MaterialBake" /> and
///         <see cref="ProjectMaterialBaker" /> in <c>Vixen.Editor.Assets</c> — the ORM packing, the
///         mip chain, the block compression, the scan-then-read-back GUID dance, the
///         <c>texturing:</c> block and the painted-over refusal. There is one baker and this is a
///         second caller of it, which is the only arrangement in which "the same code the panel runs"
///         is a fact rather than an intention.
///     </para>
///     <para>
///         ⚠ <b>It reads maps from a folder and does not evaluate a graph, and that is a real
///         limitation rather than a simplification.</b> A <c>.vxtexgraph</c> is M4's document and
///         does not exist yet; a verb that took one and apologised is what
///         <see cref="VixenCommand" />'s own header refuses. What this does is the half that is
///         finished and is independently useful — a build script with a folder of authored or
///         externally generated maps gets a packed, mipped, compressed, provenanced material out of
///         it — and the graph arrives as a second way of filling the same dictionary.
///     </para>
///     <para>
///         ⚠ <b>The inputs are named by <i>usage</i> and the outputs by <i>file</i>, which are not
///         the same vocabulary.</b> <c>hull_roughness.png</c> is an input and <c>hull_orm.png</c> is
///         an output, so re-reading a bake's own output folder does not round-trip — the packed map
///         is three inputs and one output by construction. The alternative, naming inputs after the
///         files, would mean asking an artist to pack the ORM map themselves, which is the work this
///         verb exists to do.
///     </para>
/// </remarks>
public static class TextureRunner {
    /// <summary>Bakes a folder of maps into a material in a project.</summary>
    /// <param name="project">The project to write into.</param>
    /// <param name="from">The folder holding the maps, named <c>&lt;anything&gt;_&lt;usage&gt;.png</c>.</param>
    /// <param name="name">What the material should be called.</param>
    /// <param name="folder">Which folder under <c>Assets/</c> to write into.</param>
    /// <param name="adapter">What to record as the adapter. Recorded, never asserted.</param>
    /// <param name="force">Overwrite outputs somebody has painted over.</param>
    /// <param name="output">Where to write what happened.</param>
    /// <param name="error">Where to complain.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A writer is null.</exception>
    public static ExitCode Bake(
        Project project,
        string from,
        string name,
        string folder,
        string adapter,
        bool force,
        TextWriter output,
        TextWriter error
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!Directory.Exists(from)) {
            error.WriteLine($"There is no directory at '{from}'.");
            return ExitCode.UsageError;
        }

        var outputs = Read(from, error);

        if (outputs is null) {
            return ExitCode.UsageError;
        }

        if (outputs.Count == 0) {
            error.WriteLine(
                $"Nothing in '{from}' is a map this reads. A map is called <anything>_<usage>.png, where "
                + $"<usage> is one of {string.Join(", ", MaterialMapNaming.Every.Select(MaterialMapNaming.Suffix))}."
            );

            return ExitCode.UsageError;
        }

        var editor = new EditorProject(project.Paths);
        var record = new MaterialBakeRecord {
            Source = Relative(project, from),
            Adapter = adapter
        };

        MaterialBakeSet set;

        try {
            set = new ProjectMaterialBaker(editor, folder).Write(
                name,
                MaterialBake.Encode(outputs),
                record,
                force
            );
        } catch (ArgumentException failure) {
            error.WriteLine(failure.Message);
            return ExitCode.UsageError;
        } catch (IOException failure) {
            // ⚠ The painted-over refusal lands here, and it is the one failure whose message has to
            // say what to do next: a build script that hit it has to know that --force is the answer
            // and that using it replaces somebody's painting.
            error.WriteLine(failure.Message);
            error.WriteLine("Pass --force to bake over it anyway.");

            return ExitCode.Failed;
        }

        foreach (var warning in set.Warnings) {
            error.WriteLine(warning);
        }

        output.WriteLine(
            $"{set.Name}: {(set.Files.Count - 1).ToString(CultureInfo.InvariantCulture)} maps and a material."
        );

        foreach (var file in set.Files) {
            output.WriteLine("  " + Relative(project, file));
        }

        return ExitCode.Success;
    }

    /// <summary>Every map in a folder, by what it is a map of.</summary>
    /// <remarks>
    ///     ⚠ <b>Two files claiming one usage is refused rather than resolved by enumeration order.</b>
    ///     A folder holding both <c>hull_roughness.png</c> and <c>old_roughness.png</c> is a person
    ///     who has not finished tidying, and picking one of them silently is how a bake produces the
    ///     wrong material on one machine and the right one on another.
    /// </remarks>
    static Dictionary<MaterialMapUsage, Bitmap>? Read(string from, TextWriter error) {
        var found = new Dictionary<MaterialMapUsage, Bitmap>();
        var named = new Dictionary<MaterialMapUsage, string>();

        foreach (var file in Directory.EnumerateFiles(from, "*" + MaterialMapNaming.PortableExtension)
            .OrderBy(file => file, StringComparer.Ordinal)) {
            var stem = Path.GetFileNameWithoutExtension(file);
            var split = stem.LastIndexOf('_');

            if (split < 0 || !MaterialMapNaming.TryParseSuffix(stem[(split + 1)..], out var usage)) {
                continue;
            }

            if (named.TryGetValue(usage, out var already)) {
                error.WriteLine(
                    $"'{Path.GetFileName(already)}' and '{Path.GetFileName(file)}' are both the "
                    + $"{MaterialMapNaming.Suffix(usage)} map. Only one file may be."
                );

                return null;
            }

            try {
                found[usage] = PngCodec.Decode(File.ReadAllBytes(file));
                named[usage] = file;
            } catch (Exception failure) when (failure is IOException or InvalidDataException) {
                error.WriteLine($"'{Path.GetFileName(file)}' could not be read: {failure.Message}");
                return null;
            }
        }

        return found;
    }

    /// <summary>A path measured from the project where it is inside one, and left alone where it is not.</summary>
    /// <remarks>
    ///     ⚠ An absolute path off somebody's machine in a provenance block is a fact about that
    ///     machine, and it is the sort that reaches a review as a diff nobody can act on.
    /// </remarks>
    static string Relative(Project project, string path) {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(project.Paths.Root);

        return full.StartsWith(root, StringComparison.Ordinal)
            ? full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/')
            : full;
    }
}
