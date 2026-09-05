// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;

namespace Vixen.Editor.Assets.Content;

/// <summary>What a content build is going to pack, and everything wrong with it.</summary>
/// <param name="Assets">What to pack, in address order.</param>
/// <param name="Groups">The groups those assets belong to, including any the planner had to invent.</param>
/// <param name="Diagnostics">Everything worth saying, worst first.</param>
public sealed record BuildPlan(
    ImmutableArray<BuildableAsset> Assets,
    ImmutableArray<AddressableGroup> Groups,
    ImmutableArray<ImportDiagnostic> Diagnostics
) {
    /// <summary>Whether the plan can be built.</summary>
    public bool Succeeded => !Diagnostics.Any(diagnostic => diagnostic.Severity == ImportSeverity.Error);
}

/// <summary>Turns an imported project into the list a <see cref="ContentBuilder" /> packs.</summary>
/// <remarks>
///     <para>
///         The step between "every asset has been imported" and "there is a build". Imports produce
///         chunks and know nothing about addresses; <see cref="ContentBuilder" /> takes addresses and
///         knows nothing about imports. This reads the <c>addressable:</c> block out of each sidecar,
///         resolves what it leaves unsaid, and finds the mistakes that would otherwise surface as a
///         load failure on a device.
///     </para>
///     <para>
///         <b>Group is inherited from the nearest folder that names one.</b> Doc 08 puts a
///         <c>group</c> in a folder's own sidecar and says descendants inherit it, which is what makes
///         "everything under <c>Assets/UI</c> ships together" one line rather than one line per file.
///         The walk stops at the first ancestor that names a group, so a subfolder can override its
///         parent.
///     </para>
///     <para>
///         <b>Labels are not inherited.</b> Making a folder's labels apply to its descendants would
///         also make them impossible to remove from one of them, and a label is a query — the thing
///         you most want to be able to say "all of these except that one" about.
///     </para>
///     <para>
///         <b>An asset with no address gets its path.</b> <c>Assets/Textures/Crate.png</c> is unique,
///         stable within a checkout and already what the author sees in the Project panel, so it is an
///         address every asset can have without anybody typing one. This reverses doc 08's original
///         "an unaddressed asset is not shipped by name", and the reason is what that cost: a texture
///         a game wanted to load by name and a scene in the build list were each a build error whose
///         remedy was to go and fill in a field. <see cref="AddressableInfo.Excluded" /> is how
///         something is now kept out.
///     </para>
///     <para>
///         ⚠ <b>What a build says about a broken asset therefore depends on who chose its
///         address.</b> An author who typed one meant it to ship, so anything that stops it being
///         packed is an error; a path-derived address is a convenience over every file in the
///         project, and a convenience that turns an unimported scratch file into a failed build is
///         worse than none. The same conditions are warnings there, and the asset is simply not
///         shipped.
///     </para>
///     <para>
///         <b>An addressable asset depending on one that is not shipped <i>is</i> an error</b>, and
///         this is the check worth having. The catalog records dependencies as addresses, so a
///         dependency in no bundle — excluded, or refused for one of the reasons above — makes a
///         build that succeeds, ships, and fails at load on a chunk that was never packed. That is
///         exactly the class of mistake that is expensive to find later and free to find here.
///     </para>
///     <para>
///         <b>An asset's sub-assets are addressed under it</b>, at
///         <c>characters/hero#Hero_Mesh</c> — see <see cref="SubAssetAddress" />. They have to be in
///         the catalog rather than merely in a bundle, because a chunk is only reachable once the
///         bundle holding it is mounted, and what mounts a bundle is an address in the load closure.
///     </para>
/// </remarks>
public static class BuildPlanner {
    /// <summary>What a group is called when nothing names one.</summary>
    /// <remarks>
    ///     Invented rather than demanded, so that a small project can put one <c>address:</c> in one
    ///     sidecar and get a build. It is reported through a diagnostic rather than done quietly,
    ///     because the moment a project cares about compression, packing or remote delivery it needs
    ///     a real <c>.vxgroup</c> and should be told where the one it is using came from.
    /// </remarks>
    public const string DefaultGroupName = "Default";

