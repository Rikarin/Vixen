// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vixen.DocGen;

/// <summary>One <c>[LoggerMessage]</c> in the tree — an id somebody allocated to themselves.</summary>
/// <param name="Id">The <c>EventId</c>. Zero is "unassigned" and the register says so.</param>
/// <param name="Level">The <c>LogLevel</c> member's name, or empty when it is not a literal one.</param>
/// <param name="Message">The message template, with concatenated literals joined.</param>
/// <param name="Project">The owning project's file name without its extension.</param>
/// <param name="Area">The top-level folder — <c>Core</c>, <c>Samples</c>, <c>Live</c>.</param>
/// <param name="Path">Repository-relative, so a failure can be clicked.</param>
/// <param name="Line">One-based.</param>
sealed record LogEventSite(
    int Id,
    string Level,
    string Message,
    string Project,
    string Area,
    string Path,
    int Line
) {
    public string Where => $"{Path}:{Line}";
}

/// <summary>One row of the register's range table.</summary>
/// <param name="First">Inclusive.</param>
/// <param name="Last">Inclusive.</param>
/// <param name="Owners">
///     The backticked names in the subsystem cell, with any trailing <c>.*</c> or <c>/*</c> dropped.
///     A name containing <c>/</c> is a folder — <c>Samples/*</c> — and matches on the area; anything
///     else is an assembly-name prefix, because <c>Vixen.App</c> owns <c>Vixen.App.Hosting</c>.
/// </param>
/// <param name="InUse">The status cell says <c>in use</c> rather than <c>reserved</c>.</param>
sealed record LogEventRange(int First, int Last, IReadOnlyList<string> Owners, bool InUse) {
    public bool Contains(int id) => id >= First && id <= Last;

    /// <summary>Whether a call site in this project is allowed to draw from the range.</summary>
    public bool Owns(LogEventSite site) => Owners.Any(owner => owner.Contains('/', StringComparison.Ordinal)
        ? string.Equals(owner.TrimEnd('/'), site.Area, StringComparison.Ordinal)
        : site.Project.StartsWith(owner, StringComparison.Ordinal)
        && (site.Project.Length == owner.Length || site.Project[owner.Length] == '.'));

    public string Describe() => $"{First}–{Last} ({string.Join(", ", Owners)})";
}

/// <summary>
///     The register in <c>docs/manual/log-events.md</c>, checked against the code it registers.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is a build failure and not a rule in a markdown file.</b> The register's rules
///         were followed on the day three branches each allocated 13026: every branch added its row in
///         the same commit as its log line, and every branch was internally consistent. Only the union
///         was wrong, and prose cannot see a union. The register says an id is permanent and never
///         reused, which makes a collision that survives a release unfixable by definition — so the
///         only affordable place to catch one is before the merge.
///     </para>
///     <para>
///         ⚠ <b>Test projects are out of scope</b>, the same ones <see cref="Scope.IsDocumented" />
///         leaves out and for the same reason: <c>Vixen.Core.Diagnostics.Tests</c> logs under ids 1 to
///         5 and 2001 on purpose, because what it is testing is the sink and not the register. Samples
///         and <c>Live/</c> are <em>in</em> scope — the register has a section for the first and the
///         second ships to somebody's cluster.
///     </para>
/// </remarks>
static partial class LogEventRegister {
    static readonly string[] SkippedDirectories = ["bin", "obj", "artifacts", ".git", "node_modules", "packages"];

    /// <summary>A row of the range table: two numbers, a subsystem cell and a status.</summary>
    [GeneratedRegex(@"^\|\s*([\d\s ]+?)\s*[–-]\s*([\d\s ]+?)\s*\|(.+?)\|\s*\**(.+?)\**\s*\|\s*$",
        RegexOptions.Multiline)]
    private static partial Regex RangeRow();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex Backticked();

    // ── The code half ───────────────────────────────────────────────────────────────────────────

