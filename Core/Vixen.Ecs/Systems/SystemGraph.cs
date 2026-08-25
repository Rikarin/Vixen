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

/// <summary>Where one system type lands in a frame: which phase, and where in it.</summary>
/// <param name="SystemType">The system's type.</param>
/// <param name="Phase">Which phase it belongs to.</param>
/// <param name="Order">Its position in the phase's execution order.</param>
/// <remarks>
///     <see cref="SystemNode" /> without the instance and without the access, because those are the
///     two things that need one. See <see cref="SystemPlan" /> for why that distinction is the whole
///     point.
/// </remarks>
public sealed record SystemPlacement(Type SystemType, SystemPhase Phase, int Order) {
    /// <summary>The system's short name, which is what a report calls it.</summary>
    public string Name => SystemType.Name;
}

/// <summary>The order a set of system types would run in, worked out without building any of them.</summary>
/// <param name="Placements">Every system, in phase order and then in execution order.</param>
/// <param name="Unsatisfied">
///     The ordering attributes that do nothing, one readable line each. ⚠ <b>Read this out.</b> An
///     <see cref="UpdateBeforeAttribute" /> naming a system that is not in the set is silently
///     dropped by <see cref="SystemGraph.Build" /> — deliberately, see <c>Link</c> — so a typo in one
///     is a system that runs in the wrong place and never says so.
/// </param>
/// <remarks>
///     <para>
///         <b>Why a second shape rather than a <see cref="SystemGraph" />.</b> Everything the
///         topological sort reads — the phase, the <see cref="UpdateBeforeAttribute" /> and
///         <see cref="UpdateAfterAttribute" /> edges — is metadata on the type. Only the access is
///         not: <see cref="IDeclaredAccess" /> is an instance property, for systems whose component
///         set is not known until construction. So the order is answerable about types alone and the
///         parallel schedule is not, and a tool that has a project's assembly but not its services —
///         <c>vixen doctor systems</c> is the one — can have the first without pretending to the
///         second.
///     </para>
///     <para>
///         ⚠ <b>There are no <c>DependsOn</c> edges here and that is not an omission to be fixed
///         later.</b> Those come from <see cref="SystemAccess.ConflictsWith" />, and an undeclared
///         access conflicts with everything — so guessing at it would not produce a cautious answer,
///         it would produce a confident wrong one.
///     </para>
/// </remarks>
public sealed record SystemPlan(IReadOnlyList<SystemPlacement> Placements, IReadOnlyList<string> Unsatisfied) {
    /// <summary>The phases that have systems in them, in execution order.</summary>
    public IEnumerable<SystemPhase> Phases =>
        Enum.GetValues<SystemPhase>().Where(phase => Placements.Any(placement => placement.Phase == phase));

    /// <summary>The systems in a phase, in execution order.</summary>
    /// <param name="phase">The phase.</param>
    /// <returns>Its systems, or an empty list.</returns>
    public IReadOnlyList<SystemPlacement> InPhase(SystemPhase phase) =>
        [.. Placements.Where(placement => placement.Phase == phase)];
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
            var phase = PhaseOf(system.GetType());

            if (!grouped.TryGetValue(phase, out var members)) {
                grouped[phase] = members = [];
            }

