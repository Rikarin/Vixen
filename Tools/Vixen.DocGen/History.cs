// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text.Json;

namespace Vixen.DocGen;

/// <summary>One archived release — a row of <c>docs/api-history/index.json</c>.</summary>
/// <param name="Version">The release, as the tag names it without the <c>v</c>.</param>
/// <param name="Date">ISO-8601, the day it was archived. Passed in, never read from the clock.</param>
/// <param name="Commit">What the graph was read from, so a page can link at the right lines.</param>
/// <param name="Types">Node count, for the release page's headline.</param>
/// <param name="Members">Member count, same.</param>
/// <param name="Bytes">The compressed archive's size, which is what the store costs.</param>
sealed record ReleaseRecord(
    string Version,
    string Date,
    string? Commit,
    int Types,
    int Members,
    long Bytes
);

/// <summary>
///     The version store — docs/plan/25 § 6.1.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/api-history/&lt;version&gt;/graph.json.br</c>, committed, plus an
///         <c>index.json</c> listing what is there. <b>Committed rather than rebuilt from tags</b>,
///         because rebuilding 0.1 in two years means restoring an old SDK, old native dependencies
///         and an old MSBuild, and the first release where that fails is the release where the
///         changelog quietly stops being generated.
///     </para>
///     <para>
///         The whole graph goes in, members and all — not the index tier. A diff that cannot see
///         members cannot say a parameter type changed, which is the most common breaking change
///         there is. Measured on this tree: 24 MB of JSON, <b>1.97 MB</b> after Brotli, which is the
///         cheapest insurance in [25].
///     </para>
/// </remarks>
static class History {
    public const string RelativeDirectory = "docs/api-history";

    static readonly JsonSerializerOptions IndexOptions = new() { WriteIndented = true };

    /// <summary>Every archived release, oldest first.</summary>
    public static IReadOnlyList<ReleaseRecord> Read(string repositoryRoot) {
        var path = IndexPath(repositoryRoot);

        if (!File.Exists(path)) {
            return [];
        }

        var releases = JsonSerializer.Deserialize<List<ReleaseRecord>>(File.ReadAllText(path), IndexOptions) ?? [];

        return [.. releases.OrderBy(release => release.Version, VersionOrder.Instance)];
    }

    /// <summary>Reads one archived graph back, or null when that version was never archived.</summary>
    public static DocGraph? ReadGraph(string repositoryRoot, string version) {
        var path = GraphPath(repositoryRoot, version);

        if (!File.Exists(path)) {
            return null;
        }

        using var file = File.OpenRead(path);
        using var brotli = new BrotliStream(file, CompressionMode.Decompress);

        return JsonSerializer.Deserialize<DocGraph>(brotli, GraphWriter.Options);
    }

    /// <summary>Archives a graph as a release and records it in the index.</summary>
    /// <param name="date">ISO-8601. The caller owns the clock, so a rerun writes the same file.</param>
    public static ReleaseRecord Write(string repositoryRoot, DocGraph graph, string version, string date) {
        var path = GraphPath(repositoryRoot, version);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var file = File.Create(path)) {
            // Quality 9 rather than 11: 1.97 MB against 1.83 MB, in half a second against twenty.
            // The store is committed, so what matters is that the number is small and the release
            // ritual does not stall on it.
            using var brotli = new BrotliStream(file, CompressionLevel.Optimal);

            JsonSerializer.Serialize(brotli, graph, GraphWriter.Options);
        }

        ReleaseRecord record = new(
            version,
            date,
            graph.Commit,
            graph.Nodes.Count,
            graph.Nodes.Sum(node => node.Members.Count),
            new FileInfo(path).Length);

        var releases = Read(repositoryRoot)
            .Where(other => !string.Equals(other.Version, version, StringComparison.Ordinal))
            .Append(record)
            .OrderBy(release => release.Version, VersionOrder.Instance)
            .ToList();

        File.WriteAllText(IndexPath(repositoryRoot), JsonSerializer.Serialize(releases, IndexOptions) + "\n");

        return record;
    }

    /// <summary>The newest release older than <paramref name="version" />, or null for the first one.</summary>
    public static ReleaseRecord? Previous(IReadOnlyList<ReleaseRecord> releases, string version) =>
        releases
            .Where(release => VersionOrder.Instance.Compare(release.Version, version) < 0)
            .OrderBy(release => release.Version, VersionOrder.Instance)
            .LastOrDefault();

    static string IndexPath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "docs", "api-history", "index.json");

    static string GraphPath(string repositoryRoot, string version) =>
        Path.Combine(repositoryRoot, "docs", "api-history", version, "graph.json.br");

    /// <summary>
    ///     Orders versions the way a release train does: numerically, and a prerelease before the
    ///     release it leads to.
    /// </summary>
    /// <remarks>
    ///     Ordinal string order would put <c>0.10.0</c> before <c>0.2.0</c> and <c>1.0.0</c> after
    ///     <c>1.0.0-rc.1</c>, and the second of those decides which archive a release is diffed
    ///     against — so it is worth the twenty lines.
    /// </remarks>
    sealed class VersionOrder : IComparer<string> {
        public static readonly VersionOrder Instance = new();

        public int Compare(string? left, string? right) {
            if (left is null || right is null) {
                return string.CompareOrdinal(left, right);
            }

            var (leftNumbers, leftTail) = Split(left);
            var (rightNumbers, rightTail) = Split(right);

            for (var index = 0; index < Math.Max(leftNumbers.Count, rightNumbers.Count); index++) {
                var order = At(leftNumbers, index).CompareTo(At(rightNumbers, index));

                if (order != 0) {
                    return order;
                }
            }

            return (leftTail, rightTail) switch {
                (null, null) => 0,
                (null, _) => 1, // 1.0.0 is newer than 1.0.0-rc.1
                (_, null) => -1,
                _ => string.CompareOrdinal(leftTail, rightTail)
            };
        }

        static int At(IReadOnlyList<int> numbers, int index) => index < numbers.Count ? numbers[index] : 0;

        static (IReadOnlyList<int> Numbers, string? Tail) Split(string version) {
            var dash = version.IndexOf('-', StringComparison.Ordinal);
            var head = dash < 0 ? version : version[..dash];
            var tail = dash < 0 ? null : version[(dash + 1)..];

            return ([.. head.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0)], tail);
        }
    }
}
