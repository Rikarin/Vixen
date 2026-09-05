// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;
using Vixen.Rendering.Materials;

namespace Vixen.Editor.Assets.Materials;

/// <summary>What one bake put in the project.</summary>
/// <param name="Name">What the set is called, which is not always what it was asked to be.</param>
/// <param name="Material">The <c>.vxmat</c>, as the identity that survives a rename.</param>
/// <param name="Maps">Each written file, by what it holds.</param>
/// <param name="Files">Every file written, including the material, as full paths.</param>
/// <param name="Warnings">What the artist should know, in the order it was noticed.</param>
public sealed record MaterialBakeSet(
    string Name,
    AssetReference Material,
    IReadOnlyDictionary<MaterialMapTarget, AssetReference> Maps,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings
);

/// <summary>Puts a baked texture set into the project and says what assets it became.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § D11's last paragraph.</b> The bake writes, per texture set, one file per
///         channel, one <c>.vxmat</c> naming them, and the provenance block — and it writes them
///         through the asset database's scan-then-read-back sequence rather than minting an id.
///     </para>
///     <para>
///         ⚠ <b>A scan rather than an import, and the difference is what mints the identity.</b> A
///         file in <c>Assets/</c> has no <c>AssetId</c> until the database has seen it and written a
///         <c>.meta</c> beside it. So the bake writes, scans, and reads the GUID back —
///         <c>ProjectMeshBaker</c>'s dance, for its reason.
///     </para>
///     <para>
///         ⚠ <b>Two scans, and the second one is not a tidiness.</b> The <c>.vxmat</c> names its maps
///         by <c>AssetId</c>, and those ids do not exist until the maps have been scanned — so the
///         material cannot be written in the same pass as the pixels it references. Writing it first
///         with null references and patching them afterwards would leave a material on disk that
///         resolves nothing for as long as the second pass takes, which is exactly the window a crash
///         picks.
///     </para>
///     <para>
///         ⚠ <b>Re-baking overwrites and keeps every GUID</b>, so every entity already pointing at the
///         material picks up the new maps. That is what a person re-baking means, and a second set
///         each time would leave a project full of <c>ShipHull_2</c> while every mesh went on reading
///         the first one.
///     </para>
///     <para>
///         ⚠ <b>Which is why the set is keyed on the source and not on the name.</b> "The same graph
///         again" and "another graph that happens to be called the same thing" produce identical file
///         names, and keying on those made the correct behaviour above into a silent swap of one
///         material's maps for another's, GUIDs and all — <c>ProjectMeshMapBaker</c> has the same fix
///         for the same defect, filed as
///         <a href="https://github.com/Rikarin/Vixen/issues/681">#681</a>.
///     </para>
///     <para>
///         ⚠ <b>And a painted-over output stops the bake.</b> § D4's digest exists so that a file
///         whose bytes are no longer what the bake wrote is <i>flagged</i> rather than overwritten,
///         because the most common reason for the mismatch is that somebody painted on it. See
///         <see cref="MaterialProvenance.Painted" />; <c>force</c> is how a person says they meant it.
///     </para>
/// </remarks>
/// <param name="project">The project to write into.</param>
/// <param name="folder">Which folder under <c>Assets/</c> baked materials go in.</param>
public sealed class ProjectMaterialBaker(EditorProject project, string folder = MaterialMapNaming.DefaultFolder) {
    /// <summary>
    ///     Teaches the binder how a vector reads before anything asks it to write a feature.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A feature is where a material's vectors are</b> — a base colour, an emissive tint —
    ///     and an asset type that writes one without this reads it back as zero. <c>MaterialImporter</c>
    ///     registers the same table for the same reason, from a static constructor rather than a
    ///     module initializer.
    /// </remarks>
    static ProjectMaterialBaker() => MathScalars.Register();

    /// <summary>Where baked materials go, relative to the project's assets.</summary>
    public string Folder { get; } = folder;

    /// <summary>The files the last bake wrote, as full paths.</summary>
    /// <remarks>
    ///     What a status line reports and what a test asserts on. A bake that wrote nothing and a
    ///     bake whose files the database has not caught up with look identical from the references
    ///     alone, which is <c>ProjectMeshBaker.Written</c>'s reason as well.
    /// </remarks>
    public IReadOnlyList<string> Written { get; private set; } = [];