    /// <summary>Every <c>[LoggerMessage]</c> in the checkout, outside test projects.</summary>
    /// <remarks>
    ///     Syntax only — no compilation, no MSBuild, no workspace. An <c>EventId</c> is a literal in an
    ///     attribute argument, so the parse tree is the whole answer and a test can afford to ask for
    ///     it on every run. That is deliberate: the gate this feeds also runs inside <c>CheckDocs</c>,
    ///     which needs a design-time build of forty projects, and a guard that only runs there is one
    ///     nobody can sabotage-test in under a minute.
    /// </remarks>
    public static IReadOnlyList<LogEventSite> Sites(string repositoryRoot) {
        var sites = new List<LogEventSite>();

        foreach (var path in Sources(repositoryRoot)) {
            var text = File.ReadAllText(path);

            // The cheap gate first: twenty-odd files in three thousand declare one.
            if (!text.Contains("LoggerMessage", StringComparison.Ordinal)) {
                continue;
            }

            var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            var project = ProjectOf(repositoryRoot, path);

            if (project is null || project.EndsWith(".Tests", StringComparison.Ordinal)) {
                continue;
            }

            var area = relative.Split('/')[0];
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();

            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>()) {
                var name = attribute.Name.ToString().Split('.')[^1];

                if (name is not ("LoggerMessage" or "LoggerMessageAttribute")) {
                    continue;
                }

                var arguments = attribute.ArgumentList?.Arguments ?? default;
                var positional = arguments.Where(argument => argument.NameEquals is null).ToList();

                if (Integer(Argument(arguments, "EventId") ?? Expression(positional, 0)) is not { } id) {
                    continue;
                }

                sites.Add(new LogEventSite(
                    id,
                    Member(Argument(arguments, "Level") ?? Expression(positional, 1)),
                    Text(Argument(arguments, "Message") ?? Expression(positional, 2)),
                    project,
                    area,
                    relative,
                    attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }

        return [.. sites.OrderBy(site => site.Id).ThenBy(site => site.Where, StringComparer.Ordinal)];
    }

    static IEnumerable<string> Sources(string directory) {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                continue;
            }

            foreach (var file in Sources(child)) {
                yield return file;
            }
        }
    }

