// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Players;

namespace Vixen.Net.Engine.Players;

/// <summary>Rounds each player's intent to what the wire can carry, before anything acts on it.</summary>
/// <remarks>
///     <para>
///         <b>A system rather than a line in a manual.</b> The client sends a quantized input and the
///         server computes from the decoded numbers; a client that predicted with its full-precision
///         intent would disagree with the server by the rounding on <i>every</i> tick, on a connection
///         with no loss at all — and the cost is a rollback per snapshot, which reads as jitter and
///         profiles as the prediction feature working hard.
///     </para>
///     <para>
///         It was documented in three places before it was a system, which is the weaker answer: a
///         footgun you have written about is still a footgun. Registering this makes it impossible to
///         forget, and a single-player build simply does not register it.
///     </para>
///     <para>
///         <b>Between <c>PlayerInputSystem</c> and <c>PossessionSystem</c>, in the same phase.</b> The
///         first writes the intent from a device, this rounds it in place, and the third forwards it
///         to the pawn — so the pawn, the wire and the prediction all see the same numbers, and the
///         controller's own copy is the rounded one too, which is what a UI reading it should show.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Input)]
[UpdateAfter(typeof(PlayerInputSystem))]
[UpdateBefore(typeof(PossessionSystem))]
public sealed class PlayerInputQuantizeSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription players = new QueryDescription().WithAll<PlayerController, MoveIntent>();

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PlayerController>()
        .Write<MoveIntent>()
        .Build();

    /// <summary>How many intents were rounded on the last pass.</summary>
    public int RoundedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Round(context.World);
        return dependency;
    }

    /// <summary>Rounds every player's intent in place.</summary>
    /// <param name="world">The world.</param>
    /// <remarks>
    ///     Public so a test or a tool can apply it without standing up a runner — the same reason
    ///     <c>PossessionSystem.Apply</c> is.
    /// </remarks>
    public void Round(World world) {
        ArgumentNullException.ThrowIfNull(world);

        RoundedCount = 0;

        foreach (var chunk in world.Chunks(players)) {
            var intents = chunk.Values<MoveIntent>();

            for (var index = 0; index < chunk.Count; index++) {
                intents[index] = PlayerMoveInput.Round(intents[index]);
                RoundedCount++;
            }
        }
    }
}
