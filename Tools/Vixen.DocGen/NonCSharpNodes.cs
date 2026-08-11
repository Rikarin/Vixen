// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vixen.DocGen;

/// <summary>
///     The parts of the engine that are not C# symbols — docs/plan/25 § 2.8.
/// </summary>
/// <remarks>
///     <para>
///         A shader, a diagnostic code and a log event are things the engine offers and things a
///         symbol-only tool leaves undocumented. Each already has a machine-readable source: Raven
///         writes reflection JSON beside every compiled shader, and the two registers are markdown
///         tables that the repository already treats as authoritative.
///     </para>
///     <para>
///         ⚠ <b>The registers stay the source of truth.</b> This reads them; it does not replace
///         them, and a code's meaning is still edited in
///         <c>docs/manual/diagnostic-codes.md</c> in the same commit as the diagnostic — which is
///         what [13](../../docs/plan/13-diagnostics.md) asks for and what makes the register worth
///         having.
///     </para>
/// </remarks>
static partial class NonCSharpNodes {
    public static IReadOnlyList<DocNode> Read(string repositoryRoot, SourceLinks links) => [
        .. Shaders(repositoryRoot, links),
        .. Diagnostics(repositoryRoot, links),
        .. LogEvents(repositoryRoot, links)
    ];

    // ── Shaders ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One node per Raven shader that has been compiled with <c>--emit-reflection</c>.
    /// </summary>
    /// <remarks>
    ///     The reflection is the compiler's own contract — the descriptor sets a runtime binds
    ///     against — so a shader page describes what the shader actually is rather than what its
    ///     source looks like. A `.rvn` with no reflection beside it is not documented, because the
    ///     alternative is a page that guesses at bindings.
    /// </remarks>
    static IEnumerable<DocNode> Shaders(string repositoryRoot, SourceLinks links) {
        var library = Path.Combine(repositoryRoot, "Raven", "Library");

        if (!Directory.Exists(library)) {
            yield break;
        }

        foreach (var path in Directory
            .EnumerateFiles(library, "*.reflect.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)) {
            var name = Path.GetFileName(path)[..^".reflect.json".Length];
            var group = Path.GetFileName(Path.GetDirectoryName(path)) ?? "Library";
            var source = Path.ChangeExtension(path[..^".reflect.json".Length], ".rvn");

            DocFacets facets;

            try {
                facets = ShaderFacets(File.ReadAllText(path));
            } catch (JsonException) {
                // A reflection file that does not parse is a compiler output problem, not a reason
                // to stop documenting the other 29.
                continue;
            }

            yield return new DocNode {
                Id = $"R:{group}/{name}",
                Kind = DocKind.Shader,
                Name = name,
                QualifiedName = $"{group}/{name}",
                Namespace = "Raven.Library." + group,
                Assembly = "Raven.Library",
                Area = "Raven",
                Slug = $"shaders/{group.ToLowerInvariant()}.{name.ToLowerInvariant()}",
                Signature = [new DocSpan(name, "class"), new DocSpan(".rvn", "text")],
                Summary = Summary(source),
                Facets = facets,
                Source = File.Exists(source) ? links.For(source) : null
            };
        }
    }

    static DocFacets ShaderFacets(string json) {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        List<string> Names(string property, string child) => root.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array
            ? [
                .. array.EnumerateArray()
                    .Select(entry => entry.TryGetProperty(child, out var value)
                        ? value.GetString() ?? string.Empty
                        : string.Empty)
                    .Where(value => value.Length > 0)
            ]
            : [];

        int Count(string property) => root.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array
            ? array.GetArrayLength()
            : 0;

        var stages = root.TryGetProperty("PushConstants", out var push) && push.ValueKind == JsonValueKind.Array
            ? push.EnumerateArray()
                .Select(entry => entry.TryGetProperty("Stages", out var value) ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value))
            : null;

        return new DocFacets {
            Stages = string.IsNullOrEmpty(stages) ? null : [.. stages.Split(", ", StringSplitOptions.RemoveEmptyEntries)],
            Permutations = Names("Permutations", "Name") is { Count: > 0 } permutations ? permutations : null,
            DescriptorSets = Count("Sets") is var sets and > 0 ? sets : null,
            ShaderParameters = Count("Parameters") is var parameters and > 0 ? parameters : null,
            VertexInputs = Names("VertexInputs", "Name") is { Count: > 0 } inputs ? inputs : null
        };
    }