    /// <summary>Writes a set's maps, its material and its provenance into the project.</summary>
    /// <param name="material">What the set should be called. Sanitised here.</param>
    /// <param name="images">What <see cref="MaterialBake.Encode" /> produced.</param>
    /// <param name="record">What the bake was, for the provenance block.</param>
    /// <param name="force">Overwrite outputs somebody has painted over.</param>
    /// <returns>What the project now holds.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="material" /> is empty, or there are no images.</exception>
    /// <exception cref="IOException">
    ///     An output has been painted over and <paramref name="force" /> was not given, or there are
    ///     already <see cref="Crowd" /> sets under that name.
    /// </exception>
    public MaterialBakeSet Write(
        string material,
        IReadOnlyList<MaterialMapImage> images,
        MaterialBakeRecord record,
        bool force = false
    ) {
        ArgumentException.ThrowIfNullOrEmpty(material);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(record);

        if (images.Count == 0) {
            throw new ArgumentException("A bake that produced no files has nothing to write.", nameof(images));
        }

        var directory = Path.Combine(project.Paths.Assets, Folder);

        Directory.CreateDirectory(directory);

        // ⚠ Here rather than at the caller, which is the whole of the mesh-map baker's #680: `Bake`
        // sanitised, `Write` trusted, and the editor only ever called `Write` — so a set named
        // `../Hull` wrote its maps outside `Assets/` altogether. A material is named by a person and
        // "Ship / Hull" is a perfectly good name for one and a path traversal in a file system.
        var wanted = Safe(material);
        var name = SetName(directory, record.SourceAsset, wanted, out var taken);
        var warnings = new List<string>();

        if (!taken.IsEmpty) {
            warnings.Add(Clashed(wanted, name));
        }

        var materialFile = Path.Combine(directory, name + MaterialImporter.Extension);
        var sidecar = AssetMetaFile.PathFor(materialFile);
        var recorded = Existing(sidecar)?.Extensions ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var painted = MaterialProvenance.Painted(recorded, OnDisk(directory, name, recorded));

        if (painted.Count > 0) {
            if (!force) {
                throw new IOException(Overpainted(name, painted));
            }

            warnings.Add(Overpainted(name, painted) + " It was overwritten because this bake was forced.");
        }

        var files = new List<string>(images.Count + 1);

        foreach (var image in images) {
            var file = Path.Combine(directory, MaterialMapNaming.FileName(name, image.Target, image.Extension));

            // ⚠ The same map under the other extension is removed rather than left. A set re-baked
            // across MaterialMapNaming.PortableLimit changes every file's extension, and the copy
            // that stays behind is a project asset holding the previous bake's pixels under a name
            // that says it is this one's — which is precisely the shape a generator or a second
            // material picks up by accident.
            Stale(directory, name, image, warnings);

            File.WriteAllBytes(file, image.Bytes);
            files.Add(file);
        }

        Written = files;

        // One scan for the whole set rather than one per file: a scan is a directory walk, and seven
        // of them to write seven files next to each other is six walks nobody asked for.
        project.Assets.Scan();

        var maps = new Dictionary<MaterialMapTarget, AssetReference>();

        for (var at = 0; at < images.Count; at++) {
            // ⚠ Relative to the project root and not to `Assets/`, which is what the database keys
            // on — every entry is indexed as `Paths.Relative(absolute)`, so the key for a baked map
            // is `Assets/Materials/ShipHull_orm.png`. Measuring it from `Paths.Assets` matches
            // nothing and hands back a null reference for a file that is right there, which is what
            // `ProjectMeshBaker` did silently for every block-out bake.
            if (!project.Assets.TryGetByPath(project.Paths.Relative(files[at]), out var entry)) {
                maps[images[at].Target] = AssetReference.Null;
                continue;
            }

            Describe(files[at], entry.Guid, name, images[at]);
            maps[images[at].Target] = new AssetReference(entry.Guid);
        }

        File.WriteAllText(materialFile, YamlSerializer.ToYaml(MaterialBake.Material(maps, Material(materialFile))));
        files.Add(materialFile);
        Written = files;

        project.Assets.Scan();

        var reference = project.Assets.TryGetByPath(project.Paths.Relative(materialFile), out var found)
            ? new AssetReference(found.Guid)
            : AssetReference.Null;

        if (!reference.IsNull) {
            Provenance(materialFile, found!.Guid, record, images);
        }

        project.Assets.Save();

        return new(name, reference, maps, files, warnings);
    }

    /// <summary>What this set is called in the folder, which is not simply what it was asked to be.</summary>
    /// <remarks>
    ///     <inheritdoc cref="ProjectMaterialBaker" path="/remarks/para[5]" />
    /// </remarks>
    static string SetName(string directory, AssetId source, string material, out AssetId taken) {
        taken = AssetId.Empty;

        for (var suffix = 1; suffix <= Crowd; suffix++) {
            var candidate = suffix == 1 ? material : material + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            var owner = OwnerOf(directory, candidate);

            if (owner is not { } already || already.IsEmpty || already == source) {
                return candidate;
            }

            if (taken.IsEmpty) {
                taken = already;
            }
        }

        // ⚠ Refused rather than silently overwriting the hundredth, which is the shape a ceiling has
        // to take here: the thing on the other side of it is a project's baked materials.
        throw new IOException(
            $"There are already {Crowd.ToString(CultureInfo.InvariantCulture)} baked materials called "
            + $"\"{material}\" in {directory}, from that many different sources. Rename one, or bake into "
            + "another folder."
        );
    }

