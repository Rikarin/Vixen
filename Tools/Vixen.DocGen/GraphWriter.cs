// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vixen.DocGen;

/// <summary>Writes the graph the site reads — docs/plan/25 § 2.5 and § 8.1.</summary>
/// <remarks>
///     Two tiers, because they are loaded at different times. <c>graph.json</c> is the index — every
///     type, enough of each to render a nav tree, a breadcrumb and a search result — and the site
///     holds all of it. <c>pages/&lt;namespace&gt;.json</c> carries the detail, and a route loads one.
/// </remarks>
sealed class GraphWriter(int chunkBudgetBytes = 256 * 1024) {
    static readonly JsonSerializerOptions Options = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        WriteIndented = false
    };

    /// <summary>What one write produced, for the summary the target prints.</summary>
    /// <param name="IndexBytes">Size of the index tier.</param>
    /// <param name="PageBytes">Total size of the page tier.</param>
    /// <param name="Chunks">How many page files were written.</param>
    /// <param name="SplitChunks">How many namespaces had to be split by the budget.</param>
    public sealed record Written(long IndexBytes, long PageBytes, int Chunks, int SplitChunks);

    public Written Write(DocGraph graph, string outputDirectory) {
        Directory.CreateDirectory(outputDirectory);

        var pagesDirectory = Path.Combine(outputDirectory, "pages");

        if (Directory.Exists(pagesDirectory)) {
            Directory.Delete(pagesDirectory, recursive: true);
        }

        Directory.CreateDirectory(pagesDirectory);

        AssertSlugsAreUnique(graph.Nodes);

        var indexPath = Path.Combine(outputDirectory, "graph.json");

        File.WriteAllText(indexPath, JsonSerializer.Serialize(new {
            graph.Solution,
            graph.Configuration,
            graph.Commit,
            graph.ProjectCount,
            graph.GeneratedDocumentCount,
            Namespaces = graph.Nodes
                .GroupBy(node => node.Namespace, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new {
                    Name = group.Key,
                    Slug = Slugs.ForNamespace(group.Key),
                    Areas = group.Select(node => node.Area).Distinct().OrderBy(area => area, StringComparer.Ordinal),
                    Count = group.Count()
                }),
            Nodes = graph.Nodes.Select(node => new {
                node.Id,
                Kind = Taxonomy.Slug(node.Kind),
                node.Name,
                node.QualifiedName,
                node.Namespace,
                node.Assembly,
                node.Area,
                node.Slug,
                node.Summary,
                node.Obsolete,
                node.IsGenerated,
                node.IsPackable,
                Members = node.Members.Count,
                Source = node.Source?.Url
            })
        }, Options));

        var chunks = 0;
        var split = 0;
        long pageBytes = 0;

        foreach (var group in graph.Nodes
            .GroupBy(node => node.Namespace, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)) {
            // § 8.1, forced by the spike: per namespace, because per type the median chunk is 428
            // bytes and the chunking would cost more than the content — but the largest namespace is
            // already 92 kB in the index tier alone, so a group past the budget is split rather than
            // shipped whole.
            var parts = Split(group.ToList(), out var wasSplit);

            if (wasSplit) {
                split++;
            }

            for (var index = 0; index < parts.Count; index++) {
                var suffix = index == 0 ? string.Empty : $".{index}";
                var path = Path.Combine(pagesDirectory, $"{Slugs.ForNamespace(group.Key)}{suffix}.json");

                File.WriteAllText(path, JsonSerializer.Serialize(parts[index], Options));
                pageBytes += new FileInfo(path).Length;
                chunks++;
            }
        }

        return new Written(new FileInfo(indexPath).Length, pageBytes, chunks, split);
    }

    /// <summary>Splits a namespace's nodes into parts that each fit the budget.</summary>
    /// <remarks>
    ///     One type never splits, however large: a page is the unit a route loads, and half a type is
    ///     not a page. A namespace whose single type exceeds the budget is a fact worth seeing in the
    ///     summary rather than an error.
    /// </remarks>
    List<List<DocNode>> Split(List<DocNode> nodes, out bool wasSplit) {
        var parts = new List<List<DocNode>>();
        var current = new List<DocNode>();
        long currentBytes = 0;

        foreach (var node in nodes.OrderBy(node => node.Name, StringComparer.Ordinal)) {
            var bytes = JsonSerializer.Serialize(node, Options).Length;

            if (current.Count > 0 && currentBytes + bytes > chunkBudgetBytes) {
                parts.Add(current);
                current = [];
                currentBytes = 0;
            }

            current.Add(node);
            currentBytes += bytes;
        }

        if (current.Count > 0) {
            parts.Add(current);
        }

        wasSplit = parts.Count > 1;

        return parts;
    }

    /// <summary>
    ///     Two nodes that lowercase to the same path would serve one page and hide the other.
    /// </summary>
    /// <remarks>
    ///     Cloudflare's asset paths are case-sensitive and a Windows checkout is not, so the slug is
    ///     lowercased (§ 2.2) — which makes a collision possible in a way the ids themselves never
    ///     were. Silently picking one is how a type disappears from the site with nothing to notice
    ///     it by, so the emitter asserts instead.
    /// </remarks>
    static void AssertSlugsAreUnique(IReadOnlyList<DocNode> nodes) {
        var collisions = nodes
            .GroupBy(node => node.Slug, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();

        if (collisions.Count == 0) {
            return;
        }

        var detail = string.Join(Environment.NewLine, collisions.Take(10).Select(group =>
            $"  {group.Key}: " + string.Join(", ", group.Select(node => $"{node.Id} in {node.Assembly}"))));

        throw new DocGenException(
            $"{collisions.Count} slug collisions:{Environment.NewLine}{detail}{Environment.NewLine}"
            + "Two pages at one URL means one of them is not on the site."
        );
    }
}
