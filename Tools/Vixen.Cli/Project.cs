// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;

namespace Vixen.Cli;

/// <summary>A project on disk, and everything a command needs to work on it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The stores themselves are <see cref="ProjectWorkspace" />'s, in
///         <c>Vixen.Editor.Assets</c>.</b> They moved there when the editor grew import and build
///         commands of its own: two ways of opening the same four things is two ways for one of them
///         to be pointed at the wrong directory. What is left here is finding the project a command
///         is meant to work on, which is a command-line concern and only that.
///     </para>
///     <para>
///         The properties are kept as forwarders rather than deleted, so that every call site reads
///         the way it did — <c>project.Database</c>, not <c>project.Workspace.Database</c>.
///     </para>
/// </remarks>
public sealed class Project {
    /// <summary>The stores, opened together.</summary>
    public ProjectWorkspace Workspace { get; }

    /// <summary>What a project's directories are called.</summary>
    public ProjectPaths Paths => Workspace.Paths;

    /// <summary>The GUID index over <c>Assets/</c>.</summary>
    public AssetDatabase Database => Workspace.Database;

    /// <summary>Where imported chunks are stored, under <c>Library/</c>.</summary>
    public ObjectDatabase Artifacts => Workspace.Artifacts;

    /// <summary>The store behind <see cref="Artifacts" />, which is what a content build packs from.</summary>
    public IOdbBackend Chunks => Workspace.Chunks;

    /// <summary>What the last import of each asset produced.</summary>
    public ImportCache Cache => Workspace.Cache;

    /// <summary>Where the import cache is written.</summary>
    public string CacheFile => Workspace.CacheFile;

    /// <summary>Source files, rooted at the project, which is what an importer's paths are relative to.</summary>
    public IFileProvider Files => Workspace.Files;

    Project(ProjectPaths paths) => Workspace = new(paths);

    /// <summary>Finds the project a command is meant to work on.</summary>
    /// <param name="given">What <c>--project</c> said, or <see langword="null" /> for "work it out".</param>
    /// <param name="project">The project.</param>
    /// <param name="error">Why not, if not.</param>
    /// <returns>Whether there is one.</returns>
    /// <remarks>
    ///     With no <c>--project</c>, the working directory and then each of its ancestors is tried, so
    ///     that running the tool from somewhere inside a project works the way <c>git</c> does. The
    ///     walk stops at the filesystem root and says what it was looking for, because "no project
    ///     found" without saying what one looks like is the least useful error a tool can give.
    /// </remarks>
    public static bool TryOpen(string? given, out Project project, out string error) {
        project = null!;
        error = string.Empty;

        if (given is { Length: > 0 }) {
            var root = System.IO.Path.GetFullPath(given);

            if (!Directory.Exists(root)) {
                error = $"There is no directory at '{root}'.";
                return false;
            }

            if (!ProjectWorkspace.IsProject(root)) {
                error = $"'{root}' is not a Vixen project: it has no Assets/ directory.";
                return false;
            }

            project = new(new(root));
            return true;
        }

        for (var directory = Environment.CurrentDirectory; directory is { Length: > 0 };) {
            if (ProjectWorkspace.IsProject(directory)) {
                project = new(new(directory));
                return true;
            }

            var parent = Directory.GetParent(directory);

            if (parent is null) {
                break;
            }

            directory = parent.FullName;
        }

        error = $"'{Environment.CurrentDirectory}' is not inside a Vixen project. A project is a directory with an "
            + "Assets/ folder; pass --project to name one.";

        return false;
    }

    /// <summary>Which importers this build of the tool has.</summary>
    public static ImporterRegistry Importers() => ProjectWorkspace.Importers();

    /// <summary>The build target to assume when nobody said.</summary>
    public static string HostTarget => ProjectWorkspace.HostTarget;

    /// <summary>Where a build for a target goes when nobody said.</summary>
    /// <param name="target">The target.</param>
    /// <returns>The directory.</returns>
    public string DefaultOutput(string target) => Workspace.DefaultOutput(target);

    /// <summary>Reads every <c>.vxgroup</c> the project defines.</summary>
    /// <param name="failures">What could not be read, and why.</param>
    /// <returns>The groups, in name order.</returns>
    public List<AddressableGroup> Groups(out List<string> failures) => Workspace.Groups(out failures);

    /// <summary>Writes the index and the import cache back.</summary>
    public void Save() => Workspace.Save();
}
