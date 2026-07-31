// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// Spike for docs/plan/25 § P0 — three questions, answered with numbers:
//
//   (a) Does MSBuildWorkspace open Vixen.slnx on Roslyn 5.x, and how long does the whole solution
//       take? How many projects fail, and what do they fail on?
//   (b) How large is the emitted graph, uncompressed and Brotli?
//   (c) Is the site's page data better chunked per type or per namespace? — answered here as the
//       size distribution of both groupings, which is the input that decision needs.
//
// Deliberately one file and deliberately crude. It measures; Tools/Vixen.DocGen is what gets built.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
    ?? throw new InvalidOperationException("Vixen.slnx not found above " + AppContext.BaseDirectory);

var outDir = Path.Combine(repoRoot, "artifacts", "docs-spike");
Directory.CreateDirectory(outDir);

var instance = MSBuildLocator.RegisterDefaults();
Console.WriteLine($"MSBuild      {instance.Version} at {instance.MSBuildPath}");
Console.WriteLine($"Repository   {repoRoot}");
Console.WriteLine();

return await Run(repoRoot, outDir);

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> Run(string repoRoot, string outDir) {
    var slnx = Path.Combine(repoRoot, "Vixen.slnx");
    var failures = new List<string>();

    // The configuration is not cosmetic. A design-time build resolves this repository's generators
    // through ProjectReference/OutputItemType=Analyzer, so the analyzer paths point at
    // bin/<Configuration>/…; with the default Debug against a Release tree the files do not exist,
    // the generators never run, and every consumer of generated API fails to compile.
    using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> {
        ["Configuration"] = Environment.GetEnvironmentVariable("DOCGEN_CONFIGURATION") ?? "Release"
    });
    workspace.SkipUnrecognizedProjects = true;
    using var _ = workspace.RegisterWorkspaceFailedHandler(e => {
        lock (failures) {
            failures.Add(e.Diagnostic.Message);
        }
    });

    // ── (a) Load ────────────────────────────────────────────────────────────────────────────────
    var openWatch = Stopwatch.StartNew();
    var solution = await workspace.OpenSolutionAsync(slnx, new ConsoleProgress());
    openWatch.Stop();

    var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
    Console.WriteLine();
    Console.WriteLine($"Opened       {projects.Count} C# projects in {openWatch.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine($"Failures     {failures.Count}");

    foreach (var group in failures.GroupBy(Classify).OrderByDescending(g => g.Count())) {
        Console.WriteLine($"  {group.Count(),4}  {group.Key}");
        Console.WriteLine($"        e.g. {Truncate(group.First(), 160)}");
    }

    // ── Compile and walk ────────────────────────────────────────────────────────────────────────
    var compileWatch = Stopwatch.StartNew();
    var nodes = new List<Node>();
    var generatedDocuments = 0;
    var missingGenerated = 0;
    var analyzerReferences = 0;
    var compiled = 0;
    var compileErrors = new List<string>();

    foreach (var project in projects.OrderBy(p => p.Name, StringComparer.Ordinal)) {
        var compilation = await project.GetCompilationAsync();

        if (compilation is null) {
            compileErrors.Add($"{project.Name}: no compilation");

            continue;
        }

        // 25 § 3.2 said to add these explicitly. Measured: the workspace has already run the
        // generators and their trees are in the compilation, so adding them throws "Syntax tree
        // already present". What is left worth doing is asserting it, because a generator that
        // stopped running looks exactly like a feature that was deleted.
        foreach (var document in await project.GetSourceGeneratedDocumentsAsync()) {
            var tree = await document.GetSyntaxTreeAsync() ?? throw new InvalidOperationException();

            if (!compilation.ContainsSyntaxTree(tree)) {
                compilation = compilation.AddSyntaxTrees(tree);
                missingGenerated++;
            }

            generatedDocuments++;
        }

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        var errors = diagnostics.Count;

        if (errors > 0) {
            compileErrors.Add($"{project.Name}: {errors} errors  " +
                string.Join(" ", diagnostics.GroupBy(d => d.Id)
                    .OrderByDescending(g => g.Count())
                    .Take(4)
                    .Select(g => $"{g.Key}×{g.Count()}")) +
                "   e.g. " + Truncate(diagnostics[0].GetMessage(), 90));
        }

        analyzerReferences += project.AnalyzerReferences.Count;

        var before = nodes.Count;
        Walk(compilation.Assembly.GlobalNamespace, compilation.Assembly.Name, nodes);
        compiled++;

        Console.WriteLine($"  {project.Name,-52} {nodes.Count - before,5} types  " +
            $"{project.AnalyzerReferences.Count,2} analyzers" +
            (errors > 0 ? $"   ⚠ {errors} compile errors" : ""));
    }

    compileWatch.Stop();

    Console.WriteLine();
    Console.WriteLine($"Compiled     {compiled} projects in {compileWatch.Elapsed.TotalSeconds:F1} s " +
        $"({generatedDocuments} source-generated documents, {missingGenerated} of them not already " +
        "in the compilation)");
    Console.WriteLine($"Analyzers    {analyzerReferences} analyzer references across the solution");
    Console.WriteLine($"Errors in    {compileErrors.Count} projects");

    foreach (var line in compileErrors.Take(40)) {
        Console.WriteLine($"  {line}");
    }

    // ── Report ──────────────────────────────────────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("Public types by kind");

    foreach (var group in nodes.GroupBy(n => n.Kind).OrderByDescending(g => g.Count())) {
        Console.WriteLine($"  {group.Count(),6}  {group.Key}");
    }

    Console.WriteLine($"  {nodes.Count,6}  TOTAL   " +
        $"({nodes.Sum(n => n.Members)} public members, " +
        $"{nodes.Count(n => n.Summary is not null)} with a doc comment, " +
        $"{nodes.Count(n => n.SourcePath is not null)} with a source location)");

    // ── (b) Size ────────────────────────────────────────────────────────────────────────────────
    var options = new JsonSerializerOptions {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false
    };

    var graphPath = Path.Combine(outDir, "graph.json");
    await File.WriteAllTextAsync(graphPath, JsonSerializer.Serialize(nodes, options));
    var raw = new FileInfo(graphPath).Length;

    Console.WriteLine();
    Console.WriteLine($"graph.json   {Mb(raw)} raw, {Mb(Brotli(graphPath))} Brotli → {graphPath}");

    // ── (c) Chunking ────────────────────────────────────────────────────────────────────────────
    var perType = nodes
        .Select(n => (long) JsonSerializer.Serialize(n, options).Length)
        .OrderBy(n => n)
        .ToList();

    var perNamespace = nodes
        .GroupBy(n => n.Namespace)
        .Select(g => (long) JsonSerializer.Serialize(g.ToList(), options).Length)
        .OrderBy(n => n)
        .ToList();

    Console.WriteLine();
    Console.WriteLine("Page-chunk size distribution (bytes of JSON)");
    Console.WriteLine($"  per type       n={perType.Count,5}  {Distribution(perType)}");
    Console.WriteLine($"  per namespace  n={perNamespace.Count,5}  {Distribution(perNamespace)}");

    return compiled == 0 ? 1 : 0;
}

