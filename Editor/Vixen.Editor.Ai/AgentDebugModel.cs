// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Editor.Ai;

/// <summary>Where the panel's picture of an agent came from.</summary>
public enum AgentDebugOrigin : byte {
    /// <summary>Nothing has been shown yet.</summary>
    None,

    /// <summary>Taken from an <see cref="AiSystem" /> in this process.</summary>
    Local,

    /// <summary>Read off doc 13's channel from a build somewhere else.</summary>
    Remote
}

/// <summary>
///     What the editor's agent debugger shows: one agent's state, its recent decisions, and what is
///     visibly wrong with it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 37 § P7's "editor panels over the same records", and <i>the same records</i> is the
///         whole of it.</b> This holds an <see cref="AiAgentSnapshot" /> and a list of
///         <see cref="AgentDebugRecord" />s, which are the two things the runtime overlay draws — so
///         the panel is a richer view over one implementation rather than a second one that will
///         disagree with it in six months.
///     </para>
///     <para>
///         ⚠ <b>Two ways in and one way out.</b> <see cref="Refresh" /> photographs an
///         <see cref="AiSystem" /> in this process; <see cref="Show" /> takes a snapshot that arrived
///         over a wire. After that the panel cannot tell, which is what makes debugging a dedicated
///         server the same tool as debugging play mode rather than a second one written later and
///         worse.
///     </para>
///     <para>
///         ⚠ <b>No <c>Control</c> anywhere in here.</b> It is the model, so the interesting behaviour
///         — which agent is selected, what the log says, whether a breakpoint is set — is asserted by
///         tests that stand up no window, the same bargain <c>BehaviorTreeModel</c> makes.
///     </para>
/// </remarks>
public sealed class AgentDebugModel {
    readonly List<Entity> agents = [];
    readonly List<AgentDebugRecord> log = [];
    readonly List<AiFinding> findings = [];
    readonly List<int> path = [];
    readonly List<int> keys = [];
    readonly QueryDescription query = new QueryDescription().WithAll<AiAgent>();

    /// <summary>The agent being shown.</summary>
    public AiAgentSnapshot Snapshot { get; } = new();

    /// <summary>Where that picture came from.</summary>
    public AgentDebugOrigin Origin { get; private set; }

    /// <summary>Every agent the last <see cref="Refresh" /> found, in schedule order.</summary>
    public IReadOnlyList<Entity> Agents => agents;

    /// <summary>Which one is selected.</summary>
    public Entity Selected { get; set; }

    /// <summary>What the selected agent has decided lately, oldest first.</summary>
    public IReadOnlyList<AgentDebugRecord> Log => log;

    /// <summary>What <see cref="AiDiagnosis" /> makes of the whole log.</summary>
    public IReadOnlyList<AiFinding> Findings => findings;

    /// <summary>The node breakpoints, shared with whatever is running the agents.</summary>
    public AiBreakpoints Breakpoints { get; } = new();

    /// <summary>What counts as bad enough to report.</summary>
    public AiDiagnosisSettings Thresholds { get; set; } = AiDiagnosisSettings.Default;

    /// <summary>Whether the selected agent is stopped at a breakpoint.</summary>
    public bool Halted { get; private set; }

    /// <summary>The selected agent's running tree, when it is running one.</summary>
    /// <remarks>
    ///     ⚠ <b>The instance itself, not a copy of it, and only the canvas uses it.</b> A tree tinted
    ///     by every node's last result needs a result per node, which is more than a bounded snapshot
    ///     carries — so the live canvas reads the instance and everything else reads
    ///     <see cref="Snapshot" />. That is why <see cref="BehaviorTreeInstance.Trace" /> exists and
    ///     why <see cref="Refresh" /> is what turns it on: a panel nobody has open costs nothing.
    /// </remarks>
    public BehaviorTreeInstance? Instance { get; private set; }

    /// <summary>The path from the root to the active node, root first, by execution index.</summary>
    public IReadOnlyList<int> ActivePath => path;

    /// <summary>The selected agent's plan, when it is planning over a domain.</summary>
    public GoapPlan? Plan { get; private set; }

    /// <summary>
    ///     The selected agent's world keys, projected as the search would read them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Projected here rather than taken from the snapshot's rows</b>, because the snapshot
    ///     formats them as text and the viewer needs to <i>test conditions</i> against them. Empty
    ///     when the agent is not planning, which is what makes "nobody is running this domain" a
    ///     third state the viewer can draw.
    /// </remarks>
    public ReadOnlySpan<int> WorldKeys => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(keys);

    /// <summary>Re-reads a running system.</summary>
    /// <param name="system">The system.</param>
    /// <param name="world">Its world.</param>
    /// <param name="time">The clock.</param>
    /// <returns>Whether the selected agent was found.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public bool Refresh(AiSystem system, World world, GameTime time = default) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(world);

        // ⚠ Handed over here rather than by whoever built the system, because a panel that owns the
        // breakpoint set and never installs it is a panel whose toggles do nothing — and the failure
        // is silent, which is the worst kind for a debugger.
        system.Breakpoints = Breakpoints;

