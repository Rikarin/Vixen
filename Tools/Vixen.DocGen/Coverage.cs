// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.DocGen;

/// <summary>One line of <c>docs/DocsExempt.txt</c>.</summary>
/// <param name="Id">The documentation id the line excuses.</param>
/// <param name="Reason">Why it has no page yet. Never empty — the parser rejects that.</param>
/// <param name="Line">Where it is, so the failure can be clicked.</param>
sealed record Exemption(string Id, string Reason, int Line);

/// <summary>
///     The coverage gate — docs/plan/25 § Part 5.
/// </summary>
/// <remarks>
///     <para>
///         A public type has a guide page or it has a line in <c>docs/DocsExempt.txt</c> saying why it
///         does not. That is the whole rule, and it is the same shape as
///         <a href="../Vixen.ApiCheck/README.md">the PublicAPI baselines</a> for the same reason: a
///         reviewer can see the file grow in a diff, which is not true of a coverage percentage.
///     </para>
///     <para>
///         ⚠ <b>The file is seeded full and only ever shrinks.</b> Switched on for three and a half
///         thousand types at once the gate would block every merge for a quarter and be turned off
///         within a week — § Part 5 says so about itself. So every type that existed on the day the
///         gate landed carries <c>sweep-pending</c>, and the sweep deletes lines area by area. What
///         the gate actually catches from day one is a <em>new</em> public type with no page, which is
///         the half that stops the backlog from growing while the other half is being paid down.
///     </para>
///     <para>
///         The three failures are symmetric on purpose. A type with neither page nor line fails; a
///         line for a type that has since got a page fails, because otherwise the file never empties;
///         and a line naming a type the graph no longer has fails, because otherwise it fills with
///         excuses for code nobody can find.
///     </para>
/// </remarks>
static class Coverage {
    /// <summary>Repository-relative, and named here rather than in three call sites.</summary>
    public const string RelativePath = "docs/DocsExempt.txt";

    /// <summary>What <see cref="Seed" /> writes for every type that predates the gate.</summary>
    public const string SeedReason = "sweep-pending";

    const string Header =
        """
        # Public types with no guide page yet — docs/plan/25 § Part 5, seeded by P5 and emptied by P7.
        #
        # One line per type: its documentation id, whitespace, and the reason it has no page. The
        # reason is not optional; a line nobody can review is not a baseline, it is a mute button.
        #
        # `nuke CheckDocs` fails when
        #
        #   * a public type has neither a guide page (a page naming it in `api:`) nor a line here,
        #   * a line names a type that now has a page — delete the line in the same commit,
        #   * a line names a type the graph does not have — it was renamed or removed.
        #
        # So this file should only ever shrink. A new public type is written about in the commit that
        # adds it, which is the one commit where its author knows what it is for.
        #
        # When a merge brings in types that predate the gate — a long-lived branch, most often —
        # `./build.sh CheckDocs --update-exemptions` adds their lines, drops the ones that are no
        # longer owed, and keeps every reason already written. It is deliberately a command somebody
        # runs and not something the gate does for itself: a gate that wrote its own exemptions would
        # fail on nothing, forever.

        """;

    /// <summary>Reads the file, or reports the lines that are not lines.</summary>
    /// <returns>Every entry, and one error per malformed or duplicated line.</returns>
    public static (IReadOnlyList<Exemption> Entries, IReadOnlyList<string> Errors) Read(string repositoryRoot) {
        var path = Path.Combine(repositoryRoot, "docs", "DocsExempt.txt");
        var entries = new List<Exemption>();
        var errors = new List<string>();

        if (!File.Exists(path)) {
            // Not an error on its own. The absence is reported by the coverage check, once, rather
            // than as one failure per undocumented type.
            return (entries, errors);
        }

        var lines = File.ReadAllLines(path);

        for (var index = 0; index < lines.Length; index++) {
            var line = lines[index].Trim();
            var number = index + 1;

            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            var split = line.IndexOfAny([' ', '\t']);

            if (split < 0) {
                errors.Add($"{RelativePath}:{number}: `{line}` gives no reason");

                continue;
            }

            var id = line[..split];
            var reason = line[split..].Trim();

            if (reason.Length == 0) {
                errors.Add($"{RelativePath}:{number}: `{id}` gives no reason");

                continue;
            }

            entries.Add(new Exemption(id, reason, number));
        }

        foreach (var duplicate in entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)) {
            errors.Add($"{RelativePath}: `{duplicate.Key}` is exempted twice, on lines "
                + string.Join(", ", duplicate.Select(entry => entry.Line)));
        }

