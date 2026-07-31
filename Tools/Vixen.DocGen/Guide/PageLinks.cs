// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Vixen.DocGen.Guide;

/// <summary>
///     The links between pages — the <b>Resolution</b> and <b>Orphans</b> rows of docs/plan/25 § Part 5.
/// </summary>
/// <remarks>
///     <para>
///         Two failures, one pass, because they are the same graph read in the two directions. A link
///         that resolves to nothing is a 404 a reader finds before the author does. A page nothing
///         links to is prose that was written and then lost — the failure mode of every documentation
///         tree that grows by adding files, and the one nobody notices because the file is right
///         there in the repository.
///     </para>
///     <para>
///         An index page is a root by definition: it is what the site's tree lists, so nothing has to
///         link to it. Everything else earns its place by being linked from prose or named in another
///         page's <c>related:</c>.
///     </para>
/// </remarks>
static partial class PageLinks {
    /// <summary>The site's fixed routes — www/src/app/app.routes.ts. A link to one of these resolves.</summary>
    static readonly string[] SiteRoutes = [
        "/", "/docs", "/docs/api", "/docs/components", "/docs/systems", "/docs/controls", "/docs/shaders",
        "/docs/nodes", "/docs/importers", "/docs/attributes", "/docs/diagnostics", "/docs/log-events"
    ];

    /// <summary>Markdown inline links, with the optional title the syntax allows.</summary>
    [GeneratedRegex(@"\]\((?<href>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex Link();

    /// <summary>Checks every link, and then every page's inbound count.</summary>
    /// <param name="pages">Every page that parsed.</param>
    /// <param name="nodeSlugs">Graph slugs, for <c>/docs/api/...</c> links.</param>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<GuidePage> pages,
        IReadOnlySet<string> nodeSlugs
    ) {
        var problems = new List<string>();
        var bySlug = pages.ToDictionary(page => page.Front.Slug, StringComparer.Ordinal);
        var byPath = pages.ToDictionary(page => page.Path, StringComparer.OrdinalIgnoreCase);
        var inbound = pages.ToDictionary(page => page.Front.Slug, _ => 0, StringComparer.Ordinal);

        void Reached(string slug, string from) {
            // A page linking to itself is not a page anything links to.
            if (!string.Equals(slug, from, StringComparison.Ordinal) && inbound.TryGetValue(slug, out var count)) {
                inbound[slug] = count + 1;
            }
        }

        foreach (var page in pages) {
            // `related:` is navigation, so it counts as a link — it is rendered as one.
            foreach (var related in page.Front.Related) {
                Reached(related, page.Front.Slug);
            }

            foreach (Match match in Link().Matches(page.Body)) {
                var href = match.Groups["href"].Value;
                var anchor = href.IndexOf('#', StringComparison.Ordinal);
                var target = anchor >= 0 ? href[..anchor] : href;

                if (target.Length == 0 || Uri.IsWellFormedUriString(target, UriKind.Absolute)) {
                    // A bare `#anchor`, or a link off the site. Neither is this pass's business:
                    // external links rot on someone else's schedule and would make the gate flaky.
                    continue;
                }

                var slug = Resolve(page, target, byPath);

                if (slug is not null) {
                    if (bySlug.ContainsKey(slug)) {
                        Reached(slug, page.Front.Slug);
                    } else {
                        problems.Add($"{page.Path}: `{href}` names no guide page");
                    }

                    continue;
                }

                if (target.StartsWith("/docs/api/", StringComparison.Ordinal)) {
                    if (!nodeSlugs.Contains(target["/docs/api/".Length..].TrimEnd('/'))) {
                        problems.Add($"{page.Path}: `{href}` names nothing the graph has");
                    }

                    continue;
                }

                if (target.StartsWith('/') && !SiteRoutes.Contains(target.TrimEnd('/'), StringComparer.Ordinal)) {
                    problems.Add($"{page.Path}: `{href}` is not a route the site serves");
                }
            }
        }

        foreach (var page in pages
            .Where(page => !IsIndex(page.Front.Slug) && inbound[page.Front.Slug] == 0)
            .OrderBy(page => page.Path, StringComparer.Ordinal)) {
            problems.Add($"{page.Path}: nothing links to `{page.Front.Slug}` — link it from its area's "
                + "index, or name it in a `related:` list");
        }

        return problems;
    }

    /// <summary>The guide slug a link points at, or null when it points somewhere else.</summary>
    static string? Resolve(GuidePage page, string target, IReadOnlyDictionary<string, GuidePage> byPath) {
        if (target.StartsWith("/docs/guide/", StringComparison.Ordinal)) {
            return target["/docs/guide/".Length..].TrimEnd('/');
        }

        if (!target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        // A relative link between two files in the tree, which is what the page looks like on GitHub
        // and is therefore how most of them get written. Normalised by hand rather than through
        // `Path.GetFullPath`, which would drag the host's separator and drive letter into a value
        // that has to compare equal to a repository-relative path on three operating systems.
        var segments = new List<string>(
            (Path.GetDirectoryName(page.Path) ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            switch (part) {
                case ".":
                    break;

                case ".." when segments.Count > 0:
                    segments.RemoveAt(segments.Count - 1);

                    break;

                default:
                    segments.Add(part);

                    break;
            }
        }

        var relative = string.Join('/', segments);

        // Resolved against the page's real path so that `../rendering/materials.md` lands where the
        // reader's click would, rather than being pattern-matched on the slug and quietly agreeing.
        return byPath.TryGetValue(relative, out var found) ? found.Front.Slug : relative;
    }

    static bool IsIndex(string slug) =>
        string.Equals(slug, "index", StringComparison.Ordinal)
        || slug.EndsWith("/index", StringComparison.Ordinal);
}
