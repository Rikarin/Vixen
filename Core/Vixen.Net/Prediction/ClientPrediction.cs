// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Motion;
using Vixen.Net.Replication;

namespace Vixen.Net.Prediction;

/// <summary>One tick of the game's own simulation, as the predictor replays it.</summary>
/// <param name="world">The world to advance.</param>
/// <param name="tick">The tick being simulated.</param>
/// <param name="input">What the local player did on it.</param>
/// <typeparam name="T">The game's input type.</typeparam>
/// <remarks>
///     <b>It must be a pure function of the world and the input.</b> Not a style preference — the same
///     tick is simulated twice whenever a snapshot disagrees, and anything the step reads that is not
///     in the world or the input is something that will be different the second time: wall-clock time,
///     a random number generator nobody seeded, the frame counter, an event queue that was drained.
///     A step that is not reproducible does not merely predict badly; it makes the correction itself
///     wrong, and the symptom is a player who twitches on a connection that is behaving perfectly.
/// </remarks>
public delegate void PredictedStep<T>(World world, Tick tick, in T input) where T : struct, IPredictedInput<T>;

/// <summary>Guesses the local player's future, and takes it back when the server disagrees.</summary>
/// <remarks>
///     <para>
///         <b>The loop is three lines and the subtlety is entirely in what they mean.</b> Each tick,
///         the client records its input, simulates forward with it, and records the result. When a
///         snapshot arrives describing tick T — which is some ticks in the past, because it travelled
///         — the world is holding the server's word for T and the client's guesses for everything
///         after. Comparing the server's T against the guess for T is what decides whether those
///         guesses survive.
///     </para>
///     <para>
///         <b>Agreement is the common case and it must be the cheap one.</b> A comparison of encoded
///         bytes and a restore of the already-recorded present, with no simulation at all. If
///         reconciliation cost a replay every snapshot, prediction would be a constant multiple on
///         the simulation budget rather than an occasional one — which is what
///         <see cref="ResimulatedTickCount" /> is for: it is the price of the feature, and it should
///         be near zero on a connection that is behaving.
///     </para>
///     <para>
///         <b>Disagreement replays, and replays from the server's state rather than from the guess.</b>
///         That is what makes the correction converge: the snapshot has already put the predicted
///         entities where the server says they were, so simulating T+1 onwards with the inputs that
///         were used the first time reaches a present built on truth. Correcting the present directly
///         — nudging it toward the server's value — is the tempting alternative and it does not
///         converge, because the error it is correcting was produced by ticks it is not redoing.
///     </para>
///     <para>
///         <b>What it does not do is hide the correction.</b> A rollback moves things, sometimes
///         visibly, and smoothing that is a presentation problem with its own answer —
///         <c>OwnerSmoothing</c>, which gives the error to the camera as an offset that decays while
///         the simulation takes it at once.
///     </para>
/// </remarks>
/// <typeparam name="T">The game's input type.</typeparam>
public sealed class ClientPrediction<T> where T : struct, IPredictedInput<T> {
    static readonly QueryDescription Moving = new QueryDescription()
        .RequireAll([ComponentType<NetworkId>.Id, ComponentType<Predicted>.Id, ComponentType<NetworkTransform>.Id]);

    readonly InputLog<T> log;
    readonly PredictedStep<T> step;
    readonly Dictionary<uint, Vector3> beforeReplay = [];
    readonly List<PredictionCorrection> corrections = [];

    /// <summary>What was predicted for each of the last few ticks.</summary>
    public PredictionHistory History { get; }

    /// <summary>How far each predicted object moved in the last reconciliation, if any did.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Reported rather than smoothed here.</b> A rollback moves things, sometimes visibly,
    ///         and hiding that is a presentation decision — how much to hide, over how long, and past
    ///         what distance to stop trying. <c>OwnerSmoothing</c> is the answer and
    ///         <c>PredictionSmoother</c> is the wiring; this is the fact both of them need.
    ///     </para>
    ///     <para>
    ///         Emptied at the start of every <see cref="Reconcile" />, so it describes the last one and
    ///         not a running total. A reconciliation that agreed leaves it empty, which is most of
    ///         them.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<PredictionCorrection> Corrections => corrections;

    /// <summary>The newest tick simulated.</summary>
    public Tick Current { get; private set; }

    /// <summary>Whether anything has been simulated.</summary>
    public bool HasSimulated { get; private set; }

    /// <summary>Ticks simulated forward, which is once each.</summary>
    public long PredictedTickCount { get; private set; }

    /// <summary>Snapshots that agreed with what was predicted, and cost nothing to reconcile.</summary>
    public long ConfirmedCount { get; private set; }

    /// <summary>Snapshots that disagreed, each of which cost a replay.</summary>
    /// <remarks>
    ///     The number that says whether the simulation is actually deterministic. A game whose
    ///     predicted step reads anything outside the world and the input mispredicts on <i>every</i>
    ///     snapshot, on a connection with no loss at all — and it looks like jitter rather than like a
    ///     bug, which is why this is a counter and not a log line.
    /// </remarks>
    public long MispredictionCount { get; private set; }

    /// <summary>Ticks simulated a second time. The price of the feature.</summary>
    public long ResimulatedTickCount { get; private set; }

    /// <summary>Snapshots describing a tick the history no longer holds.</summary>
    /// <remarks>
    ///     A round trip longer than <see cref="PredictionHistory.Depth" /> ticks, which is a
    ///     connection prediction cannot help with. The state is taken as it arrives and the guesses
    ///     after it are abandoned, because there is nothing to check them against.
    /// </remarks>
    public long LostHistoryCount { get; private set; }

