// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Core;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

namespace Vixen.Editor.App;

/// <summary>Puts a mesh's baked maps into the project and says what assets they became.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D12's last line, application side.</b> <see cref="IMeshMapBaker" /> is the
///         seam <c>Vixen.Editor.Assets</c> declares and this is the half that knows about the asset
///         database — the same arrangement <see cref="ProjectMeshBaker" /> has for doc 24's block-out
///         bake, and it is that file rather than a new idea, deliberately.
///     </para>
///     <para>
///         ⚠ <b>Ordinary files with ordinary sidecars, and § D12 says why in one sentence: an artist
///         wants to look at the curvature map when a generator misbehaves.</b> The same pixels
///         written into <c>Library/</c> would be a cache — invisible in the browser, unopenable,
///         un-referenceable by a material, and gone on the next clean. What lands here is a PNG a
///         double-click opens.
///     </para>
///     <para>
///         ⚠ <b>A scan rather than an import, and the difference is what mints the identity.</b> A
///         file in <c>Assets/</c> has no <c>AssetId</c> until the database has seen it and written a
///         <c>.meta</c> beside it. So the bake writes, scans, and reads the GUID back —
///         <see cref="ProjectMeshBaker" />'s dance, for its reason, and § D11 names it as the
///         sequence a material bake must use too.
///     </para>
///     <para>
///         ⚠ <b>The sidecar is finished afterwards, and it has to be.</b> <c>AssetDatabase.Scan</c>
///         mints a sidecar with a GUID and deliberately no <c>importer</c> key — which importer
///         claims a file is decided at import time, and a guess written there would be a fact the
///         file asserts and nothing checks. But a mesh map is exactly a file whose bytes do not say
///         what they mean: an id map that gets a mip chain and an object-space normal map read as a
///         tangent-space one are both silent. So the settings and the usage are written into the
///         sidecar the scan just made, keeping the GUID it minted.
///     </para>
///     <para>
///         ⚠ <b>Re-baking overwrites and keeps every GUID.</b> Baking the same mesh twice is an
///         artist raising the ray count, and a second set each time would leave the project full of
///         <c>Barrel_ao_3</c> while every generator went on reading the first one.
///     </para>
/// </remarks>
/// <param name="project">The project to write into.</param>
/// <param name="folder">Which folder under <c>Assets/</c> baked maps go in.</param>
public sealed class ProjectMeshMapBaker(EditorProject project, string folder = MeshMapNaming.DefaultFolder)
    : IMeshMapBaker {
    /// <summary>Where baked maps go, relative to the project's assets.</summary>
    public string Folder { get; } = folder;

    /// <summary>The files the last bake wrote, as full paths.</summary>
    /// <remarks>
    ///     What a status line reports and what a test asserts on. A bake that wrote nothing and a
    ///     bake whose files the database has not caught up with look identical from the references
    ///     alone, which is <see cref="ProjectMeshBaker.Written" />'s reason as well.
    /// </remarks>
    public IReadOnlyList<string> Written { get; private set; } = [];

    /// <inheritdoc />
    public MeshMapSet Bake(string mesh, EditMesh source, EditMesh target, BakeSettings settings) {
        ArgumentException.ThrowIfNullOrEmpty(mesh);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);

        var maps = MapBaker.Bake(source, target, settings);

        return Write(mesh, MeshMapBake.Encode(Safe(mesh), maps), maps.Warnings);
    }

    /// <inheritdoc />
    public MeshMapSet Write(string mesh, IReadOnlyList<MeshMapImage> images, IReadOnlyList<string> warnings) {
        ArgumentException.ThrowIfNullOrEmpty(mesh);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(warnings);

        var name = Safe(mesh);
        var directory = Path.Combine(project.Paths.Assets, Folder);

        Directory.CreateDirectory(directory);

        var files = new List<string>(images.Count);

        foreach (var image in images) {
            var file = Path.Combine(directory, image.FileName);

            File.WriteAllBytes(file, image.Png);
            files.Add(file);
        }

        Written = files;

        // One scan for the whole set rather than one per file: a scan is a directory walk, and nine
        // of them to write nine files next to each other is eight walks nobody asked for.
        project.Assets.Scan();

        var references = new Dictionary<MeshMapUsage, AssetReference>();

        for (var at = 0; at < images.Count; at++) {
            // ⚠ Relative to the project root and not to `Assets/`, which is what the database keys
            // on — `AssetDatabase` indexes every entry as `Paths.Relative(absolute)`, so the key for
            // a baked map is `Assets/MeshMaps/Barrel_ao.png`. Measuring it from `Paths.Assets`
            // instead produces `MeshMaps/Barrel_ao.png`, which matches nothing, and the read-back
            // then returns `AssetReference.Null` for a file that is right there. That is exactly
            // what `ProjectMeshBaker` did, silently, for every block-out bake — see its fix.
            var relative = project.Paths.Relative(files[at]);

            if (!project.Assets.TryGetByPath(relative, out var entry)) {
                references[images[at].Usage] = AssetReference.Null;
                continue;
            }

            Describe(files[at], entry.Guid, name, images[at]);
            references[images[at].Usage] = new AssetReference(entry.Guid);
        }

        project.Assets.Save();

        return new(name, references, files, warnings);
    }

    /// <summary>Writes what the bytes mean into the sidecar the scan minted, keeping its GUID.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The usage goes in the sidecar and not only in the file name</b>, because binding
    ///         is by usage and a rename must not unbind a generator. <see cref="MeshMapNaming" />'s
    ///         remarks say which of the two wins.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read then rewritten, so a re-bake keeps everything about the asset that is not
    ///         the bake's.</b> An addressable block an artist set on the id map, and the sub-asset
    ///         list the last import recorded, both belong to the file rather than to this run — and a
    ///         bake that helpfully replaced the whole sidecar would drop an address a build resolves
    ///         through. What is deliberately <i>not</i> preserved is the importer block: it carries
    ///         the previous import's source hash, and the pixels under it have just changed.
    ///     </para>
    /// </remarks>
    static void Describe(string file, AssetId guid, string mesh, MeshMapImage image) {
        var sidecar = AssetMetaFile.PathFor(file);
        var existing = Existing(sidecar);
        var extensions = new Dictionary<string, string>(existing?.Extensions ?? [], StringComparer.Ordinal) {
            [MeshMapNaming.UsageKey] = MeshMapNaming.Suffix(image.Usage),
            [MeshMapNaming.MeshKey] = mesh
        };

        if (image.Scale > 0f) {
            extensions[MeshMapNaming.ScaleKey] = image.Scale.ToString("R", CultureInfo.InvariantCulture);
        } else {
            extensions.Remove(MeshMapNaming.ScaleKey);
        }

        var meta = existing is null
            ? new AssetMeta { Guid = guid }
            : existing with { Guid = guid };

        AssetMetaFile.WriteFile(sidecar, meta with { Importer = image.Settings, Extensions = extensions });
    }

    /// <summary>The sidecar as it stands, or null where it cannot be read as one.</summary>
    /// <remarks>
    ///     ⚠ <b>An unreadable sidecar is replaced rather than raised.</b> The two ways this fails are
    ///     a file written by a newer editor and an importer tag belonging to a plugin that is no
    ///     longer loaded, and neither is a reason to leave a map the artist just baked without the
    ///     settings that say what it is. The GUID survives either way, because it is read back from
    ///     the database rather than from here.
    /// </remarks>
    static AssetMeta? Existing(string sidecar) {
        try {
            return File.Exists(sidecar) ? AssetMetaFile.ReadFile(sidecar) : null;
        } catch (Exception failure)
            when (failure is IOException or YamlParseException or YamlBindingException or MetaVersionException) {
            return null;
        }
    }

    /// <summary>Sanitised rather than trusted, for the reason <see cref="ProjectMeshBaker" />'s is.</summary>
    /// <remarks>
    ///     A mesh is named by a person, and "Wall / 2" is a perfectly good name for one and a path
    ///     traversal in a file system. ⚠ An underscore is left alone even though it is the separator
    ///     <see cref="MeshMapNaming.TryParseFileName" /> splits on — that reader takes the
    ///     <i>last</i> underscore, so a mesh whose own name has one still parses.
    /// </remarks>
    static string Safe(string name) {
        var made = new char[name.Length];

        for (var index = 0; index < name.Length; index++) {
            made[index] = Array.IndexOf(Path.GetInvalidFileNameChars(), name[index]) >= 0 ? '_' : name[index];
        }

        var text = new string(made).Trim();

        return text.Length == 0 ? "Mesh" : text;
    }
}
