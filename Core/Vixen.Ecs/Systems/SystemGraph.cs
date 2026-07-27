// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;

namespace Vixen.Ecs.Systems;

/// <summary>One system as the graph sees it.</summary>
/// <param name="System">The system itself.</param>
/// <param name="Phase">Which phase it belongs to.</param>
/// <param name="Access">What it reads and writes.</param>
/// <param name="Order">Its position in the phase's execution order.</param>
public sealed record SystemNode(ISystem System, SystemPhase Phase, SystemAccess Access, int Order) {
    /// <summary>The indices, within the phase, of the systems whose work this must wait for.</summary>
    public IReadOnlyList<int> DependsOn { get; internal set; } = [];

    /// <summary>The system's type name, which is what a dump shows.</summary>
    public string Name => System.GetType().Name;
}

/// <summary>
///     Systems, ordered per phase, with the data dependencies between them worked out.
/// </summary>
/// <remarks>
///     <para>
///         Two passes. First the explicit ordering — <see cref="UpdateBeforeAttribute" /> and
///         <see cref="UpdateAfterAttribute" /> — is resolved into a total order by a topological
///         sort, with registration order breaking ties so that a graph with no constraints runs in
///         the order it was written. Then, walking that order, each system takes a dependency on
///         every earlier system it conflicts with.
///     </para>
///     <para>
///         The result is a schedule, not a sequence: systems with disjoint writes get the same
///         dependency and run at the same time. A phase costs its critical path.
///     </para>
///     <para>
///         Only the <em>last</em> conflicting predecessor per chain would be enough, and computing
///         the transitive reduction is not worth it — the job system takes a handful of handles for
///         nothing, and a reduction that is wrong loses an edge, which is a data race.
///     </para>
/// </remarks>
public sealed class SystemGraph {
    readonly Dictionary<SystemPhase, List<SystemNode>> byPhase = [];

    /// <summary>The phases that have systems in them, in execution order.</summary>
    public IEnumerable<SystemPhase> Phases =>
        Enum.GetValues<SystemPhase>().Where(phase => byPhase.ContainsKey(phase));

    /// <summary>The systems in a phase, in execution order.</summary>
    /// <param name="phase">The phase.</param>
    /// <returns>Its systems, or an empty list.</returns>
    public IReadOnlyList<SystemNode> InPhase(SystemPhase phase) =>
        byPhase.TryGetValue(phase, out var nodes) ? nodes : [];

    /// <summary>Every system, in phase order and then in execution order.</summary>
    public IEnumerable<SystemNode> All => Phases.SelectMany(InPhase);

    /// <summary>Builds the graph from systems in registration order.</summary>
    /// <param name="systems">The systems.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The ordering attributes contain a cycle. The message names every system in it, because
    ///     "there is a cycle" is not something anybody can act on.
    /// </exception>
    public static SystemGraph Build(IReadOnlyList<ISystem> systems) {
        ArgumentNullException.ThrowIfNull(systems);

        var graph = new SystemGraph();
        var grouped = new Dictionary<SystemPhase, List<ISystem>>();

        foreach (var system in systems) {
            var phase = system.GetType().GetCustomAttribute<UpdateInGroupAttribute>(inherit: true)?.Phase
                ?? SystemPhase.Update;

            if (!grouped.TryGetValue(phase, out var members)) {
                grouped[phase] = members = [];
            }

            members.Add(system);
        }

        foreach (var (phase, members) in grouped) {
            graph.byPhase[phase] = Order(phase, members);
        }

        return graph;
    }

