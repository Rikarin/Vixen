// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Vixen.DocGen.Guide;

/// <summary>One heading, for the in-page outline.</summary>
/// <param name="Id">The anchor.</param>
/// <param name="Text">What it reads as.</param>
/// <param name="Level">2 or 3; deeper headings are not an outline.</param>
sealed record DocHeading(string Id, string Text, int Level);

/// <summary>A compiled guide page.</summary>
sealed record GuidePage {
    public required FrontMatter Front { get; init; }

    /// <summary>Repository-relative path of the markdown, for the edit link.</summary>
    public required string Path { get; init; }

    /// <summary>The body with snippets resolved, ready for the site's markdown renderer.</summary>
    public required string Body { get; init; }

    public required IReadOnlyList<DocHeading> Headings { get; init; }
    public required IReadOnlyList<Example> Examples { get; init; }
    public DocSource? Source { get; init; }

    /// <summary>
    ///     Classified fences, keyed by the fence's position in the body — § 3.4.
    ///
    ///     Filled by <see cref="Highlighter" /> after the graph exists, because colouring a C# fence
    ///     properly means binding it, and binding it means the engine's compilations. Null for a page
    ///     whose fences are in a language nothing here reads.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<DocSpan>>>? Tokens { get; init; }
}

/// <summary>
///     Reads <c>docs/guide/**</c> — docs/plan/25 § 4.
/// </summary>
/// <remarks>
///     The five headings, the snippet includes and the fences are checked here; whether the fences
///     <em>compile</em> is <see cref="Examples" />' job, and whether the <c>api:</c> ids resolve is
///     the graph's. Splitting it that way keeps this a text pass with no compilation in it.
/// </remarks>
static partial class GuideReader {
    /// <summary>
    ///     The page contract of § 0, in order. Blunt on purpose: it is what turns "every feature must
    ///     say what it is and what it is for" from an instruction into a build failure.
    /// </summary>
    public static readonly string[] RequiredHeadings = [
        "What it is", "What it is for", "Using it", "Examples", "See also"
    ];

    [GeneratedRegex(@"^(#{2,6})\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\{\{\s*snippet\s+([^\s#]+)#([^\s}]+)\s*\}\}")]
    private static partial Regex SnippetInclude();

    [GeneratedRegex(@"^\s*//\s*#region\s+(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RegionStart();

    /// <summary>Reads every page under the guide root.</summary>
    /// <param name="repositoryRoot">The checkout.</param>
    /// <param name="links">For the edit link on each page.</param>
    /// <returns>The pages that are whole, and one error per page that is not.</returns>
    public static (IReadOnlyList<GuidePage> Pages, IReadOnlyList<string> Errors) Read(
        string repositoryRoot,
        SourceLinks links
    ) {
        var guide = System.IO.Path.Combine(repositoryRoot, "docs", "guide");
        var pages = new List<GuidePage>();
        var errors = new List<string>();

        if (!Directory.Exists(guide)) {
            return (pages, errors);
        }

        foreach (var path in Directory
            .EnumerateFiles(guide, "*.md", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)) {
            var relative = System.IO.Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            var (page, pageErrors) = ReadPage(repositoryRoot, path, relative, links);

            errors.AddRange(pageErrors.Select(error => $"{relative}: {error}"));

            if (page is not null) {
                pages.Add(page);
            }
        }

        var duplicates = pages
            .GroupBy(page => page.Front.Slug, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var duplicate in duplicates) {
            errors.Add($"two pages claim the slug `{duplicate.Key}`: "
                + string.Join(", ", duplicate.Select(page => page.Path)));
        }

        return (pages, errors);
    }

    static (GuidePage? Page, IReadOnlyList<string> Errors) ReadPage(
        string repositoryRoot,
        string path,
        string relative,
        SourceLinks links
    ) {
        var (front, body, errors) = FrontMatter.Parse(File.ReadAllText(path));

        if (front is null) {
            return (null, errors);
        }

        var problems = new List<string>(errors);
        var headings = Headings(body);

        problems.AddRange(ContractErrors(headings, body));

        var (resolved, snippetErrors) = ResolveSnippets(repositoryRoot, body);

        problems.AddRange(snippetErrors);

        var examples = Fences(relative, resolved, problems);

        return problems.Count > 0
            ? (null, problems)
            : (new GuidePage {
                Front = front,
                Path = relative,
                Body = resolved,
                Headings = headings,
                Examples = examples,
                Source = links.For(path)
            }, problems);
    }

    static List<DocHeading> Headings(string body) => [
        .. HeadingLine().Matches(body)
            .Where(match => match.Groups[1].Value.Length <= 3)
            .Select(match => new DocHeading(
                Anchor(match.Groups[2].Value),
                match.Groups[2].Value.Trim(),
                match.Groups[1].Value.Length))
    ];

    /// <summary>
    ///     The five headings, present, in order, and with something under each.
    /// </summary>
    static IEnumerable<string> ContractErrors(IReadOnlyList<DocHeading> headings, string body) {
        var top = headings.Where(heading => heading.Level == 2).Select(heading => heading.Text).ToList();

        foreach (var required in RequiredHeadings) {
            if (!top.Contains(required, StringComparer.Ordinal)) {
                yield return $"the page contract wants a `## {required}` heading";
            }
        }

        var order = RequiredHeadings
            .Select(required => top.IndexOf(required))
            .Where(index => index >= 0)
            .ToList();

        if (order.Count > 1 && !order.SequenceEqual(order.OrderBy(index => index))) {
            yield return "the contract's headings are out of order";
        }

        foreach (var required in RequiredHeadings.Where(required => top.Contains(required, StringComparer.Ordinal))) {
            if (SectionOf(body, required).Trim().Length == 0) {
                yield return $"`## {required}` has nothing under it";
            }
        }
    }

    static string SectionOf(string body, string heading) {
        var start = body.IndexOf($"## {heading}", StringComparison.Ordinal);

        if (start < 0) {
            return string.Empty;
        }

        start = body.IndexOf('\n', start);

        if (start < 0) {
            return string.Empty;
        }

        var next = body.IndexOf("\n## ", start, StringComparison.Ordinal);

        return next < 0 ? body[start..] : body[start..next];
    }

    /// <summary>
    ///     Replaces `{{ snippet path#region }}` with the region's real text, in a fence.
    /// </summary>
    /// <remarks>
    ///     The region is a `#region docs:name` in a file the solution compiles, so an example that
    ///     stops compiling stops being an example — which is the whole point of quoting one rather
    ///     than typing it.
    /// </remarks>
    static (string Body, IReadOnlyList<string> Errors) ResolveSnippets(string repositoryRoot, string body) {
        var errors = new List<string>();

        var resolved = SnippetInclude().Replace(body, match => {
            var file = match.Groups[1].Value;
            var region = match.Groups[2].Value;
            var path = System.IO.Path.Combine(repositoryRoot, file.Replace('/', System.IO.Path.DirectorySeparatorChar));

            if (!File.Exists(path)) {
                errors.Add($"the snippet names `{file}`, which does not exist");

                return match.Value;
            }

            var text = File.ReadAllText(path);
            var extracted = Region(text, region);

            if (extracted is null) {
                errors.Add($"`{file}` has no `#region {region}`");

                return match.Value;
            }

            var language = System.IO.Path.GetExtension(path) switch {
                ".cs" => "csharp",
                ".rvn" => "rvn",
                ".vxml" => "vxml",
                ".vcss" => "vcss",
                _ => string.Empty
            };

            // `snippet` marks a fence the solution itself compiles, so it satisfies § 4.3 without
            // being compiled twice — the region would not exist if the file had stopped building.
            return $"```{language} snippet title={file}\n{extracted}\n```";
        });

        return (resolved, errors);
    }

    static string? Region(string text, string name) {
        var start = RegionStart().Matches(text)
            .FirstOrDefault(match => string.Equals(match.Groups[1].Value, name, StringComparison.Ordinal));

        if (start is null) {
            return null;
        }

        var from = text.IndexOf('\n', start.Index);
        var to = text.IndexOf("#endregion", from, StringComparison.Ordinal);

        if (from < 0 || to < 0) {
            return null;
        }

        to = text.LastIndexOf('\n', to);

        var lines = text[(from + 1)..to].Split('\n');
        var indent = lines
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : line)).TrimEnd();
    }