    /// <summary>What separates an asset's address from the sub-asset it names inside it.</summary>
    /// <remarks>
    ///     The same character doc 08 gives a <c>vx:</c> reference, where it means the same thing:
    ///     what follows selects something inside the asset rather than the asset itself.
    /// </remarks>
    public const char SubAssetSeparator = '#';

    /// <summary>Works out what to pack.</summary>
    /// <param name="assets">The project's assets, as a scan found them.</param>
    /// <param name="cache">What the imports produced.</param>
    /// <param name="readMeta">How to read an asset's sidecar.</param>
    /// <param name="groups">The <c>.vxgroup</c> policies the project defines.</param>
    /// <param name="forServer">
    ///     Whether this is a dedicated server's build, which leaves out every group whose
    ///     <see cref="AddressableGroup.IncludeInServerBuild" /> is false.
    /// </param>
    /// <returns>The plan.</returns>
    /// <remarks>
    ///     <para>
    ///         Takes the entries rather than the <see cref="AssetDatabase" /> that produced them: this
    ///         needs a list of assets and nothing else the database offers, and asking for the database
    ///         would tie planning to a type that only exists once a real directory has been walked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="forServer" /> is decided here rather than in
    ///         <see cref="ContentBuilder" />, and that placement is the safety.</b> This is where
    ///         addresses become resolvable and where a dependency is turned into one, so an asset
    ///         dropped here is an asset nothing can name — and a shipped asset that depended on it
    ///         fails the build with a sentence. Dropping it one stage later, the way
    ///         <see cref="AddressableGroup.IncludeInBuild" /> historically did, leaves the catalog
    ///         naming a dependency that is in no bundle: a load failure on a running shard rather
    ///         than a message to the person who caused it.
    ///     </para>
    /// </remarks>
    public static BuildPlan Plan(
        IEnumerable<AssetEntry> assets,
        ImportCache cache,
        Func<AssetEntry, AssetMeta?> readMeta,
        IEnumerable<AddressableGroup> groups,
        bool forServer = false
    ) {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(readMeta);
        ArgumentNullException.ThrowIfNull(groups);

        var defined = groups.ToDictionary(group => group.Name, StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<ImportDiagnostic>();
        var metas = ReadAll(assets, readMeta, diagnostics);

        // Every address in the project, resolved before any of them is planned: a dependency is
        // named by its GUID and has to be turned into the address of an asset that may be planned
        // later, or not at all.
        var claimants = new SortedDictionary<string, List<AssetEntry>>(StringComparer.Ordinal);
        var resolved = new List<PlannedAsset>();
        var refused = new HashSet<string>(StringComparer.Ordinal);

        // Which group each asset a server build left out was in, so that the error a dependent gets
        // can say which line of which .vxgroup to change. "It has no address" is true and sends
        // somebody to the wrong file.
        var strippedFrom = new Dictionary<AssetId, string>();
        var strippedPerGroup = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var (entry, meta) in metas.Values) {
            if (AddressOf(entry, meta) is not { } address) {
                continue;
            }

            // ⚠ Everything that follows is an error when somebody asked for this address and a
            // warning when the path supplied it. An author who typed an address means the asset to
            // ship, so failing to pack it is a build that must not succeed; the default is a
            // convenience over every file in the project, and a convenience that turns an unimported
            // scratch file into a broken build is worse than no convenience at all.
            var asked = meta.Addressable?.Address is { Length: > 0 };
            var severity = asked ? ImportSeverity.Error : ImportSeverity.Warning;

            // Claimed whether or not anything else about the asset works out, because two assets
            // claiming one address is a fact about the project rather than about its imports, and
            // an error that appeared only once both of them imported would be a puzzle.
            Claim(claimants, address, entry);

            // ⚠ Before the import is looked at, and that ordering is deliberate: an asset a server
            // build does not ship need not have imported cleanly, and an authoring-only group full
            // of sources a realm never opens should not be able to fail a server build.
            if (forServer && StrippedGroup(entry, metas, defined) is { } left) {
                strippedFrom[entry.Guid] = left;
                strippedPerGroup[left] = strippedPerGroup.GetValueOrDefault(left) + 1;
                refused.Add(entry.Path);
                continue;
            }

            if (!cache.TryGet(entry.Guid, out var record) || record is null) {
                if (asked) {
                    diagnostics.Add(new(
                        ImportSeverity.Error,
                        $"'{entry.Path}' is addressable as '{address}' and has not been imported, so there is no "
                        + "chunk to pack. Import before building, or remove its address.",
                        entry.Path
                    ));
                }

                refused.Add(entry.Path);
                continue;
            }

            if (Chunks(entry, meta, address, record, severity, diagnostics) is not { } chunks) {
                refused.Add(entry.Path);
                continue;
            }

            foreach (var (_, subAddress, _) in chunks.SubAssets) {
                Claim(claimants, subAddress, entry);
            }

            resolved.Add(new(entry, meta, record, address, chunks.Main, chunks.SubAssets));
        }

        var addressOf = new Dictionary<AssetId, string>();

        foreach (var (address, claiming) in claimants) {
            if (claiming.Count > 1) {
                // None of them is planned. Keeping the first would be deciding which of them wins,
                // which is the thing this error says cannot be decided — and it would decide it by
                // enumeration order, so the winner would depend on a file's name.
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"{string.Join(", ", claiming.Select(entry => $"'{entry.Path}'"))} all claim the address "
                    + $"'{address}'. An address names one thing, so a build cannot decide which of them a game "
                    + "asking for it should get."
                ));

                foreach (var entry in claiming) {
                    refused.Add(entry.Path);
                }

                continue;
            }

