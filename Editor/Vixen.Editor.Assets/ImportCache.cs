// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Editor.Assets;

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
    IReadOnlyList<ObjectId> Artifacts,
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
/// </remarks>
public sealed class ImportCache {
    const string Header = "vixen-import-cache 1";

    readonly Dictionary<AssetId, ImportRecord> byAsset = [];

    /// <summary>How many assets have a record.</summary>
    public int Count => byAsset.Count;

    /// <summary>Every record.</summary>
    public IReadOnlyCollection<ImportRecord> Records => byAsset.Values;

    /// <summary>Looks up what an asset's last import produced.</summary>
    /// <param name="asset">The asset.</param>
    /// <param name="record">What it produced.</param>
    /// <returns>Whether it has ever been imported.</returns>
    public bool TryGet(AssetId asset, out ImportRecord? record) => byAsset.TryGetValue(asset, out record);

    /// <summary>Records an import.</summary>
    /// <param name="record">What it produced.</param>
    public void Set(ImportRecord record) {
        ArgumentNullException.ThrowIfNull(record);
        byAsset[record.Asset] = record;
    }

    /// <summary>Forgets an asset, so its next import runs.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns>Whether it had a record.</returns>
    public bool Forget(AssetId asset) => byAsset.Remove(asset);

    /// <summary>Writes the cache.</summary>
    /// <param name="path">Where to put it.</param>
    /// <exception cref="ArgumentException">A dependency path contains a tab.</exception>
    public void Save(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var writer = new StreamWriter(path);
        writer.NewLine = "\n";
        writer.WriteLine(Header);

        foreach (var record in byAsset.Values.OrderBy(record => record.Asset)) {
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
                    string.Join(',', record.Artifacts),
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

        byAsset.Clear();

        while (reader.ReadLine() is { } line) {
            var parts = line.Split('\t');

            if (parts.Length < 6
                || !AssetId.TryParse(parts[0], out var asset)
                || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var version)
                || !ObjectId.TryParse(parts[3], out var key)) {
                continue;
            }

            Set(
                new(
                    asset,
                    parts[1],
                    version,
                    new(key),
                    [.. Split(parts[4]).Select(text => ObjectId.Parse(text))],
                    [.. parts[6..]],
                    [.. Split(parts[5]).Select(text => AssetId.Parse(text))]
                )
            );
        }

        return true;
    }

    static IEnumerable<string> Split(string joined) =>
        joined.Length == 0 ? [] : joined.Split(',');
}
