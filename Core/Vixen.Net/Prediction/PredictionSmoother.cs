// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;

namespace Vixen.Net.Prediction;

/// <summary>Hides a correction from the eye without hiding it from the simulation.</summary>
/// <remarks>
///     <para>
///         <b>The simulation takes the correction at once and the picture catches up.</b> That split is
///         the whole idea and it is <c>OwnerSmoothing</c>'s, applied per predicted object rather than
///         to one avatar: what the server will judge is already right, and what the player sees glides
///         there over a tenth of a second. Blending the <i>simulation</i> instead would mean the
///         client keeps predicting from a position the server has already disagreed with, which does
///         not converge.
///     </para>
///     <para>
///         <b>One smoother per object, made on demand and dropped with the object.</b> A single shared
///         error would be wrong the moment two predicted things are corrected differently — which is
///         the normal case for a player and the vehicle they are driving.
///     </para>
/// </remarks>
public sealed class PredictionSmoother {
    readonly Dictionary<uint, OwnerSmoothing> byObject = [];

    /// <summary>How long the visible half of an error takes to disappear.</summary>
    public TimeSpan HalfLife { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>How large a correction stops being smoothed and is simply shown.</summary>
    /// <remarks>
    ///     Past this the object did not drift, it was moved — a respawn, a teleport, a shove. Dragging
    ///     a camera across that is worse than arriving.
    /// </remarks>
    public float SnapDistance { get; init; } = 3f;

    /// <summary>How many objects are being smoothed right now.</summary>
    public int Count => byObject.Count;

    /// <summary>Corrections taken.</summary>
    public long CorrectionCount { get; private set; }

    /// <summary>Corrections too large to hide, which were shown instead.</summary>
    public long SnapCount { get; private set; }

    /// <summary>Takes whatever the last reconciliation moved.</summary>
    /// <param name="corrections">What <c>ClientPrediction.Corrections</c> reported.</param>
    /// <exception cref="ArgumentNullException"><paramref name="corrections" /> is null.</exception>
    public void Take(IReadOnlyList<PredictionCorrection> corrections) {
        ArgumentNullException.ThrowIfNull(corrections);

        for (var index = 0; index < corrections.Count; index++) {
            var correction = corrections[index];
            var smoothing = For(correction.Id);
            var before = smoothing.SnapCount;

            smoothing.Correct(correction.From, correction.To);
            CorrectionCount++;

            if (smoothing.SnapCount != before) {
                SnapCount++;
            }
        }
    }

    /// <summary>Moves every offset toward zero.</summary>
    /// <param name="elapsed">How long since the last time.</param>
    /// <remarks>
    ///     Once a frame rather than once a tick, because this is about what is drawn and a frame is
    ///     when that happens. An object whose error has been worked off is forgotten, so a scene that
    ///     has settled costs nothing.
    /// </remarks>
    public void Advance(TimeSpan elapsed) {
        if (byObject.Count == 0) {
            return;
        }

        var settled = new List<uint>();

        foreach (var (id, smoothing) in byObject) {
            smoothing.Apply(Vector3.Zero, elapsed);

            if (!smoothing.IsSmoothing) {
                settled.Add(id);
            }
        }

        foreach (var id in settled) {
            byObject.Remove(id);
        }
    }

    /// <summary>Where an object should be drawn, given where the simulation says it is.</summary>
    /// <param name="id">The object.</param>
    /// <param name="simulated">Where it actually is.</param>
    /// <returns>Where to draw it.</returns>
    public Vector3 Draw(NetworkId id, in Vector3 simulated) =>
        byObject.TryGetValue(id.Value, out var smoothing) ? simulated + smoothing.Error : simulated;

    /// <summary>Forgets an object, because it is gone or has been put somewhere.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it was being smoothed.</returns>
    public bool Forget(NetworkId id) => byObject.Remove(id.Value);

    OwnerSmoothing For(NetworkId id) {
        if (!byObject.TryGetValue(id.Value, out var smoothing)) {
            smoothing = new() { HalfLife = HalfLife, SnapDistance = SnapDistance };
            byObject[id.Value] = smoothing;
        }

        return smoothing;
    }
}

/// <summary>Decides which objects this client predicts, from who owns them.</summary>
/// <remarks>
///     <para>
///         <b>The policy that was missing, and it is the one everything else already answers.</b>
///         <see cref="Predicted" /> is a tag, and until now a game put it on by hand. What a client
///         should predict is what it is <i>allowed to decide</i> — which is exactly
///         <c>NetworkRules.Write</c>, the same question the rigid bodies and the animators ask.
///         Inventing a second notion of "mine" beside the rules is how the two come to disagree, and
///         the day they disagree is the day a client predicts something the server will overrule on
///         every single tick.
///     </para>
///     <para>
///         <b>It removes the tag as well as adding it</b>, because ownership is transferable: a
///         vehicle somebody else takes is one this client must stop predicting, and a predicted object
///         whose prediction is never confirmed is a correction every snapshot for as long as it lives.
///     </para>
/// </remarks>
public sealed class PredictedOwnershipSystem {
    static readonly QueryDescription Networked = new QueryDescription().RequireAll([ComponentType<NetworkId>.Id]);

    readonly List<Entity> adding = [];
    readonly List<Entity> removing = [];

    /// <summary>Who decides what. Without it nothing is predicted, which is the safe answer.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>Which player this peer is.</summary>
    public PlayerId Local { get; set; } = PlayerId.None;

    /// <summary>How many objects are predicted right now.</summary>
    public int PredictedCount { get; private set; }

    /// <summary>Objects that started being predicted.</summary>
    public long AddedCount { get; private set; }

    /// <summary>Objects that stopped.</summary>
    public long RemovedCount { get; private set; }

    /// <summary>Brings the tags in line with who owns what.</summary>
    /// <param name="world">The client's world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Update(World world) {
        ArgumentNullException.ThrowIfNull(world);

        adding.Clear();
        removing.Clear();
        PredictedCount = 0;

        foreach (var chunk in world.Chunks(Networked)) {
            var ids = chunk.ReadValues<NetworkId>();
            var entities = chunk.Entities;

            for (var row = 0; row < chunk.Count; row++) {
                var mine = Rules is { } rules && Local.IsValid && rules.MayWrite(ids[row], Local);
                var tagged = world.Has<Predicted>(entities[row]);

                if (mine) {
                    PredictedCount++;
                }

                if (mine && !tagged) {
                    adding.Add(entities[row]);
                } else if (!mine && tagged) {
                    removing.Add(entities[row]);
                }
            }
        }

        // Outside the sweep: adding or removing a tag is a structural change and the chunks are being
        // walked.
        foreach (var entity in adding) {
            world.Add<Predicted>(entity);
            AddedCount++;
        }

        foreach (var entity in removing) {
            world.Remove<Predicted>(entity);
            RemovedCount++;
        }
    }
}
