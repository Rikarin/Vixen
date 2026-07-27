// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Collections.Concurrent;
using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>Who points at what, so that "what breaks if I delete this" has an answer.</summary>
/// <remarks>
///     <para>
///         <b>A textual scan for <c>vx:</c>, not a parse.</b> That is sound rather than lazy, and it
///         is sound <i>because of how the reference format was chosen</i>: doc 08 picked a single
///         prefixed scalar over Unity's three-key flow mapping partly so that
///         <c>rg 'vx:9e8a44c9'</c> finds every referrer. This is that grep, done once over the
///         project and kept.
///     </para>
///     <para>
///         Parsing instead would mean binding every scene, material and prefab in the project — the
///         expensive half of opening one — to answer a question that is asked about one asset at a
///         time. It would also fail on exactly the files most likely to matter: an asset whose
///         importer has been uninstalled cannot be bound, but it can still be read, and "which of my
///         scenes still references this" is the question you ask about that asset.
///     </para>
///     <para>
///         What a scan can do that a parse cannot is find a reference in a comment or a string. That
///         is a false positive in a report nobody is harmed by, and the alternative — a missed
///         reference in a "safe to delete" answer — is a corrupted project.
///     </para>
/// </remarks>
public sealed class ReferenceIndex {
    static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    readonly Dictionary<AssetId, HashSet<AssetId>> referrers = [];
    readonly Dictionary<AssetId, HashSet<AssetReference>> references = [];

    /// <summary>How many assets reference at least one other.</summary>
    public int Count => references.Count;

    /// <summary>Builds the index over everything in a database.</summary>
    /// <param name="database">The database.</param>
    /// <remarks>
    ///     Every asset is read, including <c>.meta</c> files: a model importer's
    ///     <c>materialMapping</c> holds references, so a sidecar is as much a referrer as a scene.
    ///     Binary assets are read too and simply yield nothing — a texture contains no <c>vx:</c>
    ///     followed by thirty-two hex digits, and checking a file's extension against a list of
    ///     "things that might reference" is a list that goes stale the first time somebody adds a
    ///     format.
    /// </remarks>
    public void Build(AssetDatabase database) {
        ArgumentNullException.ThrowIfNull(database);
        referrers.Clear();
        references.Clear();

        var found = new ConcurrentBag<(AssetId From, AssetReference To)>();

        Parallel.ForEach(database.Entries, entry => {
                if (entry.IsFolder) {
                    return;
                }

                foreach (var reference in ScanFile(database.Paths.Absolute(entry.Path))) {
                    found.Add((entry.Guid, reference));
                }

                // The sidecar is scanned as part of the asset it belongs to, so a reference written
                // in import settings is attributed to the asset rather than to a file nobody thinks
                // of as one.
                foreach (var reference in ScanFile(Vixen.Core.Yaml.Meta.AssetMetaFile.PathFor(database.Paths.Absolute(entry.Path)))) {
                    found.Add((entry.Guid, reference));
                }
            }
        );

        foreach (var (from, to) in found) {
            if (to.IsNull || to.Asset == from) {
                continue;
            }

            (references.TryGetValue(from, out var outgoing) ? outgoing : references[from] = []).Add(to);
            (referrers.TryGetValue(to.Asset, out var incoming) ? incoming : referrers[to.Asset] = []).Add(from);
        }
    }

    /// <summary>Everything that points at an asset.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns>The referrers, or an empty set.</returns>
    public IReadOnlyCollection<AssetId> ReferrersOf(AssetId asset) =>
        referrers.TryGetValue(asset, out var found) ? found : [];

    /// <summary>Everything an asset points at.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns>The references, or an empty set.</returns>
    public IReadOnlyCollection<AssetReference> ReferencesFrom(AssetId asset) =>
        references.TryGetValue(asset, out var found) ? found : [];

    /// <summary>Whether anything would break if an asset were deleted.</summary>
    /// <param name="asset">The asset.</param>
    /// <returns>Whether anything points at it.</returns>
    public bool IsReferenced(AssetId asset) => referrers.ContainsKey(asset);

    /// <summary>Finds every reference in a piece of text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The references, in the order they appear.</returns>
    public static List<AssetReference> Scan(ReadOnlySpan<char> text) {
        var found = new List<AssetReference>();

        while (true) {
            var start = text.IndexOf(AssetReference.Prefix, StringComparison.Ordinal);

            if (start < 0) {
                return found;
            }

            text = text[(start + AssetReference.Prefix.Length)..];

            if (text.Length < AssetId.TextLength || !IsHex(text[..AssetId.TextLength])) {
                continue;
            }

            var length = AssetId.TextLength;

            if (text.Length >= AssetId.TextLength + 1 + SubAssetId.TextLength
                && text[AssetId.TextLength] == '#'
                && IsHex(text.Slice(AssetId.TextLength + 1, SubAssetId.TextLength))) {
                length += 1 + SubAssetId.TextLength;
            }

            if (AssetReference.TryParse(string.Concat(AssetReference.Prefix, text[..length]), out var reference)) {
                found.Add(reference);
            }

            text = text[length..];
        }
    }

    static List<AssetReference> ScanFile(string path) {
        try {
            return File.Exists(path) ? Scan(File.ReadAllText(path)) : [];
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static bool IsHex(ReadOnlySpan<char> span) => span.IndexOfAnyExcept(HexDigits) < 0;
}
