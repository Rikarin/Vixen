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
///     <para>
///         ⚠ <b>Which is why the set is keyed on the <i>model</i> and not on the name.</b> "The same
///         mesh" and "another model's mesh with the same name" produce identical file names, and the
///         second one is Blender's default object name — so keying on the name made the correct
///         behaviour above into a silent swap of one model's maps for another's, GUIDs and all. The
///         model's id goes in the sidecar (<see cref="MeshMapNaming.ModelKey" />) and
///         <see cref="SetName" /> is what reads it back.
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
    public MeshMapSet Write(
        AssetId model,
        string mesh,
        IReadOnlyList<MeshMapImage> images,
        IReadOnlyList<string> warnings
    ) {
        ArgumentException.ThrowIfNullOrEmpty(mesh);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(warnings);

        var directory = Path.Combine(project.Paths.Assets, Folder);

        Directory.CreateDirectory(directory);

        // ⚠ Here rather than at the caller, and this is the whole of the fix. `Bake` sanitised and
        // `Write` trusted, and the editor only ever calls `Write` — so a mesh Assimp handed back as
        // `../Wall` wrote nine PNGs outside `Assets/` altogether, which is exactly the traversal
        // `Safe`'s own remarks say it exists to stop.
        var wanted = Safe(mesh);
        var name = SetName(directory, model, wanted, out var taken);
        var said = taken.IsEmpty ? warnings : [.. warnings, Clashed(wanted, name)];

        var files = new List<string>(images.Count);

        foreach (var image in images) {
            // ⚠ Derived from the set's name and the usage rather than read off the image. An encoded
            // image used to carry a file name minted at encode time, which is before anything knows
            // the folder it lands in or whether the name is another model's — the two defects above,
            // both of which are unreachable now that the writer is the only thing that names a file.
            var file = Path.Combine(directory, MeshMapNaming.FileName(name, image.Usage));

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
                throw new InvalidOperationException(Unresolved(files[at], images[at].Usage));
            }

            Describe(files[at], entry.Guid, model, name, images[at]);
            references[images[at].Usage] = new AssetReference(entry.Guid);
        }

        project.Assets.Save();

        return new(name, references, files, said);
    }

    /// <summary>What a bake says when the database did not pick a map it wrote back up.</summary>
    /// <param name="file">The file that was written and not indexed.</param>
    /// <param name="usage">What that file measures, which is the name a generator would ask for.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A refusal rather than <see cref="AssetReference.Null" />, which is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/731">#731</a> and is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/724">#724</a>'s shape one asset type
    ///         over.</b> It survived the material fix because it is in another assembly, and the two
    ///         refusals are written separately for the same reason — <c>ProjectMaterialBaker</c> is
    ///         in <c>Vixen.Editor.Assets</c> and this is in the application, so the shared spelling
    ///         would be a new public type owing a guide page to say one sentence.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Where the null went is what makes this worth the same severity.</b> A material
    ///         bake's null went into a <c>.vxmat</c> and became the bindless fallback; a mesh map's
    ///         goes into <see cref="MeshMapSet.Maps" />, which is <em>the</em> by-usage index —
    ///         <c>MeshMapNaming</c>'s remarks say the usage is a map's identity and the file name is
    ///         a convenience. So a null there is a set that reports nine maps and resolves eight, and
    ///         the generator asking for the ninth binds nothing while the bake says it succeeded.
    ///     </para>
    ///     <para>
    ///         It is a state a project can genuinely be in: <c>AssetDatabase.Read</c> leaves a file
    ///         out of the index entirely when <c>MetaScanner</c> cannot read a GUID out of the
    ///         <c>.meta</c> beside it, and it deliberately refuses to mint a replacement, because a
    ///         new id would break every reference through the old one.
    ///     </para>
    /// </remarks>
    static string Unresolved(string file, MeshMapUsage usage) =>
        $"'{Path.GetFileName(file)}' was written and the asset database did not pick it up, so this bake cannot "
        + $"name the {MeshMapNaming.Suffix(usage)} map by id. Nothing further was written: a set that reports a "
        + "map and resolves it to nothing binds no texture in the generator that asks for that usage, and says so "
        + "nowhere. The usual cause is a .meta beside that file whose GUID cannot be read — a scan refuses to "
        + "replace one, because minting a new id would break every reference to it. Repair or remove that .meta "
        + "and bake again.";

    /// <summary>What this set is called in the folder, which is not simply what it was asked to be.</summary>
    /// <param name="directory">The folder the set lands in.</param>
    /// <param name="model">The model asset it was baked from, or empty where there is none.</param>
    /// <param name="mesh">The mesh's name, already safe for a file name.</param>
    /// <param name="taken">The model already using that name, or empty where nobody was.</param>
    /// <returns>The stem every file in the set is named from.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A re-bake and a collision produce the same nine file names, and only the model
    ///         tells them apart.</b> Overwriting is right for the first — an artist raising the ray
    ///         count has to change the maps their generators already read, which is what
    ///         <see cref="ProjectMeshBaker" />'s remarks argue — and catastrophic for the second: two
    ///         models whose meshes are both called <c>Cube</c>, which is Blender's default object
    ///         name and every exporter's fallback, silently swapped one another's pixels <i>and</i>
    ///         inherited one another's GUIDs, so every material bound to the first went on resolving
    ///         and started sampling the second.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A set with no model recorded is adopted rather than avoided.</b> That is what a
    ///         set baked before <see cref="MeshMapNaming.ModelKey" /> existed looks like, and leaving
    ///         it alone would strand it under the name while the re-bake landed beside it as
    ///         <c>Cube_2</c>. Two <i>un</i>-keyed bakes of one name are still one set, which is
    ///         honest: without a model there is nothing to tell them apart with.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <see cref="MeshMapUsage.Normal" /> map is what is asked</b>, because
    ///         <c>MeshMapBake.Always</c> guarantees a set has one whatever was measured. Asking about
    ///         the occlusion map instead would call a name free whenever the set under it was baked
    ///         with the ray-casting maps turned off.
    ///     </para>
    /// </remarks>
    /// <exception cref="IOException">There are already <see cref="Crowd" /> sets under that name.</exception>
    static string SetName(string directory, AssetId model, string mesh, out AssetId taken) {
        taken = AssetId.Empty;

        for (var suffix = 1; suffix <= Crowd; suffix++) {
            var candidate = suffix == 1 ? mesh : mesh + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            var owner = OwnerOf(directory, candidate);

            if (owner is not { } already || already.IsEmpty || already == model) {
                return candidate;
            }

            if (taken.IsEmpty) {
                taken = already;
            }
        }

        // ⚠ Refused rather than silently overwriting the hundredth, which is the shape a clamp has to
        // take here: the thing on the other side of it is a project's baked maps.
        throw new IOException(
            $"There are already {Crowd.ToString(CultureInfo.InvariantCulture)} baked sets called \"{mesh}\" in "
            + $"{directory}, from that many different models. Rename the mesh, or bake into another folder."
        );
    }

    /// <summary>How many differently-owned sets may share one mesh name before the bake refuses.</summary>
    /// <remarks>
    ///     Absurd rather than tuned — a hundred models whose meshes are all called <c>Cube</c> is a
    ///     project with a naming problem the editor cannot fix — and it is a bound on a loop that
    ///     opens a file per turn rather than a judgement about what is reasonable.
    /// </remarks>
    const int Crowd = 100;

    /// <summary>Which model owns the set under a name, or null where no set is under it.</summary>
    /// <remarks>
    ///     <see cref="AssetId.Empty" /> is the third answer and means a set is there and does not say
    ///     whose it is — see <see cref="SetName" />, which adopts one.
    /// </remarks>
    static AssetId? OwnerOf(string directory, string name) {
        var sidecar = AssetMetaFile.PathFor(
            Path.Combine(directory, MeshMapNaming.FileName(name, MeshMapUsage.Normal))
        );

        if (Existing(sidecar) is not { } meta) {
            return null;
        }

        return meta.Extensions.TryGetValue(MeshMapNaming.ModelKey, out var written)
            && AssetId.TryParse(written, out var owner)
                ? owner
                : AssetId.Empty;
    }

    /// <summary>What the artist is told when a name they did not choose was used.</summary>
    /// <remarks>
    ///     ⚠ <b>A warning rather than a refusal, and it is carried in the set's own warnings</b> so
    ///     that it reaches the same notification the bake's own <c>Missed</c> and <c>Covered</c>
    ///     complaints do. The collision this reports used to produce no message anywhere: the bake
    ///     said it had written nine maps, and it had — over somebody else's.
    /// </remarks>
    static string Clashed(string mesh, string name) =>
        $"Another model has already baked a set called \"{mesh}\" here, so this one was written as "
        + $"\"{name}\". Two meshes with one name is what that means — rename one of them to tell them apart.";

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
    static void Describe(string file, AssetId guid, AssetId model, string mesh, MeshMapImage image) {
        var sidecar = AssetMetaFile.PathFor(file);
        var existing = Existing(sidecar);
        var extensions = new Dictionary<string, string>(existing?.Extensions ?? [], StringComparer.Ordinal) {
            [MeshMapNaming.UsageKey] = MeshMapNaming.Suffix(image.Usage),
            [MeshMapNaming.MeshKey] = mesh
        };

        // ⚠ Written even though nothing reads it back except the next bake, which is the point: it is
        // the set's identity, and a set whose identity lives only in the name it happens to have is a
        // set the next model called `Cube` overwrites. See `SetName`.
        if (model.IsEmpty) {
            extensions.Remove(MeshMapNaming.ModelKey);
        } else {
            extensions[MeshMapNaming.ModelKey] = model.ToString();
        }

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
