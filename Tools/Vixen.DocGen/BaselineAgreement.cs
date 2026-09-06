// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.DocGen;

/// <summary>
///     Checks the graph against the `PublicAPI.*.txt` baselines — docs/plan/25 § 2.1.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Vixen.DocGen" /> reads source symbols and <c>Vixen.ApiCheck</c> reads compiled
///         metadata. They are looking at the same surface for different reasons, so they have to
///         agree about what is in it — and when they do not, the interesting direction is the one
///         where the graph has <em>fewer</em> types than the baseline, because that is what a
///         generator which stopped running looks like.
///     </para>
///     <para>
///         ⚠ <b>Only type declarations are compared, not members.</b> The baseline's member lines
///         carry a signature format this tool does not reproduce, and reproducing it here would make
///         two formatters that have to be kept identical. Types are enough to catch the failure this
///         exists for: an assembly, or a generator's whole output, going missing.
///     </para>
/// </remarks>
static class BaselineAgreement {
    /// <summary>What the comparison found, per assembly.</summary>
    /// <param name="Assembly">The assembly both sides were read from.</param>
    /// <param name="MissingFromGraph">Baselined types the graph does not have — the dangerous direction.</param>
    /// <param name="MissingFromBaseline">Types in the graph with no baseline entry.</param>
    public sealed record Disagreement(
        string Assembly,
        IReadOnlyList<string> MissingFromGraph,
        IReadOnlyList<string> MissingFromBaseline
    );

    public static IReadOnlyList<Disagreement> Compare(string repositoryRoot, IReadOnlyList<DocNode> nodes) {
        var disagreements = new List<Disagreement>();

        var byAssembly = nodes
            .Where(node => node.IsPackable)
            .GroupBy(node => node.Assembly, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var directory in Directory
            .EnumerateFiles(repositoryRoot, "PublicAPI.Unshipped.txt", SearchOption.AllDirectories)
            .Where(path => IsSource(Path.GetRelativePath(repositoryRoot, path)))
            .Select(path => Path.GetDirectoryName(path)!)
            .OrderBy(path => path, StringComparer.Ordinal)) {
            var assembly = Path.GetFileName(directory);
            var baselined = ReadTypes(directory);

            if (baselined.Count == 0) {
                continue;
            }

            // Both sides through the same normalisation, or `Pool<TKey, TValue>` and
            // `Pool<TKey,TValue>` read as two different types and every generic looks missing.
            var graphed = byAssembly.TryGetValue(assembly, out var assemblyNodes)
                ? assemblyNodes.Select(node => Normalize(node.QualifiedName)).ToHashSet(StringComparer.Ordinal)
                : [];

            var missingFromGraph = baselined.Where(name => !graphed.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var missingFromBaseline = graphed.Where(name => !baselined.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (missingFromGraph.Count > 0 || missingFromBaseline.Count > 0) {
                disagreements.Add(new Disagreement(assembly, missingFromGraph, missingFromBaseline));
            }
        }

        return disagreements;
    }

    /// <summary>
    ///     Whether a baseline found by the walk is the project's own, rather than a copy of it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A recursive walk of a checkout finds more baselines than there are projects</b>, and
    ///     the extras are stale by construction. This repository keeps its agent worktrees under
    ///     <c>.claude/worktrees/</c>, so the main checkout carries nine copies of every
    ///     <c>PublicAPI.Unshipped.txt</c> — eight of them from other branches. Read anyway, they made
    ///     this check report seven assemblies as disagreeing whose baselines are in fact correct: the
    ///     newest branch's types against another branch's file. Build outputs are the same mistake in
    ///     a smaller way, which is why <c>ApiCheckedProjects()</c> filters them too.
    /// </remarks>
    internal static bool IsSource(string relativePath) {
        var segments = relativePath.Replace('\\', '/').Split('/');

        return !segments.Any(segment =>
            segment.StartsWith('.')
            || string.Equals(segment, "bin", StringComparison.Ordinal)
            || string.Equals(segment, "obj", StringComparison.Ordinal)
            || string.Equals(segment, "artifacts", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The type names a baseline declares. A type's own line is the one with an arrow and a type
    ///     keyword after it — <c>Vixen.Core.DisposeBag -&gt; sealed class</c>.
    /// </summary>
    internal static HashSet<string> ReadTypes(string projectDirectory) {
        var types = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" }) {
            var path = Path.Combine(projectDirectory, file);

            if (!File.Exists(path)) {
                continue;
            }

            foreach (var line in File.ReadLines(path)) {
                if (line.StartsWith("*REMOVED*", StringComparison.Ordinal)) {
                    continue;
                }

                var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);

                if (arrow < 0) {
                    continue;
                }

                var right = line[(arrow + 4)..].Trim();
                var left = StripModifiers(line[..arrow].Trim());

                // A method's line also ends in `-> something`, and its left side is the only one with
                // a parameter list. Type parameters are not one: `Pool<TKey, TValue>` is a type.
                // The same C# 14 extension block the graph leaves out (SymbolReader): the baseline
                // records both its container's unspeakable name and the `extension(…)` declaration,
                // and neither is API a reader can name.
                if (!IsTypeDeclaration(right) || left.Contains('(') || left.Contains(">$", StringComparison.Ordinal)) {
                    continue;
                }

                types.Add(Normalize(left));
            }
        }

        return types;
    }

    /// <summary>
    ///     Drops the modifiers the analyzer's format puts before the name — <c>static</c>,
    ///     <c>const</c>, <c>abstract</c> and friends — so what is left is the name itself.
    /// </summary>
    static string StripModifiers(string left) {
        string[] modifiers = [
            "static ", "const ", "readonly ", "abstract ", "sealed ", "virtual ", "override ",
            "extension "
        ];

        var result = left;
        bool stripped;

        do {
            stripped = false;

            foreach (var modifier in modifiers) {
                if (result.StartsWith(modifier, StringComparison.Ordinal)) {
                    result = result[modifier.Length..];
                    stripped = true;
                }
            }
        } while (stripped);

        return result;
    }

    /// <summary>
    ///     `sealed class`, `readonly struct`, `enum : byte`, `interface`, … — the shapes the right
    ///     side of a type's own line takes.
    /// </summary>
    static bool IsTypeDeclaration(string right) {
        var words = right.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);

        return words.Any(word => word is "class" or "struct" or "interface" or "enum" or "delegate" or "record");
    }

    /// <summary>
    ///     One spelling for both sides. What differs between them is whitespace inside the angle
    ///     brackets, and <b>variance</b>: the baseline writes <c>IReadOnlySignal&lt;out T&gt;</c>
    ///     where the symbol writes <c>IReadOnlySignal&lt;T&gt;</c>. Neither is a different type, and
    ///     a comparison that thought so would report every covariant interface in the engine as
    ///     missing.
    /// </summary>
    static string Normalize(string name) {
        var result = name;

        foreach (var variance in new[] { "<out ", "<in ", ", out ", ", in ", ",out ", ",in " }) {
            result = result.Replace(variance, variance[0] == '<' ? "<" : ",", StringComparison.Ordinal);
        }

        return result.Replace(" ", string.Empty, StringComparison.Ordinal);
    }
}
