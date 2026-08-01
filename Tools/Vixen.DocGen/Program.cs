// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Vixen.DocGen;
using Vixen.DocGen.Guide;

// The subject of `nuke Docs`. It reads the engine's own source with Roslyn and emits the graph the
// documentation site is rendered from — docs/plan/25-documentation-generator-and-site.md.
//
//   vixen-doc-gen <solution> --output artifacts/docs [--configuration Release] [--commit <sha>]
//                            [--repository-url https://github.com/rikarin/Vixen] [--verify-baselines]

var arguments = Arguments.Parse(args);

if (arguments is null) {
    Console.Error.WriteLine(
        """
        usage: vixen-doc-gen <solution> --output <directory> [options]

          --configuration <name>   design-time build configuration. Default: Release.
                                   ⚠ It must match the configuration the tree was built in: this
                                   repository resolves its generators through ProjectReference, so
                                   the wrong one silently removes everything they emit.
          --commit <sha>           the commit being documented; source links are built from it.
          --repository-url <url>   default: https://github.com/rikarin/Vixen
          --verify-baselines       also fail when the graph and the PublicAPI baselines disagree.
          --check-docs             also fail on a guide page that breaks its contract, names a
                                   symbol the graph does not have, carries an example that does not
                                   compile, or a public type with neither a page nor a line in
                                   docs/DocsExempt.txt.
          --update-exemptions      rewrite docs/DocsExempt.txt from the graph and exit: a type with
                                   no page and no line gets one as `sweep-pending`, a line whose type
                                   now has a page or no longer exists is dropped, and every reason
                                   already written is kept. Read the diff before committing.
          --release <version>      archive this graph as a release under docs/api-history/, diff it
                                   against the previous one, and write the table into the store and
                                   into CHANGELOG.md. Committed output — run it at the tag.
          --date <yyyy-mm-dd>      the release's date. Default: today, UTC.
          --excuse <project>       tolerate compile errors in this project, and say so in the
                                   summary. Repeatable. For sources produced by an MSBuild task
                                   rather than a Roslyn generator, which a design-time build cannot
                                   see — ANTLR being the only case in this tree.
        """
    );

    return 2;
}

// Registered before anything touches MSBuild types, and in a method the JIT has not yet reached.
var instance = MSBuildLocator.RegisterDefaults();

Console.WriteLine($"MSBuild {instance.Version} at {instance.MSBuildPath}");