            // Only an asset's own address answers a dependency on it. A dependent names the asset,
            // not a part of it, and gets the whole of it through that address's closure.
            //
            // ⚠ And only if it is actually being packed. An asset that claimed an address and was
            // then refused — never imported, its importer produced nothing — is in no bundle, so
            // answering a dependency with its address would hand the catalog a dependency that
            // resolves to nothing and fail on a device instead of here. That mattered less when
            // every refusal was itself an error; it is load-bearing now that a path-derived address
            // can be dropped with only a warning.
            if (!address.Contains(SubAssetSeparator, StringComparison.Ordinal) && !refused.Contains(claiming[0].Path)) {
                addressOf[claiming[0].Guid] = address;
            }
        }

        var planned = ImmutableArray.CreateBuilder<BuildableAsset>();
        var used = new SortedDictionary<string, AddressableGroup>(StringComparer.Ordinal);
        var needsDefault = false;

        foreach (var asset in resolved) {
            var entry = asset.Entry;
            var address = asset.Address;
            var subAssets = asset.SubAssets;

            if (refused.Contains(entry.Path)) {
                continue;
            }

            if (GroupFor(entry, metas) is not { } group) {
                group = DefaultGroupName;
                needsDefault = true;
            }

            if (defined.TryGetValue(group, out var policy)) {
                used[group] = policy;
            } else if (group == DefaultGroupName) {
                used[group] = DefaultGroup;
            } else {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is in group '{group}', which no .vxgroup defines. Either the group file was "
                    + "deleted or the sidecar names a group that has not been created.",
                    entry.Path
                ));

                continue;
            }

            if (Dependencies(entry, asset.Record, addressOf, strippedFrom, address, diagnostics) is not { } dependencies) {
                continue;
            }

            // Labels are the asset's, and its parts carry them too: a label is a query over shipped
            // content, so "everything labelled level1" has to reach a labelled model's meshes, and a
            // group that packs by label has to put an asset's parts in the bundle it is in.
            //
            // The group's own labels are unioned in — see AddressableGroup.Labels for why that is not
            // the folder inheritance this type's header refuses. De-duplicated, because a label named
            // in both places must not put an asset in a bundle twice; deliberately *not* sorted,
            // because the asset's own order is observable and ContentBuilder already sorts at the one
            // place order decides anything, which is packing by label.
            var labels = ImmutableArray.CreateRange(
                (asset.Meta.Addressable?.Labels ?? [])
                    .Concat(used[group].Labels)
                    .Where(label => label.Length > 0)
                    .Distinct(StringComparer.Ordinal)
            );

            // The main object depends on its own parts, which is what mounts their bundles and
            // deserialises them first — a chunk's reference to another resolves only once the thing
            // it points at is loaded. Without it, a model in a group that packs separately would
            // load with its meshes in a bundle nothing had opened.
            planned.Add(new(
                address,
                asset.Main,
                group,
                labels,
                [.. dependencies.Concat(subAssets.Select(part => part.Address)).Order(StringComparer.Ordinal)],
                new AssetReference(entry.Guid)
            ));

            foreach (var (id, subAddress, subAsset) in subAssets) {
                // The asset's dependencies, on each of its parts. Over-claiming rather than
                // under-claiming, deliberately: which part uses which dependency is not recorded,
                // and a mesh loaded on its own with its material's bundle unmounted fails at load.
                //
                // The reference carries the sub-asset id where the address carries its name — the two
                // halves SubAssetAddress describes. This is where they are written down together, and
                // it is the only place in the build that holds both.
                planned.Add(new(subAddress, id, group, labels, [.. dependencies], new AssetReference(entry.Guid, subAsset)));
            }
        }

        if (needsDefault) {
            diagnostics.Add(new(
                ImportSeverity.Information,
                $"Some assets name no group, so they were put in '{DefaultGroupName}' — packed together, LZ4, "
                + "shipped in the application. Add a .vxgroup to choose something else."
            ));
        }

        // Said out loud, per group, because a build that is smaller than the last one has to say why.
        // The silent version of this feature is a shard that starts fast and cannot find its map.
        foreach (var (group, count) in strippedPerGroup) {
            diagnostics.Add(new(
                ImportSeverity.Information,
                $"{count} asset(s) in group '{group}' are not in this build: it is a server build and that group's "
                + "includeInServerBuild is false."
            ));
        }

        // ⚠ And the case where that loop says nothing at all, which is what a server build looks like
        // on every project that has not been told what a server does not need. The profile answers a
        // group-membership question and IncludeInServerBuild defaults to true, so "nothing was
        // stripped" and "nobody has answered yet" are the same silence — and the visible result is a
        // realm image carrying its client's textures, which costs registry bandwidth and pod start-up
        // rather than failing.
        //
        // ⚠ The default cannot simply be flipped, and that is the whole reason this is a sentence
        // rather than a fix: stripping by default takes out a terrain heightmap a realm bakes its
        // collision from and reports success — which is what
        // AGroupThatSaysNothingIsShippedToAServer exists to pin. Safe by default, and loud about
        // what the default cost.
        if (forServer && strippedPerGroup.Count == 0) {
            diagnostics.Add(new(
                ImportSeverity.Warning,
                $"This is a server build and no group is marked `includeInServerBuild: false`, so it packs the same "
                + $"{planned.Count} address(es) a client's build does. A server profile is a group-membership "
                + "question the project answers, not one the build can work out — mark the groups a dedicated "
                + "server never asks for."
            ));
        }

        planned.Sort(static (left, right) => string.CompareOrdinal(left.Address, right.Address));
        diagnostics.Sort(static (left, right) => right.Severity.CompareTo(left.Severity));

        return new(planned.ToImmutable(), [.. used.Values], diagnostics.ToImmutable());
    }

    /// <summary>What a sub-asset is addressed as.</summary>
    /// <param name="address">The asset's own address.</param>
    /// <param name="name">The sub-asset's name, as its sidecar declares it.</param>
    /// <returns>The address — <c>characters/hero#Hero_Mesh</c>.</returns>
    /// <remarks>
    ///     <b>The name, where a <c>vx:</c> reference carries the id.</b> The two are the same thing
    ///     seen from different sides: a reference is written by the editor and read by nothing, so a
    ///     fixed-width hash costs nobody anything; an address is typed by a person into a call to
    ///     <c>LoadAsync</c>, and eight hex digits would be unusable there. Both break in the same way
    ///     when a sub-asset is renamed — the id is derived from the name — so the choice trades no
    ///     stability for the readability.
    /// </remarks>
    public static string SubAssetAddress(string address, string name) {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentNullException.ThrowIfNull(name);

        return $"{address}{SubAssetSeparator}{name}";
    }

    /// <summary>The group a project gets when it has not asked for one.</summary>
    static AddressableGroup DefaultGroup => new() { Name = DefaultGroupName };

    /// <summary>What an asset is shipped as, or <see langword="null" /> if it is not shipped.</summary>
    /// <param name="entry">The asset.</param>
    /// <param name="meta">Its sidecar.</param>
    /// <returns>Its address.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The path, unless the sidecar says otherwise.</b> A project-relative path is already
    ///         unique, already stable within a checkout and already what the author sees in the
    ///         Project panel, so it is an address every asset can have without anybody typing one.
    ///         This reverses doc 08's original position that an unaddressed asset is not shipped by
    ///         name, and the reason is what that position cost in practice: a texture the game wanted
    ///         to load by name and a scene in the build list were both build errors telling somebody
    ///         to go and fill in a field.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is lost is that an address no longer survives the file moving</b>, which the
    ///         GUID always will. That is the trade an explicit address buys back, and it is the right
    ///         thing to set on the handful of addresses that are contracts — a level a save game
    ///         names, a pack a URL is built from.
    ///     </para>
    ///     <para>
    ///         Folders have no chunk and never had an address. <see cref="AddressableInfo.Excluded" />
    ///         is the way to say "not shipped" now that saying nothing means the path.
    ///     </para>
    /// </remarks>
    static string? AddressOf(AssetEntry entry, AssetMeta meta) {
        if (meta.Addressable?.Address is { Length: > 0 } asked) {
            return asked;
        }

        // ⚠ A folder has no chunk, and a .vxgroup is the build's own configuration — it lives under
        // Assets/ because that is where the things it governs are, and defaulting it into a bundle
        // would ship a project's packing policy to its players. Both are still addressable if
        // somebody types an address, which is why this is below that check rather than above it.
        return entry.IsFolder
            || meta.Addressable is { Excluded: true }
            || entry.Path.EndsWith(AddressableGroup.Extension, StringComparison.OrdinalIgnoreCase)
                ? null
                : entry.Path;
    }

    static void Claim(SortedDictionary<string, List<AssetEntry>> claimants, string address, AssetEntry entry) =>
        (claimants.TryGetValue(address, out var already) ? already : claimants[address] = []).Add(entry);

    /// <summary>
    ///     What an asset's import produced, each chunk with the address it will be packed under, or
    ///     <see langword="null" /> if any of it cannot be named.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A chunk that cannot be named refuses the whole asset</b>, rather than being dropped
    ///         from a build that otherwise succeeds. A model whose meshes are missing fails at load
    ///         on a device, and the failure names the mesh rather than the thing that dropped it.
    ///     </para>
    ///     <para>
    ///         The names come from the sidecar's <c>subAssets</c> list, which the same import wrote,
    ///         rather than from a copy in the cache. One place says what a sub-asset is called, so a
    ///         rename cannot leave the two disagreeing — and an id the sidecar does not declare is an
    ///         asset that was imported and then had its sidecar rewritten, which is a re-import.
    ///     </para>
    /// </remarks>
    static (ObjectId Main, List<(ObjectId Id, string Address, SubAssetId SubAsset)> SubAssets)? Chunks(
        AssetEntry entry,
        AssetMeta meta,
        string address,
        ImportRecord record,
        ImportSeverity severity,
        ImmutableArray<ImportDiagnostic>.Builder diagnostics
    ) {
        if (record.Artifacts.Count == 0) {
            // ⚠ Not said at all when the address came from the path. An importer that produces no
            // chunk is an ordinary thing — a folder, a file claimed by something that only reads it
            // for its dependencies — and a project of ten thousand assets would otherwise open every
            // build with a page of warnings about files nobody ever asked to ship.
            if (severity == ImportSeverity.Error) {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is addressable as '{address}' and its importer produced nothing.",
                    entry.Path
                ));
            }

            return null;
        }

        var declaredById = new Dictionary<SubAssetId, SubAssetEntry>();

        foreach (var declared in meta.SubAssets) {
            declaredById[declared.Id] = declared;
        }

        var subAssets = new List<(ObjectId, string, SubAssetId)>();
        var written = new HashSet<SubAssetId>();
        var addressed = new Dictionary<string, SubAssetEntry>(StringComparer.Ordinal);
        ObjectId? main = null;
        var complete = true;

        foreach (var artifact in record.Artifacts) {
            if (!written.Add(artifact.SubAsset)) {
                diagnostics.Add(new(
                    severity,
                    $"'{entry.Path}' imported to two chunks for sub-asset {artifact.SubAsset}, so an address would "
                    + "name both. Its importer writes one of them twice.",
                    entry.Path
                ));

                complete = false;
                continue;
            }

            if (artifact.SubAsset.IsMain) {
                main = artifact.Id;
                continue;
            }

            if (!declaredById.TryGetValue(artifact.SubAsset, out var declared) || declared.Name.Length == 0) {
                diagnostics.Add(new(
                    severity,
                    $"'{entry.Path}' imported to a chunk for sub-asset {artifact.SubAsset}, which its .meta does not "
                    + "name, so nothing can address it. Re-import it.",
                    entry.Path
                ));

                complete = false;
                continue;
            }

            var subAddress = SubAssetAddress(address, declared.Name);

            // Two sub-assets of different kinds may share a name — a mesh and a material both called
            // Body is an ordinary thing for an FBX to contain — and their ids differ while their
            // addresses would not. The id collision is caught at import; this is the other half.
            if (addressed.TryGetValue(subAddress, out var already)) {
                diagnostics.Add(new(
                    severity,
                    $"'{entry.Path}' contains a {already.Type} and a {declared.Type} both called '{declared.Name}', "
                    + $"so both would be addressed '{subAddress}'. Rename one of them.",
                    entry.Path
                ));

                complete = false;
                continue;
            }

            addressed[subAddress] = declared;
            subAssets.Add((artifact.Id, subAddress, artifact.SubAsset));
        }

        if (main is null) {
            // The address has to name something. An importer with a main object writes it under
            // SubAssetId.Main; one whose asset is only a container of parts has an address that
            // resolves to no chunk, which fails at load rather than here.
            //
            // Silent for a path-derived address, for the reason the empty case above is: an asset
            // that is only a container of parts is a shape an importer is allowed to have, and the
            // parts still ship under their own addresses.
            if (severity == ImportSeverity.Error) {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is addressable as '{address}' and its importer wrote no main object, only "
                    + $"{subAssets.Count} sub-asset(s), so that address names nothing.",
                    entry.Path
                ));
            }

            complete = false;
        }

        // Sorted, because the plan's dependency lists are derived from these and a list that
        // reorders between builds turns a rebuild of unchanged content into a catalog diff.
        subAssets.Sort(static (left, right) => string.CompareOrdinal(left.Item2, right.Item2));

        return complete ? (main!.Value, subAssets) : null;
    }

    /// <summary>
    ///     Reads every sidecar once. The group walk needs a folder's sidecar as readily as a file's,
    ///     and reading each of them on demand would read a deep project's top folder once per
    ///     descendant.
    /// </summary>
    static Dictionary<string, (AssetEntry Entry, AssetMeta Meta)> ReadAll(
        IEnumerable<AssetEntry> assets,
        Func<AssetEntry, AssetMeta?> readMeta,
        ImmutableArray<ImportDiagnostic>.Builder diagnostics
    ) {
        var metas = new Dictionary<string, (AssetEntry, AssetMeta)>(StringComparer.Ordinal);

        foreach (var entry in assets.OrderBy(entry => entry.Path, StringComparer.Ordinal)) {
            if (readMeta(entry) is { } meta) {
                metas[entry.Path] = (entry, meta);
            } else {
                diagnostics.Add(new(
                    ImportSeverity.Warning,
                    $"'{entry.Path}' has no readable sidecar, so it cannot be addressed or grouped.",
                    entry.Path
                ));
            }
        }

        return metas;
    }

    /// <summary>
    ///     The group a dedicated server's build leaves this asset out of, or <see langword="null" />
    ///     when the server ships it.
    /// </summary>
    /// <remarks>
    ///     A group the project has not defined is not answered here. The second pass reports that as
    ///     an error against the asset, and a build that quietly stripped everything whose group file
    ///     somebody had deleted would turn a typo into an empty server bundle.
    /// </remarks>
    static string? StrippedGroup(
        AssetEntry entry,
        Dictionary<string, (AssetEntry Entry, AssetMeta Meta)> metas,
        Dictionary<string, AddressableGroup> defined
    ) =>
        GroupFor(entry, metas) is { } group
        && defined.TryGetValue(group, out var policy)
        && !policy.IncludeInServerBuild
            ? group
            : null;

    /// <summary>The nearest group naming an ancestor of this asset, or its own.</summary>
    static string? GroupFor(AssetEntry entry, Dictionary<string, (AssetEntry Entry, AssetMeta Meta)> metas) {
        var path = entry.Path;

        while (true) {
            if (metas.TryGetValue(path, out var found) && found.Meta.Addressable?.Group is { Length: > 0 } group) {
                return group;
            }

            var separator = path.LastIndexOf('/');

            if (separator <= 0) {
                return null;
            }

            path = path[..separator];
        }
    }

    /// <summary>An asset's dependencies as addresses, or <see langword="null" /> if any of them has none.</summary>
    /// <remarks>
    ///     ⚠ <b><paramref name="strippedFrom" /> is what keeps the server profile from being a silent
    ///     mistake.</b> Without it, an asset a server build ships whose dependency the same build left
    ///     out gets the general "which has no address" error — true, and it sends somebody to look at
    ///     the dependency's sidecar for a field that is already fine. With it, the message names the
    ///     group and the profile, which is the one line an author can act on.
    /// </remarks>
    static List<string>? Dependencies(
        AssetEntry entry,
        ImportRecord record,
        Dictionary<AssetId, string> addressOf,
        Dictionary<AssetId, string> strippedFrom,
        string address,
        ImmutableArray<ImportDiagnostic>.Builder diagnostics
    ) {
        var found = new List<string>();
        var complete = true;

        foreach (var dependency in record.AssetDependencies) {
            if (addressOf.TryGetValue(dependency, out var dependencyAddress)) {
                found.Add(dependencyAddress);
            } else if (strippedFrom.TryGetValue(dependency, out var strippedGroup)) {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is addressable as '{address}' and depends on asset {dependency}, which is in "
                    + $"group '{strippedGroup}' — a group this project says a dedicated server does not need. A "
                    + "server build cannot both ship this asset and leave that one out. Either move this asset into "
                    + $"'{strippedGroup}' as well, or set includeInServerBuild: true on it.",
                    entry.Path
                ));

                complete = false;
            } else {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is addressable as '{address}' and depends on asset {dependency}, which has no "
                    + "address. The catalog records dependencies by address, so that chunk would not be packed and "
                    + "the load would fail on a device rather than here.",
                    entry.Path
                ));

                complete = false;
            }
        }

        // Sorted, because a dependency list that reorders turns a rebuild of unchanged content into a
        // catalog diff.
        found.Sort(StringComparer.Ordinal);

        return complete ? found : null;
    }

    /// <summary>
    ///     An asset that has an address, an import behind it, and a name for every chunk that import
    ///     produced — everything the second pass needs, worked out in the first.
    /// </summary>
    /// <remarks>
    ///     Two passes rather than one because a sub-asset's address has to be claimed before any
    ///     asset is planned: it can collide with another asset's address, and an asset that loses a
    ///     collision is not built at all.
    /// </remarks>
    sealed record PlannedAsset(
        AssetEntry Entry,
        AssetMeta Meta,
        ImportRecord Record,
        string Address,
        ObjectId Main,
        List<(ObjectId Id, string Address, SubAssetId SubAsset)> SubAssets
    );
}
