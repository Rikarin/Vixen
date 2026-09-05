// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;

namespace Vixen.Editor.Assets.MeshMaps;

/// <summary>One baked mesh map, as the project holds it.</summary>
/// <param name="Usage">What it measures, read from the sidecar rather than from the file name.</param>
/// <param name="Map">Its identity, which is what a node binds.</param>
/// <param name="Set">The set it belongs to — the stem every file in that set is named from.</param>
/// <param name="Model">The model the set was baked from, or empty where the sidecar names none.</param>
/// <param name="Scale">
///     What an encoded value is multiplied by, or zero where the map is not quantized. Only
///     <see cref="MeshMapUsage.Displacement" /> and <see cref="MeshMapUsage.Curvature" /> carry one,
///     and a reader recovers the measurement as <c>(sample·2 − 1)·Scale</c>.
/// </param>
/// <param name="Path">Where it is, project-relative, for a message and for a file browser.</param>
public readonly record struct MeshMapAsset(
    MeshMapUsage Usage,
    AssetReference Map,
    string Set,
    AssetId Model,
    float Scale,
    string Path
);

/// <summary>
///     Every baked mesh map a project holds, indexed the way § 4.8's Mesh Map Input asks for one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § 4.8's read side, and until this existed there was none.</b> Nine files land in
///         <c>Assets/MeshMaps/</c> per mesh with a sidecar naming what each one measures, and
///         <a href="https://github.com/Rikarin/Vixen/issues/702">#702</a> is the observation that
///         <see cref="MeshMapNaming.UsageKey" /> was written by <c>ProjectMeshMapBaker</c> and read
///         by no file in the repository. A generator compound that works on every mesh is exactly a
///         graph that asks for <see cref="MeshMapUsage.Curvature" /> and is handed whichever file
///         this project's bake produced — so the binding is a query, and this is the query.
///     </para>
///     <para>
///         ⚠ <b>Over the sidecar keys and not over the file names, which is the whole reason it is a
///         type rather than a <c>Path.Combine</c>.</b> <see cref="MeshMapNaming" />'s own remarks say
///         which of the two wins: a file somebody renamed still knows what it measures, and doc 08's
///         argument is that a path is a fact about today. A resolver built on
///         <see cref="MeshMapNaming.FileName" /> would silently stop finding a map the moment an
///         artist tidied the folder, which is the failure this binding exists to avoid.
///     </para>
///     <para>
///         ⚠ <b>A snapshot, deliberately, and it is stale the moment a bake writes.</b> Indexing
///         opens one sidecar per candidate file, so a query that re-read the project would be a
///         directory's worth of file reads per node per evaluation. A caller rebuilds it after a
///         bake — which is the same contract <c>AssetDatabase.Scan</c> has with everything that reads
///         its index.
///     </para>
///     <para>
///         ⚠ <b>An ambiguous query is refused rather than resolved by enumeration order.</b> A model
///         with three meshes has three sets and every one of them has a curvature map, so "the
///         curvature map of this model" is not a question with an answer — and
///         <c>TextureRunner.Read</c> makes the same call about two files claiming one usage, for the
///         same reason: picking one silently is how a graph binds the wrong map on one machine and
///         the right one on another.
///     </para>
/// </remarks>
public sealed class MeshMapLibrary {
    readonly Dictionary<(string Set, MeshMapUsage Usage), MeshMapAsset> bySet = [];