try {
    return await Run(arguments);
} catch (DocGenException failure) {
    Console.Error.WriteLine();
    Console.Error.WriteLine($"error: {failure.Message}");

    return 1;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> Run(Arguments arguments) {
    var watch = Stopwatch.StartNew();
    var root = Path.GetDirectoryName(Path.GetFullPath(arguments.Solution))!;
    var links = new SourceLinks(root, arguments.RepositoryUrl, arguments.Commit);
    var reader = new SymbolReader(links);
    var loader = new SolutionLoader(arguments.Solution, arguments.Configuration, Console.WriteLine);

    var projects = await loader.LoadAsync(CancellationToken.None);
    var broken = projects.Where(project => project.Errors.Count > 0).ToList();
    var excused = broken.Where(project => arguments.Excused.Contains(project.Name)).ToList();
    var failed = broken.Where(project => !arguments.Excused.Contains(project.Name)).ToList();

    foreach (var project in excused) {
        // Named rather than counted, and printed rather than swallowed: an excuse nobody sees is a
        // gate nobody has.
        Console.WriteLine($"excused  {project.Name}: {project.Errors.Count} compile errors");
    }

    if (failed.Count > 0) {
        // Not a warning. A project that does not compile is one whose generated API is missing, and
        // a graph built from it describes an engine that does not exist — which is exactly what the
        // wrong `--configuration` produces, silently, on 40 projects at once.
        var detail = string.Join(Environment.NewLine, failed.Take(10).Select(project =>
            $"  {project.Name}: {project.Errors.Count} errors, e.g. {project.Errors[0].GetMessage()}"));

        throw new DocGenException(
            $"{failed.Count} of {projects.Count} projects have compile errors:{Environment.NewLine}{detail}"
            + $"{Environment.NewLine}Build the tree in {arguments.Configuration} first, and check that "
            + "--configuration names the same one."
        );
    }

    var documented = projects.Where(project => Scope.IsDocumented(project.Area, project.Name)).ToList();

    var (nodes, merged) = Scope.Deduplicate(NonCSharpNodes.Read(root, links).Concat(documented
        .SelectMany(project => reader.Read(
            project.Compilation,
            project.Area,
            project.IsPackable))));

    nodes = [.. nodes.OrderBy(node => node.Id, StringComparer.Ordinal)];

    // § 2.4. Over every project rather than the documented ones: a use in a sample is the reference
    // a reader wants first, and samples are exactly what Scope leaves out of the graph.
    var referenceWatch = Stopwatch.StartNew();
    var references = await ReferenceIndex.BuildAsync(
        projects,
        nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal),
        CancellationToken.None);

    referenceWatch.Stop();

    nodes = [
        .. nodes.Select(node => {
            var (shown, total) = references.For(node.Id);

            return total == 0 ? node : node with { UsedBy = shown, UsedByCount = total };
        })
    ];

    // § 8.3 — a signature is navigable, not merely coloured. Roslyn attached an id to every run that
    // names a type; this drops the ones the graph has no page for, so a reader never clicks a
    // parameter type and lands on a 404. `System.Int32` keeps its colour and loses its link.
    var linkable = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
    var linked = 0;

    IReadOnlyList<DocSpan> Link(IReadOnlyList<DocSpan> signature) {
        if (!signature.Any(span => span.Id is not null && !linkable.Contains(span.Id))) {
            linked += signature.Count(span => span.Id is not null);

            return signature;
        }

        return [
            .. signature.Select(span => {
                if (span.Id is null || linkable.Contains(span.Id)) {
                    linked += span.Id is null ? 0 : 1;

                    return span;
                }

                return span with { Id = null };
            })
        ];
    }

    nodes = [
        .. nodes.Select(node => node with {
            Signature = Link(node.Signature),
            Members = [.. node.Members.Select(member => member with { Signature = Link(member.Signature) })]
        })
    ];

    if (nodes.Count == 0) {
        throw new DocGenException(
            "The graph is empty. Every project compiled and none of them declared a public type, "
            + "which is not a thing that happens — check the workspace packaging first."
        );
    }

    DocGraph graph = new() {
        Solution = Path.GetFileName(arguments.Solution),
        Configuration = arguments.Configuration,
        Commit = arguments.Commit,
        ProjectCount = documented.Count,
        GeneratedDocumentCount = projects.Sum(project => project.GeneratedDocuments),
        Nodes = nodes
    };

    // ── The written half — § 4 ──────────────────────────────────────────────────────────────────
    var (pages, guideErrors) = GuideReader.Read(root, links);
    var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
    var problems = new List<string>(guideErrors);

    foreach (var page in pages) {
        foreach (var id in page.Front.Api.Where(id => !byId.ContainsKey(id))) {
            problems.Add($"{page.Path}: `api: {id}` names nothing the graph has");
        }

        foreach (var related in page.Front.Related
            .Where(slug => !pages.Any(other => string.Equals(other.Front.Slug, slug, StringComparison.Ordinal)))) {
            problems.Add($"{page.Path}: `related: {related}` names no page");
        }
    }

    // § 4.3 — the single most valuable gate. Documentation examples rot silently and are the first
    // thing a new user copies.
    var examples = pages.SelectMany(page => page.Examples).ToList();
    var compiled = Examples.Compile(
        examples,
        [.. documented.Select(project => project.Compilation)],
        CancellationToken.None);

    problems.AddRange(compiled.SelectMany(result => result.Errors));

    // § 3.4 — the compiler that checked the example also colours it. Every C# fence, not only the
    // compiled ones: a `no-compile` fence still has keywords, strings and comments, and the semantic
    // model answers whatever binds.
    var (highlightHost, highlightOptions) = Examples.Host([.. documented.Select(project => project.Compilation)]);
    var highlighted = 0;

    pages = [
        .. pages.Select(page => {
            var tokens = new Dictionary<string, IReadOnlyList<IReadOnlyList<DocSpan>>>(StringComparer.Ordinal);

            for (var index = 0; index < page.Examples.Count; index++) {
                var lines = Highlighter.Highlight(
                    page.Examples[index],
                    highlightHost,
                    highlightOptions,
                    CancellationToken.None);

                if (lines is not null) {
                    tokens[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = lines;
                    highlighted++;
                }
            }

            return tokens.Count == 0 ? page : page with { Tokens = tokens };
        })
    ];

    // The join, § 1: a page declares the symbols it is the prose for, and from that one line the
    // symbol page gets the prose and the guide page gets the signature.
    var documentedBy = pages
        .SelectMany(page => page.Front.Api.Select(id => (id, page.Front.Slug)))
        .GroupBy(pair => pair.id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Slug, StringComparer.Ordinal);

    nodes = [
        .. nodes.Select(node => documentedBy.TryGetValue(node.Id, out var slug) ? node with { Docs = slug } : node)
    ];

    graph = graph with { Nodes = nodes };

    // § Part 5 — the two checks that read the join rather than one page at a time.
    var (exemptions, exemptionErrors) = Coverage.Read(root);

    problems.AddRange(exemptionErrors);
    problems.AddRange(PageLinks.Check(pages, nodes.Select(node => node.Slug).ToHashSet(StringComparer.Ordinal)));

    if (arguments.UpdateExemptions) {
        var update = Coverage.Write(root, nodes);

        Console.WriteLine($"{Coverage.RelativePath}: {update.Added} added as `{Coverage.SeedReason}`, "
            + $"{update.Documented} removed (documented), {update.Gone} removed (gone), "
            + $"{update.Total} lines.");

        if (update.Added > 0) {
            // The same warning `--update-api` prints, for the same reason: the file's value is that
            // somebody looked at what went into it.
            Console.WriteLine("Read the diff before committing. A type added here is a type nobody "
                + "has written about, and the line is what says so out loud.");
        }

        return 0;
    }

    problems.AddRange(Coverage.Check(nodes, exemptions));

    var written = new GraphWriter().Write(graph, arguments.Output);
    var guideWritten = GraphWriter.WriteGuide(pages, arguments.Output);

    // ── The release, § 6 ────────────────────────────────────────────────────────────────────────
    // Written before the summary so the numbers the summary prints include it, and after the graph
    // so a release that fails the gate below has still produced something to look at.
    var history = History.Read(root);

    if (arguments.Release is { } release) {
        var previous = History.Previous(history, release);
        var semantic = pages
            .SelectMany(page => page.Front.Breaking.Select(text => (page.Front.Slug, text)))
            .ToList();

        var changes = previous is null
            ? []
            : ReleaseDiff.Between(
                History.ReadGraph(root, previous.Version)
                ?? throw new DocGenException(
                    $"{previous.Version} is in {History.RelativeDirectory}/index.json and its graph is not. "
                    + "The index and the store have to be committed together."),
                graph,
                semantic);

        var record = History.Write(root, graph, release, arguments.Date);

        Releases.Write(root, arguments.Output, record, previous?.Version, changes);

        Console.WriteLine();
        Console.WriteLine($"release {release}: {record.Types} types, {record.Members} members, "
            + $"{record.Bytes / 1024.0 / 1024.0:F2} MB archived in {History.RelativeDirectory}/{release}");

        Console.WriteLine(previous is null
            ? "  the first release — no diff, because there is nothing to diff against"
            : $"  against {previous.Version}: {changes.Count(change => change.Kind == ChangeKind.Added)} added, "
            + $"{changes.Count(change => change.Kind == ChangeKind.Removed)} removed, "
            + $"{changes.Count(change => change.Kind == ChangeKind.Deprecated)} deprecated, "
            + $"{changes.Count(change => change.IsBreaking)} breaking");
    } else {
        // Every archived release, re-emitted for the site: the pages are rendered from what is
        // committed, so a rebuild of an old release's table cannot disagree with the release.
        Releases.WriteIndex(root, arguments.Output, history);
    }

    Console.WriteLine();
    Console.WriteLine($"{documented.Count} of {projects.Count} projects documented "
        + $"(samples, benchmarks and tests are examples rather than surface), "
        + $"{graph.GeneratedDocumentCount} generated documents");

    Console.WriteLine($"{nodes.Count(node => node.UsedByCount > 0)} types are used by another, "
        + $"indexed in {referenceWatch.Elapsed.TotalSeconds:F1} s");

    if (merged > 0) {
        // Printed rather than swallowed: these are types whose page went to another assembly's copy.
        Console.WriteLine($"{merged} linked duplicates merged into the packable copy");
    }
    Console.WriteLine($"{nodes.Count} types, {nodes.Sum(node => node.Members.Count)} members, "
        + $"{nodes.Count(node => node.Summary is not null)} documented, {linked} linked type references");

    foreach (var group in nodes.GroupBy(node => node.Kind).OrderByDescending(group => group.Count())) {
        Console.WriteLine($"  {group.Count(),6}  {Taxonomy.Slug(group.Key)}");
    }

    Console.WriteLine();
    Console.WriteLine($"index {written.IndexBytes / 1024.0:F0} kB, pages {written.PageBytes / 1024.0 / 1024.0:F2} MB "
        + $"in {written.Chunks} chunks ({written.SplitChunks} namespaces split) → {arguments.Output}");
    Console.WriteLine($"{pages.Count} guide pages, {documentedBy.Count} symbols with prose, "
        + $"{examples.Count(example => example.Compile)} of {examples.Count} examples compiled, "
        + $"{highlighted} classified, {guideWritten / 1024.0:F0} kB");

    // § Part 5's coverage row, printed whether or not it is gating: a number that only appears when
    // the build is already red is a number nobody watches go down.
    var covered = nodes.Count(node => node.Docs is not null);
    var exemptIds = exemptions.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

    Console.WriteLine($"coverage: {covered} of {nodes.Count} types have a page, {exemptions.Count} exempt, "
        + $"{nodes.Count(node => node.Docs is null && !exemptIds.Contains(node.Id))} with neither");

    foreach (var exempt in examples.Where(example => example.Reason is not null)) {
        // § 4.3: an exemption nobody sees is a gate nobody has.
        Console.WriteLine($"  not compiled  {exempt.Page}:{exempt.Line} — {exempt.Reason}");
    }

    if (problems.Count > 0) {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"{problems.Count} problem{(problems.Count == 1 ? string.Empty : "s")} "
            + "in the written half:");

        foreach (var problem in problems.Take(25)) {
            Console.Error.WriteLine($"  {problem}");
        }

        if (problems.Count > 25) {
            Console.Error.WriteLine($"  …and {problems.Count - 25} more");
        }
    }
    Console.WriteLine($"done in {watch.Elapsed.TotalSeconds:F1} s");

    if (arguments.CheckDocs && problems.Count > 0) {
        return 1;
    }

    if (!arguments.VerifyBaselines) {
        return 0;
    }

    var disagreements = BaselineAgreement.Compare(root, nodes);

    if (disagreements.Count == 0) {
        Console.WriteLine("The graph and the PublicAPI baselines agree.");

        return 0;
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine($"{disagreements.Count} assemblies disagree with their baseline:");

    foreach (var disagreement in disagreements.Take(20)) {
        Console.Error.WriteLine($"  {disagreement.Assembly}: "
            + $"{disagreement.MissingFromGraph.Count} missing from the graph, "
            + $"{disagreement.MissingFromBaseline.Count} missing from the baseline");

        foreach (var name in disagreement.MissingFromGraph.Take(3)) {
            Console.Error.WriteLine($"    graph is missing  {name}");
        }

        foreach (var name in disagreement.MissingFromBaseline.Take(3)) {
            Console.Error.WriteLine($"    baseline is missing  {name}");
        }
    }

    return 1;
}

/// <summary>The command line, parsed by hand because it is five options and one argument.</summary>
sealed record Arguments(
    string Solution,
    string Output,
    string Configuration,
    string? Commit,
    string RepositoryUrl,
    bool VerifyBaselines,
    bool CheckDocs,
    bool UpdateExemptions,
    string? Release,
    string Date,
    IReadOnlySet<string> Excused
) {
    public static Arguments? Parse(string[] args) {
        string? solution = null;
        string? output = null;
        var configuration = "Release";
        string? commit = null;
        var repositoryUrl = "https://github.com/rikarin/Vixen";
        var verify = false;
        var checkDocs = false;
        var updateExemptions = false;
        string? release = null;
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var excused = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++) {
            switch (args[index]) {
                case "--output" when index + 1 < args.Length:
                    output = args[++index];

                    break;

                case "--configuration" when index + 1 < args.Length:
                    configuration = args[++index];

                    break;

                case "--commit" when index + 1 < args.Length:
                    commit = args[++index];

                    break;

                case "--repository-url" when index + 1 < args.Length:
                    repositoryUrl = args[++index];

                    break;

                case "--verify-baselines":
                    verify = true;

                    break;

                case "--check-docs":
                    checkDocs = true;

                    break;

                case "--update-exemptions":
                    updateExemptions = true;

                    break;

                case "--release" when index + 1 < args.Length:
                    release = args[++index].TrimStart('v');

                    break;

                case "--date" when index + 1 < args.Length:
                    date = args[++index];

                    break;

                case "--excuse" when index + 1 < args.Length:
                    excused.Add(args[++index]);

                    break;

                default:
                    if (args[index].StartsWith('-') || solution is not null) {
                        return null;
                    }

                    solution = args[index];

                    break;
            }
        }

        return solution is null || output is null
            ? null
            : new Arguments(
                solution,
                output,
                configuration,
                commit,
                repositoryUrl,
                verify,
                checkDocs,
                updateExemptions,
                release,
                date,
                excused);
    }
}
