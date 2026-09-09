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
///         <a href="https://github.com/Rikarin/Vixen/issues/681">#681</a>. ⚠ And the source is a
///         <i>folder</i> as readily as an asset — see <see cref="MaterialProvenance.KeyOf" />, because
///         the asset-only form of this guard could not be reached by the one caller that exists.
///     </para>
///     <para>
///         ⚠ <b>A file goes only when the bake can prove it wrote it.</b> Both hazards this handles —
///         a map whose extension changed and an output that is no longer produced — are answered by
///         the digest in this material's own sidecar, never by the file's name. See
///         <see cref="Prune(string, string, IReadOnlyList{MaterialMapImage}, IReadOnlyDictionary{string, string}, List{string})" />
///         for what deleting on the name alone destroyed.
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
    ///     already <see cref="Crowd" /> sets under that name. Both are things a person can act on.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     The asset database did not pick up a file this bake wrote, so it cannot be named by id. Not
    ///     a thing a person did — see <see cref="Unresolved" />.
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
        var name = SetName(directory, MaterialProvenance.KeyOf(record), wanted, out var taken);
        var warnings = new List<string>();

        if (taken.Length > 0) {
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

        // Before the writes rather than during them, so that "which files under this name is the bake
        // about to own" is answered once against the disk as it stands.
        Prune(directory, name, images, recorded, warnings);

        foreach (var image in images) {
            var file = Path.Combine(directory, MaterialMapNaming.FileName(name, image.Target, image.Extension));

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
                throw new InvalidOperationException(Unresolved(files[at]));
            }

            Describe(files[at], entry.Guid, name, images[at]);
            maps[images[at].Target] = new AssetReference(entry.Guid);
        }

        var content = MaterialBake.Material(maps, Material(materialFile));

        if (Unmarched(maps, content)) {
            warnings.Add(Unsampled(name));
        }

        File.WriteAllText(materialFile, YamlSerializer.ToYaml(content));
        files.Add(materialFile);
        Written = files;

        project.Assets.Scan();

        if (!project.Assets.TryGetByPath(project.Paths.Relative(materialFile), out var found)) {
            // ⚠ The same refusal as the maps', and for a sharper reason: the provenance block is
            // written onto the material's own sidecar, so a material the database has not picked up
            // is a set with no digests — nothing the next bake could call painted, and nothing that
            // says which source owns the name.
            throw new InvalidOperationException(Unresolved(materialFile));
        }

        Provenance(materialFile, found.Guid, record, images);
        project.Assets.Save();

        return new(name, new AssetReference(found.Guid), maps, files, warnings);
    }

    /// <summary>Writes a splat map beside a layered material and binds it onto the feature.</summary>
    /// <param name="material">Which baked material the map belongs to. Sanitised here.</param>
    /// <param name="image">What <see cref="MaterialBake.Splat" /> produced.</param>
    /// <param name="layers">How many layers it weighs, which becomes the material's painted channels.</param>
    /// <param name="record">What produced the weights, for the map's own sidecar.</param>
    /// <param name="force">Overwrite a splat map somebody has painted over.</param>
    /// <returns>What the project now holds.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    ///     The name is empty, the image is not a splat map, or the count is outside one to four.
    /// </exception>
    /// <exception cref="IOException">
    ///     There is no material of that name to bind it onto, or the existing splat map has been
    ///     painted over and <paramref name="force" /> was not given.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     The asset database did not pick the file up, or the material on disk cannot be read.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>, and it is a second
    ///         write path on purpose.</b> <see cref="Write" /> owns a whole texture set: it claims the
    ///         name, prunes what the bake no longer produces and replaces the material's features
    ///         wholesale, all of which is right for a graph bake and wrong for one file added to a
    ///         material that already exists. So this shares the parts a second write path forgets —
    ///         the file naming, the digest, the painted-over guard, the scan-then-read-back GUID dance
    ///         and the sidecar — and shares none of the parts that would destroy the set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does not claim a name.</b> <see cref="SetName" /> exists so that two sources
    ///         baking "Rock" do not overwrite each other; a splat map is written <em>for</em> a
    ///         material that is already there, so a name nothing has baked is a refusal rather than a
    ///         new set — writing <c>Rock_splat.png</c> beside no <c>Rock.vxmat</c> would leave a
    ///         texture in the project that nothing samples and nothing explains.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The material's <c>texturing:</c> block is merged rather than rewritten, which is
    ///         the opposite of <see cref="Provenance" />'s rule and has to be.</b> That method drops
    ///         every <c>texturing.</c> key because a graph bake owns all of them; this one owns
    ///         exactly the splat digest, and rewriting the block would erase the graph bake's — after
    ///         which the next bake's painted-over check has nothing to compare and every map of the
    ///         set is silently overwritable. ⚠ <see cref="MaterialProvenance.WrittenDigestKey" /> is
    ///         deliberately left as the graph bake wrote it: it is the digest of <em>that</em> set,
    ///         and a splat map is not one of its outputs.
    ///     </para>
    /// </remarks>
    public MaterialBakeSet WriteSplat(
        string material,
        MaterialMapImage image,
        int layers,
        MaterialBakeRecord record,
        bool force = false
    ) {
        ArgumentException.ThrowIfNullOrEmpty(material);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(record);

        if (image.Target != MaterialMapTarget.Splat) {
            throw new ArgumentException(
                $"This writes a splat map and was handed a {MaterialMapNaming.Suffix(image.Target)} one. "
                + "Every other map of a set goes through Write, which owns the whole set.",
                nameof(image)
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(layers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(layers, 4);

        var directory = Path.Combine(project.Paths.Assets, Folder);
        var name = Safe(material);
        var materialFile = Path.Combine(directory, name + MaterialImporter.Extension);

        if (Unbindable(material) is { } missing) {
            throw new IOException(missing);
        }

        var sidecar = AssetMetaFile.PathFor(materialFile);
        var recorded = Existing(sidecar)?.Extensions ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var present = new Dictionary<MaterialMapTarget, byte[]>();

        foreach (var (target, bytes) in OnDisk(directory, name, recorded)) {
            if (target == MaterialMapTarget.Splat) {
                present[target] = bytes;
            }
        }

        if (MaterialProvenance.Painted(recorded, present).Count > 0) {
            if (!force) {
                throw new IOException(Overpainted(name, [MaterialMapTarget.Splat]));
            }

            warnings.Add(
                Overpainted(name, [MaterialMapTarget.Splat]) + " It was overwritten because this bake was forced."
            );
        }

        // The other extension of this one map, for `Prune`'s first hazard: a stack re-baked across
        // the portable limit writes `_splat.ktx2` beside the `_splat.png` a previous run left, and
        // the material would go on naming whichever the database resolved.
        Prune(directory, name, MaterialMapTarget.Splat, image.Extension, recorded, warnings);

        var file = Path.Combine(directory, MaterialMapNaming.FileName(name, MaterialMapTarget.Splat, image.Extension));

        File.WriteAllBytes(file, image.Bytes);
        Written = [file];

        project.Assets.Scan();

        if (!project.Assets.TryGetByPath(project.Paths.Relative(file), out var texture)) {
            throw new InvalidOperationException(Unresolved(file));
        }

        Describe(file, texture.Guid, name, image);

        // ⚠ Read back off the disk rather than taken from a caller, because what is being patched is
        // the material as it stands — a caller holding a stale copy would write back a material
        // missing whatever an inspector changed while the weights were being resolved.
        if (Material(materialFile) is not { } content) {
            throw new InvalidOperationException(Unreadable(name));
        }

        File.WriteAllText(
            materialFile,
            YamlSerializer.ToYaml(MaterialBake.Splatted(content, new AssetReference(texture.Guid), layers))
        );

        Written = [file, materialFile];

        project.Assets.Scan();

        if (!project.Assets.TryGetByPath(project.Paths.Relative(materialFile), out var found)) {
            throw new InvalidOperationException(Unresolved(materialFile));
        }

        Bound(materialFile, found.Guid, image, record);
        project.Assets.Save();

        return new(
            name,
            new AssetReference(found.Guid),
            new Dictionary<MaterialMapTarget, AssetReference> {
                [MaterialMapTarget.Splat] = new(texture.Guid)
            },
            [file, materialFile],
            warnings
        );
    }

    /// <summary>Adds this write's digest to the material's block without disturbing the rest of it.</summary>
    /// <remarks>
    ///     ⚠ <b>The digest key is the one <see cref="MaterialProvenance.Painted" /> and
    ///     <see cref="Ours" /> both read</b>, so writing it under any other name would leave a splat
    ///     map that no guard can tell from a file somebody authored — overwritable without a word,
    ///     which is the failure § D4's whole digest exists to prevent.
    /// </remarks>
    static void Bound(string file, AssetId guid, MaterialMapImage image, MaterialBakeRecord record) {
        var sidecar = AssetMetaFile.PathFor(file);
        var existing = Existing(sidecar);
        var extensions = new Dictionary<string, string>(existing?.Extensions ?? [], StringComparer.Ordinal) {
            [MaterialProvenance.DigestPrefix + MaterialMapNaming.Suffix(MaterialMapTarget.Splat)] =
                MaterialProvenance.Digest(image.Bytes),
            [MaterialProvenance.SplatSourceKey] = record.Source,
            [MaterialProvenance.SplatAtKey] =
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var outputs = extensions.TryGetValue(MaterialProvenance.OutputsKey, out var listed) ? listed : string.Empty;
        var suffix = MaterialMapNaming.Suffix(MaterialMapTarget.Splat);

        if (!outputs.Split(MaterialProvenance.Separator, StringSplitOptions.TrimEntries).Contains(suffix)) {
            extensions[MaterialProvenance.OutputsKey] =
                outputs.Length == 0 ? suffix : outputs + MaterialProvenance.Separator + suffix;
        }

        var meta = existing is null ? new AssetMeta { Guid = guid } : existing with { Guid = guid };

        AssetMetaFile.WriteFile(sidecar, meta with { Extensions = extensions });
    }

    /// <summary>Why <see cref="WriteSplat" /> could not bind a map onto this material, or null.</summary>
    /// <param name="material">What the material is called. Sanitised here, as the write sanitises it.</param>
    /// <returns>The sentence to tell the artist, or <see langword="null" /> when there is one to bind onto.</returns>
    /// <exception cref="ArgumentException"><paramref name="material" /> is null or empty.</exception>
    /// <remarks>
    ///     ⚠ <b>Public so that a caller can ask <em>before</em> it evaluates anything, and shared so
    ///     that the two askings cannot disagree.</b> A splat write refuses on this whatever order it
    ///     is asked in, but a route that only found out at the end would answer a project-shaped
    ///     mistake — "there is no layered material yet" — with whatever refusal it happened to hit
    ///     first, and on a host with no device that is a message about the window not being up.
    ///     Writing the sentence twice is the alternative, and two sentences drift.
    /// </remarks>
    public string? Unbindable(string material) {
        ArgumentException.ThrowIfNullOrEmpty(material);

        var directory = Path.Combine(project.Paths.Assets, Folder);
        var name = Safe(material);

        if (File.Exists(Path.Combine(directory, name + MaterialImporter.Extension))) {
            return null;
        }

        return $"There is no \"{name}{MaterialImporter.Extension}\" in {directory}, so a splat map written here "
            + "would be a texture nothing samples. A splat map's channels are one material's layer indices — bake "
            + "or author the layered material first, then bake its weights.";
    }

    /// <summary>And when the material is there and cannot be read.</summary>
    static string Unreadable(string name) =>
        $"\"{name}{MaterialImporter.Extension}\" could not be read as a material, so there is nothing to bind a "
        + "splat map onto. Nothing further was written: unlike a full bake, which rewrites the file it could not "
        + "parse, this one has to keep every feature already in it.";

    /// <summary>Whether this bake wrote a height map that the material it wrote samples nowhere.</summary>
    /// <param name="maps">What the bake wrote, by target.</param>
    /// <param name="content">The material the bake just composed for them.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The height output is the one a bake can write and leave unread</b>, and that is by
    ///         design rather than by omission: <c>MaterialBake.Material</c> composes a textured
    ///         feature for each of the other five targets, and deliberately composes none for height,
    ///         because doing so would put a per-pixel march on every material any graph ever emitted a
    ///         height map from. Parallax is the author's — kept across a re-bake, re-seated and bound
    ///         — so a material that already carries one is fed and this says nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which leaves a first bake with no way to ask, and silence was the whole of the
    ///         problem.</b> A first bake has no existing material to read the author's intent out of,
    ///         so the route is two steps — bake, add the feature, bake again — and nothing anywhere
    ///         said so. The file is written either way; what this adds is the sentence naming the
    ///         second step. See <a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>,
    ///         which stays open: a route the tool describes is not yet a route the tool offers.
    ///     </para>
    ///     <para>
    ///         <b>Read off the composed material rather than off the existing one</b>, which is what
    ///         makes it true of what was written. <c>Material</c> drops a preserved feature when this
    ///         bake wrote no height map, so asking the input would say "fed" about a material whose
    ///         output carries nothing.
    ///     </para>
    /// </remarks>
    static bool Unmarched(IReadOnlyDictionary<MaterialMapTarget, AssetReference> maps, MaterialContent content) =>
        maps.ContainsKey(MaterialMapTarget.Height)
        && !content.Features.Any(feature => feature is ParallaxOcclusionFeature);

    /// <summary>What it says, which has to name the step rather than the state.</summary>
    static string Unsampled(string name) =>
        $"'{name}' was baked with a height map and nothing in the material samples it — a height "
        + "output is written for whatever wants it, and the feature that marches one is the author's. "
        + $"Add '- !ParallaxOcclusion' as the first feature of {name}{MaterialImporter.Extension} and "
        + "bake again: the next bake keeps it, keeps its heightScale, and binds this map to it.";

    /// <summary>What a bake says when the database did not pick a file it wrote back up.</summary>
    /// <remarks>
    ///     ⚠ <b>A missed read-back is a bug in the write, not a material to ship.</b> Recording
    ///     <see cref="AssetReference.Null" /> and carrying on wrote a <c>.vxmat</c> naming a texture
    ///     that resolves to nothing: <c>MaterialBake.Material</c> keys the feature on the map being
    ///     <i>present</i>, so the feature was added with a null beside it, the bindless index stayed
    ///     zero, and the surface shaded from slot zero on every device with nothing reported — see
    ///     <a href="https://github.com/Rikarin/Vixen/issues/724">#724</a>. It is a state a project can
    ///     genuinely be in: <c>AssetDatabase</c> refuses to re-create a <c>.meta</c> whose GUID it
    ///     cannot read, because minting a new one would break every reference to that asset.
    /// </remarks>
    static string Unresolved(string file) =>
        $"'{Path.GetFileName(file)}' was written and the asset database did not pick it up, so this bake cannot "
        + "name it by id. Nothing further was written: a material naming a texture that resolves to nothing shades "
        + "every surface it covers from the bindless table's fallback, on every device, with nothing reported. The "
        + "usual cause is a .meta beside that file whose GUID cannot be read — a scan refuses to replace one, "
        + "because minting a new id would break every reference to it. Repair or remove that .meta and bake again.";

    /// <summary>What this set is called in the folder, which is not simply what it was asked to be.</summary>
    /// <remarks>
    ///     <inheritdoc cref="ProjectMaterialBaker" path="/remarks/para[5]" />
    /// </remarks>
    static string SetName(string directory, string source, string material, out string taken) {
        taken = string.Empty;

        for (var suffix = 1; suffix <= Crowd; suffix++) {
            var candidate = suffix == 1 ? material : material + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            var owner = OwnerOf(directory, candidate);

            if (owner is not { } already
                || already.Length == 0
                || string.Equals(already, source, StringComparison.Ordinal)) {
                return candidate;
            }

            if (taken.Length == 0) {
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

    /// <summary>How an overpaint refusal ends, and the only <see cref="IOException" /> force answers.</summary>
    /// <remarks>
    ///     ⚠ <b>Shared rather than matched by shape, because a caller has to tell this refusal from
    ///     the others.</b> <c>Write</c> raises an <see cref="IOException" /> for three unrelated
    ///     reasons — a map somebody painted over, the <see cref="Crowd" /> ceiling, and a locked or
    ///     read-only file — and only the first is one <c>force</c> changes. A caller that offered
    ///     force for all three would send an artist to a control that does nothing, which is the
    ///     defect this constant exists to prevent; <c>MaterialBakeRouteDeviceTests</c> holds both
    ///     halves.
    /// </remarks>
    public const string Overpaint = "Re-baking would replace that work, so it did not.";

    /// <summary>How many differently-sourced sets may share one name before the bake refuses.</summary>
    /// <remarks>
    ///     Absurd rather than tuned, and it is a bound on a loop that opens a file per turn rather
    ///     than a judgement about what is reasonable.
    /// </remarks>
    const int Crowd = 100;

    /// <summary>Which source owns the set under a name, or null where no set is under it.</summary>
    /// <remarks>
    ///     An empty string is the third answer and means a set is there and does not say what made it —
    ///     a set baked by hand, or before this block existed. <see cref="SetName" /> adopts one, because
    ///     leaving it alone would strand it under the name while the re-bake landed beside it.
    /// </remarks>
    static string? OwnerOf(string directory, string name) {
        var material = Path.Combine(directory, name + MaterialImporter.Extension);

        if (Existing(AssetMetaFile.PathFor(material)) is not { } meta) {
            return File.Exists(material) ? string.Empty : null;
        }

        return MaterialProvenance.KeyIn(meta.Extensions);
    }

    /// <summary>The bytes each recorded output has on disk now, for the painted-over check.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both extensions are tried, because the size decides which one a map is.</b> A set
    ///         that used to be 4K is a container and the same set at 2K is a PNG, and a check that
    ///         looked only under the extension this bake is about to write would call every output of a
    ///         resized re-bake "not there" and skip the guard entirely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the one that agrees with the digest wins, rather than the first one found.</b>
    ///         Stopping at the first — and the list starts at <c>.png</c> — meant that a PNG sitting
    ///         beside a container this bake wrote was compared against the container's digest, differed
    ///         from it by construction, and refused <i>every</i> further bake of that material as
    ///         painted over. A file the bake is not writing cannot be the evidence that the file it is
    ///         writing was painted on; see
    ///         <a href="https://github.com/Rikarin/Vixen/issues/723">#723</a>'s second defect. What
    ///         becomes of the loser is
    ///         <see cref="Prune(string, string, IReadOnlyList{MaterialMapImage}, IReadOnlyDictionary{string, string}, List{string})" />'s question, not this one's.
    ///     </para>
    /// </remarks>
    static Dictionary<MaterialMapTarget, byte[]> OnDisk(
        string directory,
        string name,
        IReadOnlyDictionary<string, string> recorded
    ) {
        var present = new Dictionary<MaterialMapTarget, byte[]>();

        foreach (var target in MaterialMapNaming.EveryTarget) {
            if (!recorded.TryGetValue(MaterialProvenance.DigestPrefix + MaterialMapNaming.Suffix(target),
                    out var digest)) {
                continue;
            }

            foreach (var extension in Extensions) {
                var file = Path.Combine(directory, MaterialMapNaming.FileName(name, target, extension));

                if (!File.Exists(file)) {
                    continue;
                }

                var bytes = File.ReadAllBytes(file);

                if (string.Equals(MaterialProvenance.Digest(bytes), digest, StringComparison.Ordinal)) {
                    present[target] = bytes;
                    break;
                }

                // Kept as the candidate only while nothing better turns up, so that a set whose one
                // file on disk really has been painted on is still reported.
                present.TryAdd(target, bytes);
            }
        }

        return present;
    }

    /// <summary>Every extension a map may be written with, largest container last.</summary>
    static readonly string[] Extensions = [
        MaterialMapNaming.PortableExtension, MaterialMapNaming.ContainerExtension
    ];

    /// <summary>Takes away the files under this set's names that this bake can prove it wrote.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two hazards, one question.</b> A set re-baked across
    ///         <see cref="MaterialMapNaming.PortableLimit" /> changes a map's extension
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/723">#723</a>), and a re-bake that
    ///         stops producing an output leaves that output behind
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/726">#726</a>). Both leave a project
    ///         asset holding the previous bake's pixels under a name that says it is this one's, which
    ///         is exactly the shape a generator or a second material picks up by accident — and the
    ///         dropped output is the worse of the two, because <see cref="Provenance" /> drops its
    ///         digest key with it and the next bake would not even call it painted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The digest is the proof and the name is not.</b> Deleting on the name alone
    ///         destroyed data: the first bake of a material called <c>Rock</c> removed a hand-authored
    ///         <c>Rock_baseColor.png</c> <em>and its <c>.meta</c></em>, and with it the id every scene
    ///         resolved that texture through. So a file goes only when this material's own sidecar
    ///         recorded a digest for that map and the bytes on disk still hash to it. Anything else is
    ///         named in a warning and left where it is — an orphan an artist can see is a strictly
    ///         better failure than one this code deletes for them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which means <c>force</c> deliberately does not widen this.</b> Forcing says
    ///         "overwrite what I painted", and a painted file's bytes are by definition not the ones
    ///         the bake wrote — so a painted output this bake has stopped producing survives a forced
    ///         run.
    ///     </para>
    /// </remarks>
    static void Prune(
        string directory,
        string name,
        IReadOnlyList<MaterialMapImage> images,
        IReadOnlyDictionary<string, string> recorded,
        List<string> warnings
    ) {
        foreach (var target in MaterialMapNaming.EveryTarget) {
            // ⚠ Never the splat map, and it is the one target a graph bake must not judge. Nothing a
            // graph outputs can fill one (`MaterialMapNaming.Packed` gives it no channels), so
            // `Writing` is null for it on every run — and the branch that reaches is "this bake no
            // longer produces a splat map", which would report a file `WriteSplat` wrote and the
            // material still samples as an orphan, on every re-bake, for ever.
            if (target == MaterialMapTarget.Splat) {
                continue;
            }

            Prune(directory, name, target, Writing(images, target), recorded, warnings);
        }
    }

    /// <summary>The same question about one map: which of its files this bake can prove it wrote.</summary>
    /// <param name="directory">The folder the set lives in.</param>
    /// <param name="name">What the set is called.</param>
    /// <param name="target">Which map.</param>
    /// <param name="writing">The extension this write is about to use, or null where it writes none.</param>
    /// <param name="recorded">The material sidecar's extensions, as they stand.</param>
    /// <param name="warnings">Where anything the artist should know is added.</param>
    static void Prune(
        string directory,
        string name,
        MaterialMapTarget target,
        string? writing,
        IReadOnlyDictionary<string, string> recorded,
        List<string> warnings
    ) {
        foreach (var extension in Extensions) {
            // The file this bake is about to write is overwritten rather than pruned, which is what
            // keeps its GUID and every reference through it.
            if (string.Equals(extension, writing, StringComparison.Ordinal)) {
                continue;
            }

            var file = Path.Combine(directory, MaterialMapNaming.FileName(name, target, extension));

            if (!File.Exists(file)) {
                continue;
            }

            if (!Ours(file, target, recorded)) {
                warnings.Add(Kept(target, file, writing));
                continue;
            }

            if (Remove(file, warnings)) {
                warnings.Add(Removed(target, file, writing));
            }
        }
    }

    /// <summary>Which extension this bake writes a map under, or null where it writes none.</summary>
    static string? Writing(IReadOnlyList<MaterialMapImage> images, MaterialMapTarget target) {
        foreach (var image in images) {
            if (image.Target == target) {
                return image.Extension;
            }
        }

        return null;
    }

    /// <summary>Whether a previous run of this bake wrote the file that is there now.</summary>
    /// <remarks>
    ///     ⚠ <b>An unreadable file is not this bake's.</b> The proof is a digest over bytes that can be
    ///     read, so a file the process cannot open fails the check and is kept — which is the direction
    ///     a guard whose other outcome is a deletion has to fail in.
    /// </remarks>
    static bool Ours(string file, MaterialMapTarget target, IReadOnlyDictionary<string, string> recorded) {
        if (!recorded.TryGetValue(MaterialProvenance.DigestPrefix + MaterialMapNaming.Suffix(target),
                out var digest)) {
            return false;
        }

        try {
            return string.Equals(MaterialProvenance.Digest(File.ReadAllBytes(file)), digest, StringComparison.Ordinal);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>The file and its sidecar, or a warning saying why they are still there.</summary>
    /// <remarks>
    ///     The sidecar goes with the file it names, because a sidecar whose asset is gone is what
    ///     <c>AssetDatabase</c> quarantines — a second report, in another place, about a file this bake
    ///     already reported.
    /// </remarks>
    static bool Remove(string file, List<string> warnings) {
        try {
            File.Delete(file);

            var sidecar = AssetMetaFile.PathFor(file);

            if (File.Exists(sidecar)) {
                File.Delete(sidecar);
            }

            return true;
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            warnings.Add($"{Path.GetFileName(file)} could not be removed: {failure.Message}");
            return false;
        }
    }

    /// <summary>What the artist is told about a file the bake took away.</summary>
    static string Removed(MaterialMapTarget target, string file, string? writing) =>
        $"{Path.GetFileName(file)} was removed"
        + (writing is null
            ? $", because this bake no longer produces a {MaterialMapNaming.Suffix(target)} map"
            : $", because the {MaterialMapNaming.Suffix(target)} map is now {writing}")
        + ". Its bytes were the digest this material's own sidecar recorded, so a previous run of this bake wrote it.";

    /// <summary>And about one it would not touch.</summary>
    static string Kept(MaterialMapTarget target, string file, string? writing) =>
        (writing is null
            ? $"This bake no longer produces a {MaterialMapNaming.Suffix(target)} map. "
            : $"The {MaterialMapNaming.Suffix(target)} map is now {writing}. ")
        + $"{Path.GetFileName(file)} was LEFT IN PLACE, because its bytes are not the digest this material recorded "
        + "for that map — so nothing here can tell a file this bake wrote from one somebody authored or painted. "
        + "Delete it yourself once you have checked which it is.";

    /// <summary>What the artist is told when a name they did not choose was used.</summary>
    static string Clashed(string material, string name) =>
        $"Another source has already baked a material called \"{material}\" here, so this one was written as "
        + $"\"{name}\". Two graphs with one name is what that means — rename one of them to tell them apart.";

    /// <summary>What the artist is told when the bake found their own pixels under its outputs.</summary>
    static string Overpainted(string name, IReadOnlyList<MaterialMapTarget> painted) =>
        $"The {string.Join(", ", painted.Select(MaterialMapNaming.Suffix))} "
        + $"{(painted.Count == 1 ? "map" : "maps")} of \"{name}\" "
        + $"{(painted.Count == 1 ? "is" : "are")} not what the last bake wrote, which usually means somebody "
        + "painted over them. " + Overpaint;

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
            // ⚠ And the splat map's three keys survive, which is the one exception and is owed for
            // the same reason the sweep exists. A splat map is written by `WriteSplat` and never by
            // this bake, so dropping its digest here would leave the file with nothing any guard can
            // compare it against — silently overwritable by the next `WriteSplat`, which is exactly
            // what § D4's digest exists to prevent.
            if (!key.StartsWith(Texturing, StringComparison.Ordinal) || Splat.Contains(key)) {
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

    /// <summary>The keys a splat write owns, which a graph bake neither writes nor may drop.</summary>
    static readonly HashSet<string> Splat = new(StringComparer.Ordinal) {
        MaterialProvenance.DigestPrefix + MaterialMapNaming.Suffix(MaterialMapTarget.Splat),
        MaterialProvenance.SplatSourceKey,
        MaterialProvenance.SplatAtKey
    };

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
