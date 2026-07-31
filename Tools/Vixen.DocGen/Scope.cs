// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.DocGen;

/// <summary>What the graph documents, and what it deliberately leaves out.</summary>
/// <remarks>
///     docs/plan/25 § 6.3 settles the first half of this: scope by <em>area</em> rather than by
///     packability, because <c>graph-node</c>, <c>importer</c> and <c>replicated-component</c> live in
///     assemblies with no <c>PublicAPI.*.txt</c> — a tree filtered on baselines would document the
///     engine and hide the editor. This is the other half: which areas.
/// </remarks>
static class Scope {
    /// <summary>
    ///     Areas whose public types are the product: the engine, its platforms, the editor, the
    ///     tools, and the shader compiler.
    /// </summary>
    static readonly HashSet<string> DocumentedAreas = new(StringComparer.Ordinal) {
        "Core", "Platform", "Editor", "Tools", "Raven"
    };

    /// <summary>Whether a project's types belong in the graph.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Samples and benchmarks are demonstrations, not surface.</b> They come back into the
    ///         documentation as examples and sample pages (§ 2.8), which is where a reader wants
    ///         them — nobody looks up the API of <c>03-PbrShowcase</c>. Left in, their top-level
    ///         <c>Program</c> classes are also seven types with one name.
    ///     </para>
    ///     <para>
    ///         <b>Test projects are not surface either</b>, for the same reason `CheckApi` does not
    ///         baseline them.
    ///     </para>
    /// </remarks>
    public static bool IsDocumented(string area, string projectName) =>
        DocumentedAreas.Contains(area)
        && !projectName.EndsWith(".Tests", StringComparison.Ordinal);

    /// <summary>
    ///     Picks one node per URL when a type genuinely exists in more than one assembly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A documentation id is unique inside an assembly, not across a solution.</b> This
    ///         repository links shared source across project boundaries where an assembly boundary
    ///         would be the wrong shape — <c>Vixen.Core.Syntax</c> is compiled into
    ///         <c>Vixen.Ui.Markup.Generators</c> as well as into its own assembly — so the same
    ///         qualified name is two symbols, and they would claim one page.
    ///     </para>
    ///     <para>
    ///         The rule is <b>the packable assembly wins</b>, because that is the copy a consumer
    ///         references and the one <c>CheckApi</c> has a baseline for; ties break on the assembly
    ///         name, ordinally, so the URL a type gets does not depend on the order projects loaded.
    ///         The assemblies that lost are kept on the survivor rather than thrown away.
    ///     </para>
    /// </remarks>
    public static (IReadOnlyList<DocNode> Nodes, int Merged) Deduplicate(IEnumerable<DocNode> nodes) {
        var kept = new List<DocNode>();
        var merged = 0;

        foreach (var group in nodes.GroupBy(node => node.Slug, StringComparer.Ordinal)) {
            var candidates = group
                .OrderByDescending(node => node.IsPackable)
                .ThenBy(node => node.Assembly, StringComparer.Ordinal)
                .ToList();

            if (candidates.Count == 1) {
                kept.Add(candidates[0]);

                continue;
            }

            merged += candidates.Count - 1;

            kept.Add(candidates[0] with {
                AlsoIn = [.. candidates.Skip(1).Select(node => node.Assembly)]
            });
        }

        return (kept, merged);
    }
}
