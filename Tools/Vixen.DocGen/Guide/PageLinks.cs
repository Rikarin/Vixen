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
    [GeneratedRegex(@"\]\((?<href>[^)\s]+)(?<title>\s+""[^""]*"")?\)")]
    private static partial Regex Link();

    /// <summary>What a link in a body turns out to point at.</summary>
    enum LinkKind {
        /// <summary>Off the site, or a shape this pass does not own.</summary>
        Ignored,

        /// <summary>A guide page, which the site serves at a route derived from its slug.</summary>
        Guide,

        /// <summary>Guide-shaped, and naming nothing — the 404 this pass exists to catch.</summary>
        Missing,

        /// <summary>A symbol page, checked against the graph rather than against the pages.</summary>
        Api,

        /// <summary>One of the site's fixed routes.</summary>
        Route
    }

    /// <summary>Where a link goes once the tree's conventions have been applied.</summary>
    /// <param name="Kind">Which of the five shapes it turned out to be.</param>
    /// <param name="Href">What the body should carry — rewritten for a guide page, as written otherwise.</param>
    /// <param name="Slug">The guide page or symbol it names; empty when it names neither.</param>
    /// <param name="Fragment">The anchor it lands on, without the <c>#</c>; empty when it has none.</param>
    readonly record struct Destination(LinkKind Kind, string Href, string Slug, string Fragment);

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
                var destination = Where(page, href, byPath, bySlug);

                switch (destination.Kind) {
                    case LinkKind.Guide:
                        Reached(destination.Slug, page.Front.Slug);

                        // ⚠ The anchor too, because a heading is renamed far more often than a page
                        // is, and a link that lands on the right page at the top of it looks like it
                        // worked.
                        if (destination.Fragment.Length > 0
                            && !bySlug[destination.Slug].Headings.Any(heading =>
                                string.Equals(heading.Id, destination.Fragment, StringComparison.Ordinal))) {
                            problems.Add($"{page.Path}: `{href}` names no heading on that page");
                        }

                        break;

                    case LinkKind.Missing:
                        problems.Add($"{page.Path}: `{href}` names no guide page");

                        break;

                    case LinkKind.Api when !nodeSlugs.Contains(destination.Slug):
                        problems.Add($"{page.Path}: `{href}` names nothing the graph has");

                        break;

                    case LinkKind.Route when !SiteRoutes.Contains(destination.Slug, StringComparer.Ordinal):
                        problems.Add($"{page.Path}: `{href}` is not a route the site serves");

                        break;
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

    /// <summary>
    ///     The same pages, with every link the site has to serve rewritten to the URL it serves it at.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The bodies as written do not work in a browser</b>, and nothing counted it: 558 links
    ///     are <c>queries.md</c>, which is what the page looks like on GitHub and therefore how they
    ///     get written; 191 are a bare slug; 12 are a bare <c>#anchor</c>, which resolves against the
    ///     application's <c>&lt;base href="/"&gt;</c> and lands on the site root. The resolution this
    ///     file already does for <see cref="Check" /> is the same resolution, so it is done once and
    ///     the answer is written into the body rather than checked and thrown away.
    /// </remarks>
    public static IReadOnlyList<GuidePage> WithSiteLinks(IReadOnlyList<GuidePage> pages) {
        var bySlug = pages.ToDictionary(page => page.Front.Slug, StringComparer.Ordinal);
        var byPath = pages.ToDictionary(page => page.Path, StringComparer.OrdinalIgnoreCase);

        return [
            .. pages.Select(page => page with {
                Body = Link().Replace(page.Body, match => {
                    var destination = Where(page, match.Groups["href"].Value, byPath, bySlug);

                    // Only a link that resolves: an unresolved one keeps what its author wrote, so
                    // the message `Check` prints names the string in the file.
                    return destination.Kind == LinkKind.Guide
                        ? $"]({destination.Href}{match.Groups["title"].Value})"
                        : match.Value;
                })
            })
        ];
    }

    /// <summary>Where one link points, by the conventions the tree is written in.</summary>
    /// <remarks>
    ///     Four relative shapes reach here and all four are in the tree today: <c>queries.md</c> and
    ///     <c>../rendering/materials.md</c> resolve against the page's real <em>path</em>, because
    ///     that is what makes them work on GitHub; <c>animation/move-sets</c> is a slug written
    ///     whole; and <c>writing-a-realm</c> is a sibling named by its last segment, which resolves
    ///     against the page's own area.
    /// </remarks>
    static Destination Where(
        GuidePage page,
        string href,
        IReadOnlyDictionary<string, GuidePage> byPath,
        IReadOnlyDictionary<string, GuidePage> bySlug
    ) {
        var hash = href.IndexOf('#', StringComparison.Ordinal);
        var target = hash >= 0 ? href[..hash] : href;
        var fragment = hash >= 0 ? href[(hash + 1)..] : string.Empty;

        Destination Guide(string slug) => new(
            LinkKind.Guide,
            fragment.Length == 0 ? $"/docs/guide/{slug}" : $"/docs/guide/{slug}#{fragment}",
            slug,
            fragment);

        // External links rot on someone else's schedule; a gate that watched them would be flaky.
        if (Uri.IsWellFormedUriString(target, UriKind.Absolute)) {
            return new Destination(LinkKind.Ignored, href, string.Empty, string.Empty);
        }

        // ⚠ A bare `#anchor` is not left alone. The site ships `<base href="/">`, against which a
        // bare fragment resolves to the site root rather than to a heading on the page it is on.
        if (target.Length == 0) {
            return Guide(page.Front.Slug);
        }

        if (target.StartsWith("/docs/api/", StringComparison.Ordinal)) {
            return new Destination(LinkKind.Api, href, target["/docs/api/".Length..].TrimEnd('/'), fragment);
        }

        if (target.StartsWith("/docs/guide/", StringComparison.Ordinal)) {
            var written = target["/docs/guide/".Length..].TrimEnd('/');

            return bySlug.ContainsKey(written)
                ? Guide(written)
                : new Destination(LinkKind.Missing, href, written, fragment);
        }

        if (target.StartsWith('/')) {
            return new Destination(LinkKind.Route, href, target.TrimEnd('/'), fragment);
        }

        if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) {
            // Resolved against the page's real path so that `../rendering/materials.md` lands where
            // the reader's click on GitHub would, rather than being pattern-matched on the slug and
            // quietly agreeing.
            var path = Normalize(Path.GetDirectoryName(page.Path) ?? string.Empty, target);

            return byPath.TryGetValue(path, out var found)
                ? Guide(found.Front.Slug)
                : new Destination(LinkKind.Missing, href, path, fragment);
        }

        // A slug, written whole or named from beside it.
        if (bySlug.ContainsKey(target)) {
            return Guide(target);
        }

        var sibling = Normalize(Directory(page.Front.Slug), target);

        return bySlug.ContainsKey(sibling)
            ? Guide(sibling)
            : new Destination(LinkKind.Missing, href, sibling, fragment);
    }

    /// <summary>Everything before the last <c>/</c>, which for a slug is its area.</summary>
    static string Directory(string path) {
        var separator = path.LastIndexOf('/');

        return separator < 0 ? string.Empty : path[..separator];
    }

    /// <summary>
    ///     <paramref name="target" /> resolved against <paramref name="from" />, dot segments and all.
    /// </summary>
    /// <remarks>
    ///     By hand rather than through <c>Path.GetFullPath</c>, which would drag the host's separator
    ///     and drive letter into a value that has to compare equal to a repository-relative path on
    ///     three operating systems.
    /// </remarks>
    static string Normalize(string from, string target) {
        var segments = new List<string>(
            from.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

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

        return string.Join('/', segments);
    }

    static bool IsIndex(string slug) =>
        string.Equals(slug, "index", StringComparison.Ordinal)
        || slug.EndsWith("/index", StringComparison.Ordinal);
}
