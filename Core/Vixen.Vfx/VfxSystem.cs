// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Vfx;

/// <summary>
///     One running instance of a compiled graph: its particles, its clock, and its seed.
/// </summary>
/// <remarks>
///     <para>
///         The graph is the effect and this is the effect <i>happening</i>. A graph is shared between
///         every instance of it — the same array of operations, read and never written — and
///         everything that differs between two campfires is here: how far through they are, what
///         their seeds are, and the particles themselves.
///     </para>
///     <para>
///         <b>Two instances with the same seed and the same steps are identical.</b> Not similar:
///         identical, particle for particle, because nothing in the simulation reads a clock, a
///         thread identifier, or anything else the caller did not hand it. That is what makes an
///         effect reproducible in a replay and testable against a golden image, and it is the reason
///         <see cref="VfxRandom" /> is stateless.
///     </para>
///     <para>
///         <b>Time is accumulated, not integrated.</b> A rate spawner keeps its fractional debt across
///         steps, so 2.5 particles a second at sixty hertz emits five particles every two seconds
///         rather than none at all — which is what dropping the fraction would do, and what makes a
///         low-rate emitter mysteriously silent.
///     </para>
/// </remarks>
public sealed class VfxSystem : IDisposable {
    readonly float[] debts;
    readonly int[] bursts;

    bool disposed;

    /// <summary>Starts an instance of a graph.</summary>
    /// <param name="graph">The compiled graph.</param>
    /// <param name="seed">
    ///     The instance's seed. Two instances of one graph with the same seed produce the same
    ///     particles; two with different seeds produce unrelated ones.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public VfxSystem(VfxCompiledGraph graph, uint seed = 1) {
        ArgumentNullException.ThrowIfNull(graph);

        Graph = graph;
        Seed = seed;
        Particles = new(graph.Attributes, graph.Capacity);

        debts = new float[graph.Spawners.Length];
        bursts = new int[graph.Spawners.Length];
    }

    /// <summary>The graph being run.</summary>
    public VfxCompiledGraph Graph { get; }

    /// <summary>The instance's seed.</summary>
    public uint Seed { get; }

    /// <summary>Its particles.</summary>
    public ParticleBuffer Particles { get; }

    /// <summary>How long it has been running, in seconds.</summary>
    public float Time { get; private set; }

    /// <summary>How many particles are alive.</summary>
    public int Count => Particles.Count;

    /// <summary>Whether the spawners are running. Particles already alive keep going either way.</summary>
    /// <remarks>
    ///     Stopping an effect and killing it are different things, and a fire that is put out should
    ///     let its last embers finish rather than blink out. <see cref="Reset" /> is the other one.
    /// </remarks>
    public bool Emitting { get; set; } = true;

    /// <summary>How many particles the last <see cref="Step" /> spawned.</summary>
    public int LastSpawned { get; private set; }

    /// <summary>How many the last <see cref="Step" /> could not spawn because the buffer was full.</summary>
    /// <remarks>
    ///     Reported rather than logged, because a system running at capacity is a normal state for a
    ///     dense effect and an authoring mistake for a sparse one, and only the author can tell which.
    /// </remarks>
    public int LastRefused { get; private set; }

    /// <summary>Advances the effect.</summary>
    /// <param name="deltaTime">How much time passes. Zero or less does nothing.</param>
    public void Step(float deltaTime) {
        ObjectDisposedException.ThrowIf(disposed, this);

        LastSpawned = 0;
        LastRefused = 0;

        if (deltaTime <= 0f) {
            return;
        }

        // Update before spawn, so a particle born this step is not also aged this step. The other
        // order gives every particle one step less of life than it asked for.
        VfxSimulation.Update(Particles, Graph.Updaters, deltaTime);

        Time += deltaTime;

        if (Emitting) {
            Emit(deltaTime);
        }
    }

    /// <summary>Kills every particle and puts the clock back to zero.</summary>
    public void Reset() {
        Particles.Clear();
        Time = 0f;
        Array.Clear(debts);
        Array.Clear(bursts);
        LastSpawned = 0;
        LastRefused = 0;
    }

    /// <summary>Frees the particle storage.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Particles.Dispose();
    }

    /// <summary>Asks every spawner how many particles it wants, and makes them.</summary>
    void Emit(float deltaTime) {
        var spawners = Graph.Spawners;

        for (var index = 0; index < spawners.Length; index++) {
            var spawner = spawners[index];
            var wanted = spawner.Kind switch {
                VfxSpawnerKind.Rate => Trickle(index, spawner, deltaTime),
                VfxSpawnerKind.Burst => Burst(index, spawner),
                _ => 0
            };

            if (wanted <= 0) {
                continue;
            }

            var added = Particles.Spawn(wanted, out var first);

            LastSpawned += added;
            LastRefused += wanted - added;

            VfxSimulation.Initialize(Particles, Graph.Initializers, first, added, Seed);
        }
    }

    /// <summary>How many a rate spawner owes, carrying the fraction it could not pay.</summary>
    int Trickle(int index, VfxSpawner spawner, float deltaTime) {
        if (spawner.Rate <= 0f) {
            return 0;
        }

        debts[index] += spawner.Rate * deltaTime;

        var whole = (int)debts[index];
        debts[index] -= whole;

        return whole;
    }

    /// <summary>How many a burst spawner owes for the interval just passed.</summary>
    /// <remarks>
    ///     Counted from how many bursts <i>should</i> have happened by now rather than by testing
    ///     whether one falls inside this step. A step longer than the interval would otherwise emit
    ///     one burst instead of the several it covers, which is how an effect loses its shape the
    ///     first time the frame rate drops.
    /// </remarks>
    int Burst(int index, VfxSpawner spawner) {
        if (spawner.Count <= 0 || Time < spawner.Time) {
            return 0;
        }

        var due = spawner.Interval > 0f
            ? (int)MathF.Floor((Time - spawner.Time) / spawner.Interval) + 1
            : 1;

        var already = bursts[index];

        if (due <= already) {
            return 0;
        }

        bursts[index] = due;

        return (due - already) * spawner.Count;
    }
}
