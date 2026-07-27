// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;

namespace Vixen.Ecs.Systems;

/// <summary>
///     Owns a world's systems and runs them, one phase at a time.
/// </summary>
/// <remarks>
///     <para>
///         Within a phase, systems run against the dependency graph: each takes the combined handles
///         of the earlier systems it conflicts with, and returns a handle of its own. Nothing waits
///         for anything it does not have to.
///     </para>
///     <para>
///         A phase is bracketed. The world's version moves on before anything runs, so that
///         everything the phase writes is distinguishable from everything before it; then the systems
///         run; then their work is completed; then the command buffer is played back. Completing
///         before playback is not a detail — a structural change moves rows between chunks, and a job
///         still walking one would be walking memory that has just been overwritten.
///     </para>
///     <para>
///         The fixed-step accumulator is not here. How many times <see cref="SystemPhase.FixedUpdate" />
///         runs in a frame is the game loop's decision, and the loop is <c>Vixen.Engine</c>'s
///         (see [03](../../../docs/plan/03-core-foundation.md) § GameTime). This runs a phase when
///         asked to.
///     </para>
/// </remarks>
public sealed class SystemRunner : IDisposable {
    readonly List<ISystem> registered = [];
    readonly CommandBuffer commands;

    SystemGraph? graph;
    bool initialised;

    /// <summary>The world the systems operate on.</summary>
    public World World { get; }

    /// <summary>The scheduler systems hand work to, or <see langword="null" /> to run inline.</summary>
    public JobScheduler? Jobs { get; }

    /// <summary>The buffer structural change is recorded into, played back at every phase boundary.</summary>
    public CommandBuffer Commands => commands;

    /// <summary>The graph, built on first use and rebuilt when a system is added.</summary>
    public SystemGraph Graph => graph ??= SystemGraph.Build(registered);

    /// <summary>Creates a runner.</summary>
    /// <param name="world">The world.</param>
    /// <param name="jobs">The scheduler, or <see langword="null" /> to let systems run inline.</param>
    public SystemRunner(World world, JobScheduler? jobs = null) {
        ArgumentNullException.ThrowIfNull(world);
        World = world;
        Jobs = jobs;
        commands = new(world);
    }

    /// <summary>Adds a system. Its phase and ordering come from its attributes.</summary>
    /// <param name="system">The system.</param>
    /// <returns>This runner, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The systems have already been initialised.</exception>
    public SystemRunner Add(ISystem system) {
        ArgumentNullException.ThrowIfNull(system);

        if (initialised) {
            throw new InvalidOperationException(
                "Systems cannot be added after the first phase has run. The graph is the schedule, "
                + "and rebuilding it mid-frame would change what a running system is allowed to touch."
            );
        }

        registered.Add(system);
        graph = null;
        return this;
    }

    /// <summary>Initialises every system, in the order the graph puts them.</summary>
    /// <param name="time">The clock to hand the systems.</param>
    public void Initialize(GameTime time = default) {
        if (initialised) {
            return;
        }

        initialised = true;
        var context = new SystemContext(World, time, Jobs, commands);

        foreach (var node in Graph.All) {
            node.System.Initialize(in context);
        }

        // Whatever initialisation recorded is applied before the first phase, so a system that seeds
        // entities in Initialize sees them in its first Update rather than one frame late.
        commands.Playback();
    }

    /// <summary>Runs one phase to completion.</summary>
    /// <param name="phase">Which phase.</param>
    /// <param name="time">The clock to hand the systems.</param>
    public void RunPhase(SystemPhase phase, GameTime time) {
        Initialize(time);

        var nodes = Graph.InPhase(phase);

        if (nodes.Count == 0) {
            return;
        }

        // First, so that everything this phase writes is stamped with a version strictly greater
        // than anything before it. Advancing afterwards instead would stamp the phase's writes with
        // the same version a caller reads before calling — and "what changed since then" would
        // answer "nothing", which is the one answer a change filter must never get wrong.
        World.AdvanceVersion();

        var context = new SystemContext(World, time, Jobs, commands);
        var handles = new JobHandle[nodes.Count];

        for (var index = 0; index < nodes.Count; index++) {
            handles[index] = nodes[index].System.Update(in context, Dependency(nodes[index], handles));
        }

        // Complete before playback, not after: a structural change moves rows between chunks, and a
        // job still walking one would be walking memory that has just been overwritten.
        foreach (var handle in handles) {
            Jobs?.Complete(handle);
        }

        commands.Playback();
    }

    /// <summary>Runs every phase once, in order.</summary>
    /// <param name="time">The clock to hand the systems.</param>
    /// <remarks>
    ///     Convenient for a test or a tool. A game loop calls <see cref="RunPhase" /> itself, because
    ///     it has to run <see cref="SystemPhase.FixedUpdate" /> a number of times that only it knows.
    /// </remarks>
    public void RunFrame(GameTime time) {
        foreach (var phase in Enum.GetValues<SystemPhase>()) {
            RunPhase(phase, time);
        }
    }

    static JobHandle Dependency(SystemNode node, JobHandle[] handles) {
        if (node.DependsOn.Count == 0) {
            return default;
        }

        if (node.DependsOn.Count == 1) {
            return handles[node.DependsOn[0]];
        }

        var combining = new JobHandle[node.DependsOn.Count];

        for (var index = 0; index < combining.Length; index++) {
            combining[index] = handles[node.DependsOn[index]];
        }

        return JobHandle.Combine(combining);
    }

    /// <inheritdoc />
    public void Dispose() {
        // Reverse registration order, so a system disposes before whatever it was given.
        for (var index = registered.Count - 1; index >= 0; index--) {
            registered[index].Dispose();
        }

        registered.Clear();
        graph = null;
    }
}
