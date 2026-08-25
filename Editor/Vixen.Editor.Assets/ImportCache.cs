// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Editor.Assets;

/// <summary>A chunk an import wrote, and which part of the asset it is.</summary>
/// <param name="SubAsset">
///     Which sub-asset it holds, or <see cref="SubAssetId.Main" /> for the asset's own object.
/// </param>
/// <param name="Id">The chunk.</param>
/// <remarks>
///     <b>The pair, not the id on its own.</b> A record that kept only the ids describes a model as
///     four chunks with nothing to say which is the mesh — so the build could not name them, and an
///     asset that imported to more than one thing had to be refused. Everything an address needs is
///     the sub-asset each chunk belongs to; the <i>name</i> comes from the sidecar, which the same
///     import wrote.
/// </remarks>
public readonly record struct StoredArtifact(SubAssetId SubAsset, ObjectId Id);

/// <summary>What one import produced, and what it depended on to produce it.</summary>
/// <param name="Asset">Which asset.</param>
/// <param name="Importer">Which importer ran.</param>
/// <param name="ImporterVersion">Which version of it.</param>
/// <param name="Key">The key that would have to match for this to be reusable.</param>
/// <param name="Artifacts">The chunks it wrote, in the order it wrote them.</param>
/// <param name="FileDependencies">Every file it declared, including its own source.</param>
/// <param name="AssetDependencies">Every other asset it declared.</param>
public sealed record ImportRecord(
    AssetId Asset,
    string Importer,
    int ImporterVersion,
    ArtifactKey Key,
    IReadOnlyList<StoredArtifact> Artifacts,
    IReadOnlyList<string> FileDependencies,
    IReadOnlyList<AssetId> AssetDependencies
);

/// <summary>What the last import of each asset produced.</summary>
/// <remarks>
///     <para>
///         <b>This is what resolves the cache key's chicken and egg.</b> The key includes the
///         artefact ids of everything the import depended on — and what it depends on is only known
///         once it has run. So the key is computed from the dependencies the <i>previous</i> import
///         declared: if they have not changed, neither has the answer, and the import is skipped; if
///         any of them has, the key differs and the import runs, declaring a fresh set as it goes.
///     </para>
///     <para>
///         A newly-declared dependency is therefore respected from the second import onwards, not the
///         first — which is exactly right, because the first import is the one that ran and produced
///         a correct artefact. Every incremental build system reaches this answer; it is worth
///         writing down because it looks like a bug until it is stated.
///     </para>
///     <para>
///         Tab-separated text, like the GUID index it sits next to. It lives in <c>Library/</c>, so
///         it is never committed and never has to be compatible with anything — and a hundred
///         thousand entries of it are read line by line rather than parsed as one document.
///     </para>
///     <para>
///         ⚠ <b>Every operation is safe to call from more than one thread at once</b>, because
///         <see cref="ImportPipeline.ImportAllAsync" /> runs N imports concurrently and each of them
///         both reads the cache — to price its dependencies — and writes its own record into it. A
///         <see cref="Dictionary{TKey,TValue}" /> read while another thread is resizing it does not
///         throw reliably; it returns wrong answers, which here means a cache key computed from a
///         dependency record that never existed. The lock is uncontended in the common case and the
///         work under it is a hash lookup.
///     </para>
/// </remarks>
public sealed class ImportCache {
    /// <summary>
    ///     Bumped when the encoding changes, which makes an older file unreadable rather than
    ///     misread — a cache that lives in <c>Library/</c> and describes work that can be redone has
    ///     no reason to carry a migration.
    /// </summary>
    const string Header = "vixen-import-cache 2";

    readonly Dictionary<AssetId, ImportRecord> byAsset = [];
    readonly Lock gate = new();

    /// <summary>How many assets have a record.</summary>
    public int Count {
        get {
            lock (gate) {
                return byAsset.Count;
            }
        }
    }

    /// <summary>Every record.</summary>
    /// <remarks>
    ///     ⚠ <b>A copy, not the live collection.</b> The dictionary's own <c>Values</c> view faults
    ///     if anything is added while it is being walked, and an import running beside a build plan
    ///     is exactly that. A snapshot of a few thousand references costs nothing next to what the
    ///     callers do with it.
    /// </remarks>
    public IReadOnlyCollection<ImportRecord> Records {
        get {
            lock (gate) {
                return [.. byAsset.Values];
            }
        }
    }