    /// <summary>Every fenced block, and what the build has to do about it.</summary>
    static List<Example> Fences(string page, string body, List<string> problems) {
        var examples = new List<Example>();
        var lines = body.ReplaceLineEndings("\n").Split('\n');
        var index = 0;

        while (index < lines.Length) {
            if (!lines[index].StartsWith("```", StringComparison.Ordinal)) {
                index++;

                continue;
            }

            var info = lines[index][3..].Trim();
            var opened = index + 1;
            var code = new StringBuilder();

            index++;

            while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal)) {
                code.AppendLine(lines[index]);
                index++;
            }

            index++;

            // Quote-aware, because a reason is a sentence: `no-compile="the store is not public
            // yet"` split on spaces gives "the", which is a reason nobody can act on.
            var words = InfoWords(info);
            var language = words.Count > 0 && !words[0].Contains('=') ? words[0] : string.Empty;
            var compile = words.Contains("compile") || words.Contains("compile:fragment");
            var fragment = words.Contains("compile:fragment");
            var reason = words.FirstOrDefault(word => word.StartsWith("no-compile=", StringComparison.Ordinal))?
                ["no-compile=".Length..].Trim('"');

            if (words.Contains("no-compile") && reason is null) {
                // An exemption with no reason is an exemption nobody reviewed. § 4.3 prints these in
                // the summary so they stay visible; one with nothing to print is a gap.
                problems.Add($"a `no-compile` fence at line {opened} gives no reason");
            }

            if (words.Contains("snippet")) {
                reason ??= "quoted from a file the solution compiles";
            }

            if (language is "csharp" && !compile && reason is null && !words.Contains("no-compile")) {
                problems.Add($"the C# fence at line {opened} is neither `compile` nor `no-compile=\"why\"`");
            }

            examples.Add(new Example(page, opened, language, code.ToString().TrimEnd(), compile, fragment, reason));
        }

        return examples;
    }

    /// <summary>
    ///     A fence's info string, split on spaces except inside quotes.
    /// </summary>
    static List<string> InfoWords(string info) {
        var words = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in info) {
            switch (character) {
                case '"':
                    quoted = !quoted;
                    current.Append(character);

                    break;

                case ' ' when !quoted:
                    if (current.Length > 0) {
                        words.Add(current.ToString());
                        current.Clear();
                    }

                    break;

                default:
                    current.Append(character);

                    break;
            }
        }

        if (current.Length > 0) {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>GitHub's anchor rules, which is what a reader's link will have been written for.</summary>
    internal static string Anchor(string heading) {
        var text = Regex.Replace(heading, @"\[([^\]]*)\]\([^)]*\)", "$1");
        var builder = new StringBuilder(text.Length);

        foreach (var character in text.Trim().ToLowerInvariant()) {
            if (char.IsLetterOrDigit(character) || character is '-' or '_') {
                builder.Append(character);
            } else if (character == ' ') {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }
}
