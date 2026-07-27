// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.IO.Hashing;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Editor.Assets.Content;

/// <summary>One thing the import produced, ready to be packed.</summary>
/// <param name="Address">What a game will ask for.</param>
/// <param name="Id">Its chunk.</param>
/// <param name="Group">Which group's policy governs it.</param>
/// <param name="Labels">What it is grouped by for loading.</param>
/// <param name="Dependencies">Addresses that have to load first.</param>
public readonly record struct BuildableAsset(
    string Address,
    ObjectId Id,
    string Group,
    ImmutableArray<string> Labels,
    ImmutableArray<string> Dependencies
);

/// <summary>A bundle the build produced.</summary>
/// <param name="Name">What the catalog calls it.</param>
/// <param name="FileName">What its file is called.</param>
/// <param name="Bytes">Its contents.</param>
/// <param name="Hash">The hash of those contents.</param>
public readonly record struct BuiltBundle(string Name, string FileName, ReadOnlyMemory<byte> Bytes, ObjectId Hash);

/// <summary>What a content build produced.</summary>
/// <param name="Catalog">The index a runtime reads.</param>
/// <param name="Bundles">The files to ship.</param>
/// <param name="Diagnostics">Anything the build wants to say about what it did.</param>
public sealed record ContentBuildResult(
    ContentCatalog Catalog,
    ImmutableArray<BuiltBundle> Bundles,
    ImmutableArray<ImportDiagnostic> Diagnostics
);

/// <summary>Packs imported chunks into bundles and writes the catalog that indexes them.</summary>
/// <remarks>
///     <para>
///         The last step of a content build and the one that decides what shipping costs. Everything
///         before it produced chunks that exist; this decides which file each of them lives in, what
///         that file is called, and what a runtime has to be told to find it again.
///     </para>
///     <para>
///         <b>Deterministic, because doc 12 gates the build on it.</b> Two builds of the same content
///         on two operating systems produce the same bytes, which is what makes a content update able
///         to ship a diff and what stops CI failing on a difference nobody can reproduce. Most of that
///         comes from further down — <c>BundleWriter</c> writes chunks in id order and
///         <c>CatalogFormat</c> writes entries in address order — so sorting the assets here has
///         exactly one observable effect: the order the build reports things in. That is worth having
///         on its own, because a build log that reorders between runs is noise in every diff of it.
///     </para>
///     <para>
///         <b>An asset in more than one label goes in the first of them, alphabetically.</b>
///         <see cref="BundlePacking.PackTogetherByLabel" /> has to pick one bundle and no rule is
///         obviously right; picking the alphabetically first is at least a rule, and it is stated
///         here and reported by the build so nobody has to work it out from the output.
///     </para>
/// </remarks>
public sealed class ContentBuilder {
    /// <summary>The catalog format this writes.</summary>
    public static int CatalogVersion => CatalogFormat.Version;

    /// <summary>Which target the build is for.</summary>
    public string Target { get; }

    /// <summary>Sets up a build.</summary>
    /// <param name="target">Which target — <c>Windows</c>, <c>Android/Vulkan</c>.</param>
    public ContentBuilder(string target) {
        ArgumentException.ThrowIfNullOrEmpty(target);
        Target = target;
    }

    /// <summary>Packs and indexes.</summary>
    /// <param name="groups">The policies.</param>
    /// <param name="assets">What to pack.</param>
    /// <param name="chunks">Where the chunks are, from the import.</param>
    /// <returns>The bundles and the catalog.</returns>
    /// <exception cref="ArgumentException">An asset names a group that does not exist.</exception>
    public ContentBuildResult Build(
        IEnumerable<AddressableGroup> groups,
        IEnumerable<BuildableAsset> assets,
        IOdbBackend chunks
    ) {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(chunks);

        var policies = new Dictionary<string, AddressableGroup>(StringComparer.Ordinal);

        foreach (var group in groups) {
            if (!policies.TryAdd(group.Name, group)) {
                throw new ArgumentException($"Two groups are both called '{group.Name}'.", nameof(groups));
            }
        }

        var diagnostics = ImmutableArray.CreateBuilder<ImportDiagnostic>();

        // Sorted here rather than trusted from the caller: the whole determinism story rests on this
        // one ordering, and an enumerable's order is the caller's business right up until it isn't.
        var ordered = assets.OrderBy(asset => asset.Address, StringComparer.Ordinal).ToArray();
        var planned = new SortedDictionary<string, List<BuildableAsset>>(StringComparer.Ordinal);
        var bundleOf = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var asset in ordered) {
            if (!policies.TryGetValue(asset.Group, out var group)) {
                throw new ArgumentException(
                    $"'{asset.Address}' is in group '{asset.Group}', which no .vxgroup defines. Either the group "
                    + "file was deleted or the asset's addressable metadata points at a name nothing answers to.",
                    nameof(assets)
                );
            }

            if (!group.IncludeInBuild) {
                diagnostics.Add(
                    new(
                        ImportSeverity.Information,
                        $"'{asset.Address}' is in group '{group.Name}', which is turned off, so it is not in this "
                        + "build and nothing can address it."
                    )
                );

                continue;
            }

            var name = BundleNameFor(group, asset, diagnostics);

            if (!planned.TryGetValue(name, out var contents)) {
                contents = [];
                planned[name] = contents;
            }

            contents.Add(asset);
            bundleOf[asset.Address] = name;
        }

