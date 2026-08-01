// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Players;

/// <summary>Turns each player's device into the aim and the intent their pawn will act on.</summary>
/// <remarks>
///     <para>
///         <b>Runs in <see cref="SystemPhase.Input" />, after <c>InputUpdateSystem</c>.</b> That
///         system advances the action assets from the device state the host submitted; this one reads
///         the result. The ordering is an <see cref="UpdateAfterAttribute" /> rather than a phase of
///         its own because both belong to the phase whose documented job is "devices are polled and
///         input state is published" — and because a game that replaces <c>InputUpdateSystem</c> with
///         something of its own still wants this to run after whatever it replaced it with.
///     </para>
///     <para>
///         <b>The sources are a side table, not components.</b> An <c>InputActions</c> asset is a
///         managed object per player; putting it in a chunk would make every player archetype hold a
///         reference and would put a managed field on the path a rollback copies. So a controller is
///         registered with <see cref="Bind" /> and the system walks its own list — which is also what
///         makes this testable with no world, no devices and no platform.
///     </para>
///     <para>
///         <b>A controller that is not accepting input has its intent cleared, not frozen.</b> A
///         player who was sprinting when a menu opened should not still be sprinting behind it, and a
///         held jump that survives a cutscene fires on the frame it ends. The aim is left exactly
///         where it was, because that is the thing the controller exists to preserve.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Input)]
[UpdateAfter(typeof(Input.InputUpdateSystem))]
public sealed class PlayerInputSystem : SystemBase, IDeclaredAccess {
    readonly Dictionary<Entity, IPlayerInputSource> sources = [];

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PlayerController>()
        .Write<ControlRotation>()
        .Write<MoveIntent>()
        .Build();

    /// <summary>How many controllers have a source.</summary>
    public int Count => sources.Count;

    /// <summary>Says where one controller's intent comes from.</summary>
    /// <param name="controller">The controller entity.</param>
    /// <param name="source">The source, which may be a device, a planner, a replay or a test.</param>
    /// <remarks>
    ///     Binding a controller that already has a source replaces it, which is what a device
    ///     hot-swap and a "take over this character" both want.
    /// </remarks>
    public void Bind(Entity controller, IPlayerInputSource source) {
        ArgumentNullException.ThrowIfNull(source);
        sources[controller] = source;
    }

    /// <summary>Takes a controller's source away, leaving it with its last intent cleared.</summary>
    /// <param name="controller">The controller entity.</param>
    /// <returns>Whether it had one.</returns>
    public bool Unbind(Entity controller) => sources.Remove(controller);

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Sample(context.World, context.Time.DeltaSeconds);
        return dependency;
    }

    /// <summary>Samples every bound controller.</summary>
    /// <param name="world">The world.</param>
    /// <param name="deltaTime">How long the frame was, in seconds.</param>
    /// <remarks>
    ///     Public so a test or a tool can drive a player without standing up a runner — the same
    ///     reason <c>TransformSystem.Resolve</c> and <c>VirtualCameraSystem.Evaluate</c> are.
    /// </remarks>
    public void Sample(World world, float deltaTime) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var (entity, source) in sources) {
            // A controller made by hand rather than by Player.Create may be missing one of the two
            // components this writes. Skipping is right and throwing is not: the source is bound
            // before the entity is finished often enough that a hard failure would be a worse
            // ordering rule than the one it enforced.
            if (!world.IsAlive(entity)
                || !world.Has<PlayerController>(entity)
                || !world.Has<ControlRotation>(entity)
                || !world.Has<MoveIntent>(entity)) {
                continue;
            }

            ref readonly var player = ref world.Read<PlayerController>(entity);
            ref var intent = ref world.Get<MoveIntent>(entity);

            if (!player.AcceptsInput) {
                // Cleared rather than left alone. The aim below is untouched on purpose: what a
                // player was holding is stale the moment they stop being asked, and where they were
                // looking is not.
                intent = default;
                continue;
            }

            ref var rotation = ref world.Get<ControlRotation>(entity);
            source.Sample(ref rotation, ref intent, deltaTime);
        }
    }
}
