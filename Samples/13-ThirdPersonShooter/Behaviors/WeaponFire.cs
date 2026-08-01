// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Players;
using Vixen.Physics.Ecs;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>A hitscan weapon: a ray down the aim, a cooldown, and a noise.</summary>
/// <remarks>
///     <para>
///         <b>It reads the aim from the controller and not from the body.</b> The capsule never turns
///         — see <see cref="CharacterAnimation" /> — so the body's rotation is not where the player is
///         looking. <c>ControlRotation</c> on the controller is, and it is the same value the camera
///         is built from, which is what makes the shot go where the crosshair is.
///     </para>
///     <para>
///         ⚠ <b>Fire is a button on <c>MoveIntent</c>, which means it is a level and not an edge.</b>
///         The intent says "held this frame"; whether that is a new press is this behaviour's own
///         business, because a weapon that fires once and a weapon that fires while held want
///         different answers from the same bit.
///     </para>
/// </remarks>
public sealed class WeaponFire : Behavior {
    bool wasHeld;
    float cooldown;

    /// <summary>Where the ray starts, so tracers and flashes have somewhere to be.</summary>
    public Entity Muzzle { get; init; }

    /// <summary>What the ray is cast against.</summary>
    public required PhysicsScene Physics { get; init; }

    /// <summary>Where the shot and the reload come from.</summary>
    public GameSounds Sounds { get; init; } = GameSounds.Silent;

    /// <summary>Seconds between shots. Six hundred rounds a minute.</summary>
    public float Interval { get; init; } = 0.1f;

    /// <summary>How far a shot reaches, in metres.</summary>
    public float Range { get; init; } = 120f;

    /// <summary>Whether the weapon keeps firing while the button is held.</summary>
    public bool IsAutomatic { get; init; } = true;

    /// <summary>How many shots have been fired, which is what a test asserts on.</summary>
    public int Shots { get; private set; }

    /// <summary>Where the last shot landed, or the muzzle if it hit nothing.</summary>
    public Vector3 LastImpact { get; private set; }

    /// <inheritdoc />
    protected override void Update() {
        cooldown = MathF.Max(0f, cooldown - Time.DeltaSeconds);

        if (!Has<MoveIntent>()) {
            return;
        }

        ref readonly var intent = ref Read<MoveIntent>();

        var held = intent.IsHeld(MoveButtons.Primary);
        var pressed = held && (IsAutomatic || !wasHeld);

        wasHeld = held;

        if (intent.IsHeld(MoveButtons.Reload)) {
            Sounds.Play(Sounds.Reload, 0.6f);
        }

        if (!pressed || cooldown > 0f) {
            return;
        }

        Shoot();
        cooldown = Interval;
    }

    void Shoot() {
        Sounds.Play(Sounds.Gunshot);
        Shots++;

        var controller = Player.ControllerOf(World, Entity);

        if (controller.IsNull || !World.Has<ControlRotation>(controller)) {
            return;
        }

        ref readonly var aim = ref World.Read<ControlRotation>(controller);

        // Eye height rather than the entity origin, which is at the feet: a ray from there clips the
        // floor at any downward pitch and every shot reads as a hit on the ground you are standing on.
        var origin = Position + new Vector3(0f, 1.55f, 0f);
        var direction = aim.Forward();

        LastImpact = Physics.World.Raycast(origin, direction, Range, out var hit)
            ? hit.Position
            : origin + (direction * Range);
    }
}
