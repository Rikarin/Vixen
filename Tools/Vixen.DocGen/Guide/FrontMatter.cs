// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.DocGen.Guide;

/// <summary>A guide page's front matter — docs/plan/25 § 4.2.</summary>
/// <param name="Title">What the page is called.</param>
/// <param name="Slug">Its URL under the guide, e.g. <c>ecs/queries</c>.</param>
/// <param name="Kind"><c>guide</c>, <c>tutorial</c>, <c>concept</c> or <c>reference</c>.</param>
/// <param name="Area">The subsystem it belongs to.</param>
/// <param name="Summary">One sentence, used in search and on index cards.</param>
/// <param name="Api">The documentation ids this page is the prose for.</param>
/// <param name="Tags">Search facets.</param>
/// <param name="Since">The version it first described.</param>
/// <param name="Status"><c>stable</c>, <c>preview</c> or <c>deprecated</c>.</param>
/// <param name="Related">Slugs of pages a reader should go to next.</param>
sealed record FrontMatter(
    string Title,
    string Slug,
    string Kind,
    string Area,
    string Summary,
    IReadOnlyList<string> Api,
    IReadOnlyList<string> Tags,
    string? Since,
    string Status,
    IReadOnlyList<string> Related
) {
    static readonly string[] Kinds = ["guide", "tutorial", "concept", "reference"];
    static readonly string[] Statuses = ["stable", "preview", "deprecated"];

    /// <summary>
    ///     Splits a page into its front matter and its body, and says what is wrong rather than
    ///     throwing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Hand-parsed, and the subset is deliberately small: scalars, <c>[a, b]</c> lists and
    ///         <c>- item</c> lists. A YAML parser would accept anchors, merge keys and block scalars
    ///         that nothing here would know what to do with — and the schema is checked either way,
    ///         so the parser's generosity would only widen the set of files that parse and then fail.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every field is required except <c>since</c>.</b> A page with no <c>summary</c>
    ///         has no search result and no index card, and a page with no <c>api</c> is prose the
    ///         graph cannot join to a symbol — both are the failure this schema exists to make loud.
    ///     </para>
    /// </remarks>
    public static (FrontMatter? Page, string Body, IReadOnlyList<string> Errors) Parse(string text) {
        var errors = new List<string>();
        var normalised = text.ReplaceLineEndings("\n");

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal)) {
            return (null, text, ["the page does not start with a `---` front-matter block"]);
        }

        var end = normalised.IndexOf("\n---", 3, StringComparison.Ordinal);

        if (end < 0) {
            return (null, text, ["the front-matter block is not closed with `---`"]);
        }

        var block = normalised[4..end];
        var body = normalised[(end + 4)..].TrimStart('\n');
        var fields = ReadFields(block, errors);

        string Scalar(string key) {
            if (fields.TryGetValue(key, out var value) && value.Count == 1 && value[0].Length > 0) {
                return value[0];
            }

            errors.Add($"`{key}` is missing or empty");

            return string.Empty;
        }

        IReadOnlyList<string> List(string key) =>
            fields.TryGetValue(key, out var value) ? value : [];

        var kind = Scalar("kind");
        var status = fields.TryGetValue("status", out var declared) && declared.Count == 1
            ? declared[0]
            : "stable";

        if (kind.Length > 0 && !Kinds.Contains(kind, StringComparer.Ordinal)) {
            errors.Add($"`kind: {kind}` is not one of {string.Join(", ", Kinds)}");
        }

        if (!Statuses.Contains(status, StringComparer.Ordinal)) {
            errors.Add($"`status: {status}` is not one of {string.Join(", ", Statuses)}");
        }

        var api = List("api");

        if (api.Count == 0) {
            errors.Add("`api` names no symbol — a page the graph cannot join to anything is prose nobody finds");
        }

        var page = new FrontMatter(
            Scalar("title"),
            Scalar("slug"),
            kind,
            Scalar("area"),
            Scalar("summary"),
            api,
            List("tags"),
            fields.TryGetValue("since", out var since) && since.Count == 1 ? since[0] : null,
            status,
            List("related"));

        return (errors.Count > 0 ? null : page, body, errors);
    }

    static Dictionary<string, List<string>> ReadFields(string block, List<string> errors) {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;

        foreach (var raw in block.Split('\n')) {
            var line = raw.TrimEnd();

            if (line.Length == 0 || line.TrimStart().StartsWith('#')) {
                continue;
            }

            // A `- item` continues the key above it.
            if (line.StartsWith("  - ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal)) {
                if (current is null) {
                    errors.Add($"`{line.Trim()}` is a list item with no key above it");

                    continue;
                }

                fields[current].Add(Unquote(line[(line.IndexOf("- ", StringComparison.Ordinal) + 2)..]));

                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0) {
                errors.Add($"`{line.Trim()}` is not `key: value`");

                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            current = key;
            fields[key] = [];

            if (value.Length == 0) {
                continue;
            }

            if (value.StartsWith('[') && value.EndsWith(']')) {
                fields[key].AddRange(value[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Unquote));

                continue;
            }

            fields[key].Add(Unquote(value));
        }

        return fields;
    }

    static string Unquote(string value) {
        var trimmed = value.Trim();

        return trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '"' or '\''
            ? trimmed[1..^1]
            : trimmed;
    }
}
