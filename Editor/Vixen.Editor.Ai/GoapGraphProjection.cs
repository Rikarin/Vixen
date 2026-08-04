// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Ai;

/// <summary>The GOAP graph, derived from the tables and drawn read-only.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Derived, and this is the point at which "the node editor is mandatory" has to be
///         answered honestly.</b> crashkonijn ships a <i>GraphViewer</i>, not a graph editor, and that
///         is correct: the edges of a GOAP graph are not authored, they are <b>computed</b> from which
///         effects satisfy which conditions. Drawing them by hand would be authoring the same fact
///         twice, and the two copies would disagree the first time somebody edited a condition.
///     </para>
///     <para>
///         So this projects a compiled <see cref="GoapDomain" /> onto <c>NodeCanvas</c> and there is
///         no command stack over it — which <c>NodeGraphView</c> already supports, since <i>"no stack
///         means read-only"</i>. Every edit goes through the tables.
///     </para>
///     <para>
///         The layout is by <b>depth from a goal</b>: goals on the left, then the actions that serve
///         them, then the actions that serve those. That is the shape the search walks, so a domain
///         whose plan is four steps deep looks four steps deep.
///     </para>
/// </remarks>
public sealed class GoapGraphProjection {
    readonly Dictionary<int, GraphNode> actions = [];
    readonly Dictionary<int, GraphNode> goals = [];

    /// <summary>What the canvas shows.</summary>
    public NodeGraph Graph { get; private set; } = new();

    /// <summary>How deep each action sits, in steps from the nearest goal.</summary>
    public IReadOnlyDictionary<int, int> Depths => depths;

    readonly Dictionary<int, int> depths = [];

    /// <summary>Rebuilds the picture from a domain.</summary>
    /// <param name="domain">The compiled domain.</param>
    /// <param name="plan">A plan to highlight, or null.</param>
    /// <returns>The graph, which is a fresh one every time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domain" /> is null.</exception>
    public NodeGraph Project(GoapDomain domain, GoapPlan? plan = null) {
        ArgumentNullException.ThrowIfNull(domain);

        Graph = new();
        actions.Clear();
        goals.Clear();
        Rank(domain);

        var running = plan is not null ? plan.Steps.ToArray() : [];
        var rows = new Dictionary<int, int>();

        for (var index = 0; index < domain.Goals.Length; index++) {
            var goal = domain.Goals[index];
            var box = new GraphNode(goal.Name.ToString()) {
                Position = new(0f, Row(rows, 0) * 90f),
                Width = 180f,
                Tag = goal,
                Badge = goal.Priority.ToString(CultureInfo.InvariantCulture),
                Accent = "goal"
            };

            foreach (var condition in goal.Conditions) {
                box.Attachments.Add(new(Describe(domain, condition), string.Empty, "condition"));
            }

            box.AddInput("wants");
            Graph.AddNode(box);
            goals[index] = box;
        }

        for (var index = 0; index < domain.Count; index++) {
            var action = domain[index];
            var depth = depths.GetValueOrDefault(index, 0) + 1;
            var box = new GraphNode(action.Name.ToString()) {
                Position = new(depth * 220f, Row(rows, depth) * 90f),
                Width = 180f,
                Tag = action,
                Badge = action.BaseCost.ToString("0.##", CultureInfo.InvariantCulture),
                Accent = Array.IndexOf(running, index) >= 0 ? "planned" : string.Empty
            };

            foreach (var condition in action.Conditions) {
                box.Attachments.Add(new(Describe(domain, condition), string.Empty, "condition"));
            }

            foreach (var effect in action.Effects) {
                box.Attachments.Add(
                    new(
                        $"{(effect.Increases ? "+" : "−")} {domain.Keys.NameOf(effect.Key)}",
                        string.Empty,
                        "effect",
                        Above: false
                    )
                );
            }

            box.AddInput("needs");
            box.AddOutput("gives");
            Graph.AddNode(box);
            actions[index] = box;
        }

        Connect(domain);

        return Graph;
    }

    /// <summary>The box showing an action, if it is in the picture.</summary>
    /// <param name="action">Its index in the domain.</param>
    /// <returns>The box, or null.</returns>
    public GraphNode? BoxOf(int action) => actions.GetValueOrDefault(action);

    void Connect(GoapDomain domain) {
        // A goal's edges: every action that can serve one of its conditions.
        var found = new int[Math.Max(1, domain.Count)];

        for (var index = 0; index < domain.Goals.Length; index++) {
            foreach (var condition in domain.Goals[index].Conditions) {
                var count = domain.Servers(in condition, found);

                for (var slot = 0; slot < count; slot++) {
                    Graph.Connect(actions[found[slot]].Outputs[0], goals[index].Inputs[0]);
                }
            }
        }

        // And an action's: every action that can serve one of *its* conditions, which is the edge the
        // search follows when it goes a step deeper.
        for (var index = 0; index < domain.Count; index++) {
            for (var slot = 0; slot < domain[index].Conditions.Length; slot++) {
                foreach (var server in domain.Servers(index, slot)) {
                    Graph.Connect(actions[server].Outputs[0], actions[index].Inputs[0]);
                }
            }
        }
    }

    /// <summary>How deep each action is: the fewest steps from any goal to it.</summary>
    /// <remarks>
    ///     A breadth-first walk out from the goals, which is the same direction the resolver searches
    ///     — so the picture's columns are the search's depths and a domain that plans four deep looks
    ///     four deep.
    /// </remarks>
    void Rank(GoapDomain domain) {
        depths.Clear();

        var queue = new Queue<int>();
        var found = new int[Math.Max(1, domain.Count)];

        foreach (var goal in domain.Goals) {
            foreach (var condition in goal.Conditions) {
                var count = domain.Servers(in condition, found);

                for (var slot = 0; slot < count; slot++) {
                    if (depths.TryAdd(found[slot], 0)) {
                        queue.Enqueue(found[slot]);
                    }
                }
            }
        }

        while (queue.Count > 0) {
            var action = queue.Dequeue();
            var depth = depths[action] + 1;

            for (var slot = 0; slot < domain[action].Conditions.Length; slot++) {
                foreach (var server in domain.Servers(action, slot)) {
                    if (depths.TryAdd(server, depth)) {
                        queue.Enqueue(server);
                    }
                }
            }
        }

        // ⚠ An action no goal can reach still gets a box, in the deepest column. It is almost always
        // a mistake — an effect on a key nothing wants — and hiding it would hide the mistake.
        for (var index = 0; index < domain.Count; index++) {
            depths.TryAdd(index, domain.Count);
        }
    }

    static string Describe(GoapDomain domain, in GoapCondition condition) {
        var symbol = condition.Comparison switch {
            GoapComparison.Less => "<",
            GoapComparison.LessOrEqual => "≤",
            GoapComparison.Greater => ">",
            _ => "≥"
        };

        return $"{domain.Keys.NameOf(condition.Key)} {symbol} {condition.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    static int Row(Dictionary<int, int> rows, int column) {
        rows.TryGetValue(column, out var row);
        rows[column] = row + 1;

        return row;
    }
}
