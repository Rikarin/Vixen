// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Vixen.DocGen;

/// <summary>A release as the site reads it — the record, what it is diffed against, and the rows.</summary>
sealed record ReleaseDetail {
    public required ReleaseRecord Release { get; init; }

    /// <summary>Not required: the first release has nothing before it, and the field is then absent.</summary>
    public string? Previous { get; init; }

    public required IReadOnlyList<Change> Changes { get; init; }

    /// <summary>Counts, so the index can be rendered without loading every release's rows.</summary>
    public required IReadOnlyDictionary<string, int> Counts { get; init; }
}

/// <summary>
///     The release tables — docs/plan/25 § 6.2, written twice on purpose.
/// </summary>
/// <remarks>
///     <para>
///         <b>Committed</b> as <c>docs/api-history/&lt;version&gt;/changes.json</c> and as a
///         <c>CHANGELOG.md</c> section, because a release note is part of the release and must not
///         depend on a tool being re-runnable two years later. <b>Copied</b> into the graph output,
///         because the site prerenders its release pages from what is committed rather than
///         recomputing them — a rebuilt table that disagreed with the published one would be worse
///         than no table.
///     </para>
/// </remarks>
static class Releases {
    /// <summary>Indented and kebab-cased: this one is committed, so it is read by people too.</summary>
    static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = {
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower)
        }
    };

    /// <summary>Writes one release's table, everywhere it belongs.</summary>
    public static void Write(
        string repositoryRoot,
        string outputDirectory,
        ReleaseRecord record,
        string? previousVersion,
        IReadOnlyList<Change> changes
    ) {
        ReleaseDetail detail = new() {
            Release = record,
            Previous = previousVersion,
            Changes = changes,
            // Kebab-cased like the `Kind` values themselves, so a caller can look a count up by the
            // same string it filters rows with.
            Counts = changes
                .GroupBy(change => JsonNamingPolicy.KebabCaseLower.ConvertName(change.Kind.ToString()))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
        };

        var path = DetailPath(repositoryRoot, record.Version);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(detail, Options) + "\n");

        WriteChangelog(
            repositoryRoot,
            record.Version,
            ReleaseDiff.Markdown(record.Version, previousVersion, record.Date, changes));

        WriteIndex(repositoryRoot, outputDirectory, History.Read(repositoryRoot));
    }

    /// <summary>Copies every committed release table into the graph output for the site.</summary>
    public static void WriteIndex(
        string repositoryRoot,
        string outputDirectory,
        IReadOnlyList<ReleaseRecord> releases
    ) {
        var directory = Path.Combine(outputDirectory, "releases");

        Directory.CreateDirectory(directory);

        var index = new List<object>();

        foreach (var release in releases) {
            var source = DetailPath(repositoryRoot, release.Version);

            if (!File.Exists(source)) {
                // An archived graph with no table: the store was hand-edited, or the release predates
                // the table. Listed anyway — the version exists, and saying so is more honest than a
                // gap in the switcher.
                index.Add(new { release.Version, release.Date, release.Types, release.Members, Breaking = 0, HasTable = false });

                continue;
            }

            File.Copy(source, Path.Combine(directory, $"{release.Version}.json"), overwrite: true);

            var detail = JsonSerializer.Deserialize<ReleaseDetail>(File.ReadAllText(source), Options);

            index.Add(new {
                release.Version,
                release.Date,
                release.Types,
                release.Members,
                Breaking = detail?.Changes.Count(change => change.IsBreaking) ?? 0,
                HasTable = true
            });
        }

        File.WriteAllText(
            Path.Combine(directory, "index.json"),
            JsonSerializer.Serialize(index, Options) + "\n");
    }

    /// <summary>
    ///     Inserts the section under the title, replacing the one for the same version if it is there.
    /// </summary>
    /// <remarks>
    ///     Newest first, because a changelog is read from the top, and rewritten in place rather than
    ///     appended so that re-running a release does not produce two tables for one tag.
    /// </remarks>
    static void WriteChangelog(string repositoryRoot, string version, string section) {
        var path = Path.Combine(repositoryRoot, "CHANGELOG.md");
        const string Header =
            """
            <!--
            SPDX-FileCopyrightText: Copyright (c) Rikarin
            SPDX-License-Identifier: Apache-2.0
            -->

            # Changelog

            Generated at each release by `nuke Release` from the documentation graph — the same table
            the site renders at `/docs/releases/<version>`, computed once so the two cannot disagree.
            See [docs/plan/25 § 6.2](docs/plan/25-documentation-generator-and-site.md#62-the-diff-is-generated-at-the-release-and-it-is-the-same-moment-as-the-api-fold).

            """;

        var existing = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : Header;
        var start = existing.IndexOf($"\n## {version} ", StringComparison.Ordinal);

        if (start >= 0) {
            var next = existing.IndexOf("\n## ", start + 1, StringComparison.Ordinal);

            existing = existing[..(start + 1)] + (next < 0 ? string.Empty : existing[(next + 1)..]);
        }

        var body = existing.TrimEnd('\n');
        var insertAt = body.IndexOf("\n## ", StringComparison.Ordinal);

        // Exactly one blank line between sections, whether this one is the first, the newest, or a
        // rewrite of something in the middle.
        var block = section.TrimEnd('\n') + "\n\n";

        File.WriteAllText(path, insertAt < 0
            ? body + "\n\n" + block
            : body[..(insertAt + 1)] + "\n" + block + body[(insertAt + 1)..].TrimStart('\n') + "\n");
    }

    static string DetailPath(string repositoryRoot, string version) =>
        Path.Combine(repositoryRoot, "docs", "api-history", version, "changes.json");
}
