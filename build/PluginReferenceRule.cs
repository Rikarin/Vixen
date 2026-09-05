// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Vixen.Build;

/// <summary>
///     Doc 48's exit criterion 10, as a function of project files rather than as a block inside a
///     Nuke target.
/// </summary>
/// <remarks>
///     <para>
///         <b>Extracted so that the rule's answer can be read without running the gate.</b> The rule
///         lived inside <c>CheckArchitecture</c>'s <c>Executes</c>, which means the only way to learn
///         what it says is to run a target that compiles the solution in Release — and the batch that
///         wrote it was forbidden to run one, so it shipped a rule nobody had ever seen produce an
///         answer. This repository's own standard is to ask what a gate prints on the day it does not
///         run; a rule that has never run has not answered that question even once.
///     </para>
///     <para>
///         ⚠ <b>So the logic is a pure function of paths and the gate is one caller of it.</b>
///         <c>Build.ArchitectureRules.cs</c> calls it over the globbed solution;
///         <c>PluginReferenceRuleTests</c> compiles this same file into a test assembly and calls it
///         over the repository tree it was compiled from and over synthetic fixtures. Two callers of
///         one function, not two transcriptions of one idea — which is the distinction this
///         workstream keeps having to make, most recently in § D1's "there are not two compilers here
///         to disagree".
///     </para>
///     <para>
///         <b>Project files, not assemblies, and that is the whole point of the criterion.</b>
///         <c>Assembly.GetReferencedAssemblies</c> lists what the compiler emitted a reference for,
///         so a <c>ProjectReference</c> nobody has used yet is invisible to it. "References
///         <c>Vixen.Editor.App</c> in no build" is a claim about a reference, and a reference lives in
///         a project file.
///     </para>
/// </remarks>
static class PluginReferenceRule {
    /// <summary>The contract an editor plugin is written against, and what identifies one.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived rather than listed, because a list of plugin names is the exact-equality roll
    ///     call this repository keeps going red on a merge.</b> A plugin is a project that names the
    ///     contract; eight do today and a ninth is covered the day it is added, with no edit here.
    /// </remarks>
    public const string Contract = "Vixen.Editor.Plugin";

    /// <summary>The editor application, which a plugin may not link in any build.</summary>
    /// <remarks>
    ///     <b>Transitive, which is what "in no build" means.</b> A plugin that reached the application
    ///     through one intermediate would ship the application in the plugin's folder just as surely
    ///     as a direct reference does, and doc 36 § F2's complaint — an extension surface whose own
    ///     authors never had to use it — is answered only if the whole closure is clean.
    /// </remarks>
    public const string Application = "Vixen.Editor.App";

    /// <summary>Every project's direct <c>ProjectReference</c>s, by assembly name.</summary>
    /// <param name="projects">Paths to the project files to read.</param>
    /// <returns>A map from project name to the names it references directly.</returns>
    /// <remarks>
    ///     ⚠ <b>Names rather than paths, and merged rather than keyed uniquely.</b> Two project files
    ///     with one name would throw out of a plain <c>ToDictionary</c> — a build failure with a
    ///     message about a duplicate key, which is a rotten way to learn about a naming collision — so
    ///     their edges are unioned. A backslash in an <c>Include</c> is a separator on the machine the
    ///     project was authored on and an ordinary character here, so it is normalised rather than
    ///     trusted.
    /// </remarks>
    public static Dictionary<string, HashSet<string>> Edges(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        Dictionary<string, HashSet<string>> edges = new(StringComparer.Ordinal);

        foreach (var project in projects) {
            var name = Path.GetFileNameWithoutExtension(project);

            if (!edges.TryGetValue(name, out var set)) {
                set = new(StringComparer.Ordinal);
                edges[name] = set;
            }

            foreach (var element in XDocument.Load(project).Descendants("ProjectReference")) {
                var include = element.Attribute("Include")?.Value;

                if (!string.IsNullOrWhiteSpace(include)) {
                    set.Add(Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));
                }
            }
        }

