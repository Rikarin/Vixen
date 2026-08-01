// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Net.Prediction;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine.Players;

/// <summary>Decays each predicted object's correction into an offset on its visuals.</summary>
/// <remarks>
///     <para>
///         <c>ClientPrediction</c> reports corrections and deliberately does not hide them — "a
///         rollback moves things, sometimes visibly, and smoothing that is a presentation problem with
///         its own answer". This is that answer, wired: <see cref="Take" /> after a reconciliation,
///         and the offset decays over <see cref="PredictionSmoother.HalfLife" /> from there.
///     </para>
///     <para>
///         In <see cref="SystemPhase.PreRender" /> and before <c>TransformSystem</c>, which is where
///         <c>PhysicsInterpolationSystem</c> puts the same kind of work and for the same reason: what
///         the renderer sees is the smoothed pose and what the simulation sees is the stepped one.
///     </para>
///     <para>
///         ⚠ <b>A correction larger than <see cref="PredictionSmoother.SnapDistance" /> is not
///         smoothed.</b> Hiding a teleport is hiding the fact that the player is somewhere else, and a
///         mesh sliding thirty metres across the level is worse than a cut. <c>SnapCount</c> says how
///         often that happens.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
[UpdateBefore(typeof(TransformSystem))]
public sealed class PredictionSmoothingSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription smoothed = new QueryDescription().WithAll<PredictionSmoothing, LocalTransform>();

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PredictionSmoothing>()
        .Write<LocalTransform>()
        .Build();

    /// <summary>The per-object error, its half life and its snap distance.</summary>
    public PredictionSmoother Smoother { get; init; } = new();

    /// <summary>How many entities were offset on the last pass.</summary>
    public int SmoothedCount { get; private set; }

    /// <summary>Takes the corrections a reconciliation produced.</summary>
    /// <param name="corrections">
    ///     <c>ClientPrediction.Corrections</c>, which is emptied and refilled by every reconciliation
    ///     — so this has to be called after each one rather than once in a while.
    /// </param>
    public void Take(IReadOnlyList<PredictionCorrection> corrections) => Smoother.Take(corrections);

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Apply(context.World, context.Time.Elapsed);
        return dependency;
    }

    /// <summary>Ages every error and writes the offsets.</summary>
    /// <param name="world">The world.</param>
    /// <param name="elapsed">How long the frame was.</param>
    /// <remarks>
    ///     Public so a test or a tool can settle the smoothing without standing up a runner — the
    ///     same reason <c>VirtualCameraSystem.Evaluate</c> is.
    /// </remarks>
    public void Apply(World world, TimeSpan elapsed) {
        ArgumentNullException.ThrowIfNull(world);

        Smoother.Advance(elapsed);
        SmoothedCount = 0;

        foreach (var chunk in world.Chunks(smoothed)) {
            var smoothings = chunk.ReadValues<PredictionSmoothing>();
            var transforms = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                var smoothing = smoothings[index];

                if (!world.IsAlive(smoothing.Body) || !world.TryGet<NetworkId>(smoothing.Body, out var id)) {
                    continue;
                }

                // Draw against zero, so what comes back is the error alone rather than a position.
                var error = Smoother.Draw(id, Vector3.Zero);

                // The error is measured in world space and written in the parent's, so a body that
                // turns with its aim does not drag its own smoothing round with it. An unrotated body
                // makes this the identity, which is the common case and costs four multiplies.
                if (world.TryGet<LocalTransform>(smoothing.Body, out var body)) {
                    error = Quaternion.Transform(error, Quaternion.Conjugate(body.Rotation));
                }

                transforms[index].Position = smoothing.Rest + error;
                SmoothedCount++;
            }
        }
    }
}