    /// <summary>How many differently-sourced sets may share one name before the bake refuses.</summary>
    /// <remarks>
    ///     Absurd rather than tuned, and it is a bound on a loop that opens a file per turn rather
    ///     than a judgement about what is reasonable.
    /// </remarks>
    const int Crowd = 100;

    /// <summary>Which source owns the set under a name, or null where no set is under it.</summary>
    /// <remarks>
    ///     <see cref="AssetId.Empty" /> is the third answer and means a set is there and does not say
    ///     what made it — a set baked by hand, or before this block existed. <see cref="SetName" />
    ///     adopts one, because leaving it alone would strand it under the name while the re-bake
    ///     landed beside it.
    /// </remarks>
    static AssetId? OwnerOf(string directory, string name) {
        if (Existing(AssetMetaFile.PathFor(Path.Combine(directory, name + MaterialImporter.Extension))) is not
            { } meta) {
            return File.Exists(Path.Combine(directory, name + MaterialImporter.Extension)) ? AssetId.Empty : null;
        }

        return meta.Extensions.TryGetValue(MaterialProvenance.SourceAssetKey, out var written)
            && AssetId.TryParse(written, out var owner)
                ? owner
                : AssetId.Empty;
    }

    /// <summary>The bytes each recorded output has on disk now, for the painted-over check.</summary>
    /// <remarks>
    ///     ⚠ <b>Both extensions are tried, because the size decides which one a map is.</b> A set
    ///     that used to be 4K is a container and the same set at 2K is a PNG, and a check that looked
    ///     only under the extension this bake is about to write would call every output of a
    ///     resized re-bake "not there" and skip the guard entirely.
    /// </remarks>
    static Dictionary<MaterialMapTarget, byte[]> OnDisk(
        string directory,
        string name,
        IReadOnlyDictionary<string, string> recorded
    ) {
        var present = new Dictionary<MaterialMapTarget, byte[]>();

        foreach (var target in MaterialMapNaming.EveryTarget) {
            if (!recorded.ContainsKey(MaterialProvenance.DigestPrefix + MaterialMapNaming.Suffix(target))) {
                continue;
            }

            foreach (var extension in Extensions) {
                var file = Path.Combine(directory, MaterialMapNaming.FileName(name, target, extension));

                if (File.Exists(file)) {
                    present[target] = File.ReadAllBytes(file);
                    break;
                }
            }
        }

        return present;
    }

    /// <summary>Every extension a map may be written with, largest container last.</summary>
    static readonly string[] Extensions = [
        MaterialMapNaming.PortableExtension, MaterialMapNaming.ContainerExtension
    ];

    /// <summary>Removes the same map under the extension this bake is not using.</summary>
    static void Stale(string directory, string name, MaterialMapImage image, List<string> warnings) {
        foreach (var extension in Extensions) {
            if (string.Equals(extension, image.Extension, StringComparison.Ordinal)) {
                continue;
            }

            var file = Path.Combine(directory, MaterialMapNaming.FileName(name, image.Target, extension));

            if (!File.Exists(file)) {
                continue;
            }

            File.Delete(file);
            File.Delete(AssetMetaFile.PathFor(file));

            warnings.Add(
                $"The {MaterialMapNaming.Suffix(image.Target)} map was {extension} and is now {image.Extension}, "
                + "so the old file and its id are gone. Anything that referenced it by GUID rather than through "
                + "this material has to be re-pointed."
            );
        }
    }

    /// <summary>What the artist is told when a name they did not choose was used.</summary>
    static string Clashed(string material, string name) =>
        $"Another source has already baked a material called \"{material}\" here, so this one was written as "
        + $"\"{name}\". Two graphs with one name is what that means — rename one of them to tell them apart.";

    /// <summary>What the artist is told when the bake found their own pixels under its outputs.</summary>
    static string Overpainted(string name, IReadOnlyList<MaterialMapTarget> painted) =>
        $"The {string.Join(", ", painted.Select(MaterialMapNaming.Suffix))} "
        + $"{(painted.Count == 1 ? "map" : "maps")} of \"{name}\" "
        + $"{(painted.Count == 1 ? "is" : "are")} not what the last bake wrote, which usually means somebody "
        + "painted over them. Re-baking would replace that work, so it did not.";

