// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Net.Time;

// Rooted, because this namespace is Vixen.Net.Physics and an unrooted `Vixen.Physics` resolves
// against the enclosing namespace first — finding this one, which has no PhysicsWorld in it. The
// name collision is worth having: the package is the physics half of networking and should say so.
using BodyHandle = global::Vixen.Physics.Bodies.BodyHandle;
using PhysicsWorld = global::Vixen.Physics.PhysicsWorld;

namespace Vixen.Net.Physics;

/// <summary>The world as a client plausibly saw it, for as long as it takes to ask one question.</summary>
/// <remarks>
///     <para>
///         <b>The problem.</b> A player with an 80 ms round trip aims at a running target, fires, and
///         the packet reaches the server 40 ms later — by which time the target has moved a third of
///         a metre and the shot misses something the shooter watched themselves hit. Snapshot
///         interpolation makes it worse rather than better: the client was not even rendering the
///         newest state it held, it was rendering behind it. Without compensation, hitting anything
///         moving requires leading it by an amount that depends on your own latency, which players
///         experience as the game being broken.
///     </para>
///     <para>
///         <b>The answer, and its cost.</b> The server keeps a short history of where every
///         compensated body was, moves those bodies back to where the shooter saw them, asks physics
///         the question, and puts them back. The cost is paid by the person who was shot: they had
///         already moved, and from their side they were killed after reaching cover. That trade is
///         not avoidable — it is the one every server-authoritative shooter makes — and the settings
///         are where it is decided rather than assumed.
///     </para>
///     <para>
///         <b>Only tracked bodies move.</b> Static geometry did not go anywhere, so rewinding it
///         would be work with no effect; the tracked set is the players and the vehicles, which is
///         tens of bodies rather than thousands. It also means the walls stay where they are during
///         a rewound query, which is what makes a shot through a doorway resolve against the doorway
///         as it is now — and doorways do not move.
///     </para>
///     <para>
///         <b>Nothing here believes the client.</b> A hit claim names a tick, and the client chooses
///         that number. <see cref="ClampFor" /> is the rule that decides what it is allowed to mean,
///         and it is public and separately testable because it is the anti-cheat surface: a player
///         cannot rewind further than their <i>measured</i> round trip makes plausible, however far
///         back they say they were looking.
///     </para>
///     <para>
///         Single-threaded frame code, like everything else in the tick. <see cref="Capture" /> runs
///         once a tick beside the replication capture; a rewind happens inside the handling of one
///         remote call and is over before the next thing runs.
///     </para>
/// </remarks>
public sealed class LagCompensator {
    readonly PhysicsWorld world;
    readonly TickRate rate;
    readonly Dictionary<uint, BodyHistory> histories = [];
    readonly List<uint> tracked = [];
    readonly List<Saved> restoring = [];
    readonly List<uint> forgetting = [];

    bool rewound;

    /// <summary>Creates a compensator over a physics world.</summary>
    /// <param name="world">The world whose bodies are rewound.</param>
    /// <param name="rate">The tick rate, for turning a round trip into a number of ticks.</param>
    /// <param name="settings">How far back it will go. Defaults if null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public LagCompensator(PhysicsWorld world, TickRate rate, LagCompensationSettings? settings = null) {
        ArgumentNullException.ThrowIfNull(world);