    /// <summary>Creates a predictor.</summary>
    /// <param name="registry">The component types that may be predicted, which are the replicated ones.</param>
    /// <param name="log">The input log — the same one the client sends from.</param>
    /// <param name="step">The game's simulation for one tick.</param>
    /// <param name="depth">How many ticks of history to keep.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ClientPrediction(ReplicationRegistry registry, InputLog<T> log, PredictedStep<T> step, int depth = 32) {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(step);

        History = new(registry, depth);
        this.log = log;
        this.step = step;
    }

    /// <summary>Simulates one tick forward with the local player's input.</summary>
    /// <param name="world">The client's world.</param>
    /// <param name="tick">The tick to simulate.</param>
    /// <param name="input">What the player did.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     The input is recorded <b>before</b> it is used, so that a replay of this tick uses exactly
    ///     what was used the first time. Recording after would be the same thing on the happy path and
    ///     would silently skip the record if the step threw.
    /// </remarks>
    public void Step(World world, Tick tick, in T input) {
        ArgumentNullException.ThrowIfNull(world);

        log.Record(tick, input);
        step(world, tick, input);
        History.Record(world, tick);

        Current = tick;
        HasSimulated = true;
        PredictedTickCount++;
    }

    /// <summary>Reconciles what the server said with what was guessed.</summary>
    /// <param name="world">
    ///     The client's world, immediately after a snapshot for <paramref name="confirmed" /> was
    ///     applied to it — so predicted entities hold the server's values and everything else holds
    ///     whatever the snapshot said.
    /// </param>
    /// <param name="confirmed">The tick the snapshot describes.</param>
    /// <returns>How many ticks had to be simulated again. Zero means the guess was right.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public int Reconcile(World world, Tick confirmed) {
        ArgumentNullException.ThrowIfNull(world);

        corrections.Clear();

        if (!HasSimulated || confirmed.IsAfter(Current)) {
            // A snapshot from the future of what this client has simulated. Nothing was guessed about
            // it, so there is nothing to check and the arriving state stands.
            History.Record(world, confirmed);
            Current = confirmed;
            HasSimulated = true;

            return 0;
        }

        if (!History.Has(confirmed)) {
            LostHistoryCount++;

            // The world holds the server's state for a tick this client can no longer reason about.
            // Taking it as the present is the honest answer: the alternative is replaying from it
            // with inputs whose starting state has been forgotten, which produces a confident wrong
            // answer rather than an obvious jump.
            History.Clear();
            History.Record(world, confirmed);
            Current = confirmed;

            return 0;
        }

        if (History.Matches(world, confirmed)) {
            ConfirmedCount++;

            // The guess was right, so the guesses after it stand. The snapshot moved the predicted
            // entities back to where they were at `confirmed`; putting the recorded present back is
            // what undoes that, and it is a copy rather than a simulation.
            History.TryRestore(world, Current);

            return 0;
        }

        MispredictionCount++;
        Remember(world);

        var replayed = 0;

        for (var tick = confirmed.Add(1); !tick.IsAfter(Current); tick = tick.Add(1)) {
            // An input that is no longer held is one the log trimmed, which means the server
            // acknowledged it — so the default is the same "nothing new happened" the server's own
            // starved tick would have used.
            log.TryGet(tick, out var input);

            step(world, tick, input);
            History.Record(world, tick);
            replayed++;
        }

        ResimulatedTickCount += replayed;
        Measure(world);

        return replayed;
    }

    /// <summary>Where the predicted objects were before the replay moved them.</summary>
    void Remember(World world) {
        beforeReplay.Clear();

        foreach (var chunk in world.Chunks(Moving)) {
            var ids = chunk.ReadValues<NetworkId>();
            var transforms = chunk.ReadValues<NetworkTransform>();

            for (var row = 0; row < chunk.Count; row++) {
                beforeReplay[ids[row].Value] = transforms[row].Position;
            }
        }
    }

    /// <summary>And where they ended up.</summary>
    void Measure(World world) {
        foreach (var chunk in world.Chunks(Moving)) {
            var ids = chunk.ReadValues<NetworkId>();
            var transforms = chunk.ReadValues<NetworkTransform>();

            for (var row = 0; row < chunk.Count; row++) {
                if (!beforeReplay.TryGetValue(ids[row].Value, out var from)) {
                    continue;
                }

                var to = transforms[row].Position;

                // Only what actually moved. A mispredicted object is usually one of several
                // predicted ones, and reporting the still ones would have a smoother working off a
                // zero error for every object on every correction.
                if (from != to) {
                    corrections.Add(new(ids[row], from, to));
                }
            }
        }
    }

    /// <summary>Forgets everything, for a client that is reconnecting into a fresh world.</summary>
    public void Clear() {
        History.Clear();
        HasSimulated = false;
        Current = default;
    }
}

/// <summary>How far one object moved when a prediction was corrected.</summary>
/// <param name="Id">Which object.</param>
/// <param name="From">Where it was being drawn.</param>
/// <param name="To">Where the corrected simulation says it is.</param>
/// <remarks>
///     <b>The simulation has already taken the correction</b> — this is what happened, not a proposal.
///     What a presentation layer does with it is give the difference to the camera as an offset that
///     decays, so what the player sees glides while what the server will judge is already right.
/// </remarks>
public readonly record struct PredictionCorrection(NetworkId Id, Vector3 From, Vector3 To);