    MeshMapLibrary(List<MeshMapAsset> maps) {
        Maps = maps;
        Sets = [.. maps.Select(map => map.Set).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        foreach (var map in maps) {
            // ⚠ First wins, and a duplicate is not silently dropped: `Sets` below is what says how
            // many there are, and two files claiming one (set, usage) is a project whose folder was
            // edited by hand — the sidecar is authoritative, so the second one is a genuine clash
            // rather than a stale name.
            bySet.TryAdd((map.Set, map.Usage), map);
        }
    }

    /// <summary>Every mesh map found, in the order the database enumerated them.</summary>
    public IReadOnlyList<MeshMapAsset> Maps { get; }

    /// <summary>The names of the sets found, each once, ordered.</summary>
    public IReadOnlyList<string> Sets { get; }

    /// <summary>Reads every baked mesh map a database has indexed.</summary>
    /// <param name="assets">The project's asset database, already scanned.</param>
    /// <returns>The library.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Every indexed <c>.png</c> is a candidate and the sidecar decides, which is the cost
    ///     of binding by usage rather than by folder.</b> A map an artist moved out of
    ///     <see cref="MeshMapNaming.DefaultFolder" /> is still a map — the folder is a default and
    ///     not the identity — so the index cannot be narrowed to it without reintroducing exactly
    ///     the path dependence the sidecar exists to remove.
    /// </remarks>
    public static MeshMapLibrary Index(AssetDatabase assets) {
        ArgumentNullException.ThrowIfNull(assets);

        var found = new List<MeshMapAsset>();

        foreach (var entry in assets.Entries) {
            if (entry.IsFolder
                || !entry.Path.EndsWith(MeshMapNaming.Extension, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (Sidecar(assets.Paths.Absolute(entry.Path)) is not { } meta) {
                continue;
            }

            if (!meta.Extensions.TryGetValue(MeshMapNaming.UsageKey, out var written)
                || !MeshMapNaming.TryParseSuffix(written, out var usage)) {
                // Not one of ours. Every other PNG in the project lands here.
                continue;
            }

            found.Add(
                new(
                    usage,
                    new AssetReference(entry.Guid),
                    // ⚠ The sidecar's set name, falling back to the file's stem. `ProjectMeshMapBaker`
                    // has always written the key, but a set written by an older bake — or by a
                    // third-party baker through `IMeshMapBaker` — may not have, and a map with no set
                    // is unqueryable rather than merely unlabelled.
                    meta.Extensions.TryGetValue(MeshMapNaming.MeshKey, out var set) && set.Length > 0
                        ? set
                        : Stem(entry.Path),
                    meta.Extensions.TryGetValue(MeshMapNaming.ModelKey, out var owner)
                    && AssetId.TryParse(owner, out var model)
                        ? model
                        : AssetId.Empty,
                    meta.Extensions.TryGetValue(MeshMapNaming.ScaleKey, out var scale)
                    && float.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var range)
                        ? range
                        : 0f,
                    entry.Path
                )
            );
        }

        return new(found);
    }

    /// <summary>The map one set measures a given thing with.</summary>
    /// <param name="set">The set's name — the stem every file in it is named from.</param>
    /// <param name="usage">What the map has to measure.</param>
    /// <param name="map">What was found.</param>
    /// <returns>Whether that set has that map.</returns>
    /// <exception cref="ArgumentException"><paramref name="set" /> is null or empty.</exception>
    /// <remarks>
    ///     ⚠ <b>A set may legitimately lack a usage.</b> <c>MeshMapBake.Always</c> guarantees only the
    ///     normal and the displacement; the seven that cost rays are baked when
    ///     <c>BakeSettings</c> asked for them, so a graph asking for occlusion of a set baked without
    ///     it gets <see langword="false" /> and not an empty map — the difference a generator has to
    ///     report to the artist.
    /// </remarks>
    public bool TryResolve(string set, MeshMapUsage usage, out MeshMapAsset map) {
        ArgumentException.ThrowIfNullOrEmpty(set);
        return bySet.TryGetValue((set, usage), out map);
    }

    /// <summary>The map a model's one set measures a given thing with.</summary>
    /// <param name="model">The model asset the set was baked from.</param>
    /// <param name="usage">What the map has to measure.</param>
    /// <param name="map">What was found.</param>
    /// <returns>Whether exactly one set of that model has that map.</returns>
    /// <exception cref="ArgumentException"><paramref name="model" /> is empty.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A model is not a set, and this is the overload that says so out loud.</b> A model
    ///         with three meshes has three sets under it and every one of them has a normal map, so
    ///         "the normal map of this model" has three answers — and returning the first is how a
    ///         graph binds the barrel's lid on the machine whose file system enumerated it first.
    ///         Where a model has one set the question is well posed and this is the convenience that
    ///         answers it; where it has several the caller has to name the mesh, and
    ///         <see cref="SetsOf" /> is what it names them from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sets whose sidecar names no model are not candidates.</b>
    ///         <see cref="AssetId.Empty" /> means "nobody said", which
    ///         <see cref="MeshMapNaming.ModelKey" /> distinguishes from "no model" on purpose;
    ///         treating it as a match would make every un-keyed set in the project a candidate for
    ///         every model.
    ///     </para>
    /// </remarks>
    public bool TryResolve(AssetId model, MeshMapUsage usage, out MeshMapAsset map) {
        map = default;

        if (model.IsEmpty) {
            throw new ArgumentException(
                "An empty asset id is what a sidecar that names no model reads back as, so it matches "
                + "every un-keyed set rather than one. Resolve by set name instead.",
                nameof(model)
            );
        }

        var sets = SetsOf(model);

        return sets.Count == 1 && TryResolve(sets[0], usage, out map);
    }

    /// <summary>The sets a model has baked, each once.</summary>
    /// <param name="model">The model asset.</param>
    /// <returns>Their names, ordered, or empty where that model has baked none.</returns>
    public IReadOnlyList<string> SetsOf(AssetId model) =>
        model.IsEmpty
            ? []
            : [
                .. Maps.Where(map => map.Model == model)
                    .Select(map => map.Set)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ];

    /// <summary>The stem of a file name, for a set whose sidecar does not name one.</summary>
    static string Stem(string path) =>
        MeshMapNaming.TryParseFileName(path, out var mesh, out _) ? mesh : System.IO.Path.GetFileName(path);

    /// <summary>The sidecar beside a file, or null where there is not one that can be read.</summary>
    /// <remarks>
    ///     ⚠ <b>An unreadable sidecar is skipped rather than raised, and that is not the same
    ///     decision <c>ProjectMeshMapBaker</c> makes about writing one.</b> Indexing walks every PNG
    ///     in a project, so one file written by a newer editor would otherwise stop a graph from
    ///     binding any map at all. What it costs is a map that silently does not appear — which is
    ///     the state the database is already in for that file, since a sidecar it cannot read is a
    ///     file it leaves out of the index.
    /// </remarks>
    static AssetMeta? Sidecar(string file) {
        var path = AssetMetaFile.PathFor(file);

        try {
            return File.Exists(path) ? AssetMetaFile.ReadFile(path) : null;
        } catch (Exception failure)
            when (failure is IOException or YamlParseException or YamlBindingException or MetaVersionException) {
            return null;
        }
    }
}