    /// <summary>The material as it already stands, or null where there is none to keep anything from.</summary>
    /// <remarks>
    ///     ⚠ <b>An unreadable material is replaced rather than raised.</b> What is being read back is
    ///     the shading model and the pass, and a file that cannot be parsed carries neither — while
    ///     refusing the bake over it would leave an artist unable to re-bake past a broken file the
    ///     bake is about to rewrite anyway.
    /// </remarks>
    static MaterialContent? Material(string file) {
        try {
            return File.Exists(file) ? YamlSerializer.Parse<MaterialContent>(File.ReadAllText(file)) : null;
        } catch (Exception failure) when (failure is IOException or YamlParseException or YamlBindingException
            or FormatException) {
            return null;
        }
    }

    /// <summary>Writes what a map is into the sidecar the scan minted, keeping its GUID.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sidecar is finished afterwards, and it has to be.</b> <c>AssetDatabase.Scan</c>
    ///         mints a sidecar with a GUID and deliberately no <c>importer</c> key — which importer
    ///         claims a file is decided at import time. But a baked map is exactly a file whose bytes
    ///         do not say what they mean: an ORM map read as colour is sRGB-decoded roughness, and
    ///         nothing anywhere reports it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read then rewritten, so a re-bake keeps everything about the asset that is not the
    ///         bake's</b> — an addressable block somebody set, the sub-asset list the last import
    ///         recorded. What is deliberately not preserved is the importer block: it carries the
    ///         previous import's settings, and the pixels under it have just changed.
    ///     </para>
    /// </remarks>
    static void Describe(string file, AssetId guid, string material, MaterialMapImage image) {
        var sidecar = AssetMetaFile.PathFor(file);
        var existing = Existing(sidecar);
        var extensions = new Dictionary<string, string>(existing?.Extensions ?? [], StringComparer.Ordinal) {
            [MaterialProvenance.MapKey] = MaterialMapNaming.Suffix(image.Target),
            [MaterialProvenance.MaterialKey] = material
        };

        var meta = existing is null ? new AssetMeta { Guid = guid } : existing with { Guid = guid };

        AssetMetaFile.WriteFile(sidecar, meta with { Importer = image.Settings, Extensions = extensions });
    }

    /// <summary>Writes the <c>texturing:</c> block into the material's sidecar, keeping its GUID.</summary>
    /// <remarks>
    ///     ⚠ <b>Every <c>texturing.</c> key is dropped before the new block is written, and that is
    ///     the half a merge would get wrong.</b> A bake that stopped producing an emissive output
    ///     would otherwise leave the previous run's digest for it in place, and the next bake's
    ///     painted-over check would compare a file nobody writes any more against a digest nobody
    ///     wrote it from.
    /// </remarks>
    static void Provenance(
        string file,
        AssetId guid,
        MaterialBakeRecord record,
        IReadOnlyList<MaterialMapImage> images
    ) {
        var sidecar = AssetMetaFile.PathFor(file);
        var existing = Existing(sidecar);
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in existing?.Extensions ?? []) {
            if (!key.StartsWith(Texturing, StringComparison.Ordinal)) {
                extensions[key] = value;
            }
        }

        foreach (var (key, value) in MaterialProvenance.Describe(record, images, DateTimeOffset.UtcNow)) {
            extensions[key] = value;
        }

        var meta = existing is null ? new AssetMeta { Guid = guid } : existing with { Guid = guid };

        AssetMetaFile.WriteFile(sidecar, meta with { Extensions = extensions });
    }

    /// <summary>What every provenance key starts with.</summary>
    const string Texturing = "texturing.";

    /// <summary>The sidecar as it stands, or null where it cannot be read as one.</summary>
    /// <remarks>
    ///     ⚠ <b>An unreadable sidecar is replaced rather than raised</b>, for the reason
    ///     <c>ProjectMeshMapBaker</c>'s is: a file written by a newer editor and an importer tag from
    ///     a plugin that is no longer loaded are the two ways this fails, and neither is a reason to
    ///     leave a map the artist just baked without the settings that say what it is. ⚠ It is also
    ///     what makes the painted-over check fail <i>open</i> — an unreadable sidecar records no
    ///     digests, so nothing is called painted — which is the right direction for a guard whose
    ///     other failure is refusing to bake.
    /// </remarks>
    static AssetMeta? Existing(string sidecar) {
        try {
            return File.Exists(sidecar) ? AssetMetaFile.ReadFile(sidecar) : null;
        } catch (Exception failure)
            when (failure is IOException or YamlParseException or YamlBindingException or MetaVersionException) {
            return null;
        }
    }

    /// <summary>Sanitised rather than trusted, for the reason <c>ProjectMeshBaker</c>'s is.</summary>
    static string Safe(string name) {
        var made = new char[name.Length];

        for (var index = 0; index < name.Length; index++) {
            made[index] = Array.IndexOf(Path.GetInvalidFileNameChars(), name[index]) >= 0 ? '_' : name[index];
        }

        var text = new string(made).Trim();

        return text.Length == 0 ? "Material" : text;
    }
}