            members.Add(system);
        }

        foreach (var (phase, members) in grouped) {
            var types = new Type[members.Count];

            for (var position = 0; position < members.Count; position++) {
                types[position] = members[position].GetType();
            }

            var ordered = new List<SystemNode>(members.Count);

            foreach (var position in Sort(phase, types, everywhere: null, unsatisfied: null)) {
                var system = members[position];

                var access = system is IDeclaredAccess declared
                    ? declared.Access
                    : SystemAccess.FromAttributes(system.GetType());

                ordered.Add(new(system, phase, access, ordered.Count));
            }

            Connect(ordered);
            graph.byPhase[phase] = ordered;
        }

        return graph;
    }

    /// <summary>Works out the order a set of system types would run in, building none of them.</summary>
    /// <param name="systemTypes">The system types, in registration order.</param>
    /// <returns>Where each lands, and the ordering attributes that turned out to do nothing.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The ordering attributes contain a cycle — the same refusal, with the same message, that
    ///     <see cref="Build" /> raises, because it is the same sort.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is <see cref="Build" />'s own sort and not a second implementation of it.</b>
    ///         The two share <c>Sort</c> exactly so that a tool cannot report an order the runner
    ///         will not produce — which is the failure mode that makes a diagnostic worse than none.
    ///     </para>
    ///     <para>
    ///         What it adds over <see cref="Build" /> is the <see cref="SystemPlan.Unsatisfied" />
    ///         list. The runner drops an edge naming a system it does not have, on purpose and for a
    ///         good reason; but "on purpose" is a property of the scheduler, not of the project, and
    ///         a person whose <c>[UpdateAfter]</c> silently does nothing wants to be told.
    ///     </para>
    /// </remarks>
    public static SystemPlan Plan(IReadOnlyList<Type> systemTypes) {
        ArgumentNullException.ThrowIfNull(systemTypes);

        var everywhere = new Dictionary<Type, SystemPhase>();
        var grouped = new Dictionary<SystemPhase, List<Type>>();

        foreach (var type in systemTypes) {
            var phase = PhaseOf(type);
            everywhere[type] = phase;

            if (!grouped.TryGetValue(phase, out var members)) {
                grouped[phase] = members = [];
            }

            members.Add(type);
        }

        var placements = new List<SystemPlacement>(systemTypes.Count);
        var unsatisfied = new List<string>();

        // Phase order rather than dictionary order, so the plan reads down the frame.
        foreach (var phase in Enum.GetValues<SystemPhase>()) {
            if (!grouped.TryGetValue(phase, out var members)) {
                continue;
            }

            var order = 0;

            foreach (var position in Sort(phase, members, everywhere, unsatisfied)) {
                placements.Add(new(members[position], phase, order++));
            }
        }

        return new(placements, unsatisfied);
    }

    /// <summary>Which phase a system type belongs to. Without an attribute, <see cref="SystemPhase.Update" />.</summary>
    static SystemPhase PhaseOf(Type systemType) =>
        systemType.GetCustomAttribute<UpdateInGroupAttribute>(inherit: true)?.Phase ?? SystemPhase.Update;

    /// <summary>Topologically sorts one phase's systems, and returns their positions in run order.</summary>
    /// <param name="phase">The phase being sorted, which the cycle message names.</param>
    /// <param name="members">That phase's system types, in registration order.</param>
    /// <param name="everywhere">
    ///     Every system type under consideration and its phase, or <see langword="null" /> when
    ///     nobody is collecting <paramref name="unsatisfied" />. It is what tells a constraint naming
    ///     an absent system apart from one naming a system in another phase — two different mistakes
    ///     with the same symptom.
    /// </param>
    /// <param name="unsatisfied">Told about every edge that was dropped, or <see langword="null" />.</param>
    static List<int> Sort(
        SystemPhase phase,
        IReadOnlyList<Type> members,
        IReadOnlyDictionary<Type, SystemPhase>? everywhere,
        List<string>? unsatisfied
    ) {
        var index = new Dictionary<Type, int>();

        for (var position = 0; position < members.Count; position++) {
            index[members[position]] = position;
        }

        var successors = new List<int>[members.Count];
        var incoming = new int[members.Count];

        for (var position = 0; position < members.Count; position++) {
            successors[position] = [];
        }

        for (var position = 0; position < members.Count; position++) {
            var type = members[position];

            foreach (var attribute in type.GetCustomAttributes<UpdateBeforeAttribute>(inherit: true)) {
                if (index.TryGetValue(attribute.SystemType, out var after)) {
                    Link(position, after, successors, incoming);
                } else if (unsatisfied is not null) {
                    unsatisfied.Add(Explain(type, "UpdateBefore", attribute.SystemType, phase, everywhere));
                }
            }

            foreach (var attribute in type.GetCustomAttributes<UpdateAfterAttribute>(inherit: true)) {
                if (index.TryGetValue(attribute.SystemType, out var before)) {
                    Link(before, position, successors, incoming);
                } else if (unsatisfied is not null) {
                    unsatisfied.Add(Explain(type, "UpdateAfter", attribute.SystemType, phase, everywhere));
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

        var ordered = new List<int>(members.Count);

        while (ready.TryDequeue(out var position, out _)) {
            ordered.Add(position);

            foreach (var successor in successors[position]) {
                if (--incoming[successor] == 0) {
                    ready.Enqueue(successor, successor);
                }
            }
        }

        if (ordered.Count != members.Count) {
            var stuck = Enumerable.Range(0, members.Count)
                .Where(position => incoming[position] > 0)
                .Select(position => members[position].Name);

            throw new InvalidOperationException(
                $"The {phase} systems contain an ordering cycle. These could never run: "
                + $"{string.Join(", ", stuck)}. One of the [UpdateBefore] or [UpdateAfter] "
                + "attributes on them has to go."
            );
        }

        return ordered;
    }

    /// <summary>Says, in a sentence somebody can act on, why one ordering attribute did nothing.</summary>
    /// <remarks>
    ///     The two cases are worth telling apart. A named system that is nowhere in the set is
    ///     usually a system somebody forgot to add, or a rename. A named system that is in the set
    ///     but in another phase is a subtler thing: the constraint is redundant where it agrees with
    ///     the phase order and impossible where it does not, and either way the attribute is not
    ///     doing what its author thought.
    /// </remarks>
    static string Explain(
        Type declaring,
        string attribute,
        Type named,
        SystemPhase phase,
        IReadOnlyDictionary<Type, SystemPhase>? everywhere
    ) =>
        everywhere is not null && everywhere.TryGetValue(named, out var other)
            ? $"{declaring.Name}'s [{attribute}(typeof({named.Name}))] does nothing: {named.Name} is in "
            + $"the {other} phase and {declaring.Name} is in {phase}, and ordering only ever applies "
            + "within a phase. Phases already run in their declared order."
            : $"{declaring.Name}'s [{attribute}(typeof({named.Name}))] does nothing: no {named.Name} "
            + $"is in this set, so the constraint is dropped and {declaring.Name} runs wherever "
            + "registration order puts it.";

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