        agents.Clear();

        foreach (var chunk in world.Chunks(query)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                agents.Add(entities[index]);
            }
        }

        agents.Sort();

        if (Selected.IsNull || !agents.Contains(Selected)) {
            Selected = agents.Count > 0 ? agents[0] : Entity.Null;
        }

        AiDiagnosis.Analyse(system.Debug, findings, Thresholds);
        AiDiagnosis.Read(system.Debug, Selected, log);

        Instance = Selected.IsNull || !world.Has<AiAgent>(Selected)
            ? null
            : system.TreeOf(in world.Read<AiAgent>(Selected));

        Halted = Instance is { Halted: true };
        path.Clear();
        keys.Clear();
        Plan = null;

        if (!Selected.IsNull && world.Has<AiAgent>(Selected)) {
            ref readonly var agent = ref world.Read<AiAgent>(Selected);

            Plan = system.PlanningOf(in agent)?.Plan;

            if (Plan is not null && agent.Asset < system.Domains.Count) {
                var domain = system.Domains[agent.Asset];
                var projected = new int[domain.Keys.Count];
                var context = new AgentContext(
                    world,
                    Selected,
                    system.BlackboardOf(in agent) ?? new Blackboard(system.Layout),
                    system.Shared,
                    time,
                    agent.Seed,
                    system.Actions
                );

                domain.Keys.Project(in context, projected);
                keys.AddRange(projected);
            }
        }

        if (Instance is { } tree) {
            // ⚠ Turned on here rather than by whoever built the system, because it is a cost only a
            // panel that is open wants paid — and a panel that owned the flag and never set it would
            // be a canvas that never tinted anything, silently.
            tree.Trace = true;

            Span<int> walked = stackalloc int[64];
            var depth = tree.ActivePath(walked);

            for (var index = 0; index < depth; index++) {
                path.Add(walked[index]);
            }
        }

        if (Selected.IsNull || !AiSnapshots.Take(system, world, Selected, Snapshot, time)) {
            Snapshot.Clear();
            Origin = AgentDebugOrigin.None;

            return false;
        }

        Origin = AgentDebugOrigin.Local;

        return true;
    }

    /// <summary>Shows a picture that arrived from somewhere else.</summary>
    /// <param name="snapshot">The agent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot" /> is null.</exception>
    /// <remarks>
    ///     The log and the findings are left alone: a remote snapshot is one agent at one instant and
    ///     says nothing about what the far end recorded, so overwriting the local log with nothing
    ///     would be losing the only history the panel has.
    /// </remarks>
    public void Show(AiAgentSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        Snapshot.Clear();
        Snapshot.Entity = snapshot.Entity;
        Snapshot.Tick = snapshot.Tick;
        Snapshot.Planner = snapshot.Planner;
        Snapshot.Asset = snapshot.Asset;
        Snapshot.Action = snapshot.Action;
        Snapshot.Status = snapshot.Status;
        Snapshot.Reason = snapshot.Reason;
        Snapshot.Position = snapshot.Position;
        Snapshot.Located = snapshot.Located;

        foreach (var row in snapshot.Rows) {
            Snapshot.Add(row);
        }

        Selected = snapshot.Entity;
        Origin = AgentDebugOrigin.Remote;
        Halted = false;

        // ⚠ A remote picture has no instance behind it, so the live canvas draws nothing rather than
        // last frame's local agent — which is the failure mode of every debugger that keeps a stale
        // handle around.
        Instance = null;
        Plan = null;
        path.Clear();
        keys.Clear();
    }

    /// <summary>The rows of one section, which is what one table in the panel draws.</summary>
    /// <param name="section">The section.</param>
    /// <returns>Them.</returns>
    public IEnumerable<AiDebugRow> Section(AiDebugSection section) => Snapshot.Section(section);

    /// <summary>What the panel says is wrong with the agent it is showing.</summary>
    /// <returns>The findings for it, in order.</returns>
    public IEnumerable<AiFinding> SelectedFindings() {
        foreach (var finding in findings) {
            if (finding.Entity == Selected) {
                yield return finding;
            }
        }
    }

    /// <summary>Sets or clears a breakpoint on a node of a tree.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="node">Which node.</param>
    /// <returns>Whether it is now set.</returns>
    public bool ToggleBreakpoint(Symbol tree, int node) => Breakpoints.Toggle(tree, node);

    /// <summary>Lets the selected agent go, if a breakpoint stopped it.</summary>
    /// <param name="system">The system running it.</param>
    /// <param name="world">Its world.</param>
    /// <returns>Whether anything was resumed.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public bool Resume(AiSystem system, World world) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(world);

        if (Selected.IsNull || !world.IsAlive(Selected) || !world.Has<AiAgent>(Selected)) {
            return false;
        }

        if (system.TreeOf(in world.Read<AiAgent>(Selected)) is not { Halted: true } instance) {
            return false;
        }

        instance.Resume();
        Halted = false;

        return true;
    }
}
