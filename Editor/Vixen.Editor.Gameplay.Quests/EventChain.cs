// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Quests;

namespace Vixen.Editor.Gameplay.Quests;

/// <summary>Which way an edge leaves an event.</summary>
public enum EventBranch {
    /// <summary>What succeeding starts.</summary>
    Success,

    /// <summary>What failing starts.</summary>
    Failure
}

/// <summary>One edge of a chain.</summary>
/// <param name="From">Which event it leaves.</param>
/// <param name="To">Which it arrives at.</param>
/// <param name="Branch">Which way it left.</param>
/// <param name="Address">The address it named, kept so a dangling edge can be drawn.</param>
public readonly record struct EventChainEdge(DefId From, DefId To, EventBranch Branch, string Address);

/// <summary>A chain of dynamic events, walked into a shape a canvas can draw.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An event chain is cyclic by design, and that is what decided this library's
///         shape.</b> Doc 28 says the chain graph is authored "on the existing node-graph host", and
///         building it found that the host is the <em>canvas</em> and not
///         <c>Vixen.Editor.NodeGraph</c>'s document model. That model enforces three rules, and a
///         chain breaks two of them: it refuses a cycle as the edge is made, and the camp being lost,
///         retaken and lost again is the content rather than a mistake; and it gives an input one
///         edge, while an event reached from two branches — a failure here, a success there — is
///         ordinary authoring. <c>Vixen.Editor.Ai</c> reached the same conclusion from the other
///         direction, for a tree.
///     </para>
///     <para>
///         <b>So the chain is walked into a spanning tree, and everything the walk could not use is
///         named rather than dropped.</b> A depth-first walk from the entries wires each event once;
///         every remaining edge is a <see cref="BackEdges" /> entry the projection draws as a labelled
///         badge. Nothing is hidden and the picture stays a picture the canvas can lay out.
///     </para>
///     <para>
///         ⚠ <b>A chain with no roots still has entries.</b> Two events that only lead to each other
///         have no event with nothing pointing at it, and a walk that started only from roots would
///         draw an empty canvas for a perfectly good chain. So a component with no root contributes
///         its lowest-addressed event as an entry, which is arbitrary but stable — and stable is what
///         a picture nobody wants to see rearrange itself needs.
///     </para>
/// </remarks>
public sealed class EventChain {
    readonly Dictionary<uint, DynamicEventTemplate> byId;

    EventChain(
        Dictionary<uint, DynamicEventTemplate> byId,
        DynamicEventTemplate[] events,
        EventChainEdge[] edges,
        EventChainEdge[] treeEdges,
        EventChainEdge[] backEdges,
        EventChainEdge[] dangling,
        DefId[] roots,
        DefId[] entries,
        DefId[] order
    ) {
        this.byId = byId;
        Events = events;
        Edges = edges;
        TreeEdges = treeEdges;
        BackEdges = backEdges;
        Dangling = dangling;
        Roots = roots;
        Entries = entries;
        Order = order;
    }

    /// <summary>Every event, in address order.</summary>
    public IReadOnlyList<DynamicEventTemplate> Events { get; }

    /// <summary>Every edge, in the order the events and their branches were authored.</summary>
    public IReadOnlyList<EventChainEdge> Edges { get; }

    /// <summary>The edges the walk drew as wires: one arriving at each event it reached.</summary>
    public IReadOnlyList<EventChainEdge> TreeEdges { get; }

    /// <summary>The edges it could not — a cycle closing, or a second way into an event.</summary>
    public IReadOnlyList<EventChainEdge> BackEdges { get; }

    /// <summary>Edges naming an event this build does not have.</summary>
    public IReadOnlyList<EventChainEdge> Dangling { get; }

    /// <summary>The events nothing leads to.</summary>
    public IReadOnlyList<DefId> Roots { get; }

    /// <summary>Where the walk started: the roots, plus one per rootless component.</summary>
    public IReadOnlyList<DefId> Entries { get; }

    /// <summary>Every reachable event, in the order the walk found them.</summary>
    public IReadOnlyList<DefId> Order { get; }

    /// <summary>Whether anything in it leads back to something it came from.</summary>
    public bool IsCyclic => BackEdges.Any(edge => edge.To.IsSome);

    /// <summary>An event by id.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>It, or null.</returns>
    public DynamicEventTemplate? Find(DefId id) => byId.GetValueOrDefault(id.Value);

    /// <summary>Builds the chain of everything in a library.</summary>
    /// <param name="library">Where the events come from.</param>
    /// <returns>The chain.</returns>
    public static EventChain Build(QuestLibrary library) {
        ArgumentNullException.ThrowIfNull(library);

        var events = library.Events.ToArray();
        var byId = events.ToDictionary(entry => entry.Id.Value);
        var edges = new List<EventChainEdge>();

        foreach (var entry in events) {
            foreach (var link in entry.OnSuccess) {
                edges.Add(new(entry.Id, byId.ContainsKey(link.Def.Value) ? link.Def : DefId.None, EventBranch.Success, link.Address));
            }

            foreach (var link in entry.OnFailure) {
                edges.Add(new(entry.Id, byId.ContainsKey(link.Def.Value) ? link.Def : DefId.None, EventBranch.Failure, link.Address));
            }
        }

        var incoming = new HashSet<uint>(edges.Where(edge => edge.To.IsSome).Select(edge => edge.To.Value));
        var roots = events.Where(entry => !incoming.Contains(entry.Id.Value)).Select(entry => entry.Id).ToArray();

        var visited = new HashSet<uint>();
        var order = new List<DefId>();
        var tree = new List<EventChainEdge>();
        var back = new List<EventChainEdge>();
        var entries = new List<DefId>(roots);

        // Roots first, then whatever is left — which is exactly the events that are only reachable
        // from inside a cycle. Both passes take the events in address order, so the picture is a
        // function of the content and not of a dictionary's mood.
        foreach (var root in roots) {
            Walk(root);
        }

        // ⚠ Whatever is left is reachable only from inside a cycle, and this is the common case rather
        // than the exotic one: in a chain that loops — camp lost, retaken, lost again — *every* event
        // has something pointing at it, so `roots` is empty and a walk that started only from roots
        // would draw nothing at all. The event that leads to the most places is the one a designer
        // reads as the start, ties broken by how much leads *to* it and then by address, so the
        // picture is a function of the content rather than of the order a dictionary enumerated.
        foreach (var entry in events
                     .OrderByDescending(entry => Outgoing(entry.Id))
                     .ThenBy(entry => Incoming(entry.Id))
                     .ThenBy(entry => entry.Definition.Address, StringComparer.Ordinal)) {
            if (visited.Contains(entry.Id.Value)) {
                continue;
            }

            entries.Add(entry.Id);
            Walk(entry.Id);
        }

        return new(
            byId,
            events,
            [.. edges],
            [.. tree],
            [.. back],
            [.. edges.Where(edge => !edge.To.IsSome)],
            roots,
            [.. entries],
            [.. order]
        );

        int Outgoing(DefId id) => edges.Count(edge => edge.From == id && edge.To.IsSome);

        int Incoming(DefId id) => edges.Count(edge => edge.To == id);

        void Walk(DefId id) {
            if (!visited.Add(id.Value)) {
                return;
            }

            order.Add(id);

            foreach (var edge in edges) {
                if (edge.From != id || !edge.To.IsSome) {
                    continue;
                }

                if (visited.Contains(edge.To.Value)) {
                    back.Add(edge);

                    continue;
                }

                tree.Add(edge);
                Walk(edge.To);
            }
        }
    }
}