        return edges;
    }

    /// <summary>The projects this rule holds to the ban.</summary>
    /// <param name="edges">Every project's direct references, from <see cref="Edges" />.</param>
    /// <returns>The plugin project names, ordered.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A test project may reference anything</b> — it is not shipped, and forbidding it
    ///         would mean the plugin contract could not be tested against the host it plugs into.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the application itself is not a plugin, which is a correction rather than a
    ///         nicety.</b> <c>Vixen.Editor.App</c> references the contract — it is the thing that
    ///         <em>hosts</em> plugins — so the plain "names the contract" test classified the host
    ///         application as the ninth plugin. It could never have produced a violation, because
    ///         nothing reaches itself through a reference graph with no cycles, and that is exactly
    ///         why it went unnoticed: a rule's subject set is the half of it nothing checks. The
    ///         count in the gate's own log said nine, and eight of them were plugins.
    ///     </para>
    /// </remarks>
    public static List<string> Plugins(Dictionary<string, HashSet<string>> edges) {
        ArgumentNullException.ThrowIfNull(edges);

        return edges
            .Where(entry => entry.Value.Contains(Contract, StringComparer.Ordinal))
            .Where(entry => !entry.Key.EndsWith(".Tests", StringComparison.Ordinal))
            .Where(entry => !string.Equals(entry.Key, Application, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether one project reaches another through any chain of project references.</summary>
    /// <param name="edges">Every project's direct references, from <see cref="Edges" />.</param>
    /// <param name="from">The project to walk from.</param>
    /// <param name="target">The project to look for.</param>
    /// <returns><c>true</c> if a chain of references leads from one to the other.</returns>
    /// <remarks>
    ///     A plain depth-first walk with a visited set, which is also what makes it safe on a graph
    ///     with a cycle — MSBuild refuses one, but this runs over project files rather than over a
    ///     restore, so it cannot assume the graph is acyclic.
    /// </remarks>
    public static bool Reaches(Dictionary<string, HashSet<string>> edges, string from, string target) {
        ArgumentNullException.ThrowIfNull(edges);

        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new(edges.TryGetValue(from, out var direct) ? direct : []);

        while (pending.Count > 0) {
            var next = pending.Pop();

            if (string.Equals(next, target, StringComparison.Ordinal)) {
                return true;
            }

            if (!seen.Add(next)) {
                continue;
            }

            if (edges.TryGetValue(next, out var further)) {
                foreach (var reference in further) {
                    pending.Push(reference);
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Why this rule would be checking nothing over these projects, or <c>null</c> if it is not.
    /// </summary>
    /// <param name="edges">Every project's direct references, from <see cref="Edges" />.</param>
    /// <returns>The message to fail with, or <c>null</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument, and it is the whole reason this rule is not three lines.</b> A
    ///         rule whose subject set is empty passes silently, which is how a gate becomes
    ///         decoration: if the contract is renamed, nothing is a plugin any more and every plugin
    ///         is clean by vacuity.
    ///     </para>
    ///     <para>
    ///         <b>And the other half: an edge into the application has to be something this walk can
    ///         see.</b> <c>Vixen.Editor.Host</c> has one and is not a plugin, which is what makes a
    ///         clean result a measurement rather than a property of a walk that finds no edges at all.
    ///     </para>
    /// </remarks>
    public static string? Vacuity(Dictionary<string, HashSet<string>> edges) {
        ArgumentNullException.ThrowIfNull(edges);

        if (Plugins(edges).Count == 0) {
            return $"No project references {Contract}, so the plugin rule checked nothing. Either the contract "
                + "was renamed — update PluginReferenceRule.Contract — or the editor no longer has plugins.";
        }

        if (!edges.Keys.Any(name => Reaches(edges, name, Application))) {
            return $"Nothing in these projects reaches {Application}, which cannot be true while the editor is "
                + "built — so the reference walk is not finding edges and the plugin rule cannot fire.";
        }

        return null;
    }

    /// <summary>Every plugin that reaches the editor application, as the messages to fail with.</summary>
    /// <param name="edges">Every project's direct references, from <see cref="Edges" />.</param>
    /// <returns>One message per offending plugin, ordered; empty when the rule holds.</returns>
    public static List<string> Violations(Dictionary<string, HashSet<string>> edges) {
        ArgumentNullException.ThrowIfNull(edges);

        return Plugins(edges)
            .Where(plugin => Reaches(edges, plugin, Application))
            .Select(plugin =>
                $"{plugin} references {Contract} and reaches {Application}. A plugin links the application in "
                + "no build — doc 48 § D14 and its exit criterion 10 — because everything a plugin needs from "
                + "the host comes through PluginServices, and a plugin that reaches around the contract stops "
                + "being evidence that the contract is wide enough."
            )
            .ToList();
    }
}
