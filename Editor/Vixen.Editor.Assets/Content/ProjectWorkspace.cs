// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.Assets.Content;

/// <summary>The on-disk stores importing and building a project need, opened together.</summary>
/// <remarks>
///     <para>
///         <b>Four things that have to agree about which directory they are looking at.</b> The GUID
///         index, the artefact store, the import cache and the file provider are separate objects with
///         separate lifetimes, and pointing one of them at the wrong place produces a build with no
///         content in it and no error. Opening them as a unit is what makes that unrepresentable.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in the CLI, which is where it was, because the editor needs the same
///         four.</b> Two orchestrations over the same components drift, and the way this particular
///         drift shows up is the editor and <c>vixen content build</c> producing different output for
///         one project — which reads as a machine problem for as long as it takes somebody to compare
///         two catalogs by hand. This assembly's whole stated job is the part shared with the CLI.
///     </para>
///     <para>
///         <b>Nothing here is lazy.</b> Opening a workspace means the directories exist, the index is
///         readable and the artefact store can be written to — better discovered before an import has
///         run for two minutes than after.
///     </para>
/// </remarks>
public sealed class ProjectWorkspace {
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

    /// <summary>The committed settings under <c>ProjectSettings/</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Here because a build reads them, and both heads run the same build.</b>
    ///         <see cref="ContentPipeline.Build" /> resolves <c>PlayerBuildSettings.Scenes</c> into
    ///         the manifest a player boots from; an editor that read the list and a
    ///         <c>vixen content build</c> that did not would be two builds of one project that differ
    ///         in whether the game opens a level — which is this class's whole reason for existing,
    ///         one file along.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its own store, and the editor's is its own too.</b>
    ///         <see cref="ProjectSettingsStore" /> caches by type and hands every caller the same
    ///         instance, so sharing the editor's would mean a background build reading an object the
    ///         frame thread is editing. Reading the file again costs a parse of a few hundred bytes,
    ///         and gets the build what is <i>on disk</i> — which is what a build should be of.
    ///     </para>
    /// </remarks>
    public ProjectSettingsStore Settings { get; }

    /// <summary>Opens the stores for a project.</summary>
    /// <param name="paths">The project's directories.</param>
    /// <remarks>
    ///     ⚠ <b>The database is its own, and an editor sharing the one its panels read would be a
    ///     race.</b> <see cref="AssetDatabase.Scan" /> clears and repopulates its dictionaries, and an
    ///     import runs on a background thread — so a browser enumerating <c>Entries</c> mid-scan gets
    ///     an exception at best. A workspace scanning privately and the editor rescanning afterwards,
    ///     on the thread that owns its panels, costs one extra walk and is correct.
    /// </remarks>
    public ProjectWorkspace(ProjectPaths paths) {
        ArgumentNullException.ThrowIfNull(paths);

        Paths = paths;
        Database = new(paths);
        Settings = new(paths);

        var files = new VirtualFileSystem();
        files.Mount(new("/library"), new PhysicalFileProvider(paths.Library));

        Chunks = new FileOdbBackend(files, new("/library/ArtifactDb"));
        Artifacts = new(Chunks);
        Files = new PhysicalFileProvider(paths.Root, isReadOnly: true);
        Cache.TryLoad(CacheFile);
    }

    /// <summary>Which importers this build of the engine has.</summary>
    /// <remarks>
    ///     <see cref="BuiltInImporters" />'s list and not a second one. The worker processes
    ///     <c>Tools/Vixen.AssetCompiler</c> starts build their registry from the same call, because a
    ///     worker with a different set produces different artefacts for the same file — and that
    ///     shows up as a cache that never hits, or as a build whose output depends on the machine.
    /// </remarks>
    public static ImporterRegistry Importers() => BuiltInImporters.Create();

    /// <summary>The build target to assume when nobody said.</summary>
    /// <remarks>
    ///     The machine this is running on. A content build is target-specific — the same texture is
    ///     BC7 on a desktop and ASTC on a phone — so there is no neutral default, and the one that
    ///     surprises nobody is "for this computer".
    /// </remarks>
    public static string HostTarget =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "MacOS"
        : "Linux";

    /// <summary>Where a build for a target goes when nobody said.</summary>
    /// <param name="target">The target.</param>
    /// <returns>The directory.</returns>
    public string DefaultOutput(string target) {
        ArgumentException.ThrowIfNullOrEmpty(target);
        return System.IO.Path.Combine(Paths.Build, target.Replace('/', '-'));
    }

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

    /// <summary>Whether a directory looks like a project.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>Whether it has a <c>.vxproj</c> in it, or an <c>Assets/</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Either, and the order is the interesting part.</b> The marker is the answer doc
    ///         08 always specified and <see cref="ProjectMarker" /> now writes; the <c>Assets/</c>
    ///         rule is what answered before it existed, and it stays because every project made
    ///         before this has no marker and must go on opening. A project that acquires one gets the
    ///         stronger test for free.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The marker is what makes the second half of the question answerable.</b>
    ///         <c>Assets/</c> alone is weak in both directions: any directory that happens to contain
    ///         a folder of that name qualifies — a source tree, an unrelated game's export — and a
    ///         project whose assets have all been deleted stops being one, which is a project the
    ///         editor refuses to reopen exactly when somebody most needs it to.
    ///     </para>
    /// </remarks>
    public static bool IsProject(string directory) =>
        ProjectMarker.TryFind(directory, out _)
        || Directory.Exists(System.IO.Path.Combine(directory, "Assets"));
}