// ── The taxonomy of 25 § 2.3, as far as a spike needs it ────────────────────────────────────────
static void Walk(INamespaceSymbol ns, string assembly, List<Node> nodes) {
    foreach (var member in ns.GetMembers()) {
        switch (member) {
            case INamespaceSymbol child:
                Walk(child, assembly, nodes);

                break;

            case INamedTypeSymbol type when IsPublic(type):
                nodes.Add(Describe(type, assembly));

                break;
        }
    }
}

static bool IsPublic(INamedTypeSymbol type) {
    for (var t = type; t is not null; t = t.ContainingType) {
        if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected
            or Accessibility.ProtectedOrInternal)) {
            return false;
        }
    }

    return true;
}

static Node Describe(INamedTypeSymbol type, string assembly) {
    var location = type.Locations.FirstOrDefault(l => l.IsInSource);
    var span = location?.GetLineSpan();
    var xml = type.GetDocumentationCommentXml();

    return new Node {
        Id = type.GetDocumentationCommentId() ?? "?:" + type.ToDisplayString(),
        Kind = Kind(type),
        Name = type.Name,
        Namespace = type.ContainingNamespace.ToDisplayString(),
        Assembly = assembly,
        Summary = Summary(xml),
        Attributes = [.. type.GetAttributes()
            .Select(a => a.AttributeClass?.Name)
            .Where(n => n is not null)!],
        Members = type.GetMembers().Count(m => m.DeclaredAccessibility is Accessibility.Public
            or Accessibility.Protected && !m.IsImplicitlyDeclared),
        SourcePath = span?.Path,
        StartLine = span?.StartLinePosition.Line + 1,
        EndLine = span?.EndLinePosition.Line + 1
    };
}