    /// <summary>The `.rvn`'s leading `//` comment, which is the only prose a shader has.</summary>
    static string? Summary(string sourcePath) {
        if (!File.Exists(sourcePath)) {
            return null;
        }

        var lines = new List<string>();

        foreach (var line in File.ReadLines(sourcePath)) {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)) {
                var text = trimmed[2..].Trim();

                // The SPDX header is a licence, not a description.
                if (!text.StartsWith("SPDX", StringComparison.Ordinal)) {
                    lines.Add(text);
                }

                continue;
            }

            if (trimmed.Length > 0) {
                break;
            }
        }

        var summary = string.Join(' ', lines).Trim();

        return summary.Length == 0 ? null : summary;
    }

    // ── The registers ───────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^\|\s*`?(VX[A-Z]*\d+)`?\s*\|(.+?)\|(.+?)\|\s*$", RegexOptions.Multiline)]
    private static partial Regex DiagnosticRow();

    [GeneratedRegex(@"^\|\s*`?(\d{3,5})`?\s*\|\s*(\w+)\s*\|(.+?)\|(.+?)\|\s*$", RegexOptions.Multiline)]
    private static partial Regex LogEventRow();

    [GeneratedRegex(@"^###\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    static IEnumerable<DocNode> Diagnostics(string repositoryRoot, SourceLinks links) {
        var path = Path.Combine(repositoryRoot, "docs", "manual", "diagnostic-codes.md");

        if (!File.Exists(path)) {
            yield break;
        }

        var text = File.ReadAllText(path);

        foreach (Match row in DiagnosticRow().Matches(text)) {
            var code = row.Groups[1].Value;

            yield return new DocNode {
                Id = $"D:{code}",
                Kind = DocKind.Diagnostic,
                Name = code,
                QualifiedName = code,
                Namespace = "Diagnostics",
                Assembly = "Vixen.Tools",
                Area = "Tools",
                Slug = $"diagnostics/{code.ToLowerInvariant()}",
                Signature = [new DocSpan(code, "class")],
                Summary = Cell(row.Groups[2].Value),
                Facets = new DocFacets { EmittedBy = [.. Codes(row.Groups[3].Value)] },
                Source = links.For(path)
            };
        }
    }

    /// <summary>
    ///     One node per row of the log-event register.
    /// </summary>
    /// <remarks>
    ///     Public inside this assembly because <see cref="LogEventRegister" /> — the gate that checks
    ///     the register against the <c>[LoggerMessage]</c> attributes it registers — has to read the
    ///     same rows the graph is built from. A second parser of the same table is how the register and
    ///     the gate would come to disagree about what a row is.
    /// </remarks>
    public static IEnumerable<DocNode> LogEvents(string repositoryRoot, SourceLinks links) {
        var path = Path.Combine(repositoryRoot, "docs", "manual", "log-events.md");

        if (!File.Exists(path)) {
            yield break;
        }

        var text = File.ReadAllText(path);

        // The register groups its ids under `### Vixen.Graphics`-style headings, and the heading is
        // the subsystem — the one fact about an id that the row itself does not carry.
        var headings = Heading().Matches(text)
            .Select(match => (match.Index, Subsystem: Cell(match.Groups[1].Value)))
            .ToList();

        foreach (Match row in LogEventRow().Matches(text)) {
            var id = row.Groups[1].Value;

            // The ranges table at the top has rows shaped like an id row; a range is two numbers and
            // an en dash, so its "id" never parses as one on its own.
            if (row.Groups[2].Value is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical")) {
                continue;
            }

            var subsystem = headings.LastOrDefault(heading => heading.Index < row.Index).Subsystem;

            yield return new DocNode {
                Id = $"L:{id}",
                Kind = DocKind.LogEvent,
                Name = id,
                QualifiedName = id,
                Namespace = "LogEvents",
                Assembly = subsystem is { Length: > 0 } ? subsystem : "Vixen",
                Area = "Core",
                Slug = $"log-events/{id}",
                Signature = [new DocSpan(id, "number")],
                Summary = Cell(row.Groups[3].Value),
                Facets = new DocFacets {
                    Level = row.Groups[2].Value,
                    Since = Cell(row.Groups[4].Value) is { Length: > 0 } since ? since : null
                },
                Source = links.For(path)
            };
        }
    }

    /// <summary>A table cell without its backticks and padding.</summary>
    static string Cell(string value) => value.Replace("`", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>The `code`-quoted names in a cell — what emits a diagnostic.</summary>
    static IEnumerable<string> Codes(string value) => Regex
        .Matches(value, "`([^`]+)`")
        .Select(match => match.Groups[1].Value.Trim())
        .DefaultIfEmpty(Cell(value))
        .Where(text => text.Length > 0);
}