        var built = ImmutableArray.CreateBuilder<BuiltBundle>(planned.Count);
        var described = ImmutableArray.CreateBuilder<CatalogBundle>(planned.Count);

        foreach (var (name, contents) in planned) {
            var group = policies[contents[0].Group];
            var writer = new BundleWriter();

            foreach (var asset in contents) {
                if (chunks.TryRead(asset.Id, out var blob)) {
                    using (blob) {
                        writer.Add(asset.Id, blob.Bytes.ToArray());
                    }

                    continue;
                }

                diagnostics.Add(
                    new(
                        ImportSeverity.Error,
                        $"'{asset.Address}' says its content is chunk {asset.Id}, and no such chunk was imported. "
                        + "The catalog will name it and loading it will fail."
                    )
                );
            }

            var bytes = writer.Build();
            var hash = ContentHash.Compute(bytes);

            var fileName = group.BundleNaming == BundleNaming.FilenameHash
                ? $"{name}_{hash.ToString()[..16]}.bundle"
                : $"{name}.bundle";

            built.Add(new(name, fileName, bytes, hash));

            described.Add(
                new(
                    name,
                    group.LoadPath == ContentProvider.Remote ? Join(group.RemoteUrl, fileName) : string.Empty,
                    hash,
                    bytes.Length,
                    Crc32.HashToUInt32(bytes),
                    group.Compression,
                    []
                )
            );
        }

        var entries = ordered
            .Where(asset => bundleOf.ContainsKey(asset.Address))
            .Select(asset => new CatalogEntry(
                    asset.Address,
                    asset.Id,
                    bundleOf[asset.Address],
                    policies[asset.Group].LoadPath,
                    asset.Dependencies.IsDefault ? [] : asset.Dependencies,
                    asset.Labels.IsDefault ? [] : asset.Labels,
                    0
                )
            );

        var catalog = new ContentCatalog(CatalogVersion, BuildHash(built), Target, entries, described);
        return new(catalog, built.ToImmutable(), diagnostics.ToImmutable());
    }

    /// <summary>Which bundle an asset belongs in, from its group's packing policy.</summary>
    static string BundleNameFor(
        AddressableGroup group,
        BuildableAsset asset,
        ImmutableArray<ImportDiagnostic>.Builder diagnostics
    ) {
        switch (group.Packing) {
            case BundlePacking.PackSeparately:
                // Hashed rather than the address itself: an address contains slashes and may contain
                // anything else a filesystem dislikes, and a bundle name becomes a file name.
                return $"{group.Name}_{ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(asset.Address))
                    .ToString()[..12]}";

            case BundlePacking.PackTogetherByLabel: {
                if (asset.Labels.IsDefaultOrEmpty) {
                    return group.Name;
                }

                var labels = asset.Labels.Sort(StringComparer.Ordinal);

                if (labels.Length > 1) {
                    diagnostics.Add(
                        new(
                            ImportSeverity.Information,
                            $"'{asset.Address}' carries {labels.Length} labels and its group packs by label, so it "
                            + $"is in '{labels[0]}' — the first alphabetically. Only one bundle can hold it."
                        )
                    );
                }

                return $"{group.Name}_{labels[0]}";
            }

            default:
                return group.Name;
        }
    }

    /// <summary>
    ///     The build's own hash: every bundle's hash, in name order. Two builds that produced the
    ///     same bundles have the same one, which is how a runtime decides whether a fetched catalog
    ///     describes content it already has.
    /// </summary>
    static ObjectId BuildHash(ImmutableArray<BuiltBundle>.Builder bundles) {
        var hashing = new System.IO.Hashing.XxHash128();
        Span<byte> buffer = stackalloc byte[ObjectId.SizeInBytes];

        foreach (var bundle in bundles.OrderBy(bundle => bundle.Name, StringComparer.Ordinal)) {
            bundle.Hash.WriteTo(buffer);
            hashing.Append(buffer);
        }

        hashing.GetHashAndReset(buffer);
        return ObjectId.FromBytes(buffer);
    }

    static string Join(string prefix, string fileName) =>
        prefix.Length == 0 ? fileName
        : prefix.EndsWith('/') ? prefix + fileName
        : $"{prefix}/{fileName}";
}
