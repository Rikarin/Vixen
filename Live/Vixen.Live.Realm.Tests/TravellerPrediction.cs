// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net;
using Vixen.Net.Messaging;
using Vixen.Net.Prediction;
using Vixen.Net.Replication;

namespace Vixen.Live.Realms.Tests;

/// <summary>One step of walking, which is the whole of what a traveller predicts here.</summary>
readonly record struct Stride : IPredictedInput<Stride> {
    /// <summary>How far, this tick.</summary>
    public float Along { get; init; }

    /// <inheritdoc />
    public void Write(ref BitWriter writer) => writer.WriteSingle(Along);

    /// <inheritdoc />
    public static bool TryRead(ref BitReader reader, out Stride value) {
        value = default;

        if (!reader.TryReadSingle(out var along)) {
            return false;
        }

        value = new() { Along = along };

        return true;
    }
}

/// <summary>Where the local player thinks they have got to.</summary>
struct Waypoint {
    /// <summary>How far along.</summary>
    public float Along;
}

/// <summary>The one replicator this harness needs, written by hand.</summary>
/// <remarks>
///     ⚠ <b>The history is snapshotted through the registry, so a registry with nothing in it makes
///     every recorded frame empty.</b> <c>PredictionHistory.Count</c> would still climb and a test
///     asserting on it would still pass — while measuring nothing being thrown away, which is the
///     opposite of what "a transfer costs one prediction reset" is a claim about.
/// </remarks>
sealed class WaypointReplicator : IComponentReplicator {
    static readonly QuantizeRange Range = new(-1000f, 1000f, 16);

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<Waypoint>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName(typeof(Waypoint).FullName!);

    /// <inheritdoc />
    public string TypeName => typeof(Waypoint).FullName!;

    /// <inheritdoc />
    public Channel Channel => Channel.Unreliable;

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<Waypoint>.Id]);

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<Waypoint>(entity);
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ArgumentNullException.ThrowIfNull(world);

        writer.WriteQuantized(world.Read<Waypoint>(entity).Along, Range);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        ArgumentNullException.ThrowIfNull(world);

        if (!reader.TryReadQuantized(Range, out var along)) {
            return false;
        }

        world.Set(entity, new Waypoint { Along = along });

        return true;
    }
}

/// <summary>A traveller's client-side prediction, and what a realm transfer costs it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 27 § Intra-map seams is specific about the price of a transfer:</b>
///         <i>"<c>ClientPrediction</c>'s history is cleared … the visible cost of a transfer is one
///         interpolation delay of extra smoothing and one prediction reset"</i>. This is a real
///         <see cref="ClientPrediction{T}" /> stepping every pump so that "one reset" is a claim about
///         a history that had something in it.
///     </para>
///     <para>
///         ⚠ <b>A reset counter over a prediction loop that never ran would read zero for the wrong
///         reason</b>, which is why the earlier version of this harness declined to assert on one at
///         all rather than assert it green.
///     </para>
///     <para>
///         ⚠ <b>Rolling back across a realm boundary is meaningless</b> — the state to replay from
///         belongs to a simulation that no longer owns this player — so the transfer's cost is a
///         <em>clear</em> and never a resimulation. <see cref="Resimulated" /> staying at zero is what
///         says the harness never quietly turned the seam into a rollback.
///     </para>
/// </remarks>
sealed class TravellerPrediction : IDisposable {
    readonly World world;
    readonly InputLog<Stride> log = new();
    readonly ClientPrediction<Stride> prediction;
    readonly Entity player;

    uint tick;

    public TravellerPrediction(PlayerKey key) {
        var registry = new ReplicationRegistry();

        registry.Register(new WaypointReplicator());

        world = new($"traveller-{key.Character:N}");
        prediction = new(registry, log, Simulate);
        player = world.Create(new NetworkId(1), default(Predicted), default(Waypoint));
    }

    /// <summary>How many times the history has been thrown away.</summary>
    public int Resets { get; private set; }

    /// <summary>How many predicted ticks those resets discarded, in total.</summary>
    /// <remarks>Zero would mean the resets were free, which would mean they were not resets.</remarks>
    public int Discarded { get; private set; }

    /// <summary>How deep the history is right now.</summary>
    public int Depth => prediction.History.Count;

    /// <summary>How many ticks have ever been predicted.</summary>
    public long Predicted => prediction.PredictedTickCount;

    /// <summary>How many ticks have been replayed after a correction.</summary>
    public long Resimulated => prediction.ResimulatedTickCount;

    /// <summary>One tick of walking forward.</summary>
    public void Step() => prediction.Step(world, new Tick(++tick), new Stride { Along = 1 });

    /// <summary>What a commit costs: everything the client had guessed, dropped.</summary>
    public void Reset() {
        Resets++;
        Discarded += prediction.History.Count;
        prediction.Clear();
    }

    public void Dispose() => world.Dispose();

    void Simulate(World simulated, Tick at, in Stride input) {
        ref var waypoint = ref simulated.Get<Waypoint>(player);

        waypoint.Along += input.Along;
    }
}