static string Kind(INamedTypeSymbol type) {
    var attributes = type.GetAttributes()
        .Select(a => a.AttributeClass?.ToDisplayString())
        .Where(n => n is not null)
        .ToHashSet(StringComparer.Ordinal)!;

    var bases = Bases(type).ToList();
    var interfaces = type.AllInterfaces.Select(i => i.ToDisplayString()).ToHashSet(StringComparer.Ordinal);

    if (attributes.Contains("Vixen.Core.ComponentAttribute")) {
        return attributes.Contains("Vixen.Core.DataContractAttribute") ? "scene-component" : "component";
    }

    if (interfaces.Contains("Vixen.Ecs.Systems.ISystem") || bases.Contains("Vixen.Ecs.Systems.SystemBase")) {
        return "system";
    }

    if (bases.Contains("Vixen.Engine.Behaviors.Behavior")) {
        return "behavior";
    }

    if (attributes.Contains("Vixen.Net.Replication.ReplicatedAttribute")) {
        return "replicated-component";
    }

    if (attributes.Contains("Vixen.Editor.NodeGraph.NodeAttribute")) {
        return "graph-node";
    }

    if (attributes.Contains("Vixen.Editor.Assets.ImporterAttribute")) {
        return "importer";
    }

    if (bases.Contains("System.Attribute")) {
        return "annotation";
    }

    if (interfaces.Contains("Microsoft.CodeAnalysis.IIncrementalGenerator")
        || bases.Contains("Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer")) {
        return "generator";
    }

    if (type.ContainingNamespace.ToDisplayString().StartsWith("Vixen.Ui.Controls", StringComparison.Ordinal)) {
        return "ui-control";
    }

    return type.TypeKind switch {
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        TypeKind.Struct => "struct",
        _ => "class"
    };
}

static IEnumerable<string> Bases(INamedTypeSymbol type) {
    for (var b = type.BaseType; b is not null; b = b.BaseType) {
        yield return b.ToDisplayString();
    }
}

static string? Summary(string? xml) {
    if (string.IsNullOrWhiteSpace(xml)) {
        return null;
    }

    var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
    var end = xml.IndexOf("</summary>", StringComparison.Ordinal);

    return start < 0 || end < start
        ? null
        : string.Join(' ', xml[(start + 9)..end].Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
}

// ── Reporting helpers ───────────────────────────────────────────────────────────────────────────
static string Classify(string message) => message switch {
    _ when message.Contains("not supported", StringComparison.OrdinalIgnoreCase) => "unsupported project type",
    _ when message.Contains("NETSDK", StringComparison.Ordinal) => "SDK error",
    _ when message.Contains("workload", StringComparison.OrdinalIgnoreCase) => "missing workload",
    _ when message.Contains("could not be found", StringComparison.OrdinalIgnoreCase) => "missing file/reference",
    _ => "other"
};

static string Truncate(string s, int max) =>
    s.Length <= max ? s.ReplaceLineEndings(" ") : s.ReplaceLineEndings(" ")[..max] + "…";

static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F2} MB";

static long Brotli(string path) {
    var target = path + ".br";
    using (var input = File.OpenRead(path))
    using (var output = File.Create(target))
    using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize)) {
        input.CopyTo(brotli);
    }

    return new FileInfo(target).Length;
}

static string Distribution(List<long> sorted) {
    if (sorted.Count == 0) {
        return "empty";
    }

    long At(double q) => sorted[Math.Min(sorted.Count - 1, (int) (sorted.Count * q))];

    return $"median {At(0.5),7:N0}  p95 {At(0.95),8:N0}  max {sorted[^1],9:N0}  total {sorted.Sum() / 1024.0 / 1024.0,6:F2} MB";
}

static string? FindRepoRoot(string start) {
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent) {
        if (File.Exists(Path.Combine(dir.FullName, "Vixen.slnx"))) {
            return dir.FullName;
        }
    }

    return null;
}

sealed class ConsoleProgress : IProgress<ProjectLoadProgress> {
    int _loaded;

    public void Report(ProjectLoadProgress value) {
        if (value.Operation != ProjectLoadOperation.Resolve) {
            return;
        }

        _loaded++;

        if (_loaded % 25 == 0) {
            Console.WriteLine($"  … {_loaded} projects resolved");
        }
    }
}

sealed class Node {
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public required string Assembly { get; init; }
    public string? Summary { get; init; }
    public string[] Attributes { get; init; } = [];
    public int Members { get; init; }
    public string? SourcePath { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
}
