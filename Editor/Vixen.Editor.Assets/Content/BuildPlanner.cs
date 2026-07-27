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
///         <b>An asset with no address is not an error.</b> It is not shipped by name, which is the
///         ordinary state of most files in a project — a source texture that only a material refers
///         to is reached through the chunk graph and never asked for by anyone.
///     </para>
///     <para>
///         <b>An addressable asset depending on one that is not addressable <i>is</i> an error</b>,
///         and this is the check worth having. The catalog records dependencies as addresses, so a
///         dependency with no address is not in any bundle — the build succeeds, ships, and fails at
///         load on a chunk that was never packed. That is exactly the class of mistake that is
///         expensive to find later and free to find here.
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

    /// <summary>Works out what to pack.</summary>
    /// <param name="assets">The project's assets, as a scan found them.</param>
    /// <param name="cache">What the imports produced.</param>
    /// <param name="readMeta">How to read an asset's sidecar.</param>
    /// <param name="groups">The <c>.vxgroup</c> policies the project defines.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    ///     Takes the entries rather than the <see cref="AssetDatabase" /> that produced them: this
    ///     needs a list of assets and nothing else the database offers, and asking for the database
    ///     would tie planning to a type that only exists once a real directory has been walked.
    /// </remarks>
    public static BuildPlan Plan(
        IEnumerable<AssetEntry> assets,
        ImportCache cache,
        Func<AssetEntry, AssetMeta?> readMeta,
        IEnumerable<AddressableGroup> groups
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

        foreach (var (entry, meta) in metas.Values) {
            if (AddressOf(meta) is { } address) {
                (claimants.TryGetValue(address, out var already) ? already : claimants[address] = []).Add(entry);
            }
        }

        var addressOf = new Dictionary<AssetId, string>();
        var claimedBy = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (address, claiming) in claimants) {
            if (claiming.Count > 1) {
                // Neither is planned. Keeping the first would be deciding which of them wins, which
                // is the thing this error says cannot be decided — and it would decide it by
                // enumeration order, so the winner would depend on a file's name.
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"{string.Join(", ", claiming.Select(entry => $"'{entry.Path}'"))} all claim the address "
                    + $"'{address}'. An address names one thing, so a build cannot decide which of them a game "
                    + "asking for it should get."
                ));

                continue;
            }

            claimedBy[address] = claiming[0].Path;
            addressOf[claiming[0].Guid] = address;
        }

        var planned = ImmutableArray.CreateBuilder<BuildableAsset>();
        var used = new SortedDictionary<string, AddressableGroup>(StringComparer.Ordinal);
        var needsDefault = false;

        foreach (var (entry, meta) in metas.Values) {
            if (AddressOf(meta) is not { } address || claimedBy.GetValueOrDefault(address) != entry.Path) {
                continue;
            }

            if (!cache.TryGet(entry.Guid, out var record) || record is null) {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is addressable as '{address}' and has not been imported, so there is no chunk "
                    + "to pack. Import before building, or remove its address."
                ));

                continue;
            }

            if (record.Artifacts.Count == 0) {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is addressable as '{address}' and its importer produced nothing."
                ));

                continue;
            }

            if (record.Artifacts.Count > 1) {
                // The record keeps artefact ids and not the sub-asset each belongs to, so there is no
                // way to name the extra ones. Packing only the first would ship a model whose meshes
                // are missing and fail at load rather than here.
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' imported to {record.Artifacts.Count} artefacts, and addressing sub-assets is "
                    + "not built yet. Packing only the first would ship an asset with its parts missing."
                ));

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
                    + "deleted or the sidecar names a group that has not been created."
                ));

                continue;
            }

            if (Dependencies(entry, record, addressOf, address, diagnostics) is not { } dependencies) {
                continue;
            }

            planned.Add(new(
                address,
                record.Artifacts[0],
                group,
                [.. meta.Addressable?.Labels ?? []],
                [.. dependencies]
            ));
        }

        if (needsDefault) {
            diagnostics.Add(new(
                ImportSeverity.Information,
                $"Some assets name no group, so they were put in '{DefaultGroupName}' — packed together, LZ4, "
                + "shipped in the application. Add a .vxgroup to choose something else."
            ));
        }

        planned.Sort(static (left, right) => string.CompareOrdinal(left.Address, right.Address));
        diagnostics.Sort(static (left, right) => right.Severity.CompareTo(left.Severity));

        return new(planned.ToImmutable(), [.. used.Values], diagnostics.ToImmutable());
    }

    /// <summary>The group a project gets when it has not asked for one.</summary>
    static AddressableGroup DefaultGroup => new() { Name = DefaultGroupName };

    static string? AddressOf(AssetMeta meta) =>
        meta.Addressable?.Address is { Length: > 0 } address ? address : null;

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
                    $"'{entry.Path}' has no readable sidecar, so it cannot be addressed or grouped."
                ));
            }
        }

        return metas;
    }

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
    static List<string>? Dependencies(
        AssetEntry entry,
        ImportRecord record,
        Dictionary<AssetId, string> addressOf,
        string address,
        ImmutableArray<ImportDiagnostic>.Builder diagnostics
    ) {
        var found = new List<string>();
        var complete = true;

        foreach (var dependency in record.AssetDependencies) {
            if (addressOf.TryGetValue(dependency, out var dependencyAddress)) {
                found.Add(dependencyAddress);
            } else {
                diagnostics.Add(new(
                    ImportSeverity.Error,
                    $"'{entry.Path}' is addressable as '{address}' and depends on asset {dependency}, which has no "
                    + "address. The catalog records dependencies by address, so that chunk would not be packed and "
                    + "the load would fail on a device rather than here."
                ));

                complete = false;
            }
        }

        // Sorted, because a dependency list that reorders turns a rebuild of unchanged content into a
        // catalog diff.
        found.Sort(StringComparer.Ordinal);

        return complete ? found : null;
    }
}
