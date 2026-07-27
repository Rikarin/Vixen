// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;

namespace Vixen.Cli;

/// <summary>A project on disk, and everything a command needs to work on it.</summary>
/// <remarks>
///     <para>
///         Built once per command rather than passed around as four separate objects, because every
///         command needs the same four and getting one of them pointed at the wrong directory is the
///         kind of mistake that produces a build with no content in it and no error.
///     </para>
///     <para>
///         <b>Nothing here is lazy.</b> Opening a project means the directories exist, the index is
///         readable and the artefact store can be written to — all of which are better discovered
///         before an import has run for two minutes than after.
///     </para>
/// </remarks>
public sealed class Project {
    /// <summary>What a project's directories are called.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>The GUID index over <c>Assets/</c>.</summary>
    public AssetDatabase Database { get; }

    /// <summary>Where imported chunks are stored, under <c>Library/</c>.</summary>
    public ObjectDatabase Artifacts { get; }

    /// <summary>The store behind <see cref="Artifacts" />, which is what a content build packs from.</summary>
    public IOdbBackend Chunks { get; }

    /// <summary>What the last import of each asset produced.</summary>
    public ImportCache Cache { get; } = new();

    /// <summary>Where the import cache is written.</summary>
    public string CacheFile => System.IO.Path.Combine(Paths.Library, "ImportCache");

    /// <summary>Source files, rooted at the project, which is what an importer's paths are relative to.</summary>
    public IFileProvider Files { get; }

    Project(ProjectPaths paths) {
        Paths = paths;
        Database = new(paths);

        var files = new VirtualFileSystem();
        files.Mount(new("/library"), new PhysicalFileProvider(paths.Library));

        Chunks = new FileOdbBackend(files, new("/library/ArtifactDb"));
        Artifacts = new(Chunks);
        Files = new PhysicalFileProvider(paths.Root, isReadOnly: true);
        Cache.TryLoad(CacheFile);
    }

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

            if (!IsProject(root)) {
                error = $"'{root}' is not a Vixen project: it has no Assets/ directory.";
                return false;
            }

            project = new(new(root));
            return true;
        }

        for (var directory = Environment.CurrentDirectory; directory is { Length: > 0 };) {
            if (IsProject(directory)) {
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
    /// <remarks>
    ///     Told, never discovered. An assembly scan would read metadata a trimmed publish has already
    ///     deleted, and would make "which importers imported this project" a question with different
    ///     answers in the editor and here. This list is the answer, and it is short because Phase 3
    ///     has one real importer in it.
    /// </remarks>
    public static ImporterRegistry Importers() =>
        new ImporterRegistry()
            .Add(new TextureImporter())
            .Add(new FolderImporter())
            .AddFallback(new RawImporter());

    /// <summary>The build target to assume when nobody said.</summary>
    /// <remarks>
    ///     The machine the tool is running on. A content build is target-specific — the same texture
    ///     is BC7 on a desktop and ASTC on a phone — so there is no neutral default, and the one that
    ///     surprises nobody is "for this computer".
    /// </remarks>
    public static string HostTarget =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "MacOS"
        : "Linux";

    /// <summary>Where a build for a target goes when nobody said.</summary>
    /// <param name="target">The target.</param>
    /// <returns>The directory.</returns>
    public string DefaultOutput(string target) =>
        System.IO.Path.Combine(Paths.Build, target.Replace('/', '-'));

    /// <summary>Reads every <c>.vxgroup</c> the project defines.</summary>
    /// <param name="failures">What could not be read, and why.</param>
    /// <returns>The groups, in name order.</returns>
    /// <remarks>
    ///     A group file that will not parse is reported and skipped rather than thrown on: the assets
    ///     that name it will each say so with their own path attached, which is more use than one
    ///     stack trace about a file the author may not have touched.
    /// </remarks>
    public List<AddressableGroup> Groups(out List<string> failures) {
        var groups = new List<AddressableGroup>();
        failures = [];

        if (!Directory.Exists(Paths.Assets)) {
            return groups;
        }

        foreach (var file in Directory.EnumerateFiles(Paths.Assets, "*.vxgroup", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal)) {
            try {
                var group = YamlSerializer.Parse<AddressableGroup>(File.ReadAllText(file));

                if (group.Name.Length == 0) {
                    failures.Add($"'{Paths.Relative(file)}' names no group, so nothing can be put in it.");
                    continue;
                }

                groups.Add(group);
            } catch (Exception failure) when (failure is YamlBindingException or YamlParseException or IOException) {
                failures.Add($"'{Paths.Relative(file)}' could not be read: {failure.Message}");
            }
        }

        var duplicates = groups.GroupBy(group => group.Name, StringComparer.Ordinal).Where(named => named.Count() > 1);

        foreach (var duplicate in duplicates) {
            failures.Add(
                $"Two .vxgroup files both define '{duplicate.Key}'. A group's policy has to have one answer."
            );
        }

        groups.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return groups;
    }

    /// <summary>Writes the index and the import cache back.</summary>
    public void Save() {
        Database.Save();
        Cache.Save(CacheFile);
    }

    static bool IsProject(string directory) => Directory.Exists(System.IO.Path.Combine(directory, "Assets"));
}
