// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;

namespace Vixen.Editor.App;

/// <summary>Puts a baked block-out mesh into the project and says what asset it became.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P7 bake, application side.</b> <see cref="IMeshBaker" /> is the seam
///         <c>Vixen.Editor.SceneView</c> declares and this is the half that knows about the asset
///         database — the same arrangement <c>IMeshSource</c>, <c>ISurfaceSource</c> and
///         <c>ISceneWriter</c> already have.
///     </para>
///     <para>
///         ⚠ <b>Written as an OBJ, which the existing model importer already reads.</b> The plan asks
///         for the bake to go "through the existing importer machinery", and that machinery is
///         <c>ModelImporter</c> — so the baked file is the same file the artist opens, compiled by the
///         same path as anything they hand back. A mesh format of the editor's own would have been a
///         second compiler to keep in step with the first, for geometry whose whole purpose is to be
///         replaced.
///     </para>
///     <para>
///         ⚠ <b>A scan rather than an import, and the difference is what mints the identity.</b> A
///         file in <c>Assets/</c> has no <c>AssetId</c> until the database has seen it and written a
///         <c>.meta</c> beside it; the compile that turns it into a chunk happens afterwards and on
///         its own schedule. So the bake writes, scans, and reads back the GUID — and the entity is
///         pointed at an asset that exists whether or not the content build has caught up, which is
///         what <c>MeshExtractionSystem</c>'s "ask again next frame" is already written for.
///     </para>
///     <para>
///         ⚠ <b>An existing file of the same name is overwritten, and it keeps its GUID.</b> Baking
///         the same wall twice is a designer iterating, and a second asset each time would leave the
///         project full of <c>Wall_3</c> — where overwriting means every entity already pointing at
///         that asset picks up the new shape, which is what somebody re-baking a shared piece means.
///     </para>
/// </remarks>
/// <param name="project">The project to write into.</param>
/// <param name="folder">Which folder under <c>Assets/</c> baked meshes go in.</param>
public sealed class ProjectMeshBaker(EditorProject project, string folder = "Blockout") : IMeshBaker {
    /// <summary>Where baked meshes go, relative to the project's assets.</summary>
    public string Folder { get; } = folder;

    /// <summary>The last file this wrote, as a full path, or null if it has written none.</summary>
    /// <remarks>What a status line reports and what a test asserts on: a bake that silently wrote
    ///     nothing and a bake that wrote something look identical from the return value alone when
    ///     the database has not caught up.</remarks>
    public string? Written { get; private set; }

    /// <inheritdoc />
    public AssetReference Bake(string name, string extension, string content) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.Combine(project.Paths.Assets, Folder);

        Directory.CreateDirectory(directory);

        // ⚠ Sanitised rather than trusted. An entity is named by a person and "Wall / 2" is a
        // perfectly good name for one and a path traversal in a file system.
        var file = Path.Combine(directory, Safe(name) + extension);

        File.WriteAllText(file, content);
        Written = file;

        project.Assets.Scan();
        project.Assets.Save();

        // ⚠ Relative to the project *root*, which is what the database keys on. `AssetDatabase`
        // indexes every entry as `Paths.Relative(absolute)` — `Assets/Blockout/Wall.obj` — so
        // measuring it from `Paths.Assets` asked the index for `Blockout/Wall.obj`, matched nothing,
        // and returned `AssetReference.Null` for a file that had just been written and sidecarred.
        // Every block-out bake did that, silently: the file was on disk to prove the bake had
        // worked, and the entity was pointed at nothing.
        var relative = project.Paths.Relative(file);

        return project.Assets.TryGetByPath(relative, out var entry)
            ? new AssetReference(entry.Guid)
            : AssetReference.Null;
    }

    static string Safe(string name) {
        var made = new char[name.Length];

        for (var index = 0; index < name.Length; index++) {
            made[index] = Array.IndexOf(Path.GetInvalidFileNameChars(), name[index]) >= 0 ? '_' : name[index];
        }

        var text = new string(made).Trim();

        return text.Length == 0 ? "Blockout" : text;
    }
}