    static List<SystemNode> Order(SystemPhase phase, List<ISystem> members) {
        var index = new Dictionary<Type, int>();

        for (var position = 0; position < members.Count; position++) {
            index[members[position].GetType()] = position;
        }

        var successors = new List<int>[members.Count];
        var incoming = new int[members.Count];

        for (var position = 0; position < members.Count; position++) {
            successors[position] = [];
        }

        for (var position = 0; position < members.Count; position++) {
            var type = members[position].GetType();

            foreach (var attribute in type.GetCustomAttributes<UpdateBeforeAttribute>(inherit: true)) {
                if (index.TryGetValue(attribute.SystemType, out var after)) {
                    Link(position, after, successors, incoming);
                }
            }

            foreach (var attribute in type.GetCustomAttributes<UpdateAfterAttribute>(inherit: true)) {
                if (index.TryGetValue(attribute.SystemType, out var before)) {
                    Link(before, position, successors, incoming);
                }
            }
        }

        // Kahn's algorithm with a ready set ordered by registration position, so a graph with no
        // constraints comes out in the order it was written and a graph with some constraints keeps
        // that order everywhere the constraints do not speak.
        var ready = new PriorityQueue<int, int>();

        for (var position = 0; position < members.Count; position++) {
            if (incoming[position] == 0) {
                ready.Enqueue(position, position);
            }
        }

        var ordered = new List<SystemNode>(members.Count);

        while (ready.TryDequeue(out var position, out _)) {
            var system = members[position];

            var access = system is IDeclaredAccess declared
                ? declared.Access
                : SystemAccess.FromAttributes(system.GetType());

            ordered.Add(new(system, phase, access, ordered.Count));

            foreach (var successor in successors[position]) {
                if (--incoming[successor] == 0) {
                    ready.Enqueue(successor, successor);
                }
            }
        }

        if (ordered.Count != members.Count) {
            var stuck = Enumerable.Range(0, members.Count)
                .Where(position => incoming[position] > 0)
                .Select(position => members[position].GetType().Name);

            throw new InvalidOperationException(
                $"The {phase} systems contain an ordering cycle. These could never run: "
                + $"{string.Join(", ", stuck)}. One of the [UpdateBefore] or [UpdateAfter] "
                + "attributes on them has to go."
            );
        }

        Connect(ordered);
        return ordered;
    }

    /// <summary>
    ///     Records that one system must run before another. A constraint naming a system that is not
    ///     in this phase never reaches here: it is ignored rather than treated as an error, because a
    ///     build that registers half its systems should not fail to boot on the other half's
    ///     attributes.
    /// </summary>
    static void Link(int before, int after, List<int>[] successors, int[] incoming) {
        if (before == after) {
            return;
        }

        successors[before].Add(after);
        incoming[after]++;
    }

    static void Connect(List<SystemNode> ordered) {
        for (var position = 0; position < ordered.Count; position++) {
            var dependencies = new List<int>();

            for (var earlier = 0; earlier < position; earlier++) {
                if (ordered[position].Access.ConflictsWith(ordered[earlier].Access)) {
                    dependencies.Add(earlier);
                }
            }

            ordered[position].DependsOn = dependencies;
        }
    }

    /// <summary>Renders the graph as Graphviz DOT.</summary>
    /// <returns>The DOT source.</returns>
    public string ToDot() {
        var text = new StringBuilder("digraph systems {\n  rankdir=LR;\n  node [shape=box];\n");

        foreach (var phase in Phases) {
            text.Append("  subgraph cluster_").Append(phase).Append(" {\n    label=\"").Append(phase).Append("\";\n");

            foreach (var node in InPhase(phase)) {
                text.Append("    \"").Append(phase).Append('.').Append(node.Name).Append("\" [label=\"")
                    .Append(node.Name).Append("\\n").Append(node.Access).Append("\"];\n");
            }

            text.Append("  }\n");
        }

        foreach (var phase in Phases) {
            var nodes = InPhase(phase);

            foreach (var node in nodes) {
                foreach (var dependency in node.DependsOn) {
                    text.Append("  \"").Append(phase).Append('.').Append(nodes[dependency].Name)
                        .Append("\" -> \"").Append(phase).Append('.').Append(node.Name).Append("\";\n");
                }
            }
        }

        text.Append("}\n");
        return text.ToString();
    }

    /// <summary>Renders the graph as Mermaid, which pastes straight into a pull request.</summary>
    /// <returns>The Mermaid source.</returns>
    public string ToMermaid() {
        var text = new StringBuilder("flowchart LR\n");

        foreach (var phase in Phases) {
            var nodes = InPhase(phase);
            text.Append("  subgraph ").Append(phase).Append('\n');

            foreach (var node in nodes) {
                text.Append("    ").Append(phase).Append('_').Append(node.Name).Append('[').Append(node.Name)
                    .Append("]\n");
            }

            text.Append("  end\n");

            foreach (var node in nodes) {
                foreach (var dependency in node.DependsOn) {
                    text.Append("  ").Append(phase).Append('_').Append(nodes[dependency].Name).Append(" --> ")
                        .Append(phase).Append('_').Append(node.Name).Append('\n');
                }
            }
        }

        return text.ToString();
    }
}