        this.world = world;
        this.rate = rate.IsValid ? rate : TickRate.Default;
        Settings = settings ?? new LagCompensationSettings();
    }

    /// <summary>How far back it is willing to look.</summary>
    public LagCompensationSettings Settings { get; }

    /// <summary>How many bodies are having their history kept.</summary>
    public int TrackedCount => tracked.Count;

    /// <summary>The newest tick captured.</summary>
    public Tick NewestTick { get; private set; }

    /// <summary>Whether anything has been captured at all.</summary>
    public bool HasCaptured { get; private set; }

    /// <summary>How many rewinds have been performed.</summary>
    public long RewindCount { get; private set; }

    /// <summary>How many claims were clamped to the window rather than honoured as asked.</summary>
    /// <remarks>
    ///     Worth watching. A few is a high-latency player; a lot from one connection is either a
    ///     player on a connection nothing can rescue or somebody sending ticks by hand, and the
    ///     difference between those two is a question for whatever reads this counter rather than
    ///     for this type.
    /// </remarks>
    public long ClampedCount { get; private set; }

    /// <summary>Whether a rewind is in progress. Bodies are not where the simulation left them.</summary>
    public bool IsRewound => rewound;

    /// <summary>Starts keeping history for a body.</summary>
    /// <param name="body">The body — a player, a vehicle, anything a shot is aimed at.</param>
    /// <remarks>
    ///     Tracking a body that is already tracked does nothing. Its history begins empty, so a body
    ///     tracked this tick cannot be rewound past this tick, which is correct: the server does not
    ///     know where it was before it was asked to remember.
    /// </remarks>
    public void Track(BodyHandle body) {
        if (body.IsNone || histories.ContainsKey(body.Value)) {
            return;
        }

        histories[body.Value] = new(Settings.HistoryTicks);
        tracked.Add(body.Value);
    }

    /// <summary>Stops keeping history for a body, and forgets what it had.</summary>
    /// <param name="body">The body.</param>
    /// <returns>Whether it was being tracked.</returns>
    public bool Forget(BodyHandle body) {
        if (!histories.Remove(body.Value)) {
            return false;
        }

        tracked.Remove(body.Value);

        return true;
    }

    /// <summary>Whether a body is having its history kept.</summary>
    /// <param name="body">The body.</param>
    /// <returns>Whether it is.</returns>
    public bool IsTracked(BodyHandle body) => histories.ContainsKey(body.Value);

    /// <summary>The history kept for a body, for a diagnostic that wants to draw it.</summary>
    /// <param name="body">The body.</param>
    /// <param name="history">Its history, if it is tracked.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetHistory(BodyHandle body, out BodyHistory? history) => histories.TryGetValue(body.Value, out history);

    /// <summary>Records where every tracked body is, once, for this tick.</summary>
    /// <param name="at">The tick.</param>
    /// <exception cref="InvalidOperationException">A rewind is in progress.</exception>
    /// <remarks>
    ///     <para>
    ///         Call it from the server's tick, after the simulation has stepped and beside the
    ///         replication capture — the two are recording the same instant for two different
    ///         reasons, and a history that lags the snapshot by a tick rewinds to a world the client
    ///         was never sent.
    ///     </para>
    ///     <para>
    ///         Refuses to run during a rewind, because the poses it would record are the historical
    ///         ones it just installed. That mistake is self-reinforcing — the history fills with its
    ///         own past — and it is far better as a thrown exception at the call site than as a
    ///         slowly rotting hit-registration bug.
    ///     </para>
    ///     <para>
    ///         Bodies that have gone away are dropped here rather than at destruction, because
    ///         nothing tells this type that a handle was destroyed and asking the world is what it is
    ///         already doing.
    ///     </para>
    /// </remarks>
    public void Capture(Tick at) {
        if (rewound) {
            throw new InvalidOperationException(
                "Capture ran during a rewind, so it would have recorded the historical poses as the present. "
                    + "The rewind scope has to be disposed before the tick captures."
            );
        }

        forgetting.Clear();

        foreach (var value in tracked) {
            var body = new BodyHandle(value);

            if (!world.IsAlive(body)) {
                forgetting.Add(value);

                continue;
            }

            world.GetTransform(body, out var position, out var rotation);
            histories[value].Add(new(at, position, rotation));
        }

        foreach (var value in forgetting) {
            histories.Remove(value);
            tracked.Remove(value);
        }

        NewestTick = at;
        HasCaptured = true;
    }

    /// <summary>What a claimed tick is allowed to mean, given what the server measured.</summary>
    /// <param name="claimed">The tick the client says it was looking at.</param>
    /// <param name="roundTrip">
    ///     That player's round trip, as the session measured it — never as the packet asserts.
    /// </param>
    /// <returns>The tick that will actually be rewound to.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Three bounds, and each of them is somebody trying something.</b> A claim cannot be
    ///         in the future, because a client cannot have seen a tick the server has not run. It
    ///         cannot be older than <see cref="LagCompensationSettings.MaxRewind" />, which is the
    ///         limit of what anybody is willing to be shot from. And it cannot be older than the
    ///         player's own measured round trip plus the interpolation slack, which is the bound that
    ///         does the real work: a player on a 20 ms connection claiming to have been looking at
    ///         the world 200 ms ago is claiming to have been shown something they were not shown.
    ///     </para>
    ///     <para>
    ///         Clamped rather than refused, deliberately. A refusal punishes a player for their
    ///         latency by discarding the shot entirely; a clamp resolves it against the oldest world
    ///         they could honestly have seen, which is the worst case that is still fair to them.
    ///         <see cref="ClampedCount" /> is how often that happened.
    ///     </para>
    /// </remarks>
    public Tick ClampFor(Tick claimed, TimeSpan roundTrip) {
        if (!HasCaptured) {
            return claimed;
        }

        var allowed = Math.Max(0, rate.ToTicks(roundTrip)) + Math.Max(0, Settings.InterpolationSlackTicks);
        var ceiling = Math.Max(0, rate.ToTicks(Settings.MaxRewind));
        var window = Math.Min(allowed, ceiling);

        // Signed distances throughout, because ticks are modular and have no ordering. "How far back
        // is this claim" is the only question that means anything, and it is a subtraction.
        var back = NewestTick.Subtract(claimed);

        if (back <= 0) {
            // At or after the newest tick. Not a lie worth counting — a client's clock legitimately
            // runs ahead of the server's, which is what TickManager.LeadTicks exists to do.
            return NewestTick;
        }

        if (back <= window) {
            return claimed;
        }

        ClampedCount++;

        return NewestTick.Subtract(window);
    }

    /// <summary>Moves every tracked body to where it was, until the scope is disposed.</summary>
    /// <param name="at">The tick to rewind to.</param>
    /// <param name="fraction">How far past that tick, from 0 to 1.</param>
    /// <returns>The scope. <b>Dispose it</b>, or the world stays in the past.</returns>
    /// <exception cref="InvalidOperationException">A rewind is already in progress.</exception>
    /// <remarks>
    ///     Prefer <see cref="RewindFor" />, which applies the clamp. This one rewinds to exactly what
    ///     it is told and is for a caller that has already decided what is allowed — a replay tool,
    ///     a test, or a server validating something that did not come from a client.
    /// </remarks>
    public RewindScope Rewind(Tick at, float fraction = 0f) {
        if (rewound) {
            throw new InvalidOperationException(
                "A rewind is already in progress. Rewinds do not nest: the second one would record the "
                    + "first one's historical poses as the present and restore the world to them."
            );
        }

        restoring.Clear();

        foreach (var value in tracked) {
            var body = new BodyHandle(value);

            if (!world.IsAlive(body) || !histories[value].TrySample(at, fraction, Settings.Interpolate, out var pose)) {
                continue;
            }

            world.GetTransform(body, out var position, out var rotation);
            restoring.Add(new(body, position, rotation));

            // Not activated. A rewound body is being asked a question, not simulated, and waking it
            // would have the solver integrate from a pose it is about to be moved out of.
            world.SetTransform(body, pose.Position, pose.Rotation, activate: false);
        }

        rewound = true;
        RewindCount++;

        return new(this, at, restoring.Count);
    }

    /// <summary>Rewinds to what a player's claim is allowed to mean.</summary>
    /// <param name="claimed">The tick the client says it was looking at.</param>
    /// <param name="roundTrip">That player's measured round trip.</param>
    /// <param name="fraction">How far past that tick, from 0 to 1.</param>
    /// <returns>The scope. Dispose it.</returns>
    /// <remarks>The one to use for anything a client asked for. See <see cref="ClampFor" />.</remarks>
    public RewindScope RewindFor(Tick claimed, TimeSpan roundTrip, float fraction = 0f) =>
        Rewind(ClampFor(claimed, roundTrip), fraction);

    /// <summary>Forgets every history, for a match that is starting again.</summary>
    /// <exception cref="InvalidOperationException">A rewind is in progress.</exception>
    public void Clear() {
        if (rewound) {
            throw new InvalidOperationException("The world is rewound. Dispose the scope before clearing.");
        }

        foreach (var history in histories.Values) {
            history.Clear();
        }

        HasCaptured = false;
        NewestTick = default;
    }

    internal void Restore() {
        if (!rewound) {
            return;
        }

        // Reversed, so that if two tracked bodies ever share a handle value through a bug the last
        // write wins in the same order the reads happened.
        for (var i = restoring.Count - 1; i >= 0; i--) {
            var entry = restoring[i];

            if (world.IsAlive(entry.Body)) {
                world.SetTransform(entry.Body, entry.Position, entry.Rotation, activate: false);
            }
        }

        restoring.Clear();
        rewound = false;
    }

    readonly record struct Saved(BodyHandle Body, Vector3 Position, Quaternion Rotation);
}