        return (entries, errors);
    }

    /// <summary>The gate itself.</summary>
    /// <param name="nodes">The graph, with <see cref="DocNode.Docs" /> already joined.</param>
    /// <param name="entries">What <see cref="Read" /> found.</param>
    /// <param name="limit">How many uncovered types to name before counting the rest.</param>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<DocNode> nodes,
        IReadOnlyList<Exemption> entries,
        int limit = 20
    ) {
        var problems = new List<string>();
        var exempt = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);

        var uncovered = nodes
            .Where(node => node.Docs is null && !exempt.ContainsKey(node.Id))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var node in uncovered.Take(limit)) {
            problems.Add($"{node.Id} has no guide page and no line in {RelativePath} "
                + $"({Taxonomy.Slug(node.Kind)}, {node.Assembly})");
        }

        if (uncovered.Count > limit) {
            problems.Add($"…and {uncovered.Count - limit} more types with neither a page nor an exemption. "
                + $"Write the page, or add the line with a reason.");
        }

        foreach (var entry in entries.OrderBy(entry => entry.Line)) {
            if (!byId.TryGetValue(entry.Id, out var node)) {
                problems.Add($"{RelativePath}:{entry.Line}: `{entry.Id}` names nothing the graph has — "
                    + "it was renamed or removed, and the line goes with it");

                continue;
            }

            if (node.Docs is not null) {
                problems.Add($"{RelativePath}:{entry.Line}: `{entry.Id}` is documented by "
                    + $"`{node.Docs}` now — delete this line");
            }
        }

        return problems;
    }

    /// <summary>What one <c>--update-exemptions</c> run did, for the summary it prints.</summary>
    /// <param name="Added">Types that had no line and now carry <see cref="SeedReason" />.</param>
    /// <param name="Documented">Lines dropped because the type now has a page.</param>
    /// <param name="Gone">Lines dropped because the graph no longer has the type.</param>
    /// <param name="Total">Lines in the file afterwards.</param>
    public sealed record Update(int Added, int Documented, int Gone, int Total);

    /// <summary>
    ///     The file as it should be: every uncovered type, each keeping the reason it already had.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Existing reasons are preserved, and that is the whole difference between this and
    ///         a re-seed.</b> The sweep replaces <c>sweep-pending</c> with reasons somebody wrote —
    ///         "the managed-component store is not part of the packable surface yet" — and a rewrite
    ///         that flattened those back to <c>sweep-pending</c> would delete the only review anybody
    ///         had done.
    ///     </para>
    ///     <para>
    ///         Sorted by id and nothing else, so the file diffs as the lines that were actually added
    ///         or removed rather than as a reordering.
    ///     </para>
    /// </remarks>
    public static (string Text, Update Summary) Rewrite(
        IReadOnlyList<DocNode> nodes,
        IReadOnlyList<Exemption> existing
    ) {
        var reasons = existing
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Reason, StringComparer.Ordinal);

        var builder = new StringBuilder(Header);
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var added = 0;

        var uncovered = nodes
            .Where(node => node.Docs is null)
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();

        // One column, not two: the ids run to sixty characters and an aligned second column would
        // reflow the whole file every time the longest name changes.
        foreach (var node in uncovered) {
            if (!reasons.TryGetValue(node.Id, out var reason)) {
                reason = SeedReason;
                added++;
            }

            builder.Append(node.Id).Append(' ').Append(reason).Append('\n');
        }

        var documented = existing.Count(entry =>
            byId.TryGetValue(entry.Id, out var node) && node.Docs is not null);

        var gone = existing.Count(entry => !byId.ContainsKey(entry.Id));

        return (builder.ToString(), new Update(added, documented, gone, uncovered.Count));
    }

    /// <summary>Rewrites the file where <see cref="Read" /> looks for it.</summary>
    public static Update Write(string repositoryRoot, IReadOnlyList<DocNode> nodes) {
        var path = Path.Combine(repositoryRoot, "docs", "DocsExempt.txt");
        var (text, summary) = Rewrite(nodes, Read(repositoryRoot).Entries);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);

        return summary;
    }
}