    /// <summary>The nearest <c>.csproj</c> at or above the file, by name.</summary>
    static string? ProjectOf(string repositoryRoot, string path) {
        var directory = Path.GetDirectoryName(path);

        while (directory is not null && directory.StartsWith(repositoryRoot, StringComparison.Ordinal)) {
            var project = Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();

            if (project is not null) {
                return Path.GetFileNameWithoutExtension(project);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    static ExpressionSyntax? Argument(SeparatedSyntaxList<AttributeArgumentSyntax> arguments, string name) =>
        arguments.FirstOrDefault(argument =>
            string.Equals(argument.NameEquals?.Name.Identifier.ValueText, name, StringComparison.Ordinal))?.Expression;

    static ExpressionSyntax? Expression(IReadOnlyList<AttributeArgumentSyntax> positional, int index) =>
        index < positional.Count ? positional[index].Expression : null;

    static int? Integer(ExpressionSyntax? expression) => expression is LiteralExpressionSyntax literal
        && literal.Token.Value is int value
            ? value
            : null;

    /// <summary><c>LogLevel.Warning</c> → <c>Warning</c>.</summary>
    static string Member(ExpressionSyntax? expression) => expression is MemberAccessExpressionSyntax access
        ? access.Name.Identifier.ValueText
        : string.Empty;

    /// <summary>A string literal, or the literals of a `"a" + "b"` template.</summary>
    static string Text(ExpressionSyntax? expression) => expression switch {
        LiteralExpressionSyntax { Token.Value: string value } => value,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
            Text(binary.Left) + Text(binary.Right),
        _ => string.Empty
    };

    // ── The register half ───────────────────────────────────────────────────────────────────────

    /// <summary>The range table of <c>docs/manual/log-events.md</c>.</summary>
    public static IReadOnlyList<LogEventRange> Ranges(string repositoryRoot) {
        var path = Path.Combine(repositoryRoot, "docs", "manual", "log-events.md");

        if (!File.Exists(path)) {
            return [];
        }

        var ranges = new List<LogEventRange>();

        foreach (Match row in RangeRow().Matches(File.ReadAllText(path))) {
            if (Number(row.Groups[1].Value) is not { } first || Number(row.Groups[2].Value) is not { } last) {
                continue;
            }

            var owners = Backticked().Matches(row.Groups[3].Value)
                .Select(match => match.Groups[1].Value.Trim())
                .Select(owner => owner.EndsWith(".*", StringComparison.Ordinal)
                    ? owner[..^2]
                    : owner.EndsWith("/*", StringComparison.Ordinal)
                        ? owner[..^1]
                        : owner)
                .Where(owner => owner.Length > 0)
                .ToList();

            if (owners.Count == 0) {
                continue;
            }

            ranges.Add(new LogEventRange(
                first,
                last,
                owners,
                row.Groups[4].Value.Trim().StartsWith("in use", StringComparison.OrdinalIgnoreCase)));
        }

        return ranges;
    }

    /// <summary>`13 000` — the table writes its numbers with a thousands space.</summary>
    static int? Number(string cell) {
        var digits = new string([.. cell.Where(char.IsAsciiDigit)]);

        return digits.Length > 0 && int.TryParse(digits, out var value) ? value : null;
    }

    /// <summary>
    ///     A register row keeps its id after the log line is deleted, so it needs a way to say the
    ///     line is gone. The <c>Since</c> cell says it: <c>0.1.0, retired in 0.3.0</c>.
    /// </summary>
    public static bool IsRetired(DocNode row) =>
        row.Facets?.Since?.Contains("retired", StringComparison.OrdinalIgnoreCase) == true;

    // ── The gate ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every way the register, the code and the guide can disagree about an id.
    /// </summary>
    /// <param name="rows">
    ///     The <c>L:</c> nodes — <see cref="NonCSharpNodes.LogEvents" />' output, so the register is
    ///     parsed once and by the parser the documentation graph itself is built from.
    /// </param>
    /// <param name="sites">What <see cref="Sites" /> found.</param>
    /// <param name="ranges">What <see cref="Ranges" /> found.</param>
    /// <param name="claims">
    ///     Every <c>api:</c> entry of every guide page, as (id, page slug). Filtered to <c>L:</c> here
    ///     rather than by the caller, so a caller cannot forget.
    /// </param>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<DocNode> rows,
        IReadOnlyList<LogEventSite> sites,
        IReadOnlyList<LogEventRange> ranges,
        IReadOnlyList<(string Id, string Page)> claims
    ) {
        var problems = new List<string>();
        var registered = rows
            .Where(row => row.Kind == DocKind.LogEvent)
            .ToDictionary(row => row.Name, StringComparer.Ordinal);

        // 1 ── The collision itself. Everything else on this list is a way of drifting; this is the
        //      one that cannot be repaired after a release, because the register's first rule is that
        //      an id never changes meaning.
        foreach (var group in sites
            .GroupBy(site => site.Id)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key)) {
            problems.Add($"log event {group.Key} is claimed by {group.Count()} call sites — "
                + string.Join(", ", group.Select(site => site.Where))
                + ". An id names one call site or it names nothing; pick the next free one in the range");
        }

        // 2 ── An id in the code with no row. The register is what Vixen.DocGen reads and what a
        //      support log is decoded against, so an unregistered id is invisible to both.
        foreach (var site in sites.Where(site => !registered.ContainsKey(site.Id.ToString())).DistinctBy(site => site.Id)) {
            problems.Add($"{site.Where}: log event {site.Id} has no row in docs/manual/log-events.md — "
                + $"add `| {site.Id} | {(site.Level.Length > 0 ? site.Level : "…")} | "
                + $"{Truncate(site.Message)} | 0.1.0 |` in this commit");
        }

        // 3 ── A row with no call site, which is the other direction the register drifts in. A row for
        //      a deleted event is not the same problem: the rules keep it so an old log still decodes,
        //      and the Since cell is where it says so.
        var byId = sites.ToLookup(site => site.Id.ToString(), StringComparer.Ordinal);

        foreach (var row in registered.Values.OrderBy(row => row.Name, StringComparer.Ordinal)) {
            var retired = IsRetired(row);
            var live = byId[row.Name].Any();

            if (!retired && !live) {
                problems.Add($"docs/manual/log-events.md: log event {row.Name} has a row and no "
                    + "[LoggerMessage] anywhere — if the line was deleted the id stays, so say so in "
                    + "the Since cell (`0.1.0, retired in 0.2.0`) rather than deleting the row");
            }

            if (retired && live) {
                problems.Add($"docs/manual/log-events.md: log event {row.Name} is marked retired and is "
                    + $"logged at {byId[row.Name].First().Where} — a retired id is never reused");
            }
        }

        // 4 ── The link that hid the others. A page's `api:` list is what makes an id "covered", so a
        //      page claiming an id it does not own silences that id's coverage failure — and its own
        //      ids are then covered by nothing.
        var logClaims = claims
            .Where(claim => claim.Id.StartsWith("L:", StringComparison.Ordinal))
            .ToList();

        foreach (var claim in logClaims.Where(claim => !registered.ContainsKey(claim.Id[2..]))) {
            problems.Add($"{claim.Page}: `api: {claim.Id}` names no row in docs/manual/log-events.md");
        }

        foreach (var group in logClaims
            .GroupBy(claim => claim.Id, StringComparer.Ordinal)
            .Where(group => group.DistinctBy(claim => claim.Page, StringComparer.Ordinal).Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)) {
            problems.Add($"`api: {group.Key}` is claimed by "
                + string.Join(" and ", group.Select(claim => claim.Page).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                + " — one of them is documenting an event it does not own, and the id it should be "
                + "claiming instead is covered by nothing");
        }

        // 5 ── The range table. An id logged from the wrong assembly still decodes, and still tells a
        //      reader holding the log that it came from somewhere it did not.
        problems.AddRange(RangeProblems(sites, ranges));

        return problems;
    }

    /// <summary>
    ///     Every allocated id is written about by exactly one page, or has a line saying it is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the half that caught the capturing-a-frame defect, and the only half that
    ///         would have.</b> When that page's <c>api:</c> list claimed 13026 and 13027 — the ids the
    ///         capture feature had before the collision was renumbered — nothing about the claim itself
    ///         was wrong: both ids existed, and only one page claimed each. What said so was 13028 and
    ///         13029 being covered by nothing, which is this rule.
    ///     </para>
    ///     <para>
    ///         <see cref="Coverage.Check" /> already asserts it over the whole graph, and that is what
    ///         <c>CheckDocs</c> runs. It is repeated here because <c>CheckDocs</c> needs a design-time
    ///         build of the solution and this needs three markdown files: a rule that only fires after
    ///         a merge is a rule that fires after the merge that broke it.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Uncovered(
        IReadOnlyList<DocNode> rows,
        IReadOnlyList<(string Id, string Page)> claims,
        IReadOnlyList<Exemption> exemptions
    ) {
        var covered = claims.Select(claim => claim.Id).ToHashSet(StringComparer.Ordinal);
        var excused = exemptions.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        return [
            .. rows
                .Where(row => row.Kind == DocKind.LogEvent)
                .Where(row => !covered.Contains(row.Id) && !excused.Contains(row.Id))
                .OrderBy(row => row.Name, StringComparer.Ordinal)
                .Select(row => $"{row.Id} is written about by no guide page and has no line in "
                    + $"{Coverage.RelativePath} — an id nobody documented is the shape a renumbering "
                    + "leaves behind when a page keeps claiming the ids the feature used to have")
        ];
    }

    static IEnumerable<string> RangeProblems(
        IReadOnlyList<LogEventSite> sites,
        IReadOnlyList<LogEventRange> ranges
    ) {
        if (ranges.Count == 0) {
            yield break;
        }

        foreach (var site in sites.DistinctBy(site => site.Id)) {
            var range = ranges.FirstOrDefault(range => range.Contains(site.Id));

            if (range is null) {
                yield return $"{site.Where}: log event {site.Id} is in no range the register allocates — "
                    + "a range is a thousand ids and a subsystem, and one that does not exist is one "
                    + "nobody can be told they are inside";

                continue;
            }

            if (!range.Owns(site)) {
                yield return $"{site.Where}: log event {site.Id} is logged from `{site.Project}`, and "
                    + $"{range.Describe()} belongs to somebody else — the range is what makes an id "
                    + "name its origin on sight";
            }
        }

        // The status column, which is the cheapest of these and the one that ages: a range goes from
        // reserved to in use exactly once and nobody remembers to say so.
        foreach (var range in ranges) {
            var used = sites.Any(site => range.Contains(site.Id));

            if (used && !range.InUse) {
                yield return $"docs/manual/log-events.md: {range.Describe()} is marked reserved and is "
                    + "logged from — the status column says **in use** now";
            }

            if (!used && range.InUse) {
                yield return $"docs/manual/log-events.md: {range.Describe()} is marked **in use** and "
                    + "nothing logs in it — the status column says reserved";
            }
        }
    }

    static string Truncate(string message) =>
        message.Length <= 60 ? message : message[..57].TrimEnd() + "…";
}