    /// <summary>Looks up what an asset's last import produced.</summary>
    /// <param name="asset">The asset.</param>
    /// <param name="record">What it produced.</param>
    /// <returns>Whether it has ever been imported.</returns>
    public bool TryGet(AssetId asset, out ImportRecord? record) {
        lock (gate) {
            return byAsset.TryGetValue(asset, out record);
        }
    }

    /// <summary>Records an import.</summary>
    /// <param name="record">What it produced.</param>
    public void Set(ImportRecord record) {
        ArgumentNullException.ThrowIfNull(record);

        lock (gate) {
            byAsset[record.Asset] = record;
        }
    }

    /// <summary>Forgets an asset, so its next import runs.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns>Whether it had a record.</returns>
    public bool Forget(AssetId asset) {
        lock (gate) {
            return byAsset.Remove(asset);
        }
    }

    /// <summary>Writes the cache.</summary>
    /// <param name="path">Where to put it.</param>
    /// <exception cref="ArgumentException">A dependency path contains a tab.</exception>
    public void Save(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var writer = new StreamWriter(path);
        writer.NewLine = "\n";
        writer.WriteLine(Header);

        foreach (var record in Records.OrderBy(record => record.Asset)) {
            foreach (var dependency in record.FileDependencies) {
                if (dependency.Contains('\t', StringComparison.Ordinal)) {
                    throw new ArgumentException(
                        $"'{dependency}' contains a tab, which this file cannot represent. Rename it.",
                        nameof(path)
                    );
                }
            }

            writer.WriteLine(
                string.Join(
                    '\t',
                    record.Asset,
                    record.Importer,
                    record.ImporterVersion.ToString(CultureInfo.InvariantCulture),
                    record.Key.Value,
                    string.Join(',', record.Artifacts.Select(artifact => $"{artifact.SubAsset}:{artifact.Id}")),
                    string.Join(',', record.AssetDependencies),
                    string.Join('\t', record.FileDependencies)
                )
            );
        }
    }

    /// <summary>Reads the cache back, if it is there.</summary>
    /// <param name="path">Where it is.</param>
    /// <returns>Whether it was.</returns>
    public bool TryLoad(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path)) {
            return false;
        }

        using var reader = new StreamReader(path);

        if (reader.ReadLine() != Header) {
            return false;
        }

        lock (gate) {
            byAsset.Clear();
        }

        while (reader.ReadLine() is { } line) {
            var parts = line.Split('\t');

            if (parts.Length < 6
                || !AssetId.TryParse(parts[0], out var asset)
                || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var version)
                || !ObjectId.TryParse(parts[3], out var key)
                || TryReadArtifacts(parts[4]) is not { } artifacts
                || TryReadAssets(parts[5]) is not { } dependencies) {
                // A line that does not parse is dropped rather than thrown on. This file is a
                // cache in Library/ that a truncated write or a killed editor can leave malformed,
                // and the cost of not understanding one line of it is re-importing one asset.
                continue;
            }

            Set(new(asset, parts[1], version, new(key), artifacts, [.. parts[6..]], dependencies));
        }

        return true;
    }

    /// <summary>Reads the artefact list, or <see langword="null" /> if any of it is malformed.</summary>
    static List<StoredArtifact>? TryReadArtifacts(string joined) {
        var artifacts = new List<StoredArtifact>();

        foreach (var text in Split(joined)) {
            var separator = text.IndexOf(':');

            if (separator < 0
                || !SubAssetId.TryParse(text.AsSpan(..separator), out var subAsset)
                || !ObjectId.TryParse(text.AsSpan((separator + 1)..), out var id)) {
                return null;
            }

            artifacts.Add(new(subAsset, id));
        }

        return artifacts;
    }

    /// <summary>Reads the asset-dependency list, or <see langword="null" /> if any of it is malformed.</summary>
    static List<AssetId>? TryReadAssets(string joined) {
        var assets = new List<AssetId>();

        foreach (var text in Split(joined)) {
            if (!AssetId.TryParse(text, out var asset)) {
                return null;
            }

            assets.Add(asset);
        }

        return assets;
    }

    static IEnumerable<string> Split(string joined) =>
        joined.Length == 0 ? [] : joined.Split(',');
}
